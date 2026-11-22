using System.Collections.Concurrent;
using System.Numerics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace UExplorer.Rendering;

/// <summary>
/// UE 5.2 post-process chain for the native renderer, in the same order as FDeferredShadingSceneRenderer / AddPostProcessingPasses:
/// TemporalAA (history resolve with Halton jitter) -> EyeAdaptation (Histogram, PostProcessHistogram.usf + PostProcessEyeAdaptation.usf)
/// -> Bloom (Standard / Gaussian, 6 stages, PostProcessBloomSetup + GaussianBlur) -> Tonemapper (done in Unreal52DeferredShader.PSToneMap).
/// Defaults follow FPostProcessSettings in UE 5.2 (BloomIntensity 0.675, Bloom1..6 Size/Tint, AutoExposureBias 1, SpeedUp 3, SpeedDown 1,
/// LowPercent 10, HighPercent 90, extended luminance range EV100 -10..20).
/// </summary>
public sealed class NativePostProcess : IDisposable
{
    public const int HistogramBins = 64;
    public const float HistogramLogMin = -10f; // EV100 histogram range when ExtendDefaultLuminanceRange is enabled
    public const float HistogramLogMax = 20f;

    // FPostProcessSettings defaults (UE 5.2 Engine/Source/Runtime/Engine/Private/Scene.cpp)
    private static readonly (float Size, Vector3 Tint)[] BloomStages =
    [
        (0.3f, new Vector3(0.3465f)),
        (1.0f, new Vector3(0.138f)),
        (2.0f, new Vector3(0.1176f)),
        (10.0f, new Vector3(0.066f)),
        (30.0f, new Vector3(0.066f)),
        (64.0f, new Vector3(0.061f)),
    ];
    private const float BloomSizeScale = 4.0f;

    private readonly ID3D11Device _device;
    private readonly List<IDisposable> _owned = [];
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _taaPs, _downsamplePs, _upsamplePs;
    private readonly ID3D11ComputeShader _histogramCs, _adaptCs;
    private readonly ID3D11Buffer _cb;
    private readonly ID3D11SamplerState _linearClamp;
    private readonly ID3D11Buffer _histogram, _exposure;
    private readonly ID3D11UnorderedAccessView _histogramUav, _exposureUav;
    public ID3D11ShaderResourceView ExposureSrv { get; }

    private readonly Vector4[] _cbScratch = new Vector4[8];
    private readonly ID3D11ShaderResourceView[] _taaSrvs = new ID3D11ShaderResourceView[3];
    private readonly ID3D11ShaderResourceView[] _null3Srvs = new ID3D11ShaderResourceView[3];

    private int _width, _height;
    private ID3D11Texture2D[] _history = [];
    private ID3D11RenderTargetView[] _historyRtv = [];
    private ID3D11ShaderResourceView[] _historySrv = [];
    private int _historyIndex;
    private bool _historyValid;

    private ID3D11Texture2D[] _down = [], _bloom = [];
    private ID3D11RenderTargetView[] _downRtv = [], _bloomRtv = [];
    private ID3D11ShaderResourceView[] _downSrv = [], _bloomSrv = [];
    private (int W, int H)[] _levelSize = [];

    private Matrix4x4 _prevWorldToClip;
    private Vector3 _prevCamera;
    private bool _hasPrev;
    private int _jitterIndex;
    private bool _exposureValid;

    public Vector2 CurrentJitterPixels { get; private set; }

    private static readonly ConcurrentDictionary<string, byte[]> _shaderBytecodeCache = new(StringComparer.Ordinal);
    private static byte[] GetOrCompileBytecode(string entry, string profile) =>
        _shaderBytecodeCache.GetOrAdd($"{entry}|{profile}",
            _ => Compiler.Compile(Hlsl, entry, entry, profile, ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray());

    private T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }

    public NativePostProcess(ID3D11Device device)
    {
        _device = device;
        _vs = Own(device.CreateVertexShader(GetOrCompileBytecode("VSPost", "vs_5_0")));
        _taaPs = Own(device.CreatePixelShader(GetOrCompileBytecode("PSTemporalAA", "ps_5_0")));
        _downsamplePs = Own(device.CreatePixelShader(GetOrCompileBytecode("PSBloomDownsample", "ps_5_0")));
        _upsamplePs = Own(device.CreatePixelShader(GetOrCompileBytecode("PSBloomUpsample", "ps_5_0")));
        _histogramCs = Own(device.CreateComputeShader(GetOrCompileBytecode("CSHistogram", "cs_5_0")));
        _adaptCs = Own(device.CreateComputeShader(GetOrCompileBytecode("CSEyeAdaptation", "cs_5_0")));
        _cb = Own(device.CreateBuffer(16 * 16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        _linearClamp = Own(device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue)));

        _histogram = Own(device.CreateBuffer(new BufferDescription(HistogramBins * 4, BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferAllowRawViews, 0)));
        _histogramUav = Own(device.CreateUnorderedAccessView(_histogram, new UnorderedAccessViewDescription(_histogram, Format.R32_Typeless, 0, HistogramBins, BufferUnorderedAccessViewFlags.Raw)));

        var initial = new[] { new Vector4(1f, 0.18f, 0f, 0f) };
        _exposure = Own(device.CreateBuffer(initial.AsSpan(), new BufferDescription(16, BindFlags.UnorderedAccess | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16)));
        _exposureUav = Own(device.CreateUnorderedAccessView(_exposure, new UnorderedAccessViewDescription(_exposure, Format.Unknown, 0, 1)));
        ExposureSrv = Own(device.CreateShaderResourceView(_exposure, new ShaderResourceViewDescription(_exposure, Format.Unknown, 0, 1)));
    }

    /// <summary>Halton(2,3) sub-pixel jitter (TemporalAA.cpp, 8 samples), in pixels in [-0.5, 0.5].</summary>
    public Matrix4x4 JitterProjection(Matrix4x4 worldToClip, int width, int height, bool enabled)
    {
        if (!enabled)
        {
            CurrentJitterPixels = Vector2.Zero;
            return worldToClip;
        }
        _jitterIndex = (_jitterIndex + 1) % 8;
        var j = new Vector2(Halton(_jitterIndex + 1, 2) - 0.5f, Halton(_jitterIndex + 1, 3) - 0.5f);
        CurrentJitterPixels = j;
        var jitter = Matrix4x4.Identity;
        jitter.M41 = 2f * j.X / width;
        jitter.M42 = -2f * j.Y / height;
        return worldToClip * jitter;
    }

    private static float Halton(int index, int b)
    {
        float result = 0f, f = 1f;
        while (index > 0)
        {
            f /= b;
            result += f * (index % b);
            index /= b;
        }
        return result;
    }

    public void InvalidateHistory()
    {
        _historyValid = false;
        _hasPrev = false;
    }

    private void EnsureTargets(int width, int height)
    {
        if (width == _width && height == _height && _history.Length > 0)
            return;
        DisposeTargets();
        _width = width;
        _height = height;
        _history = new ID3D11Texture2D[2];
        _historyRtv = new ID3D11RenderTargetView[2];
        _historySrv = new ID3D11ShaderResourceView[2];
        for (var i = 0; i < 2; i++)
        {
            _history[i] = _device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, (uint) width, (uint) height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
            _historyRtv[i] = _device.CreateRenderTargetView(_history[i]);
            _historySrv[i] = _device.CreateShaderResourceView(_history[i]);
        }
        _historyValid = false;

        var levels = BloomStages.Length;
        _levelSize = new (int, int)[levels];
        _down = new ID3D11Texture2D[levels]; _downRtv = new ID3D11RenderTargetView[levels]; _downSrv = new ID3D11ShaderResourceView[levels];
        _bloom = new ID3D11Texture2D[levels]; _bloomRtv = new ID3D11RenderTargetView[levels]; _bloomSrv = new ID3D11ShaderResourceView[levels];
        for (var i = 0; i < levels; i++)
        {
            var w = Math.Max(1, width >> (i + 1));
            var h = Math.Max(1, height >> (i + 1));
            _levelSize[i] = (w, h);
            Create(ref _down[i], ref _downRtv[i], ref _downSrv[i], w, h);
            Create(ref _bloom[i], ref _bloomRtv[i], ref _bloomSrv[i], w, h);
        }

        void Create(ref ID3D11Texture2D tex, ref ID3D11RenderTargetView rtv, ref ID3D11ShaderResourceView srv, int w, int h)
        {
            tex = _device.CreateTexture2D(new Texture2DDescription(Format.R11G11B10_Float, (uint) w, (uint) h, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
            rtv = _device.CreateRenderTargetView(tex);
            srv = _device.CreateShaderResourceView(tex);
        }
    }

    public readonly record struct Result(ID3D11ShaderResourceView SceneColor, ID3D11ShaderResourceView? Bloom);

    /// <summary>
    /// Runs TAA, eye adaptation and bloom on the HDR scene color. <paramref name="jitteredClipToTranslatedWorld"/> is the current (jittered)
    /// clip-to-translated-world matrix; <paramref name="worldToClip"/> the current unjittered world-to-clip used as next frame's history.
    /// </summary>
    public Result Execute(ID3D11DeviceContext ctx, ID3D11ShaderResourceView sceneColor, ID3D11ShaderResourceView depth,
        int width, int height, Matrix4x4 jitteredClipToTranslatedWorld, Matrix4x4 worldToClip, Vector3 cameraPosition, float deltaSeconds,
        bool temporalAA, bool autoExposure, bool bloom)
    {
        EnsureTargets(width, height);
        ctx.ClearState();
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.PSSetSampler(0, _linearClamp);

        var current = sceneColor;

        // ---------------------------------------------------------------- Temporal AA
        if (temporalAA)
        {
            // Large camera jumps (teleport / new level) reset the history like UE's bCameraCut.
            if (_hasPrev && Vector3.Distance(_prevCamera, cameraPosition) > 5000f)
                _historyValid = false;

            var prevTranslatedToClip = Matrix4x4.CreateTranslation(cameraPosition) * (_hasPrev ? _prevWorldToClip : worldToClip);
            var clipToPrevClip = jitteredClipToTranslatedWorld * prevTranslatedToClip;
            var cb = _cbScratch;
            WriteMatrix(cb, 0, clipToPrevClip);
            cb[4] = new Vector4(CurrentJitterPixels, 0, 0);
            cb[5] = new Vector4(width, height, 1f / width, 1f / height);
            cb[6] = new Vector4(0.04f, _historyValid ? 1f : 0f, 0, 0); // r.TemporalAACurrentFrameWeight = 0.04
            Upload(ctx, cb);

            var write = _historyIndex;
            var read = 1 - _historyIndex;
            ctx.OMSetRenderTargets(_historyRtv[write]);
            ctx.RSSetViewport(0, 0, width, height);
            ctx.PSSetShader(_taaPs);
            ctx.PSSetConstantBuffer(0, _cb);
            _taaSrvs[0] = sceneColor;
            _taaSrvs[1] = depth;
            _taaSrvs[2] = _historySrv[read];
            ctx.PSSetShaderResources(0, _taaSrvs);
            ctx.Draw(3, 0);
            ctx.PSSetShaderResources(0, _null3Srvs);
            ctx.OMSetRenderTargets((ID3D11RenderTargetView) null!);
            current = _historySrv[write];
            _historyIndex = read;
            _historyValid = true;
        }
        else
        {
            _historyValid = false;
        }
        _prevWorldToClip = worldToClip;
        _prevCamera = cameraPosition;
        _hasPrev = true;

        // ---------------------------------------------------------------- Eye adaptation (histogram)
        if (autoExposure)
        {
            var cb = _cbScratch;
            var log2Min = EV100ToLog2Luminance(Settings.MinEV100);
            var log2Max = EV100ToLog2Luminance(Settings.MaxEV100);
            cb[0] = new Vector4(width, height, HistogramLogMin, HistogramLogMax);
            cb[1] = new Vector4(log2Min, MathF.Max(log2Min, log2Max), Settings.ExposureBias, deltaSeconds);
            cb[2] = new Vector4(Settings.SpeedUp, Settings.SpeedDown, 0.10f, 0.90f); // AutoExposureLowPercent / HighPercent
            cb[3] = new Vector4(_exposureValid ? 1f : 0f, 0, 0, 0);
            Upload(ctx, cb);

            ctx.ClearUnorderedAccessView(_histogramUav, new Int4(0, 0, 0, 0));
            ctx.CSSetShader(_histogramCs);
            ctx.CSSetConstantBuffer(0, _cb);
            ctx.CSSetShaderResource(0, current);
            ctx.CSSetUnorderedAccessView(0, _histogramUav);
            ctx.Dispatch((uint) ((width / 2 + 15) / 16), (uint) ((height / 2 + 15) / 16), 1);
            ctx.CSSetShaderResource(0, null!);

            ctx.CSSetShader(_adaptCs);
            ctx.CSSetUnorderedAccessView(1, _exposureUav);
            ctx.Dispatch(1, 1, 1);
            ctx.CSSetUnorderedAccessView(0, null!);
            ctx.CSSetUnorderedAccessView(1, null!);
            ctx.CSSetShader(null);
            _exposureValid = true;
        }
        else
        {
            _exposureValid = false;
        }

        // ---------------------------------------------------------------- Bloom (13-tap Karis Downsample + 9-tap 3x3 Tent Upsample)
        ID3D11ShaderResourceView? bloomSrv = null;
        if (bloom && width >= 64 && height >= 64)
        {
            var levels = BloomStages.Length;
            var src = current;
            var srcSize = new Vector2(width, height);
            var cb = _cbScratch;
            for (var i = 0; i < levels; i++)
            {
                var (w, h) = _levelSize[i];
                cb[0] = new Vector4(1f / srcSize.X, 1f / srcSize.Y, i == 0 ? 1f : 0f, 0);
                Upload(ctx, cb);
                ctx.OMSetRenderTargets(_downRtv[i]);
                ctx.RSSetViewport(0, 0, w, h);
                ctx.PSSetShader(_downsamplePs);
                ctx.PSSetConstantBuffer(0, _cb);
                ctx.PSSetShaderResource(0, src);
                ctx.Draw(3, 0);
                ctx.PSSetShaderResource(0, null!);
                ctx.OMSetRenderTargets((ID3D11RenderTargetView) null!);
                src = _downSrv[i];
                srcSize = new Vector2(w, h);
            }

            // Smallest to largest: 9-tap 3x3 tent upsample over previous bloom mip + current downsample * tint
            for (var i = levels - 1; i >= 0; i--)
            {
                var (w, h) = _levelSize[i];
                var (size, tint) = BloomStages[i];
                var hasPrev = i < levels - 1;
                var prevSize = hasPrev ? _levelSize[i + 1] : (w, h);
                var filterRadius = Math.Clamp(0.85f + MathF.Log2(1f + size * 0.25f) * 0.25f, 0.85f, 1.75f);

                cb[0] = new Vector4(1f / w, 1f / h, 1f / prevSize.Item1, 1f / prevSize.Item2);
                cb[1] = new Vector4(filterRadius, hasPrev ? 1f : 0f, 0f, 0f);
                cb[2] = new Vector4(tint, 0f);
                Upload(ctx, cb);
                ctx.OMSetRenderTargets(_bloomRtv[i]);
                ctx.RSSetViewport(0, 0, w, h);
                ctx.PSSetShader(_upsamplePs);
                ctx.PSSetConstantBuffer(0, _cb);
                ctx.PSSetShaderResource(0, _downSrv[i]);
                ctx.PSSetShaderResource(4, hasPrev ? _bloomSrv[i + 1] : null!);
                ctx.Draw(3, 0);
                ctx.PSSetShaderResource(0, null!);
                ctx.PSSetShaderResource(4, null!);
                ctx.OMSetRenderTargets((ID3D11RenderTargetView) null!);
            }
            bloomSrv = _bloomSrv[0];
        }

        ctx.ClearState();
        return new Result(current, bloomSrv);
    }

    /// <summary>EyeAdaptationCommon.ush: luminance = 2^EV100 * 12.5 / 100 (K = 12.5, ISO 100).</summary>
    public static float EV100ToLog2Luminance(float ev100) => ev100 + MathF.Log2(0.125f);

    public struct ExposureSettings
    {
        public float MinEV100, MaxEV100, ExposureBias, SpeedUp, SpeedDown;
    }

    public ExposureSettings Settings = new() { MinEV100 = -10f, MaxEV100 = 20f, ExposureBias = 0.5f, SpeedUp = 3f, SpeedDown = 1f };

    private void Upload(ID3D11DeviceContext ctx, Vector4[] data)
    {
        var mapped = ctx.Map(_cb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe
        {
            fixed (Vector4* p = data)
                Buffer.MemoryCopy(p, (void*) mapped.DataPointer, 16 * 16, data.Length * 16);
        }
        ctx.Unmap(_cb, 0);
    }

    private static void WriteMatrix(Vector4[] registers, int index, Matrix4x4 m)
    {
        registers[index] = new Vector4(m.M11, m.M12, m.M13, m.M14);
        registers[index + 1] = new Vector4(m.M21, m.M22, m.M23, m.M24);
        registers[index + 2] = new Vector4(m.M31, m.M32, m.M33, m.M34);
        registers[index + 3] = new Vector4(m.M41, m.M42, m.M43, m.M44);
    }

    private void DisposeTargets()
    {
        foreach (var d in _history.Cast<IDisposable>().Concat(_historyRtv).Concat(_historySrv)
                     .Concat(_down).Concat(_downRtv).Concat(_downSrv)
                     .Concat(_bloom).Concat(_bloomRtv).Concat(_bloomSrv))
            d?.Dispose();
        _history = []; _historyRtv = []; _historySrv = [];
        _down = []; _downRtv = []; _downSrv = [];
        _bloom = []; _bloomRtv = []; _bloomSrv = [];
    }

    public void Dispose()
    {
        DisposeTargets();
        foreach (var d in _owned) d.Dispose();
        _owned.Clear();
    }

    public const string Hlsl = """
        struct V { float4 Pos : SV_Position; };
        V VSPost(uint id : SV_VertexID)
        {
            V o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.Pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        cbuffer PostCb : register(b0)
        {
            float4 P0; float4 P1; float4 P2; float4 P3;
            float4 P4; float4 P5; float4 P6; float4 P7;
        };

        SamplerState LinearClamp : register(s0);
        Texture2D<float4> InputA : register(t0);
        Texture2D<float>  InputDepth : register(t1);
        Texture2D<float4> InputHistory : register(t2);

        float Luma4(float3 c) { return dot(c, float3(0.299f, 0.587f, 0.114f)); }
        float3 RGBToYCoCg(float3 c)
        {
            float Y  = dot(c, float3( 1, 2,  1));
            float Co = dot(c, float3( 2, 0, -2));
            float Cg = dot(c, float3(-1, 2, -1));
            return float3(Y, Co, Cg);
        }
        float3 YCoCgToRGB(float3 c)
        {
            float Y = c.x * 0.25f; float Co = c.y * 0.25f; float Cg = c.z * 0.25f;
            return float3(Y + Co - Cg, Y + Cg, Y - Co - Cg);
        }
        float HdrWeightY(float y) { return rcp(y * 0.25f + 1.0f); }

        // Catmull-Rom history fetch with 5 bilinear taps (TemporalAA.usf: HISTORY_CATMULL_ROM)
        float3 SampleHistoryCatmullRom(float2 uv, float2 size, float2 invSize)
        {
            float2 samplePos = uv * size;
            float2 tc1 = floor(samplePos - 0.5f) + 0.5f;
            float2 f = samplePos - tc1;
            float2 w0 = f * (-0.5f + f * (1.0f - 0.5f * f));
            float2 w1 = 1.0f + f * f * (-2.5f + 1.5f * f);
            float2 w2 = f * (0.5f + f * (2.0f - 1.5f * f));
            float2 w3 = f * f * (-0.5f + 0.5f * f);
            float2 w12 = w1 + w2;
            float2 tc0 = (tc1 - 1.0f) * invSize;
            float2 tc3 = (tc1 + 2.0f) * invSize;
            float2 tc12 = (tc1 + w2 / w12) * invSize;
            float3 r = 0;
            r += InputHistory.SampleLevel(LinearClamp, float2(tc12.x, tc0.y), 0).rgb * (w12.x * w0.y);
            r += InputHistory.SampleLevel(LinearClamp, float2(tc0.x, tc12.y), 0).rgb * (w0.x * w12.y);
            r += InputHistory.SampleLevel(LinearClamp, float2(tc12.x, tc12.y), 0).rgb * (w12.x * w12.y);
            r += InputHistory.SampleLevel(LinearClamp, float2(tc3.x, tc12.y), 0).rgb * (w3.x * w12.y);
            r += InputHistory.SampleLevel(LinearClamp, float2(tc12.x, tc3.y), 0).rgb * (w12.x * w3.y);
            float wsum = w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y;
            return max(r / wsum, 0.0f);
        }

        // P0..P3 = ClipToPrevClip rows, P4.xy = jitter (px), P5 = size/invSize, P6.x = current frame weight, P6.y = history valid
        float4 PSTemporalAA(V i) : SV_Target
        {
            int2 pix = int2(i.Pos.xy);
            float2 size = P5.xy;
            float2 invSize = P5.zw;
            float2 uv = i.Pos.xy * invSize;

            // Neighborhood (3x3) in YCoCg with HDR weighting, find closest depth (reversed Z: largest) for velocity dilation
            float3 m1 = 0, m2 = 0, cmin = 1e30f, cmax = -1e30f;
            float3 center = 0;
            float closestDepth = 0.0f;
            int2 closestOffset = int2(0, 0);
            float3 filtered = 0; float filteredW = 0;
            [unroll] for (int y = -1; y <= 1; y++)
            [unroll] for (int x = -1; x <= 1; x++)
            {
                int2 sp = clamp(pix + int2(x, y), int2(0, 0), int2(size) - 1);
                float3 c = InputA.Load(int3(sp, 0)).rgb;
                c = (any(isnan(c)) || any(isinf(c))) ? 0.0f : c;
                float3 ycc = RGBToYCoCg(c);
                float w = HdrWeightY(ycc.x);
                ycc *= w;
                m1 += ycc; m2 += ycc * ycc;
                cmin = min(cmin, ycc); cmax = max(cmax, ycc);
                // Blackman-Harris approximation of the reconstruction filter around the jittered sample (TemporalAA.usf)
                float2 d = float2(x, y) - P4.xy;
                float fw = exp(-2.29f * dot(d, d));
                filtered += ycc * fw; filteredW += fw;
                if (x == 0 && y == 0) center = ycc;
                float dz = InputDepth.Load(int3(sp, 0));
                if (dz > closestDepth) { closestDepth = dz; closestOffset = int2(x, y); }
            }
            float3 current = filtered / max(filteredW, 1e-5f);
            float3 mean = m1 / 9.0f;
            float3 stddev = sqrt(abs(m2 / 9.0f - mean * mean));
            float3 boxMin = max(cmin, mean - stddev * 1.25f);
            float3 boxMax = min(cmax, mean + stddev * 1.25f);

            // Reproject using the closest depth in the neighborhood (camera motion only; the scene is static)
            float2 ndc = float2(uv.x * 2 - 1, 1 - uv.y * 2);
            float4 clip = float4(ndc, closestDepth, 1);
            float4 prevClip = clip.x * P0 + clip.y * P1 + clip.z * P2 + clip.w * P3;
            float2 prevNdc = prevClip.xy / (abs(prevClip.w) > 1e-8f ? prevClip.w : 1e-8f);
            float2 prevUv = float2(prevNdc.x * 0.5f + 0.5f, 0.5f - prevNdc.y * 0.5f);
            bool offscreen = any(prevUv < 0.0f) || any(prevUv > 1.0f) || prevClip.w <= 0.0f;

            if (P6.y < 0.5f || offscreen)
            {
                float3 raw = InputA.Load(int3(pix, 0)).rgb;
                return float4((any(isnan(raw)) || any(isinf(raw))) ? 0.0f : raw, 1);
            }

            float3 history = SampleHistoryCatmullRom(prevUv, size, invSize);
            float3 histYcc = RGBToYCoCg(history);
            histYcc *= HdrWeightY(histYcc.x);

            // Clip history towards the box center (AA_CLIP)
            float3 boxCenter = 0.5f * (boxMax + boxMin);
            float3 boxExtent = 0.5f * (boxMax - boxMin) + 1e-5f;
            float3 dir = histYcc - boxCenter;
            float3 scaled = abs(dir / boxExtent);
            float maxScale = max(scaled.x, max(scaled.y, scaled.z));
            if (maxScale > 1.0f) histYcc = boxCenter + dir / maxScale;

            // Velocity based blend factor: more current frame when moving fast (r.TemporalAACurrentFrameWeight + velocity)
            float velocityPx = length((prevUv - uv) * size);
            float blend = saturate(P6.x + velocityPx * 0.02f);
            blend = max(blend, P6.x);

            float3 resYcc = lerp(histYcc, current, blend);
            float3 res = YCoCgToRGB(resYcc / HdrWeightY(resYcc.x));
            res = (any(isnan(res)) || any(isinf(res))) ? 0.0f : max(res, 0.0f);
            return float4(res, 1);
        }

        // ---------------------------------------------------------------- Bloom

        // 13 tap downsample (PostProcessDownsample.usf, high quality) with Karis average on the first level to suppress fireflies
        float4 PSBloomDownsample(V i) : SV_Target
        {
            float2 texel = P0.xy;
            float2 uv = (i.Pos.xy * 2.0f) * texel;
            float3 a = InputA.SampleLevel(LinearClamp, uv + texel * float2(-2, -2), 0).rgb;
            float3 b = InputA.SampleLevel(LinearClamp, uv + texel * float2( 0, -2), 0).rgb;
            float3 c = InputA.SampleLevel(LinearClamp, uv + texel * float2( 2, -2), 0).rgb;
            float3 d = InputA.SampleLevel(LinearClamp, uv + texel * float2(-1, -1), 0).rgb;
            float3 e = InputA.SampleLevel(LinearClamp, uv + texel * float2( 1, -1), 0).rgb;
            float3 f = InputA.SampleLevel(LinearClamp, uv + texel * float2(-2,  0), 0).rgb;
            float3 g = InputA.SampleLevel(LinearClamp, uv, 0).rgb;
            float3 h = InputA.SampleLevel(LinearClamp, uv + texel * float2( 2,  0), 0).rgb;
            float3 ii = InputA.SampleLevel(LinearClamp, uv + texel * float2(-1,  1), 0).rgb;
            float3 j = InputA.SampleLevel(LinearClamp, uv + texel * float2( 1,  1), 0).rgb;
            float3 k = InputA.SampleLevel(LinearClamp, uv + texel * float2(-2,  2), 0).rgb;
            float3 l = InputA.SampleLevel(LinearClamp, uv + texel * float2( 0,  2), 0).rgb;
            float3 m = InputA.SampleLevel(LinearClamp, uv + texel * float2( 2,  2), 0).rgb;

            float3 res;
            if (P0.z > 0.5f)
            {
                float3 g0 = (d + e + ii + j) * 0.25f;
                float3 g1 = (a + b + f + g) * 0.25f;
                float3 g2 = (b + c + g + h) * 0.25f;
                float3 g3 = (f + g + k + l) * 0.25f;
                float3 g4 = (g + h + l + m) * 0.25f;
                float w0 = 1.0f / (1.0f + Luma4(g0)); float w1 = 1.0f / (1.0f + Luma4(g1));
                float w2 = 1.0f / (1.0f + Luma4(g2)); float w3 = 1.0f / (1.0f + Luma4(g3));
                float w4 = 1.0f / (1.0f + Luma4(g4));
                res = (g0 * w0 * 0.5f + (g1 * w1 + g2 * w2 + g3 * w3 + g4 * w4) * 0.125f)
                    / (w0 * 0.5f + (w1 + w2 + w3 + w4) * 0.125f);
                // Soft-shoulder extreme emissive fireflies so small 500+ nit screens don't wash out neighboring walls
                float luma = Luma4(res);
                if (luma > 8.0f)
                    res *= (8.0f + log2(1.0f + (luma - 8.0f))) / luma;
            }
            else
            {
                res = (d + e + ii + j) * 0.125f + (a + c + k + m) * 0.03125f + (b + f + h + l) * 0.0625f + g * 0.125f;
            }
            res = (any(isnan(res)) || any(isinf(res))) ? 0.0f : res;
            return float4(min(res, 65000.0f), 1);
        }

        // Fast 9-tap 3x3 tent upsample over the 6-level downsample chain.
        // P0.xy = current dst invSize, P0.zw = previous bloom mip invSize, P1.x = filter radius, P1.y = hasPrev, P2.rgb = tint
        Texture2D<float4> AdditiveInput : register(t4);
        float4 PSBloomUpsample(V i) : SV_Target
        {
            float2 uv = i.Pos.xy * P0.xy;
            float3 res = InputA.SampleLevel(LinearClamp, uv, 0).rgb * P2.rgb;
            if (P1.y > 0.5f)
            {
                float2 d = P0.zw * P1.x;
                float3 upsample = (
                    AdditiveInput.SampleLevel(LinearClamp, uv + float2(-d.x, -d.y), 0).rgb +
                    AdditiveInput.SampleLevel(LinearClamp, uv + float2( d.x, -d.y), 0).rgb +
                    AdditiveInput.SampleLevel(LinearClamp, uv + float2(-d.x,  d.y), 0).rgb +
                    AdditiveInput.SampleLevel(LinearClamp, uv + float2( d.x,  d.y), 0).rgb
                ) * 1.0f + (
                    AdditiveInput.SampleLevel(LinearClamp, uv + float2( 0.0f, -d.y), 0).rgb +
                    AdditiveInput.SampleLevel(LinearClamp, uv + float2(-d.x,  0.0f), 0).rgb +
                    AdditiveInput.SampleLevel(LinearClamp, uv + float2( d.x,  0.0f), 0).rgb +
                    AdditiveInput.SampleLevel(LinearClamp, uv + float2( 0.0f,  d.y), 0).rgb
                ) * 2.0f + AdditiveInput.SampleLevel(LinearClamp, uv, 0).rgb * 4.0f;
                res += upsample * (1.0f / 16.0f);
            }
            return float4(res, 1);
        }

        // ---------------------------------------------------------------- Eye adaptation
        // P0 = (width, height, histogramLogMin, histogramLogMax) in EV100
        // P1 = (minLog2Lum, maxLog2Lum, exposureBias (EV), deltaTime)
        // P2 = (speedUp, speedDown, lowPercent, highPercent), P3.x = previous exposure valid
        RWByteAddressBuffer Histogram : register(u0);
        RWStructuredBuffer<float4> ExposureOut : register(u1);
        groupshared uint LocalBins[64];

        float LuminanceToHistogramPos(float lum)
        {
            float ev100 = log2(max(lum, 1e-10f)) - log2(0.125f);
            return saturate((ev100 - P0.z) / (P0.w - P0.z));
        }

        [numthreads(16, 16, 1)]
        void CSHistogram(uint3 dtid : SV_DispatchThreadID, uint gi : SV_GroupIndex)
        {
            if (gi < 64) LocalBins[gi] = 0;
            GroupMemoryBarrierWithGroupSync();
            uint2 pix = dtid.xy * 2;
            if (pix.x < (uint) P0.x && pix.y < (uint) P0.y)
            {
                float3 c = InputA.Load(int3(pix, 0)).rgb;
                if (!(any(isnan(c)) || any(isinf(c))))
                {
                    float lum = max(dot(c, float3(0.3f, 0.59f, 0.11f)), 0.0f);
                    // Ignore pure-black unlit void pixels so interior maps surrounded by void don't over-expose
                    if (lum > 1e-4f)
                    {
                        float2 uv = (pix + 0.5f) / P0.xy * 2.0f - 1.0f;
                        uint weight = 1;
                        uint bin = (uint) (LuminanceToHistogramPos(lum) * 63.0f + 0.5f);
                        InterlockedAdd(LocalBins[bin], weight);
                    }
                }
            }
            GroupMemoryBarrierWithGroupSync();
            if (gi < 64 && LocalBins[gi] > 0)
                Histogram.InterlockedAdd(gi * 4, LocalBins[gi]);
        }

        [numthreads(1, 1, 1)]
        void CSEyeAdaptation(uint3 dtid : SV_DispatchThreadID)
        {
            float total = 0;
            [loop] for (uint b = 0; b < 64; b++) total += (float) Histogram.Load(b * 4);
            float lowCount = total * P2.z;
            float highCount = total * P2.w;
            float acc = 0, sum = 0, weightSum = 0;
            [loop] for (uint k = 0; k < 64; k++)
            {
                float v = (float) Histogram.Load(k * 4);
                float lo = max(acc, lowCount);
                float hi = min(acc + v, highCount);
                float used = max(hi - lo, 0.0f);
                float ev = lerp(P0.z, P0.w, k / 63.0f);
                sum += ev * used;
                weightSum += used;
                acc += v;
            }
            float avgEV100 = weightSum > 0 ? sum / weightSum : 0.0f;
            float log2Lum = avgEV100 + log2(0.125f);
            log2Lum = clamp(log2Lum, P1.x, P1.y);
            float avgLuminance = exp2(log2Lum);

            // UE 5.2: ExposureScale = 1 / (Luminance / CalibrationConstant(0.18)) * 2^ExposureBias
            float targetExposure = 0.18f / max(avgLuminance, 1e-6f) * exp2(P1.z);
            float4 prev = ExposureOut[0];
            float oldExposure = P3.x > 0.5f ? prev.x : targetExposure;
            // PostProcessEyeAdaptation.usf: ComputeEyeAdaptation (exponential transition in log space)
            float oldLog = log2(max(oldExposure, 1e-8f));
            float targetLog = log2(max(targetExposure, 1e-8f));
            float diff = targetLog - oldLog;
            float speed = diff > 0 ? P2.x : P2.y;
            float factor = 1.0f - exp2(-P1.w * speed);
            float newExposure = exp2(oldLog + diff * factor);
            ExposureOut[0] = float4(newExposure, avgLuminance, targetExposure, 0);
        }
        """;
}
