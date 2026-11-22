using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

using UExplorer.Scene;
namespace UExplorer.Rendering;

public enum ENativePreviewMode { Lit, Emissive, BaseColor, Normal, MetallicSpecularRoughness, Wireframe }

/// <summary>Per-renderer feature switches (UE 5.2 scalability "Epic" defaults for the main viewport; off for thumbnails).</summary>
public sealed record NativeRenderFeatures
{
    public bool DynamicShadows { get; init; }
    public float ShadowDistance { get; init; } = 20000f; // cm
    public int ShadowCascades { get; init; } = 4;
    public int ShadowResolution { get; init; } = 2048;
    public bool TemporalAA { get; init; }
    public bool AutoExposure { get; init; }
    public float ExposureBias { get; init; } = 0f;
    public float AutoExposureMinEV100 { get; init; } = -1.5f;
    public float AutoExposureMaxEV100 { get; init; } = 9f;
    public float AutoExposureSpeedUp { get; init; } = 3f;
    public float AutoExposureSpeedDown { get; init; } = 1f;
    public bool Bloom { get; init; }
    public float BloomIntensity { get; init; } = 0.5f;
    public float VignetteIntensity { get; init; }
    public bool ScreenSpaceAO { get; init; } = true;
    public bool TiledLightCulling { get; init; } = true;
    public bool EnableHlod { get; init; } = true;
    public bool ShadowCascadeCaching { get; init; } = true;
    public float MaxLightDistanceMeters { get; init; } = 1200f;

    public static NativeRenderFeatures None { get; } = new()
    {
        ScreenSpaceAO = false,
        TiledLightCulling = false,
        EnableHlod = false,
        ShadowCascadeCaching = false
    };
}

public readonly record struct NativeDrawItem(
    NativeMeshLods Mesh,
    Matrix4x4 LocalToWorld,
    NativeMaterialPipeline?[] MaterialsBySlot,
    bool IsHlod = false,
    bool IsSelected = false);

public struct RenderPassTimings
{
    public double FrameCpuMs;
    public double CpuGatherMs;
    public double CpuCullSortMs;
    public double CpuGBufferMs;
    public double CpuShadowsMs;
    public double CpuSsaoMs;
    public double CpuLightCullAndLightingMs;
    public double CpuTranslucencyMs;
    public double CpuPostProcessMs;

    public bool GpuValid;
    public double GpuTotalMs;
    public double GpuGBufferMs;
    public double GpuShadowsMs;
    public double GpuSsaoMs;
    public double GpuLightCullAndLightingMs;
    public double GpuTranslucencyMs;
    public double GpuPostProcessMs;
}

public sealed partial class NativeSceneRenderer : IDisposable
{
    public static int DetectedDiffuseOverrideReg { get; private set; } = 136;
    public static int DetectedPreViewTranslationReg { get; private set; } = 70;
    private static bool _viewLayoutDetected = true;
    public static bool IsViewLayoutDetected => _viewLayoutDetected;

    public float MinScreenSize = 0.008f;
    public float HlodTransitionDistance = 28000f;
    public int MaxVisiblePrimitives = 25000;
    public float LodScreenBias = 0.25f;

    public static void RegisterShaderViewLayout(string? vsAsm, string? psAsm)
    {
    }

    private const int
        ViewTranslatedWorldToClip = 0,
        ViewWorldToClip = 4,
        ViewClipToWorld = 8,
        ViewTranslatedWorldToView = 12,
        ViewViewToTranslatedWorld = 16,
        ViewViewToClip = 24,
        ViewClipToView = 32,
        ViewClipToTranslatedWorld = 36,
        ViewSVPositionToTranslatedWorld = 44,
        ViewScreenToWorld = 48,
        ViewScreenToTranslatedWorld = 52,
        ViewViewForward = 56,
        ViewViewUp = 57,
        ViewViewRight = 58,
        ViewInvDeviceZToWorldZTransform = 65,
        ViewScreenPositionScaleBias = 66,
        ViewWorldCameraOrigin = 67,
        ViewTranslatedWorldCameraOrigin = 68,
        ViewWorldViewOrigin = 69,
        ViewPreViewTranslation = 70,
        ViewPrevTranslatedWorldToClip = 91,
        ViewPrevPreViewTranslation = 113,
        ViewRegisters = 256;

    private const int PrimitiveStride = 36;
    private const int QueryRingSize = 8;
    private const int QueryTimestampsPerFrame = 7;

    private const string SelectionMaskHlsl = """
        cbuffer SelParams : register(b0)
        {
            float4 LocalToClipRow0;
            float4 LocalToClipRow1;
            float4 LocalToClipRow2;
            float4 LocalToClipRow3;
        };

        struct VSIn
        {
            float3 Pos : ATTRIBUTE0;
        };

        struct PSIn
        {
            float4 Pos : SV_Position;
        };

        PSIn VSMainSel(VSIn input)
        {
            PSIn o;
            o.Pos = input.Pos.xxxx * LocalToClipRow0
                  + input.Pos.yyyy * LocalToClipRow1
                  + input.Pos.zzzz * LocalToClipRow2
                  + LocalToClipRow3;
            return o;
        }

        // r = 1.0 (selected mask), g = clip depth z (to detect if visible or occluded behind walls)
        float2 PSMainSel(PSIn i) : SV_Target0
        {
            return float2(1.0, i.Pos.z);
        }
        """;

    private readonly record struct VisiblePrimitive(NativeDrawItem Item, NativeMesh Lod, float Distance, float ScreenSize);
    private readonly record struct DrawCmd(NativeMaterialPipeline Pipeline, int Primitive, NativeMesh Mesh, NativeMesh.Section Section, bool Mirrored, int DepthBucket);
    private readonly record struct ShadowDrawCmd(NativeMaterialPipeline Pipeline, int Primitive, NativeMesh Mesh, NativeMesh.Section Section);
    private readonly record struct IsmFastDraw(RetainedPrimitiveGroup Group, NativeMesh Lod, NativeMesh.Section Section, NativeMaterialPipeline Pipeline);

    private sealed class OpaqueDrawComparer : IComparer<DrawCmd>
    {
        public static readonly OpaqueDrawComparer Instance = new();
        public int Compare(DrawCmd a, DrawCmd b)
        {
            var c = a.Pipeline.NeedsEarlyZMaskPass.CompareTo(b.Pipeline.NeedsEarlyZMaskPass);
            if (c != 0) return c;
            c = a.Pipeline.IsRvtShader.CompareTo(b.Pipeline.IsRvtShader);
            if (c != 0) return c;
            c = a.Pipeline.PipelineId.CompareTo(b.Pipeline.PipelineId);
            if (c != 0) return c;
            c = a.Mesh.MeshId.CompareTo(b.Mesh.MeshId);
            if (c != 0) return c;
            c = a.Section.FirstIndex.CompareTo(b.Section.FirstIndex);
            if (c != 0) return c;
            return a.Mirrored.CompareTo(b.Mirrored);
        }
    }

    private sealed class TranslucentDrawComparer : IComparer<DrawCmd>
    {
        public static readonly TranslucentDrawComparer Instance = new();
        public int Compare(DrawCmd a, DrawCmd b)
        {
            var aAdd = a.Pipeline.BlendMode == ENativeBlendMode.Additive;
            var bAdd = b.Pipeline.BlendMode == ENativeBlendMode.Additive;
            if (aAdd != bAdd) return aAdd ? -1 : 1; // Additive is commutative: group together for max instancing
            if (!aAdd)
            {
                var d = b.DepthBucket.CompareTo(a.DepthBucket); // back-to-front coarse quantized bucket
                if (d != 0) return d;
            }
            var c = a.Pipeline.PipelineId.CompareTo(b.Pipeline.PipelineId);
            if (c != 0) return c;
            c = a.Mesh.MeshId.CompareTo(b.Mesh.MeshId);
            if (c != 0) return c;
            c = a.Section.FirstIndex.CompareTo(b.Section.FirstIndex);
            if (c != 0) return c;
            return a.Mirrored.CompareTo(b.Mirrored);
        }
    }

    private sealed class ShadowDrawComparer : IComparer<ShadowDrawCmd>
    {
        public static readonly ShadowDrawComparer Instance = new();
        public int Compare(ShadowDrawCmd a, ShadowDrawCmd b)
        {
            var c = a.Pipeline.HasDepthOnlyDiscard.CompareTo(b.Pipeline.HasDepthOnlyDiscard);
            if (c != 0) return c;
            c = a.Pipeline.PipelineId.CompareTo(b.Pipeline.PipelineId);
            if (c != 0) return c;
            c = a.Mesh.MeshId.CompareTo(b.Mesh.MeshId);
            if (c != 0) return c;
            return a.Section.FirstIndex.CompareTo(b.Section.FirstIndex);
        }
    }

    private struct QueryFrameSlot
    {
        public ID3D11Query Disjoint;
        public ID3D11Query[] Ts;
        public bool Issued;
        public int Epoch;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly List<IDisposable> _owned = [];
    private readonly ID3D11Buffer _view, _translucentBasePass, _compositeParams, _selectionCb, _tileCullCb;
    private ID3D11Buffer _primitiveBuffer = null!, _primitiveIds = null!, _shadowPrimitiveIds = null!;
    private ID3D11ShaderResourceView _primitiveSrv = null!;
    private int _primitiveCapacity;
    private int _instanceIdCapacity;
    private int _shadowIdCapacity;
    private readonly ID3D11RasterizerState _cullBack, _cullFront, _cullNone;
    private readonly ID3D11RasterizerState _wireCullBack, _wireCullFront, _wireCullNone;
    public bool FlipCulling;
    public NativeRenderFeatures Features { get; set; } = NativeRenderFeatures.None;
    public bool ReadbackEnabled { get; set; }
    private NativePostProcess? _post;
    private NativeShadowMaps? _shadows;
    private ID3D11Buffer? _postParams;
    private ID3D11ShaderResourceView? _blackSrv;
    private ID3D11ShaderResourceView? _whiteSrv;
    private ID3D11ShaderResourceView? _zeroExposureSrv;
    public int VisibleCount { get; private set; }
    public int ActiveLocalLights => _lightRenderer.ActiveLocalLightCount;
    private readonly ID3D11DepthStencilState _depthWrite, _depthRead, _depthEqualRead;
    private readonly ID3D11VertexShader _compositeVs;
    private readonly ID3D11PixelShader _deferredLitPs, _tonemapPs, _ssaoHalfPs, _ssaoBlurPs;
    private readonly ID3D11ComputeShader? _tileLightCullCs;
    private readonly ID3D11VertexShader _selectionVs;
    private readonly ID3D11PixelShader _selectionPs;
    private readonly ID3D11InputLayout _selectionInputLayout;
    private readonly ID3D11ShaderResourceView _preintegratedGfSrv;
    private readonly ID3D11ShaderResourceView _ltcMatSrv;
    private readonly ID3D11ShaderResourceView _ltcAmpSrv;
    private readonly NativeSharedResources _sharedTemplate;
    private readonly NativeLandscapeRenderer _landscapeRenderer;
    private readonly NativeNiagaraRenderer _niagaraRenderer;
    private readonly NativeLightRenderer _lightRenderer;
    private readonly NativeEditorGizmoRenderer _gizmoRenderer;

    private int _width, _height;
    private int _tileCountX, _tileCountY;
    private ID3D11Texture2D[] _targets = [];
    private ID3D11RenderTargetView[] _rtvs = [];
    private ID3D11ShaderResourceView[] _targetSrvs = [];
    private ID3D11Texture2D? _depthTexture, _sceneColorCopyTex, _sceneDepthCopyTex, _selectionMaskTex, _output, _statsEmissive, _statsNormal, _statsBase;
    private ID3D11ShaderResourceView? _depthSrv, _sceneColorCopySrv, _sceneDepthCopySrv, _selectionMaskSrv, _outputSrv;
    private ID3D11Texture2D? _ssaoHalfTex, _ssaoBlurTex;
    private ID3D11RenderTargetView? _ssaoHalfRtv, _ssaoBlurRtv;
    private ID3D11ShaderResourceView? _ssaoHalfSrv, _ssaoBlurSrv;
    private ID3D11Buffer? _tileLightBuffer;
    private ID3D11UnorderedAccessView? _tileLightUav;
    private ID3D11ShaderResourceView? _tileLightSrv;
    public ID3D11ShaderResourceView? OutputSrv => _outputSrv;
    public ID3D11Texture2D? OutputTexture => _output;
    private ID3D11RenderTargetView? _selectionMaskRtv;
    private ID3D11Texture2D[] _staging = [];
    private int _readbackFrame;
    private ID3D11DepthStencilView? _dsv;
    private ID3D11RenderTargetView? _outputRtv;

    private static readonly Stopwatch _clock = Stopwatch.StartNew(); // shared by all viewports
    private float _previousTime;
    private int _frame;

    // Pre-allocated zero-alloc per-frame scratch buffers
    private readonly Vector4[] _viewScratch = new Vector4[ViewRegisters];
    private readonly Vector4[] _cascadeViewScratch = new Vector4[ViewRegisters];
    private readonly Vector4[] _transPassScratch = new Vector4[256];
    private readonly Vector4[] _compDataScratch = new Vector4[17];
    private readonly Vector4[] _postParamsScratch = new Vector4[1];
    private readonly Vector4[] _selCbScratch = new Vector4[4];
    private readonly Vector4[] _frustumPlanesScratch = new Vector4[5];
    private readonly Vector4[] _tileCullCbScratch = new Vector4[9];
    private readonly ID3D11ShaderResourceView[] _litSrvs = new ID3D11ShaderResourceView[18];
    private readonly ID3D11ShaderResourceView[] _tonemapSrvs = new ID3D11ShaderResourceView[16];
    private readonly ID3D11ShaderResourceView[] _null18Srvs = new ID3D11ShaderResourceView[18];
    private readonly ID3D11ShaderResourceView[] _null2Srvs = new ID3D11ShaderResourceView[2];
    private readonly ID3D11UnorderedAccessView[] _null1Uav = new ID3D11UnorderedAccessView[1];
    private readonly ID3D11RenderTargetView[] _basePassRtvs = new ID3D11RenderTargetView[6];
    private readonly ID3D11RenderTargetView[] _rvtRtvs = new ID3D11RenderTargetView[3];
    private readonly ID3D11RenderTargetView[] _emptyRtvs = [];
    private readonly ID3D11Buffer[] _vbPair = new ID3D11Buffer[2];
    private readonly uint[] _vbStrides = [12u, 4u];
    private readonly uint[] _vbOffsets = new uint[2];

    private Vector4[] _primitivesScratch = [];
    private uint[] _instancePrimIdsScratch = [];
    private uint[] _shadowPrimIdsScratch = [];
    private Vector4[] _primitiveBoundsCache = [];

    private readonly List<VisiblePrimitive> _visibleScratch = new(256);
    private readonly List<(Matrix4x4 LocalToWorld, NativeMesh Lod)> _selectedScratch = new(16);
    private readonly List<VisiblePrimitive> _shadowOnlyScratch = new(256);
    private readonly List<IsmFastDraw> _ismFastOpaqueScratch = new(64);
    private readonly List<DrawCmd> _opaqueScratch = new(512);
    private readonly List<DrawCmd> _translucentScratch = new(128);
    private readonly List<ShadowDrawCmd> _shadowDrawsScratch = new(512);
    private readonly List<ShadowDrawCmd> _cascadeShadowScratch = new(512);
    private readonly List<NativeLandscapeDrawItem> _visibleLandscapesScratch = new(16);
    private readonly List<NativeLandscapeDrawItem> _landscapeCastersScratch = new(16);

    // CPU & GPU pass timings
    private readonly QueryFrameSlot[] _queryRing = new QueryFrameSlot[QueryRingSize];
    private int _queryWriteIdx;
    private int _timingsEpoch;
    private RetainedSceneDrawCache? _lastRetainedCache;
    private object? _lastSceneToken;
    private RenderPassTimings _lastTimings;
    internal string LastGpuQueryStatus { get; private set; } = "uninitialized";
    private readonly double[] _frameMsRing = new double[32];
    private int _frameMsCursor;
    private int _frameMsCount;

    public void ResetTimings()
    {
        _timingsEpoch++;
        _lastTimings = default;
        LastGpuQueryStatus = "reset";
        Array.Clear(_frameMsRing);
        _frameMsCursor = 0;
        _frameMsCount = 0;
        RollingFrameMs = 0.0;
        RollingFps = 0.0;
    }

    public RenderPassTimings LastTimings => _lastTimings;
    public double RollingFps { get; private set; }
    public double RollingFrameMs { get; private set; }
    public float CurrentTimeSeconds => (float) _clock.Elapsed.TotalSeconds;
    public string OutputSummary { get; private set; } = "measuring...";
    public int DrawCalls { get; private set; }
    internal NativeSharedResources SharedResources => _sharedTemplate;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _shaderBytecodeCache = new(StringComparer.Ordinal);
    private static byte[] GetOrCompileBytecode(string cacheKey, string hlsl, string entryPoint, string target) =>
        _shaderBytecodeCache.GetOrAdd(cacheKey, _ => Compiler.Compile(hlsl, entryPoint, cacheKey, target, ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray());

    private T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }

    public NativeSceneRenderer(D3D11NativeShaderDevice host)
    {
        _device = host.Device;
        _context = host.Context;

        _view = Own(_device.CreateBuffer(ViewRegisters * 16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        _translucentBasePass = Own(_device.CreateBuffer(256 * 16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        _compositeParams = Own(_device.CreateBuffer(16 * 17, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        _selectionCb = Own(_device.CreateBuffer(16 * 4, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        _tileCullCb = Own(_device.CreateBuffer(16 * 9, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        EnsurePrimitiveCapacity(256);
        EnsureInstanceIdCapacity(512);
        EnsureShadowIdCapacity(512);

        var zeroVectors = new Vector4[4096];
        var blacks = new Dictionary<EDxbcResourceKind, ID3D11ShaderResourceView>();
        var zeroStructured = Own(_device.CreateBuffer(zeroVectors.AsSpan(), new BufferDescription((uint) (zeroVectors.Length * 16), BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16)));
        blacks[EDxbcResourceKind.Structured] = Own(_device.CreateShaderResourceView(zeroStructured, new ShaderResourceViewDescription(zeroStructured, Format.Unknown, 0, (uint) zeroVectors.Length)));
        var zeroTyped = Own(_device.CreateBuffer(zeroVectors.AsSpan(), BindFlags.ShaderResource));
        blacks[EDxbcResourceKind.Buffer] = Own(_device.CreateShaderResourceView(zeroTyped, new ShaderResourceViewDescription(zeroTyped, Format.R32G32B32A32_Float, 0, (uint) zeroVectors.Length)));
        var zeroUIntTyped = Own(_device.CreateBuffer(zeroVectors.AsSpan(), BindFlags.ShaderResource));
        var zeroUIntBufferSrv = Own(_device.CreateShaderResourceView(zeroUIntTyped, new ShaderResourceViewDescription(zeroUIntTyped, Format.R32G32B32A32_UInt, 0, (uint) zeroVectors.Length)));
        var black2D = Own(_device.CreateTexture2D(new uint[] { 0 }.AsSpan(), Format.R8G8B8A8_UNorm, 1, 1));
        var black2DSrv = Own(_device.CreateShaderResourceView(black2D));
        blacks[EDxbcResourceKind.Texture2D] = black2DSrv;
        var zeroTexel = new uint[] { 0u };
        ID3D11Texture2D blackArray;
        unsafe
        {
            fixed (uint* pZero = zeroTexel)
            {
                blackArray = Own(_device.CreateTexture2D(
                    new Texture2DDescription(Format.R8G8B8A8_UNorm, 1, 1, 1, 1, BindFlags.ShaderResource),
                    [new SubresourceData((IntPtr) pZero, 4)]));
            }
        }
        blacks[EDxbcResourceKind.Texture2DArray] = Own(_device.CreateShaderResourceView(blackArray, new ShaderResourceViewDescription(blackArray, ShaderResourceViewDimension.Texture2DArray, Format.R8G8B8A8_UNorm, 0, 1, 0, 1)));

        var cubeData = new uint[] { 0xFFD8C0A0, 0xFFD8C0A0, 0xFFD8C0A0, 0xFFD8C0A0, 0xFFF0D8B8, 0xFF605850 };
        var cubeSub = new SubresourceData[6];
        ID3D11ShaderResourceView defaultCubeSrv;
        ID3D11ShaderResourceView defaultCubeArraySrv;
        unsafe
        {
            fixed (uint* p = cubeData)
            {
                for (var f = 0; f < 6; f++)
                    cubeSub[f] = new SubresourceData((IntPtr) (p + f), 4);
                var cube = Own(_device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, 1, 1, 6, 1, BindFlags.ShaderResource, ResourceUsage.Immutable, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.TextureCube), cubeSub));
                defaultCubeSrv = Own(_device.CreateShaderResourceView(cube, new ShaderResourceViewDescription(cube, ShaderResourceViewDimension.TextureCube, Format.R8G8B8A8_UNorm, 0, 1, 0, 1)));
                defaultCubeArraySrv = Own(_device.CreateShaderResourceView(cube, new ShaderResourceViewDescription(cube, ShaderResourceViewDimension.TextureCubeArray, Format.R8G8B8A8_UNorm, 0, 1, 0, 1)));
            }
        }
        blacks[EDxbcResourceKind.TextureCube] = defaultCubeSrv;
        blacks[EDxbcResourceKind.TextureCubeArray] = defaultCubeArraySrv;

        ID3D11Texture3D volume;
        unsafe
        {
            fixed (uint* pZero = zeroTexel)
            {
                volume = Own(_device.CreateTexture3D(
                    new Texture3DDescription(Format.R8G8B8A8_UNorm, 1, 1, 1, 1, BindFlags.ShaderResource),
                    [new SubresourceData((IntPtr) pZero, 4, 4)]));
            }
        }
        blacks[EDxbcResourceKind.Texture3D] = Own(_device.CreateShaderResourceView(volume));

        var white = Own(_device.CreateTexture2D(new uint[] { 0xFFFFFFFF }.AsSpan(), Format.R8G8B8A8_UNorm, 1, 1));
        var neutralYCoCg = Own(_device.CreateTexture2D(new uint[] { 0x6E008080 }.AsSpan(), Format.R8G8B8A8_UNorm, 1, 1));
        var flatNormal = Own(_device.CreateTexture2D(new uint[] { 0xFFFF8080 }.AsSpan(), Format.R8G8B8A8_UNorm, 1, 1));
        var zeroUInt4 = new uint[] { 0u, 0u, 0u, 0u };
        var zeroUIntTex = Own(_device.CreateTexture2D(zeroUInt4.AsSpan(), Format.R32G32B32A32_UInt, 1, 1));
        var whiteSrv = Own(_device.CreateShaderResourceView(white));
        var neutralYCoCgSrv = Own(_device.CreateShaderResourceView(neutralYCoCg));
        var flatNormalSrv = Own(_device.CreateShaderResourceView(flatNormal));
        var zeroUIntSrv = Own(_device.CreateShaderResourceView(zeroUIntTex));
        ID3D11ShaderResourceView zeroUIntArraySrv;
        ID3D11ShaderResourceView zeroUInt3DSrv;
        unsafe
        {
            fixed (uint* pUInt4 = zeroUInt4)
            {
                var zeroUIntArray = Own(_device.CreateTexture2D(
                    new Texture2DDescription(Format.R32G32B32A32_UInt, 1, 1, 1, 1, BindFlags.ShaderResource),
                    [new SubresourceData((IntPtr) pUInt4, 16)]));
                zeroUIntArraySrv = Own(_device.CreateShaderResourceView(zeroUIntArray, new ShaderResourceViewDescription(zeroUIntArray, ShaderResourceViewDimension.Texture2DArray, Format.R32G32B32A32_UInt, 0, 1, 0, 1)));
                var zeroUInt3D = Own(_device.CreateTexture3D(
                    new Texture3DDescription(Format.R32G32B32A32_UInt, 1, 1, 1, 1, BindFlags.ShaderResource),
                    [new SubresourceData((IntPtr) pUInt4, 16, 16)]));
                zeroUInt3DSrv = Own(_device.CreateShaderResourceView(zeroUInt3D));
            }
        }

        var (gfTex, gfSrv) = NativeSystemTextures.CreatePreintegratedGF(_device);
        Own(gfTex);
        _preintegratedGfSrv = Own(gfSrv);

        var (ltcMatTex, ltcMatSrv, ltcAmpTex, ltcAmpSrv) = NativeSystemTextures.CreateLtcTextures(_device);
        Own(ltcMatTex);
        _ltcMatSrv = Own(ltcMatSrv);
        Own(ltcAmpTex);
        _ltcAmpSrv = Own(ltcAmpSrv);

        var (noise2DTex, noise2DSrv, noise3DTex, noise3DSrv) = NativeSystemTextures.CreateNoiseTextures(_device);
        Own(noise2DTex);
        Own(noise2DSrv);
        Own(noise3DTex);
        Own(noise3DSrv);

        _sharedTemplate = new NativeSharedResources
        {
            View = _view,
            TranslucentBasePass = _translucentBasePass,
            PrimitiveData = _primitiveSrv,
            Zeros = Own(_device.CreateBuffer(new byte[16384].AsSpan(), BindFlags.ConstantBuffer)),
            White2D = whiteSrv,
            NeutralYCoCg2D = neutralYCoCgSrv,
            FlatNormal2D = flatNormalSrv,
            DefaultUInt2D = zeroUIntSrv,
            DefaultUIntBuffer = zeroUIntBufferSrv,
            DefaultUInt2DArray = zeroUIntArraySrv,
            DefaultUInt3D = zeroUInt3DSrv,
            DefaultCube = defaultCubeSrv,
            AmbientVolume3D = blacks[EDxbcResourceKind.Texture3D],
            Noise2D = noise2DSrv,
            NoiseVolume3D = noise3DSrv,
            SceneColorCopy = whiteSrv,
            SceneDepthCopy = whiteSrv,
            PreIntegratedGF = _preintegratedGfSrv,
            BlackDefaults = blacks,
            Wrap = Own(_device.CreateSamplerState(new SamplerDescription(Filter.Anisotropic, TextureAddressMode.Wrap, 0, 8, ComparisonFunction.Never, 0, float.MaxValue))),
            Clamp = Own(_device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue)))
        };

        _blackSrv = black2DSrv;
        _whiteSrv = whiteSrv;
        try
        {
            _post = Own(new NativePostProcess(_device));
        }
        catch (Exception e)
        {
            SnooperDiagnostics.LogWarning("PostProcess", "NativePostProcess", "Post-process shaders failed: " + e.Message);
            _post = null;
        }
        _shadows = Own(new NativeShadowMaps(_device));
        _shadows.EnsureCreated(512);
        _postParams = Own(_device.CreateBuffer(16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        _zeroExposureSrv = blacks[EDxbcResourceKind.Structured];
        _landscapeRenderer = Own(new NativeLandscapeRenderer(_device, black2DSrv, whiteSrv, flatNormalSrv));
        _niagaraRenderer = Own(new NativeNiagaraRenderer(_device, whiteSrv));
        _lightRenderer = Own(new NativeLightRenderer(_device));
        _gizmoRenderer = Own(new NativeEditorGizmoRenderer(_device));

        _cullBack = Own(_device.CreateRasterizerState(new RasterizerDescription(CullMode.Back, FillMode.Solid) { FrontCounterClockwise = true, DepthClipEnable = true }));
        _cullFront = Own(_device.CreateRasterizerState(new RasterizerDescription(CullMode.Front, FillMode.Solid) { FrontCounterClockwise = true, DepthClipEnable = true }));
        _cullNone = Own(_device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { FrontCounterClockwise = true, DepthClipEnable = true }));
        _wireCullBack = Own(_device.CreateRasterizerState(new RasterizerDescription(CullMode.Back, FillMode.Wireframe) { FrontCounterClockwise = true, DepthClipEnable = true }));
        _wireCullFront = Own(_device.CreateRasterizerState(new RasterizerDescription(CullMode.Front, FillMode.Wireframe) { FrontCounterClockwise = true, DepthClipEnable = true }));
        _wireCullNone = Own(_device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Wireframe) { FrontCounterClockwise = true, DepthClipEnable = true }));
        _depthWrite = Own(_device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.GreaterEqual)));
        _depthRead = Own(_device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.GreaterEqual)));
        _depthEqualRead = Own(_device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.Equal)));
        _compositeVs = Own(_device.CreateVertexShader(GetOrCompileBytecode("composite", Unreal52DeferredShader.HlslSource, "VSMain", "vs_5_0")));
        _deferredLitPs = Own(_device.CreatePixelShader(GetOrCompileBytecode("deferred_lit", Unreal52DeferredShader.HlslSource, "PSDeferredLit", "ps_5_0")));
        _tonemapPs = Own(_device.CreatePixelShader(GetOrCompileBytecode("tonemap", Unreal52DeferredShader.HlslSource, "PSToneMap", "ps_5_0")));
        _ssaoHalfPs = Own(_device.CreatePixelShader(GetOrCompileBytecode("ssao_half", Unreal52DeferredShader.HlslSource, "PSSsaoHalf", "ps_5_0")));
        _ssaoBlurPs = Own(_device.CreatePixelShader(GetOrCompileBytecode("ssao_blur", Unreal52DeferredShader.HlslSource, "PSSsaoBlur", "ps_5_0")));

        try
        {
            _tileLightCullCs = Own(_device.CreateComputeShader(GetOrCompileBytecode("tile_light_cull", Unreal52DeferredShader.TileCullHlsl, "CSTileLightCull", "cs_5_0")));
        }
        catch (Exception e)
        {
            SnooperDiagnostics.LogWarning("TiledLightCull", "NativeSceneRenderer", "CSTileLightCull failed: " + e.Message);
            _tileLightCullCs = null;
        }

        var selVsBytes = GetOrCompileBytecode("sel_vs", SelectionMaskHlsl, "VSMainSel", "vs_5_0");
        var selPsBytes = GetOrCompileBytecode("sel_ps", SelectionMaskHlsl, "PSMainSel", "ps_5_0");
        _selectionVs = Own(_device.CreateVertexShader(selVsBytes));
        _selectionPs = Own(_device.CreatePixelShader(selPsBytes));
        _selectionInputLayout = Own(_device.CreateInputLayout(
        [
            new InputElementDescription("ATTRIBUTE", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerVertexData, 0)
        ], selVsBytes));

        for (var q = 0; q < QueryRingSize; q++)
        {
            _queryRing[q].Disjoint = Own(_device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint)));
            _queryRing[q].Ts = new ID3D11Query[QueryTimestampsPerFrame];
            for (var t = 0; t < QueryTimestampsPerFrame; t++)
                _queryRing[q].Ts[t] = Own(_device.CreateQuery(new QueryDescription(QueryType.Timestamp)));
        }
    }

    private NativeSharedResources BuildShared(ID3D11ShaderResourceView? primitiveSrvOverride = null) => new()
    {
        View = _sharedTemplate.View,
        TranslucentBasePass = _sharedTemplate.TranslucentBasePass,
        PrimitiveData = primitiveSrvOverride ?? _primitiveSrv,
        Zeros = _sharedTemplate.Zeros,
        White2D = _sharedTemplate.White2D,
        NeutralYCoCg2D = _sharedTemplate.NeutralYCoCg2D,
        FlatNormal2D = _sharedTemplate.FlatNormal2D,
        DefaultUInt2D = _sharedTemplate.DefaultUInt2D,
        DefaultUIntBuffer = _sharedTemplate.DefaultUIntBuffer,
        DefaultUInt2DArray = _sharedTemplate.DefaultUInt2DArray,
        DefaultUInt3D = _sharedTemplate.DefaultUInt3D,
        DefaultCube = _sharedTemplate.DefaultCube,
        AmbientVolume3D = _sharedTemplate.AmbientVolume3D,
        Noise2D = _sharedTemplate.Noise2D,
        NoiseVolume3D = _sharedTemplate.NoiseVolume3D,
        SceneColorCopy = _sceneColorCopySrv ?? _sharedTemplate.White2D,
        SceneDepthCopy = _sceneDepthCopySrv ?? _sharedTemplate.White2D,
        PreIntegratedGF = _preintegratedGfSrv,
        BlackDefaults = _sharedTemplate.BlackDefaults,
        Wrap = _sharedTemplate.Wrap,
        Clamp = _sharedTemplate.Clamp
    };

    private void EnsurePrimitiveCapacity(int count)
    {
        if (count <= _primitiveCapacity)
            return;
        var capacity = Math.Max(count, Math.Max(_primitiveCapacity * 2, 64));
        _primitiveSrv?.Dispose();
        _primitiveBuffer?.Dispose();
        _primitiveBuffer = _device.CreateBuffer(new BufferDescription((uint) (capacity * PrimitiveStride * 16), BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.BufferStructured, 16));
        _primitiveSrv = _device.CreateShaderResourceView(_primitiveBuffer, new ShaderResourceViewDescription(_primitiveBuffer, Format.Unknown, 0, (uint) (capacity * PrimitiveStride)));
        _primitiveCapacity = capacity;
    }

    private void EnsureInstanceIdCapacity(int count)
    {
        if (count <= _instanceIdCapacity)
            return;
        var capacity = Math.Max(count, Math.Max(_instanceIdCapacity * 2, 128));
        _primitiveIds?.Dispose();
        _primitiveIds = _device.CreateBuffer(new BufferDescription((uint) (capacity * 4), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _instanceIdCapacity = capacity;
    }

    private void EnsureShadowIdCapacity(int count)
    {
        if (count <= _shadowIdCapacity)
            return;
        var capacity = Math.Max(count, Math.Max(_shadowIdCapacity * 2, 128));
        _shadowPrimitiveIds?.Dispose();
        _shadowPrimitiveIds = _device.CreateBuffer(new BufferDescription((uint) (capacity * 4), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _shadowIdCapacity = capacity;
    }

    private void EnsureTargets(int width, int height)
    {
        if (width == _width && height == _height && _output is not null)
            return;
        DisposeTargets();
        _width = width;
        _height = height;

        Format[] formats =
        [
            Format.R16G16B16A16_Float,
            Format.R16G16B16A16_Float,
            Format.R8G8B8A8_UNorm,
            Format.R16G16B16A16_Float,
            Format.R16G16B16A16_Float,
            Format.R16G16B16A16_Float,
            Format.R16G16B16A16_Float
        ];
        _targets = formats.Select(f => _device.CreateTexture2D(new Texture2DDescription(f, (uint) width, (uint) height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource))).ToArray();
        _rtvs = _targets.Select(t => _device.CreateRenderTargetView(t)).ToArray();
        _targetSrvs = _targets.Select(t => _device.CreateShaderResourceView(t)).ToArray();
        for (var i = 0; i < 6; i++) _basePassRtvs[i] = _rtvs[i];
        _rvtRtvs[0] = _rtvs[3];
        _rvtRtvs[1] = _rtvs[1];
        _rvtRtvs[2] = _rtvs[2];

        _depthTexture = _device.CreateTexture2D(new Texture2DDescription(Format.R32_Typeless, (uint) width, (uint) height, 1, 1, BindFlags.DepthStencil | BindFlags.ShaderResource));
        _dsv = _device.CreateDepthStencilView(_depthTexture, new DepthStencilViewDescription(_depthTexture, DepthStencilViewDimension.Texture2D, Format.D32_Float));
        _depthSrv = _device.CreateShaderResourceView(_depthTexture, new ShaderResourceViewDescription(_depthTexture, ShaderResourceViewDimension.Texture2D, Format.R32_Float, 0, 1));

        _sceneColorCopyTex = _device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, (uint) width, (uint) height, 1, 1, BindFlags.ShaderResource));
        _sceneColorCopySrv = _device.CreateShaderResourceView(_sceneColorCopyTex);
        _sceneDepthCopyTex = _device.CreateTexture2D(new Texture2DDescription(Format.R32_Typeless, (uint) width, (uint) height, 1, 1, BindFlags.ShaderResource));
        _sceneDepthCopySrv = _device.CreateShaderResourceView(_sceneDepthCopyTex, new ShaderResourceViewDescription(_sceneDepthCopyTex, ShaderResourceViewDimension.Texture2D, Format.R32_Float, 0, 1));

        var hw = Math.Max(1, width / 2);
        var hh = Math.Max(1, height / 2);
        _ssaoHalfTex = _device.CreateTexture2D(new Texture2DDescription(Format.R8_UNorm, (uint) hw, (uint) hh, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        _ssaoHalfRtv = _device.CreateRenderTargetView(_ssaoHalfTex);
        _ssaoHalfSrv = _device.CreateShaderResourceView(_ssaoHalfTex);
        _ssaoBlurTex = _device.CreateTexture2D(new Texture2DDescription(Format.R8_UNorm, (uint) hw, (uint) hh, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        _ssaoBlurRtv = _device.CreateRenderTargetView(_ssaoBlurTex);
        _ssaoBlurSrv = _device.CreateShaderResourceView(_ssaoBlurTex);

        _tileCountX = (width + 15) / 16;
        _tileCountY = (height + 15) / 16;
        var totalTileInts = Math.Max(1, _tileCountX * _tileCountY * 128);
        _tileLightBuffer = _device.CreateBuffer(new BufferDescription((uint) (totalTileInts * 4), BindFlags.ShaderResource | BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 4));
        _tileLightUav = _device.CreateUnorderedAccessView(_tileLightBuffer, new UnorderedAccessViewDescription(_tileLightBuffer, Format.Unknown, 0, (uint) totalTileInts));
        _tileLightSrv = _device.CreateShaderResourceView(_tileLightBuffer, new ShaderResourceViewDescription(_tileLightBuffer, Format.Unknown, 0, (uint) totalTileInts));

        _selectionMaskTex = _device.CreateTexture2D(new Texture2DDescription(Format.R16G16_Float, (uint) width, (uint) height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        _selectionMaskRtv = _device.CreateRenderTargetView(_selectionMaskTex);
        _selectionMaskSrv = _device.CreateShaderResourceView(_selectionMaskTex);

        _output = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint) width, (uint) height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        _outputRtv = _device.CreateRenderTargetView(_output);
        _outputSrv = _device.CreateShaderResourceView(_output);
        _staging = Enumerable.Range(0, 3).Select(_ => _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint) width, (uint) height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read))).ToArray();
        _readbackFrame = 0;
        _statsEmissive = _device.CreateTexture2D(new Texture2DDescription(formats[0], (uint) width, (uint) height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _statsNormal = _device.CreateTexture2D(new Texture2DDescription(formats[1], (uint) width, (uint) height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _statsBase = _device.CreateTexture2D(new Texture2DDescription(formats[3], (uint) width, (uint) height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
    }

    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private void PollGpuQueries(ref RenderPassTimings timings)
    {
        timings.GpuValid = _lastTimings.GpuValid;
        timings.GpuTotalMs = _lastTimings.GpuTotalMs;
        timings.GpuGBufferMs = _lastTimings.GpuGBufferMs;
        timings.GpuShadowsMs = _lastTimings.GpuShadowsMs;
        timings.GpuSsaoMs = _lastTimings.GpuSsaoMs;
        timings.GpuLightCullAndLightingMs = _lastTimings.GpuLightCullAndLightingMs;
        timings.GpuTranslucencyMs = _lastTimings.GpuTranslucencyMs;
        timings.GpuPostProcessMs = _lastTimings.GpuPostProcessMs;

        // Drain oldest-to-newest completed query slots without blocking.
        Span<ulong> ts = stackalloc ulong[QueryTimestampsPerFrame];
        for (var offset = 0; offset < QueryRingSize; offset++)
        {
            var idx = (_queryWriteIdx + offset) % QueryRingSize;
            ref var slot = ref _queryRing[idx];
            if (!slot.Issued)
                continue;

            if (!_context.GetData(slot.Disjoint, AsyncGetDataFlags.DoNotFlush, out QueryDataTimestampDisjoint disjoint))
            {
                LastGpuQueryStatus = $"slot{idx}:DisjointNotReady";
                break; // Oldest still in flight; preserve ring order
            }

            if (disjoint.Disjoint || disjoint.Frequency == 0)
            {
                slot.Issued = false;
                LastGpuQueryStatus = $"slot{idx}:Disjoint={disjoint.Disjoint},Freq={disjoint.Frequency}";
                continue;
            }

            var allOk = true;
            var failTs = -1;
            for (var k = 0; k < QueryTimestampsPerFrame; k++)
            {
                if (!_context.GetData(slot.Ts[k], AsyncGetDataFlags.DoNotFlush, out ts[k]))
                {
                    allOk = false;
                    failTs = k;
                    break;
                }
            }

            if (!allOk)
            {
                LastGpuQueryStatus = $"slot{idx}:Ts{failTs}NotReady";
                break;
            }

            slot.Issued = false;
            if (slot.Epoch != _timingsEpoch)
            {
                LastGpuQueryStatus = $"drainedStale(slot{idx},epoch{slot.Epoch})";
                continue;
            }

            var scale = 1000.0 / disjoint.Frequency;
            timings.GpuValid = true;
            timings.GpuGBufferMs = Math.Max(0.0, (ts[1] - ts[0]) * scale);
            timings.GpuShadowsMs = Math.Max(0.0, (ts[2] - ts[1]) * scale);
            timings.GpuSsaoMs = Math.Max(0.0, (ts[3] - ts[2]) * scale);
            timings.GpuLightCullAndLightingMs = Math.Max(0.0, (ts[4] - ts[3]) * scale);
            timings.GpuTranslucencyMs = Math.Max(0.0, (ts[5] - ts[4]) * scale);
            timings.GpuPostProcessMs = Math.Max(0.0, (ts[6] - ts[5]) * scale);
            timings.GpuTotalMs = Math.Max(0.0, (ts[6] - ts[0]) * scale);
            LastGpuQueryStatus = $"ok(slot{idx},freq={disjoint.Frequency})";
        }

        _lastTimings = timings;
    }

    public byte[] Render(int width, int height, Matrix4x4 worldToClip, Vector3 cameraPosition, float screenMultiple,
        IReadOnlyList<NativeDrawItem> items, ENativePreviewMode mode, float exposure, Vector3 lightDirection, bool measure,
        IReadOnlyList<NativeLandscapeDrawItem>? landscapes = null,
        IReadOnlyList<NativeNiagaraSpriteBatch>? niagaraSprites = null,
        IReadOnlyList<NativeNiagaraMeshBatch>? niagaraMeshes = null,
        IReadOnlyList<SceneLight>? sceneLights = null,
        IReadOnlyList<NativeBillboardDrawItem>? editorBillboards = null,
        SceneHeightFog? heightFog = null,
        ScenePostProcess? postProcess = null,
        float? timeOverride = null,
        RetainedSceneDrawCache? retainedCache = null,
        WorldSettingsState? worldSettings = null,
        (SceneMesh? Mesh, int Slot, NativeMaterialPipeline? Pipeline) hoverOverride = default,
        IReadOnlyList<(Vector3 MinCm, Vector3 MaxCm)>? loadedWpCellBounds = null)
    {
        var tFrameStart = Stopwatch.GetTimestamp();
        var sceneToken = retainedCache?.BuiltScene;
        if (!ReferenceEquals(retainedCache, _lastRetainedCache) || !ReferenceEquals(sceneToken, _lastSceneToken))
        {
            if (_lastRetainedCache is not null || retainedCache is not null)
                ResetTimings();
            _lastRetainedCache = retainedCache;
            _lastSceneToken = sceneToken;
        }
        var timings = new RenderPassTimings();

        width = Math.Max(16, width);
        height = Math.Max(16, height);
        EnsureTargets(width, height);

        var time = timeOverride ?? (float) _clock.Elapsed.TotalSeconds;
        var delta = Math.Clamp(time - _previousTime, 1e-4f, 0.1f);
        _previousTime = time;

        var features = Features;
        var worldToClipUnjittered = worldToClip;
        var temporalAA = features.TemporalAA && mode is ENativePreviewMode.Lit or ENativePreviewMode.Emissive;
        if (_post is null) temporalAA = false;
        worldToClip = _post?.JitterProjection(worldToClipUnjittered, width, height, temporalAA) ?? worldToClipUnjittered;
        FillFrustumPlanes(worldToClipUnjittered, _frustumPlanesScratch);
        var planes = _frustumPlanesScratch;
        _lightRenderer.UpdateLights(_context, sceneLights, cameraPosition, planes, lightDirection, worldSettings, features.MaxLightDistanceMeters);
        var dirLight = _lightRenderer.DirectionalLight;
        var skyLight = _lightRenderer.SkyLight;

        var translatedWorldToClip = Matrix4x4.CreateTranslation(cameraPosition) * worldToClip;
        Matrix4x4.Invert(translatedWorldToClip, out var clipToTranslatedWorld);
        Matrix4x4.Invert(worldToClip, out var clipToWorld);
        var screenToClip = new Matrix4x4(2f / width, 0, 0, 0, 0, -2f / height, 0, 0, 0, 0, 1, 0, -1, 1, 0, 1);
        var svPosToTranslatedWorld = screenToClip * clipToTranslatedWorld;

        var fwd4 = Vector4.Transform(new Vector4(0, 0, 0.5f, 1f), clipToTranslatedWorld);
        var camFwd = fwd4.W != 0 ? Vector3.Normalize(new Vector3(fwd4.X, fwd4.Y, fwd4.Z) / fwd4.W) : new Vector3(1, 0, 0);
        var up4 = Vector4.Transform(new Vector4(0, 1f, 0.5f, 1f), clipToTranslatedWorld);
        var camUp = up4.W != 0 ? Vector3.Normalize(new Vector3(up4.X, up4.Y, up4.Z) / up4.W - camFwd * Vector3.Dot(new Vector3(up4.X, up4.Y, up4.Z) / up4.W, camFwd)) : new Vector3(0, 0, 1);
        var camRight = Vector3.Normalize(Vector3.Cross(camUp, camFwd));

        var translatedWorldToView = new Matrix4x4(
            camRight.X, camUp.X, camFwd.X, 0f,
            camRight.Y, camUp.Y, camFwd.Y, 0f,
            camRight.Z, camUp.Z, camFwd.Z, 0f,
            0f, 0f, 0f, 1f);
        var viewToTranslatedWorld = Matrix4x4.Transpose(translatedWorldToView);

        var viewToClip = viewToTranslatedWorld * translatedWorldToClip;
        if (MathF.Abs(viewToClip.M34) > 1e-6f && MathF.Abs(viewToClip.M34 - 1f) > 1e-3f)
        {
            var wScale = 1f / viewToClip.M34;
            worldToClip *= wScale;
            worldToClipUnjittered *= wScale;
            translatedWorldToClip = Matrix4x4.CreateTranslation(cameraPosition) * worldToClip;
            Matrix4x4.Invert(translatedWorldToClip, out clipToTranslatedWorld);
            Matrix4x4.Invert(worldToClip, out clipToWorld);
            svPosToTranslatedWorld = screenToClip * clipToTranslatedWorld;
            viewToClip = viewToTranslatedWorld * translatedWorldToClip;
        }
        var nearCm = MathF.Max(MathF.Abs(viewToClip.M43), 0.1f);
        Matrix4x4.Invert(viewToClip, out var clipToView);
        var screenToWorld = screenToClip * clipToWorld;

        var view = _viewScratch;
        Array.Clear(view);
        WriteMatrix(view, ViewTranslatedWorldToClip, translatedWorldToClip);
        WriteMatrix(view, ViewWorldToClip, worldToClip);
        WriteMatrix(view, ViewClipToWorld, clipToWorld);
        WriteMatrix(view, ViewTranslatedWorldToView, translatedWorldToView);
        WriteMatrix(view, ViewViewToTranslatedWorld, viewToTranslatedWorld);
        WriteMatrix(view, ViewViewToClip, viewToClip);
        WriteMatrix(view, ViewClipToView, clipToView);
        WriteMatrix(view, ViewClipToTranslatedWorld, clipToTranslatedWorld);
        WriteMatrix(view, ViewSVPositionToTranslatedWorld, svPosToTranslatedWorld);
        WriteMatrix(view, ViewScreenToWorld, screenToWorld);
        WriteMatrix(view, ViewScreenToTranslatedWorld, svPosToTranslatedWorld);

        view[ViewViewForward] = new Vector4(camFwd, 0);
        view[ViewViewUp] = new Vector4(camUp, 0);
        view[ViewViewRight] = new Vector4(camRight, 0);
        view[60] = new Vector4(camFwd, 0);

        view[ViewInvDeviceZToWorldZTransform] = new Vector4(0f, 0f, 1f / nearCm, -1e-7f);
        view[ViewScreenPositionScaleBias] = new Vector4(0.5f, -0.5f, 0.5f, 0.5f);
        view[ViewWorldCameraOrigin] = new Vector4(cameraPosition, 1);
        view[ViewTranslatedWorldCameraOrigin] = new Vector4(0, 0, 0, 1);
        view[ViewWorldViewOrigin] = new Vector4(cameraPosition, 1);
        view[ViewPreViewTranslation] = new Vector4(-cameraPosition, 0);

        WriteMatrix(view, ViewPrevTranslatedWorldToClip, translatedWorldToClip);
        view[ViewPrevPreViewTranslation] = new Vector4(-cameraPosition, 0);

        view[129] = new Vector4(0, 0, width, height);
        view[130] = new Vector4(width, height, 1f / width, 1f / height);
        view[131] = new Vector4(width, height, 1f / width, 1f / height);
        view[132] = new Vector4(0, 0, 1, 1);
        view[134] = new Vector4(1f, 1f, 1f, 1f);
        view[135] = new Vector4(1f, 1f, 1f, 1f);
        view[136] = new Vector4(0, 0, 0, 1);
        view[137] = new Vector4(0, 0, 0, 1);
        view[138] = new Vector4(0, 0, 0, 1);
        view[139] = new Vector4(0, 1, 0, 0);
        view[140] = new Vector4(0, 0, 0, 1);
        view[141] = new Vector4(1f, 1f, 1f, 1f);
        view[142] = new Vector4(delta, 0, time, time);
        view[143] = new Vector4(delta, 0, 1f, 1f);
        view[144] = new Vector4(1f, 0f, 1f, 1f);
        view[145] = new Vector4(1f, 0f, 1f, 1f);
        var frameIdx = temporalAA ? _frame : 0;
        view[152] = new Vector4(frameIdx % 8, frameIdx, frameIdx % 4, 0f);
        view[157] = new Vector4(skyLight.SkyColor * skyLight.Intensity, 1f);
        view[161] = new Vector4(dirLight.DirectionToLight, 0f);
        view[165] = new Vector4(dirLight.Color * dirLight.Intensity, 1f);
        view[179] = new Vector4(1f, 0f, 1f, 1f);
        view[182] = Vector4.Zero;
        view[183] = Vector4.Zero;
        view[195] = new Vector4(1f, 1f, 4f, 1f);
        view[210] = new Vector4(0f, 0f, 0f, 1f);
        view[226] = new Vector4(0.04f, 0.08f, 0.04f, 0.04f);
        UploadSpan(_view, view.AsSpan());

        var transPass = _transPassScratch;
        Array.Clear(transPass);
        var oneUintBits = BitConverter.Int32BitsToSingle(1);
        var channelMaskBits = BitConverter.Int32BitsToSingle(0xFF02);
        transPass[0] = new Vector4(0, 0, oneUintBits, 0);
        transPass[1] = new Vector4(oneUintBits, oneUintBits, oneUintBits, 0);
        transPass[3] = new Vector4(0.001f, 0f, 1f, 1f);
        transPass[4] = new Vector4(dirLight.DirectionToLight, 0);
        transPass[5] = new Vector4(dirLight.Color * dirLight.Intensity, 1f);
        transPass[6] = new Vector4(channelMaskBits, 0, 0, 1f);
        transPass[107] = new Vector4(0f, 0.001f, 0f, 0f);
        transPass[108] = new Vector4(0f, 0.001f, 0f, 0f);
        transPass[109] = new Vector4(skyLight.SkyColor * skyLight.Intensity, 1f);
        transPass[110] = new Vector4(0f, 0f, 0f, 1e9f);
        transPass[111] = new Vector4(0f, 0f, 0f, -1f);
        transPass[114] = Vector4.Zero;
        transPass[135] = new Vector4(0.5f, -0.5f, 0.5f, 0.5f);
        UploadSpan(_translucentBasePass, transPass.AsSpan());

        var shadowsActive = features.DynamicShadows && dirLight.Intensity > 0f && mode is ENativePreviewMode.Lit;
        var shadowDistance = MathF.Max(500f, features.ShadowDistance);

        _visibleScratch.Clear();
        _selectedScratch.Clear();
        _shadowOnlyScratch.Clear();
        _ismFastOpaqueScratch.Clear();
        _opaqueScratch.Clear();
        _translucentScratch.Clear();
        _shadowDrawsScratch.Clear();
        _visibleLandscapesScratch.Clear();

        var tAfterGather = Stopwatch.GetTimestamp();
        timings.CpuGatherMs = ToMs(tAfterGather - tFrameStart);

        var usingRetained = retainedCache is not null && retainedCache.PrimitiveSrv is not null;
        var totalVisibleCount = 0;

        if (usingRetained)
        {
            var groups = retainedCache!.Groups;
            var hasAnyHlod = false;
            for (var gIdx = 0; gIdx < groups.Count; gIdx++)
            {
                if (groups[gIdx].IsHlod) { hasAnyHlod = true; break; }
            }

            if (_primitiveBoundsCache.Length < Math.Max(1, retainedCache.TotalPrimitives))
                _primitiveBoundsCache = new Vector4[Math.Max(retainedCache.TotalPrimitives, Math.Max(_primitiveBoundsCache.Length * 2, 256))];

            var retainedCount = 0;
            for (var gIdx = 0; gIdx < groups.Count; gIdx++)
            {
                var g = groups[gIdx];
                if (!g.Source.IsVisible || !g.Source.IsVisibleRecursive) continue;
                if (g.IsHlod && !features.EnableHlod) continue;

                var isSel = g.Source.IsSelectedRecursive;
                var hasHover = hoverOverride.Mesh == g.Source && hoverOverride.Pipeline is not null;

                if (!g.IsIsm)
                {
                    var bounds = g.WorldBounds;
                    var center = new Vector3(bounds.X, bounds.Y, bounds.Z);
                    var radius = bounds.W;
                    _primitiveBoundsCache[g.BasePrimitiveId] = bounds;
                    var distance = Vector3.Distance(center, cameraPosition);
                    var screenSize = 2f * radius * screenMultiple / MathF.Max(1f, distance);

                    if (hasAnyHlod && features.EnableHlod && !isSel)
                    {
                        if (g.IsHlod && (distance < HlodTransitionDistance || IsInsideLoadedWpCell(center, loadedWpCellBounds))) continue;
                        if (!g.IsHlod && distance >= HlodTransitionDistance && radius < 4000f && !IsInsideLoadedWpCell(center, loadedWpCellBounds)) continue;
                    }
                    if (!isSel && groups.Count > 1024 && !g.IsHlod && distance > 3500f && screenSize < MinScreenSize)
                        continue;

                    var inFrustum = InFrustum(planes, center, MathF.Max(radius * 1.25f, 100f));
                    if (!inFrustum)
                    {
                        if (shadowsActive && radius > 50f && distance < shadowDistance + radius + NativeShadowMaps.CasterExtrusion * 0.1f)
                        {
                            var shadowLod = g.Mesh.Select(screenSize * LodScreenBias);
                            var sections = shadowLod.Sections;
                            for (var s = 0; s < sections.Count; s++)
                            {
                                var sec = sections[s];
                                if (sec.MaterialIndex < 0 || sec.MaterialIndex >= g.MaterialsBySlot.Length) continue;
                                var pipe = (hasHover && sec.MaterialIndex == hoverOverride.Slot) ? hoverOverride.Pipeline : g.MaterialsBySlot[sec.MaterialIndex];
                                if (pipe is not null && !pipe.IsForward && !pipe.IsRvtShader)
                                    _shadowDrawsScratch.Add(new ShadowDrawCmd(pipe, g.BasePrimitiveId, shadowLod, sec));
                            }
                        }
                        continue;
                    }

                    retainedCount++;
                    var lod = g.Mesh.Select(screenSize * LodScreenBias);
                    if (isSel)
                        _selectedScratch.Add((g.LocalToWorld, lod));

                    var depthBucket = (int) (distance / 150f);
                    var secs = lod.Sections;
                    for (var s = 0; s < secs.Count; s++)
                    {
                        var sec = secs[s];
                        if (sec.MaterialIndex < 0 || sec.MaterialIndex >= g.MaterialsBySlot.Length) continue;
                        var pipe = (hasHover && sec.MaterialIndex == hoverOverride.Slot) ? hoverOverride.Pipeline : g.MaterialsBySlot[sec.MaterialIndex];
                        if (pipe is null) continue;
                        var cmd = new DrawCmd(pipe, g.BasePrimitiveId, lod, sec, g.Mirrored, depthBucket);
                        if (pipe.IsForward)
                            _translucentScratch.Add(cmd);
                        else
                        {
                            _opaqueScratch.Add(cmd);
                            if (shadowsActive && !pipe.IsRvtShader)
                                _shadowDrawsScratch.Add(new ShadowDrawCmd(pipe, g.BasePrimitiveId, lod, sec));
                        }
                    }
                }
                else
                {
                    // ISM group: coarse aggregate frustum check + fast path when wholly inside frustum
                    var agg = g.AggregateBounds;
                    var aggCenter = new Vector3(agg.X, agg.Y, agg.Z);
                    var aggRadius = agg.W;
                    var aggDist = Vector3.Distance(aggCenter, cameraPosition);
                    var aggInFrustum = InFrustum(planes, aggCenter, MathF.Max(aggRadius * 1.25f, 100f));

                    if (!aggInFrustum)
                    {
                        if (shadowsActive && aggDist < shadowDistance + aggRadius + NativeShadowMaps.CasterExtrusion * 0.1f)
                        {
                            for (var k = 0; k < g.PrimitiveCount; k++)
                            {
                                var primId = g.BasePrimitiveId + k;
                                var ib = g.InstanceWorldBounds[k];
                                _primitiveBoundsCache[primId] = ib;
                                var ic = new Vector3(ib.X, ib.Y, ib.Z);
                                if (ib.W <= 50f) continue;
                                var d = Vector3.Distance(ic, cameraPosition);
                                if (d >= shadowDistance + ib.W + NativeShadowMaps.CasterExtrusion * 0.1f) continue;
                                var ss = 2f * ib.W * screenMultiple / MathF.Max(1f, d);
                                var sLod = g.Mesh.Select(ss * LodScreenBias);
                                for (var s = 0; s < sLod.Sections.Count; s++)
                                {
                                    var sec = sLod.Sections[s];
                                    if (sec.MaterialIndex < 0 || sec.MaterialIndex >= g.MaterialsBySlot.Length) continue;
                                    var pipe = g.MaterialsBySlot[sec.MaterialIndex];
                                    if (pipe is not null && !pipe.IsForward && !pipe.IsRvtShader)
                                        _shadowDrawsScratch.Add(new ShadowDrawCmd(pipe, primId, sLod, sec));
                                }
                            }
                        }
                        continue;
                    }

                    var hlodSkipFast = hasAnyHlod && features.EnableHlod && !isSel &&
                                       ((g.IsHlod && (aggDist - aggRadius < HlodTransitionDistance || IsInsideLoadedWpCell(aggCenter, loadedWpCellBounds))) ||
                                        (!g.IsHlod && aggDist + aggRadius >= HlodTransitionDistance && !IsInsideLoadedWpCell(aggCenter, loadedWpCellBounds)));
                    var whollyInside = g.PersistentInstanceIdVb is not null &&
                                       !hasHover &&
                                       !isSel &&
                                       g.HasUniformMirrored &&
                                       !hlodSkipFast &&
                                       IsWhollyInFrustum(planes, aggCenter, aggRadius);

                    if (whollyInside)
                    {
                        var aggSs = 2f * (g.Mesh.BoundsRadius * MathF.Max(g.InstanceScales[0].X, 1f)) * screenMultiple / MathF.Max(1f, aggDist);
                        var lod = g.Mesh.Select(aggSs * LodScreenBias);
                        var allOpaqueSimple = true;
                        for (var s = 0; s < lod.Sections.Count; s++)
                        {
                            var sec = lod.Sections[s];
                            if (sec.MaterialIndex < 0 || sec.MaterialIndex >= g.MaterialsBySlot.Length) continue;
                            var pipe = g.MaterialsBySlot[sec.MaterialIndex];
                            if (pipe is null) continue;
                            if (pipe.IsForward || pipe.NeedsEarlyZMaskPass || pipe.IsRvtShader)
                            { allOpaqueSimple = false; break; }
                        }

                        if (allOpaqueSimple)
                        {
                            retainedCount += g.PrimitiveCount;
                            for (var s = 0; s < lod.Sections.Count; s++)
                            {
                                var sec = lod.Sections[s];
                                if (sec.MaterialIndex < 0 || sec.MaterialIndex >= g.MaterialsBySlot.Length) continue;
                                if (g.MaterialsBySlot[sec.MaterialIndex] is { } pipe)
                                    _ismFastOpaqueScratch.Add(new IsmFastDraw(g, lod, sec, pipe));
                            }
                            if (shadowsActive)
                            {
                                for (var k = 0; k < g.PrimitiveCount; k++)
                                {
                                    var primId = g.BasePrimitiveId + k;
                                    _primitiveBoundsCache[primId] = g.InstanceWorldBounds[k];
                                    for (var s = 0; s < lod.Sections.Count; s++)
                                    {
                                        var sec = lod.Sections[s];
                                        if (sec.MaterialIndex < 0 || sec.MaterialIndex >= g.MaterialsBySlot.Length) continue;
                                        if (g.MaterialsBySlot[sec.MaterialIndex] is { } pipe)
                                            _shadowDrawsScratch.Add(new ShadowDrawCmd(pipe, primId, lod, sec));
                                    }
                                }
                            }
                            continue;
                        }
                    }

                    // Per-instance frustum cull for partial ISM intersection
                    for (var k = 0; k < g.PrimitiveCount; k++)
                    {
                        var primId = g.BasePrimitiveId + k;
                        var ib = g.InstanceWorldBounds[k];
                        _primitiveBoundsCache[primId] = ib;
                        var center = new Vector3(ib.X, ib.Y, ib.Z);
                        var radius = ib.W;
                        var distance = Vector3.Distance(center, cameraPosition);
                        var screenSize = 2f * radius * screenMultiple / MathF.Max(1f, distance);

                        if (hasAnyHlod && features.EnableHlod && !isSel)
                        {
                            if (g.IsHlod && (distance < HlodTransitionDistance || IsInsideLoadedWpCell(center, loadedWpCellBounds))) continue;
                            if (!g.IsHlod && distance >= HlodTransitionDistance && radius < 4000f && !IsInsideLoadedWpCell(center, loadedWpCellBounds)) continue;
                        }

                        var inFrustum = InFrustum(planes, center, MathF.Max(radius * 1.25f, 100f));
                        if (!inFrustum)
                        {
                            if (shadowsActive && radius > 50f && distance < shadowDistance + radius + NativeShadowMaps.CasterExtrusion * 0.1f)
                            {
                                var sLod = g.Mesh.Select(screenSize * LodScreenBias);
                                for (var s = 0; s < sLod.Sections.Count; s++)
                                {
                                    var sec = sLod.Sections[s];
                                    if (sec.MaterialIndex < 0 || sec.MaterialIndex >= g.MaterialsBySlot.Length) continue;
                                    var pipe = g.MaterialsBySlot[sec.MaterialIndex];
                                    if (pipe is not null && !pipe.IsForward && !pipe.IsRvtShader)
                                        _shadowDrawsScratch.Add(new ShadowDrawCmd(pipe, primId, sLod, sec));
                                }
                            }
                            continue;
                        }

                        retainedCount++;
                        var lod = g.Mesh.Select(screenSize * LodScreenBias);
                        if (isSel)
                            _selectedScratch.Add((g.InstanceLocalToWorlds[k], lod));

                        var depthBucket = (int) (distance / 150f);
                        var mirrored = g.InstanceMirrored[k];
                        for (var s = 0; s < lod.Sections.Count; s++)
                        {
                            var sec = lod.Sections[s];
                            if (sec.MaterialIndex < 0 || sec.MaterialIndex >= g.MaterialsBySlot.Length) continue;
                            var pipe = (hasHover && sec.MaterialIndex == hoverOverride.Slot) ? hoverOverride.Pipeline : g.MaterialsBySlot[sec.MaterialIndex];
                            if (pipe is null) continue;
                            var cmd = new DrawCmd(pipe, primId, lod, sec, mirrored, depthBucket);
                            if (pipe.IsForward)
                                _translucentScratch.Add(cmd);
                            else
                            {
                                _opaqueScratch.Add(cmd);
                                if (shadowsActive && !pipe.IsRvtShader)
                                    _shadowDrawsScratch.Add(new ShadowDrawCmd(pipe, primId, lod, sec));
                            }
                        }
                    }
                }
            }
            totalVisibleCount = retainedCount;
        }
        else
        {
            var hasAnyHlod = false;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].IsHlod) { hasAnyHlod = true; break; }
            }

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.IsHlod && !features.EnableHlod) continue;
                var m = item.LocalToWorld;
                var scale = MathF.Max(new Vector3(m.M11, m.M12, m.M13).Length(), MathF.Max(new Vector3(m.M21, m.M22, m.M23).Length(), new Vector3(m.M31, m.M32, m.M33).Length()));
                var center = Vector3.Transform(item.Mesh.BoundsCenter, m);
                var radius = item.Mesh.BoundsRadius * scale;
                if (!InFrustum(planes, center, MathF.Max(radius * 1.25f, 100f)))
                {
                    if (shadowsActive && radius > 50f && Vector3.Distance(center, cameraPosition) < shadowDistance + radius + NativeShadowMaps.CasterExtrusion * 0.1f)
                    {
                        var d = Vector3.Distance(center, cameraPosition);
                        var ss = 2f * radius * screenMultiple / MathF.Max(1f, d);
                        _shadowOnlyScratch.Add(new VisiblePrimitive(item, item.Mesh.Select(ss * LodScreenBias), d, ss));
                    }
                    continue;
                }

                var distance = Vector3.Distance(center, cameraPosition);
                var screenSize = 2f * radius * screenMultiple / MathF.Max(1f, distance);

                if (!item.IsSelected)
                {
                    if (hasAnyHlod && features.EnableHlod)
                    {
                        if (item.IsHlod && distance < HlodTransitionDistance) continue;
                        if (!item.IsHlod && distance >= HlodTransitionDistance && radius < 4000f) continue;
                    }
                    if (items.Count > 1024 && !item.IsHlod && distance > 3500f && screenSize < MinScreenSize)
                        continue;
                }

                var lod = item.Mesh.Select(screenSize * LodScreenBias);
                _visibleScratch.Add(new VisiblePrimitive(item, lod, distance, screenSize));
                if (item.IsSelected)
                    _selectedScratch.Add((m, lod));
            }

            if (_visibleScratch.Count > MaxVisiblePrimitives)
            {
                _visibleScratch.Sort(static (a, b) => b.ScreenSize.CompareTo(a.ScreenSize));
                _visibleScratch.RemoveRange(MaxVisiblePrimitives, _visibleScratch.Count - MaxVisiblePrimitives);
            }

            if (_shadowOnlyScratch.Count > 8192)
            {
                _shadowOnlyScratch.Sort(static (a, b) => b.ScreenSize.CompareTo(a.ScreenSize));
                _shadowOnlyScratch.RemoveRange(8192, _shadowOnlyScratch.Count - 8192);
            }

            totalVisibleCount = _visibleScratch.Count;
            var visibleCount = _visibleScratch.Count;
            _visibleScratch.AddRange(_shadowOnlyScratch);

            var primTotal = Math.Max(1, _visibleScratch.Count);
            EnsurePrimitiveCapacity(primTotal);
            var neededVecs = primTotal * PrimitiveStride;
            if (_primitivesScratch.Length < neededVecs)
                _primitivesScratch = new Vector4[Math.Max(neededVecs, Math.Max(_primitivesScratch.Length * 2, PrimitiveStride * 256))];
            if (_primitiveBoundsCache.Length < primTotal)
                _primitiveBoundsCache = new Vector4[Math.Max(primTotal, Math.Max(_primitiveBoundsCache.Length * 2, 256))];

            var maskBits = BitConverter.Int32BitsToSingle(0x7FF);
            for (var i = 0; i < _visibleScratch.Count; i++)
            {
                var vp = _visibleScratch[i];
                var m = vp.Item.LocalToWorld;
                Matrix4x4.Invert(m, out var worldToLocal);
                var o = i * PrimitiveStride;
                WriteMatrix(_primitivesScratch, o, m);
                var scale = new Vector3(new Vector3(m.M11, m.M12, m.M13).Length(), new Vector3(m.M21, m.M22, m.M23).Length(), new Vector3(m.M31, m.M32, m.M33).Length());
                _primitivesScratch[o + 4] = new Vector4(1 / MathF.Max(scale.X, 1e-6f), 1 / MathF.Max(scale.Y, 1e-6f), 1 / MathF.Max(scale.Z, 1e-6f), m.GetDeterminant() < 0 ? -1 : 1);
                var center = Vector3.Transform(vp.Item.Mesh.BoundsCenter, m);
                var maxScale = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
                var worldRadius = vp.Item.Mesh.BoundsRadius * maxScale;
                _primitivesScratch[o + 5] = new Vector4(center, worldRadius);
                _primitiveBoundsCache[i] = new Vector4(center, worldRadius);
                WriteMatrix(_primitivesScratch, o + 6, worldToLocal);
                WriteMatrix(_primitivesScratch, o + 10, m);
                WriteMatrix(_primitivesScratch, o + 14, worldToLocal);
                _primitivesScratch[o + 18] = new Vector4(m.Translation, 0);
                var halfExt = new Vector3(MathF.Max(worldRadius, 10000f));
                _primitivesScratch[o + 19] = new Vector4(halfExt.X, halfExt.Y, halfExt.Z, 1);
                _primitivesScratch[o + 20] = new Vector4(scale, 0.0f);
                _primitivesScratch[o + 21] = new Vector4(vp.Item.Mesh.BoundsCenter - halfExt, 0);
                _primitivesScratch[o + 22] = new Vector4(vp.Item.Mesh.BoundsCenter + halfExt, 0);
                _primitivesScratch[o + 23] = new Vector4(maskBits, maskBits, maskBits, maskBits);
            }
            UploadSpan(_primitiveBuffer, _primitivesScratch.AsSpan(0, neededVecs));

            for (var i = 0; i < _visibleScratch.Count; i++)
            {
                var (item, lod, distance, _) = _visibleScratch[i];
                var mirrored = item.LocalToWorld.GetDeterminant() < 0;
                var depthBucket = (int) (distance / 150f);
                var secs = lod.Sections;
                for (var s = 0; s < secs.Count; s++)
                {
                    var section = secs[s];
                    if (section.MaterialIndex < 0 || section.MaterialIndex >= item.MaterialsBySlot.Length)
                        continue;
                    if (item.MaterialsBySlot[section.MaterialIndex] is not { } pipeline)
                        continue;
                    if (i < visibleCount)
                    {
                        var cmd = new DrawCmd(pipeline, i, lod, section, mirrored, depthBucket);
                        if (pipeline.IsForward)
                            _translucentScratch.Add(cmd);
                        else
                            _opaqueScratch.Add(cmd);
                    }
                    if (shadowsActive && !pipeline.IsForward && !pipeline.IsRvtShader)
                        _shadowDrawsScratch.Add(new ShadowDrawCmd(pipeline, i, lod, section));
                }
            }
        }

        if (landscapes is { Count: > 0 })
        {
            for (var i = 0; i < landscapes.Count; i++)
            {
                var lItem = landscapes[i];
                var center = Vector3.Transform(lItem.Tile.BoundsCenter, lItem.LocalToWorld);
                var scale = MathF.Max(new Vector3(lItem.LocalToWorld.M11, lItem.LocalToWorld.M12, lItem.LocalToWorld.M13).Length(), 1f);
                var radius = lItem.Tile.BoundsRadius * scale;
                if (InFrustum(planes, center, radius * 1.5f))
                {
                    var dist = Vector3.Distance(center, cameraPosition);
                    _visibleLandscapesScratch.Add(lItem with { Distance = dist });
                }
            }
        }

        VisibleCount = totalVisibleCount + _visibleLandscapesScratch.Count + (niagaraSprites?.Count ?? 0) + (niagaraMeshes?.Count ?? 0);

        _opaqueScratch.Sort(OpaqueDrawComparer.Instance);
        _translucentScratch.Sort(TranslucentDrawComparer.Instance);
        _shadowDrawsScratch.Sort(ShadowDrawComparer.Instance);

        var totalDrawInstances = Math.Max(1, _opaqueScratch.Count + _translucentScratch.Count);
        EnsureInstanceIdCapacity(totalDrawInstances);
        if (_instancePrimIdsScratch.Length < totalDrawInstances)
            _instancePrimIdsScratch = new uint[Math.Max(totalDrawInstances, Math.Max(_instancePrimIdsScratch.Length * 2, 256))];

        var cursor = 0;
        for (var i = 0; i < _opaqueScratch.Count; i++)
            _instancePrimIdsScratch[cursor++] = (uint) _opaqueScratch[i].Primitive;
        for (var i = 0; i < _translucentScratch.Count; i++)
            _instancePrimIdsScratch[cursor++] = (uint) _translucentScratch[i].Primitive;
        UploadSpan(_primitiveIds, _instancePrimIdsScratch.AsSpan(0, totalDrawInstances));

        var tAfterCullSort = Stopwatch.GetTimestamp();
        timings.CpuCullSortMs = ToMs(tAfterCullSort - tAfterGather);

        var shared = BuildShared(usingRetained ? retainedCache!.PrimitiveSrv : null);
        var ctx = _context;

        // Poll completed GPU queries from oldest in-flight slot (zero pipeline stall)
        PollGpuQueries(ref timings);

        ref var writeQuery = ref _queryRing[_queryWriteIdx];
        var canWriteQuery = !writeQuery.Issued;
        if (canWriteQuery)
        {
            writeQuery.Epoch = _timingsEpoch;
            ctx.Begin(writeQuery.Disjoint);
        }

        ctx.ClearState();
        for (var r = 0; r < _rtvs.Length; r++)
            ctx.ClearRenderTargetView(_rtvs[r], new Color4(0, 0, 0, 0));
        ctx.ClearRenderTargetView(_selectionMaskRtv!, new Color4(0, 0, 0, 0));
        ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth, 0, 0);
        ctx.RSSetViewport(0, 0, width, height);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        DrawCalls = 0;

        if (canWriteQuery)
            ctx.End(writeQuery.Ts[0]); // Start GPU work

        var tGBufferStart = Stopwatch.GetTimestamp();
        var isWireframe = mode == ENativePreviewMode.Wireframe;
        if (_visibleLandscapesScratch.Count > 0)
        {
            ctx.OMSetRenderTargets(_basePassRtvs, _dsv);
            ctx.OMSetDepthStencilState(_depthWrite);
            var dc = DrawCalls;
            _landscapeRenderer.Render(ctx, worldToClip, _visibleLandscapesScratch, SelectRasterState(false, false, isWireframe), ref dc);
            DrawCalls = dc;
        }

        if (_opaqueScratch.Count > 0)
        {
            var firstMaskedIdx = -1;
            if (!isWireframe)
            {
                for (var idx = 0; idx < _opaqueScratch.Count; idx++)
                {
                    if (_opaqueScratch[idx].Pipeline.NeedsEarlyZMaskPass) { firstMaskedIdx = idx; break; }
                }
            }

            if (firstMaskedIdx >= 0)
            {
                ctx.OMSetRenderTargets(_emptyRtvs, _dsv);
                ctx.OMSetDepthStencilState(_depthWrite);
                ctx.OMSetBlendState(null);
                NativeMaterialPipeline? prePipe = null;
                NativeMesh? preMesh = null;
                var m = firstMaskedIdx;
                while (m < _opaqueScratch.Count)
                {
                    var first = _opaqueScratch[m];
                    var batchCount = 1;
                    while (m + batchCount < _opaqueScratch.Count &&
                           ReferenceEquals(_opaqueScratch[m + batchCount].Pipeline, first.Pipeline) &&
                           ReferenceEquals(_opaqueScratch[m + batchCount].Mesh, first.Mesh) &&
                           _opaqueScratch[m + batchCount].Section.FirstIndex == first.Section.FirstIndex &&
                           _opaqueScratch[m + batchCount].Section.IndexCount == first.Section.IndexCount &&
                           _opaqueScratch[m + batchCount].Mirrored == first.Mirrored)
                    {
                        batchCount++;
                    }

                    if (!ReferenceEquals(first.Pipeline, prePipe))
                    {
                        prePipe = first.Pipeline;
                        preMesh = null;
                        first.Pipeline.BindDepthOnlyMaterial(ctx, shared);
                    }
                    if (!ReferenceEquals(first.Mesh, preMesh))
                    {
                        preMesh = first.Mesh;
                        first.Pipeline.BindDepthOnlyMesh(ctx, shared, first.Mesh);
                        ctx.IASetIndexBuffer(first.Mesh.Indices, Format.R32_UInt, 0);
                    }

                    _vbPair[0] = first.Mesh.Positions;
                    _vbPair[1] = _primitiveIds;
                    _vbOffsets[0] = 0u;
                    _vbOffsets[1] = (uint) (m * 4);
                    ctx.IASetVertexBuffers(0, _vbPair, _vbStrides, _vbOffsets);
                    ctx.RSSetState(first.Pipeline.TwoSided ? _cullNone : (first.Mirrored ^ FlipCulling) ? _cullFront : _cullBack);
                    ctx.DrawIndexedInstanced((uint) first.Section.IndexCount, (uint) batchCount, (uint) first.Section.FirstIndex, 0, 0);
                    DrawCalls++;
                    m += batchCount;
                }
            }

            ctx.OMSetDepthStencilState(_depthWrite);
            var usingEqualDepth = false;
            NativeMaterialPipeline? currentPipeline = null;
            NativeMesh? currentMesh = null;
            bool? currentRvtMode = null;

            var i = 0;
            while (i < _opaqueScratch.Count)
            {
                var first = _opaqueScratch[i];
                var batchCount = 1;
                while (i + batchCount < _opaqueScratch.Count &&
                       ReferenceEquals(_opaqueScratch[i + batchCount].Pipeline, first.Pipeline) &&
                       ReferenceEquals(_opaqueScratch[i + batchCount].Mesh, first.Mesh) &&
                       _opaqueScratch[i + batchCount].Section.FirstIndex == first.Section.FirstIndex &&
                       _opaqueScratch[i + batchCount].Section.IndexCount == first.Section.IndexCount &&
                       _opaqueScratch[i + batchCount].Mirrored == first.Mirrored)
                {
                    batchCount++;
                }

                if (!isWireframe && usingEqualDepth != first.Pipeline.NeedsEarlyZMaskPass)
                {
                    usingEqualDepth = first.Pipeline.NeedsEarlyZMaskPass;
                    ctx.OMSetDepthStencilState(usingEqualDepth ? _depthEqualRead : _depthWrite);
                }

                if (currentRvtMode != first.Pipeline.IsRvtShader)
                {
                    currentRvtMode = first.Pipeline.IsRvtShader;
                    ctx.OMSetRenderTargets(first.Pipeline.IsRvtShader ? _rvtRtvs : _basePassRtvs, _dsv);
                }

                if (!ReferenceEquals(first.Pipeline, currentPipeline))
                {
                    currentPipeline = first.Pipeline;
                    currentMesh = null;
                    first.Pipeline.BindMaterial(ctx, shared);
                    ctx.OMSetBlendState(first.Pipeline.Blend);
                }
                if (!ReferenceEquals(first.Mesh, currentMesh))
                {
                    currentMesh = first.Mesh;
                    first.Pipeline.BindMesh(ctx, shared, first.Mesh);
                    ctx.IASetIndexBuffer(first.Mesh.Indices, Format.R32_UInt, 0);
                }

                _vbPair[0] = first.Mesh.Positions;
                _vbPair[1] = _primitiveIds;
                _vbOffsets[0] = 0u;
                _vbOffsets[1] = (uint) (i * 4);
                ctx.IASetVertexBuffers(0, _vbPair, _vbStrides, _vbOffsets);
                ctx.RSSetState(SelectRasterState(first.Pipeline.TwoSided, first.Mirrored, isWireframe));
                ctx.DrawIndexedInstanced((uint) first.Section.IndexCount, (uint) batchCount, (uint) first.Section.FirstIndex, 0, 0);
                DrawCalls++;
                i += batchCount;
            }
            ctx.OMSetBlendState(null);
        }

        // Fast-path ISM opaque draws using PersistentInstanceIdVb (zero per-frame instance ID upload)
        if (_ismFastOpaqueScratch.Count > 0)
        {
            ctx.OMSetRenderTargets(_basePassRtvs, _dsv);
            ctx.OMSetDepthStencilState(_depthWrite);
            ctx.OMSetBlendState(null);
            NativeMaterialPipeline? curPipe = null;
            NativeMesh? curMesh = null;
            for (var i = 0; i < _ismFastOpaqueScratch.Count; i++)
            {
                var ism = _ismFastOpaqueScratch[i];
                if (!ReferenceEquals(ism.Pipeline, curPipe))
                {
                    curPipe = ism.Pipeline;
                    curMesh = null;
                    ism.Pipeline.BindMaterial(ctx, shared);
                }
                if (!ReferenceEquals(ism.Lod, curMesh))
                {
                    curMesh = ism.Lod;
                    ism.Pipeline.BindMesh(ctx, shared, ism.Lod);
                    ctx.IASetIndexBuffer(ism.Lod.Indices, Format.R32_UInt, 0);
                }
                _vbPair[0] = ism.Lod.Positions;
                _vbPair[1] = ism.Group.PersistentInstanceIdVb!;
                _vbOffsets[0] = 0u;
                _vbOffsets[1] = 0u;
                ctx.IASetVertexBuffers(0, _vbPair, _vbStrides, _vbOffsets);
                var mirrored = ism.Group.InstanceMirrored[0];
                ctx.RSSetState(SelectRasterState(ism.Pipeline.TwoSided, mirrored, isWireframe));
                ctx.DrawIndexedInstanced((uint) ism.Section.IndexCount, (uint) ism.Group.PrimitiveCount, (uint) ism.Section.FirstIndex, 0, 0);
                DrawCalls++;
            }
        }

        if (canWriteQuery) ctx.End(writeQuery.Ts[1]); // After GBuffer
        var tAfterGBuffer = Stopwatch.GetTimestamp();
        timings.CpuGBufferMs = ToMs(tAfterGBuffer - tGBufferStart);

        // ---------------------------------------------------------------- Directional light shadow depths
        if (shadowsActive)
        {
            Matrix4x4.Invert(worldToClipUnjittered, out var clipToWorldUnj);
            Span<Vector3> rays = stackalloc Vector3[4];
            ReadOnlySpan<Vector2> ndcCorners = [new(-1, -1), new(1, -1), new(-1, 1), new(1, 1)];
            for (var k = 0; k < 4; k++)
            {
                var p4 = Vector4.Transform(new Vector4(ndcCorners[k], 0.5f, 1f), clipToWorldUnj);
                var p3 = new Vector3(p4.X, p4.Y, p4.Z) / (MathF.Abs(p4.W) > 1e-12f ? p4.W : 1e-12f);
                rays[k] = p3 - cameraPosition;
            }
            var rev = retainedCache?.DataVersion ?? 0L;
            _shadows!.Prepare(features.ShadowResolution, features.ShadowCascades, shadowDistance, cameraPosition, camFwd, rays, dirLight.DirectionToLight,
                rev, _frame, features.ShadowCascadeCaching, out var dirtyCascadeMask);
            if (dirtyCascadeMask != 0u)
            {
                RenderShadowDepths(ctx, shared, view, cameraPosition, _shadowDrawsScratch, landscapes, cameraPosition, shadowDistance, dirtyCascadeMask);
                UploadSpan(_view, view.AsSpan());
                ctx.RSSetViewport(0, 0, width, height);
            }
        }
        _shadows!.UploadConstants(ctx, camFwd, shadowDistance, shadowsActive);

        if (canWriteQuery) ctx.End(writeQuery.Ts[2]); // After Shadows
        var tAfterShadows = Stopwatch.GetTimestamp();
        timings.CpuShadowsMs = ToMs(tAfterShadows - tAfterGBuffer);

        _frame++;
        if (measure && _frame % 20 == 0 && _translucentScratch.Count == 0)
            MeasureOutputs();

        var skyCubeSrv = skyLight.Cubemap is not null
            ? NativeTextureCache.Get(_device, skyLight.Cubemap, asCube: true)
            : null;
        var hasSkyCubemap = skyCubeSrv is not null;
        const float maxCubemapMip = 8.0f;

        var hasSelection = _selectedScratch.Count > 0;
        var isStudioPreview = (sceneLights is null || sceneLights.Count == 0 ||
                               (_opaqueScratch.Count == 0 && _ismFastOpaqueScratch.Count == 0 && !sceneLights.Any(l => l is SceneDirectionalLight or SceneSkyLight))) &&
                              _visibleLandscapesScratch.Count == 0;

        var skyDomeOn = (worldSettings?.SkyEnabled ?? true) && (worldSettings?.TodmEnabled ?? true);
        var fogOn = (worldSettings?.FogEnabled ?? true) && (worldSettings?.TodmEnabled ?? true);
        var useTiledLight = features.TiledLightCulling && _tileLightCullCs is not null && _tileLightUav is not null && _lightRenderer.ActiveLocalLightCount > 0;
        var useSsao = features.ScreenSpaceAO && mode is ENativePreviewMode.Lit && !isStudioPreview && _ssaoHalfRtv is not null && _ssaoBlurRtv is not null;

        var compData = _compDataScratch;
        Array.Clear(compData);
        compData[0] = new Vector4(
            BitConverter.Int32BitsToSingle((int) mode),
            exposure,
            BitConverter.Int32BitsToSingle(_lightRenderer.ActiveLocalLightCount),
            skyLight.Intensity);
        compData[1] = new Vector4(dirLight.DirectionToLight, dirLight.Intensity);
        compData[2] = new Vector4(dirLight.Color, dirLight.SinHalfSourceAngle);
        compData[3] = new Vector4(skyLight.SkyColor, hasSkyCubemap ? 1.0f : 0.0f);
        compData[4] = new Vector4(skyLight.LowerHemisphereColor, maxCubemapMip);
        var bgCol = (_opaqueScratch.Count == 0 && _ismFastOpaqueScratch.Count == 0 && _visibleLandscapesScratch.Count == 0 && _translucentScratch.Count > 0)
            ? new Vector4(0.26f, 0.34f, 0.42f, hasSelection ? 1f : 0f)
            : new Vector4(0.015f, 0.017f, 0.022f, hasSelection ? 1f : 0f);
        compData[5] = bgCol;
        compData[6] = new Vector4(cameraPosition, isStudioPreview ? 1.0f : 0.0f);
        WriteMatrix(compData, 7, svPosToTranslatedWorld);

        if (fogOn && heightFog is not null && heightFog.IsVisible && (heightFog.Actor?.IsVisibleRecursive ?? true))
        {
            const float nightFog = 0.02f;
            var fogInscatter = heightFog.InscatteringColor * float.Lerp(nightFog, 1.0f, dirLight.Daylight);
            compData[11] = new Vector4(
                heightFog.Density,
                heightFog.HeightFalloff,
                heightFog.Height * 100.0f,
                heightFog.StartDistance * 100.0f);
            compData[12] = new Vector4(fogInscatter, heightFog.MaxOpacity);
            compData[13] = new Vector4(heightFog.DirectionalInscatteringColor, heightFog.DirectionalInscatteringExponent);
        }
        compData[14] = new Vector4(width, height, 1.0f / width, 1.0f / height);
        compData[15] = new Vector4(skyDomeOn ? 1f : 0f, fogOn ? 1f : 0f, useTiledLight ? 1f : 0f, useSsao ? 1f : 0f);
        compData[16] = new Vector4(
            BitConverter.UInt32BitsToSingle((uint) _tileCountX),
            BitConverter.UInt32BitsToSingle(16u),
            BitConverter.UInt32BitsToSingle(useTiledLight ? 1u : 0u),
            0f);
        UploadSpan(_compositeParams, compData.AsSpan());

        // ---------------------------------------------------------------- Half-res SSAO + Bilateral Blur
        if (useSsao)
        {
            var hw = Math.Max(1, width / 2);
            var hh = Math.Max(1, height / 2);
            ctx.IASetInputLayout(null);
            ctx.RSSetState(_cullNone);
            ctx.OMSetDepthStencilState(null);
            ctx.OMSetBlendState(null);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.VSSetShader(_compositeVs);
            ctx.PSSetConstantBuffer(0, _compositeParams);
            ctx.PSSetSampler(0, _sharedTemplate.Clamp);

            ctx.OMSetRenderTargets(_ssaoHalfRtv!, null);
            ctx.RSSetViewport(0, 0, hw, hh);
            ctx.PSSetShader(_ssaoHalfPs);
            ctx.PSSetShaderResource(1, _targetSrvs[1]);
            ctx.PSSetShaderResource(4, _depthSrv!);
            ctx.Draw(3, 0);

            ctx.OMSetRenderTargets(_ssaoBlurRtv!, null);
            ctx.PSSetShader(_ssaoBlurPs);
            ctx.PSSetShaderResource(1, null!);
            ctx.PSSetShaderResource(17, _ssaoHalfSrv!);
            ctx.Draw(3, 0);

            ctx.PSSetShaderResource(4, null!);
            ctx.PSSetShaderResource(17, null!);
            ctx.OMSetRenderTargets(_emptyRtvs, null);
        }

        if (canWriteQuery) ctx.End(writeQuery.Ts[3]); // After SSAO
        var tAfterSsao = Stopwatch.GetTimestamp();
        timings.CpuSsaoMs = ToMs(tAfterSsao - tAfterShadows);

        // ---------------------------------------------------------------- Compute Tiled Light Culling (16x16)
        if (useTiledLight)
        {
            _tileCullCbScratch[0] = new Vector4(clipToView.M11, clipToView.M12, clipToView.M13, clipToView.M14);
            _tileCullCbScratch[1] = new Vector4(clipToView.M21, clipToView.M22, clipToView.M23, clipToView.M24);
            _tileCullCbScratch[2] = new Vector4(clipToView.M31, clipToView.M32, clipToView.M33, clipToView.M34);
            _tileCullCbScratch[3] = new Vector4(clipToView.M41, clipToView.M42, clipToView.M43, clipToView.M44);
            _tileCullCbScratch[4] = new Vector4(translatedWorldToView.M11, translatedWorldToView.M12, translatedWorldToView.M13, translatedWorldToView.M14);
            _tileCullCbScratch[5] = new Vector4(translatedWorldToView.M21, translatedWorldToView.M22, translatedWorldToView.M23, translatedWorldToView.M24);
            _tileCullCbScratch[6] = new Vector4(translatedWorldToView.M31, translatedWorldToView.M32, translatedWorldToView.M33, translatedWorldToView.M34);
            _tileCullCbScratch[7] = new Vector4(width, height, 1f / width, 1f / height);
            _tileCullCbScratch[8] = new Vector4(
                BitConverter.UInt32BitsToSingle((uint) _tileCountX),
                BitConverter.UInt32BitsToSingle((uint) _tileCountY),
                BitConverter.UInt32BitsToSingle((uint) _lightRenderer.ActiveLocalLightCount),
                0f);
            UploadSpan(_tileCullCb, _tileCullCbScratch.AsSpan());

            ctx.OMSetRenderTargets(_emptyRtvs, null);
            ctx.CSSetShader(_tileLightCullCs!);
            ctx.CSSetConstantBuffer(0, _tileCullCb);
            ctx.CSSetShaderResource(0, _depthSrv!);
            ctx.CSSetShaderResource(1, _lightRenderer.LocalLightSrv);
            ctx.CSSetUnorderedAccessView(0, _tileLightUav!);
            ctx.Dispatch((uint) _tileCountX, (uint) _tileCountY, 1);

            ctx.CSSetUnorderedAccessViews(0, _null1Uav);
            ctx.CSSetShaderResources(0, _null2Srvs);
            ctx.CSSetShader(null);
        }

        // ---------------------------------------------------------------- Deferred Lighting Pass
        ctx.IASetInputLayout(null);
        ctx.RSSetState(_cullNone);
        ctx.OMSetDepthStencilState(null);
        ctx.OMSetBlendState(null);
        ctx.OMSetRenderTargets(_rtvs[6], null);
        ctx.RSSetViewport(0, 0, width, height);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_compositeVs);
        ctx.PSSetShader(_deferredLitPs);
        ctx.PSSetConstantBuffer(0, _compositeParams);
        ctx.PSSetConstantBuffer(1, _shadows.Constants);
        ctx.PSSetSampler(0, _sharedTemplate.Clamp);
        ctx.PSSetSampler(1, _shadows.ComparisonSampler);
        _litSrvs[0] = _targetSrvs[0];
        _litSrvs[1] = _targetSrvs[1];
        _litSrvs[2] = _targetSrvs[2];
        _litSrvs[3] = _targetSrvs[3];
        _litSrvs[4] = _depthSrv!;
        _litSrvs[5] = _lightRenderer.LocalLightSrv;
        _litSrvs[6] = _selectionMaskSrv!;
        _litSrvs[7] = _targetSrvs[4];
        _litSrvs[8] = _targetSrvs[5];
        _litSrvs[9] = _preintegratedGfSrv;
        _litSrvs[10] = _ltcMatSrv;
        _litSrvs[11] = _ltcAmpSrv;
        _litSrvs[12] = skyCubeSrv ?? _sharedTemplate.DefaultCube;
        _litSrvs[13] = _shadows.Srv!;
        _litSrvs[14] = _blackSrv!;
        _litSrvs[15] = _zeroExposureSrv!;
        _litSrvs[16] = _tileLightSrv ?? _zeroExposureSrv!;
        _litSrvs[17] = useSsao && _ssaoBlurSrv is not null ? _ssaoBlurSrv : _whiteSrv!;
        ctx.PSSetShaderResources(0, _litSrvs);
        ctx.Draw(3, 0);
        ctx.PSSetShaderResources(0, _null18Srvs);

        if (canWriteQuery) ctx.End(writeQuery.Ts[4]); // After LightCull + DeferredLit
        var tAfterLighting = Stopwatch.GetTimestamp();
        timings.CpuLightCullAndLightingMs = ToMs(tAfterLighting - tAfterSsao);

        if (mode != ENativePreviewMode.Emissive)
        {
            ctx.CopyResource(_targets[0], _targets[6]);
        }

        // Conditional SceneColorCopy / SceneDepthCopy based on shader resource declarations
        var needSceneColor = false;
        var needSceneDepth = false;
        for (var i = 0; i < _translucentScratch.Count; i++)
        {
            var p = _translucentScratch[i].Pipeline;
            needSceneColor |= p.NeedsSceneColor;
            needSceneDepth |= p.NeedsSceneDepth;
            if (needSceneColor && needSceneDepth) break;
        }
        if (niagaraMeshes is { Count: > 0 } && !(needSceneColor && needSceneDepth))
        {
            for (var i = 0; i < niagaraMeshes.Count; i++)
            {
                if (niagaraMeshes[i].MaterialPipeline is { } mp)
                {
                    needSceneColor |= mp.NeedsSceneColor;
                    needSceneDepth |= mp.NeedsSceneDepth;
                    if (needSceneColor && needSceneDepth) break;
                }
            }
        }
        if (niagaraSprites is { Count: > 0 } && !(needSceneColor && needSceneDepth))
        {
            for (var i = 0; i < niagaraSprites.Count; i++)
            {
                if (niagaraSprites[i].MaterialPipeline is { } sp)
                {
                    needSceneColor |= sp.NeedsSceneColor;
                    needSceneDepth |= sp.NeedsSceneDepth;
                    if (needSceneColor && needSceneDepth) break;
                }
            }
        }

        var hasNiagara = (niagaraMeshes?.Count ?? 0) > 0 || (niagaraSprites?.Count ?? 0) > 0;
        if (_translucentScratch.Count > 0 || hasNiagara)
        {
            if (needSceneColor)
                ctx.CopyResource(_sceneColorCopyTex!, _targets[6]);
            if (needSceneDepth)
                ctx.CopyResource(_sceneDepthCopyTex!, _depthTexture!);

            view[135] = new Vector4(1f, 1f, 1f, 1f);
            view[145] = new Vector4(1f, 0.55f, 1f, 1f);
            UploadSpan(_view, view.AsSpan());
        }

        // ---------------------------------------------------------------- Translucent Pass (Sorted + Batch Instanced)
        if (_translucentScratch.Count > 0)
        {
            ctx.OMSetRenderTargets(_rtvs[0], _dsv);
            ctx.OMSetDepthStencilState(_depthRead);

            NativeMaterialPipeline? currentPipeline = null;
            NativeMesh? currentMesh = null;
            var currentVolumeMode = false;
            var baseOffset = _opaqueScratch.Count;

            var i = 0;
            while (i < _translucentScratch.Count)
            {
                var first = _translucentScratch[i];
                var batchCount = 1;
                while (i + batchCount < _translucentScratch.Count &&
                       ReferenceEquals(_translucentScratch[i + batchCount].Pipeline, first.Pipeline) &&
                       ReferenceEquals(_translucentScratch[i + batchCount].Mesh, first.Mesh) &&
                       _translucentScratch[i + batchCount].Section.FirstIndex == first.Section.FirstIndex &&
                       _translucentScratch[i + batchCount].Section.IndexCount == first.Section.IndexCount &&
                       _translucentScratch[i + batchCount].Mirrored == first.Mirrored)
                {
                    batchCount++;
                }

                if (first.Pipeline.IsVolumeDomain != currentVolumeMode)
                {
                    currentVolumeMode = first.Pipeline.IsVolumeDomain;
                    view[145] = new Vector4(1f, currentVolumeMode ? 0f : 0.55f, 1f, 1f);
                    UploadSpan(_view, view.AsSpan());
                }

                if (!ReferenceEquals(first.Pipeline, currentPipeline))
                {
                    currentPipeline = first.Pipeline;
                    currentMesh = null;
                    first.Pipeline.BindMaterial(ctx, shared);
                    ctx.OMSetBlendState(first.Pipeline.Blend);
                }
                if (!ReferenceEquals(first.Mesh, currentMesh))
                {
                    currentMesh = first.Mesh;
                    first.Pipeline.BindMesh(ctx, shared, first.Mesh);
                    ctx.IASetIndexBuffer(first.Mesh.Indices, Format.R32_UInt, 0);
                }

                _vbPair[0] = first.Mesh.Positions;
                _vbPair[1] = _primitiveIds;
                _vbOffsets[0] = 0u;
                _vbOffsets[1] = (uint) ((baseOffset + i) * 4);
                ctx.IASetVertexBuffers(0, _vbPair, _vbStrides, _vbOffsets);
                ctx.RSSetState(SelectRasterState(first.Pipeline.TwoSided, first.Mirrored, isWireframe));
                ctx.DrawIndexedInstanced((uint) first.Section.IndexCount, (uint) batchCount, (uint) first.Section.FirstIndex, 0, 0);
                DrawCalls++;
                i += batchCount;
            }
            ctx.OMSetBlendState(null);
        }

        if (niagaraMeshes is { Count: > 0 })
        {
            var culledMeshes = new List<NativeNiagaraMeshBatch>();
            foreach (var b in niagaraMeshes)
            {
                if (InFrustum(planes, b.WorldMatrix.Translation, 5000f))
                    culledMeshes.Add(b);
            }
            if (culledMeshes.Count > 0)
            {
                ctx.OMSetRenderTargets(_rtvs[0], _dsv);
                ctx.OMSetDepthStencilState(_depthRead);
                var dc = DrawCalls;
                _niagaraRenderer.RenderMeshParticles(ctx, worldToClip, time, culledMeshes, shared, _sceneDepthCopySrv ?? _depthSrv!, _cullNone, _cullBack, ref dc);
                DrawCalls = dc;
            }
        }

        if (niagaraSprites is { Count: > 0 })
        {
            var culledSprites = new List<NativeNiagaraSpriteBatch>();
            foreach (var b in niagaraSprites)
            {
                var radius = Math.Max(b.Desc.SpawnRadius, 500f);
                if (InFrustum(planes, b.WorldMatrix.Translation, radius))
                    culledSprites.Add(b);
            }
            if (culledSprites.Count > 0)
            {
                ctx.OMSetRenderTargets(_rtvs[0], _dsv);
                ctx.OMSetDepthStencilState(_depthRead);
                var dc = DrawCalls;
                _niagaraRenderer.RenderSprites(ctx, worldToClip, camRight, camUp, time, culledSprites, shared, _sceneDepthCopySrv ?? _depthSrv!, _cullNone, _cullBack, ref dc);
                DrawCalls = dc;
            }
        }

        if (editorBillboards is { Count: > 0 })
        {
            ctx.OMSetRenderTargets(_rtvs[0], _dsv);
            _gizmoRenderer.Render(ctx, worldToClip, cameraPosition, camRight, camUp, editorBillboards, _cullNone);
        }

        if (hasSelection)
        {
            ctx.OMSetRenderTargets(_selectionMaskRtv!, null);
            ctx.OMSetDepthStencilState(null);
            ctx.OMSetBlendState(null);
            ctx.RSSetState(_cullNone);
            ctx.IASetInputLayout(_selectionInputLayout);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.VSSetShader(_selectionVs);
            ctx.PSSetShader(_selectionPs);
            ctx.VSSetConstantBuffer(0, _selectionCb);

            for (var s = 0; s < _selectedScratch.Count; s++)
            {
                var (l2w, selLod) = _selectedScratch[s];
                var localToClip = l2w * worldToClipUnjittered;
                WriteMatrix(_selCbScratch, 0, localToClip);
                UploadSpan(_selectionCb, _selCbScratch.AsSpan());

                ctx.IASetVertexBuffer(0, selLod.Positions, 12u, 0u);
                ctx.IASetIndexBuffer(selLod.Indices, Format.R32_UInt, 0);
                for (var secIdx = 0; secIdx < selLod.Sections.Count; secIdx++)
                {
                    var section = selLod.Sections[secIdx];
                    ctx.DrawIndexed((uint) section.IndexCount, (uint) section.FirstIndex, 0);
                }
            }
        }

        if (canWriteQuery) ctx.End(writeQuery.Ts[5]); // After Translucency + Niagara + Selection
        var tAfterTranslucency = Stopwatch.GetTimestamp();
        timings.CpuTranslucencyMs = ToMs(tAfterTranslucency - tAfterLighting);

        if (measure && _frame % 20 == 0 && _translucentScratch.Count > 0)
            MeasureOutputs();

        // ---------------------------------------------------------------- Post processing (TAA -> Eye Adaptation -> Bloom)
        var autoExposure = features.AutoExposure && mode is ENativePreviewMode.Lit or ENativePreviewMode.Emissive && !isStudioPreview;
        var bloomOn = features.Bloom && mode is ENativePreviewMode.Lit or ENativePreviewMode.Emissive;
        if (_post is null) { autoExposure = false; bloomOn = false; }
        var postSettings = new NativePostProcess.ExposureSettings
        {
            MinEV100 = postProcess?.AutoExposureMinEV100 ?? features.AutoExposureMinEV100,
            MaxEV100 = postProcess?.AutoExposureMaxEV100 ?? features.AutoExposureMaxEV100,
            ExposureBias = (postProcess?.AutoExposureBias ?? 0f) + features.ExposureBias,
            SpeedUp = features.AutoExposureSpeedUp,
            SpeedDown = features.AutoExposureSpeedDown
        };
        var bloomIntensity = postProcess?.BloomIntensity ?? features.BloomIntensity;
        var post = new NativePostProcess.Result(_targetSrvs[0], null);
        if (_post is not null)
        {
            _post.Settings = postSettings;
            post = _post.Execute(ctx, _targetSrvs[0], _depthSrv!, width, height, clipToTranslatedWorld, worldToClipUnjittered, cameraPosition, delta,
                temporalAA, autoExposure, bloomOn);
        }
        _postParamsScratch[0] = new Vector4(autoExposure ? 1f : 0f, post.Bloom is not null ? bloomIntensity : 0f, mode is ENativePreviewMode.Lit or ENativePreviewMode.Emissive ? features.VignetteIntensity : 0f, 0f);
        UploadSpan(_postParams!, _postParamsScratch.AsSpan());

        ctx.ClearState();
        ctx.OMSetRenderTargets(_outputRtv!, null);
        ctx.RSSetViewport(0, 0, width, height);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_compositeVs);
        ctx.PSSetShader(_tonemapPs);
        ctx.PSSetConstantBuffer(0, _compositeParams);
        ctx.PSSetConstantBuffer(2, _postParams!);
        ctx.PSSetSampler(0, _sharedTemplate.Clamp);
        _tonemapSrvs[0] = post.SceneColor;
        _tonemapSrvs[1] = _targetSrvs[1];
        _tonemapSrvs[2] = _targetSrvs[2];
        _tonemapSrvs[3] = _targetSrvs[3];
        _tonemapSrvs[4] = _depthSrv!;
        _tonemapSrvs[5] = _lightRenderer.LocalLightSrv;
        _tonemapSrvs[6] = _selectionMaskSrv!;
        _tonemapSrvs[7] = _targetSrvs[4];
        _tonemapSrvs[8] = _targetSrvs[5];
        _tonemapSrvs[9] = _preintegratedGfSrv;
        _tonemapSrvs[10] = _ltcMatSrv;
        _tonemapSrvs[11] = _ltcAmpSrv;
        _tonemapSrvs[12] = skyCubeSrv ?? _sharedTemplate.DefaultCube;
        _tonemapSrvs[13] = _shadows.Srv!;
        _tonemapSrvs[14] = post.Bloom ?? _blackSrv!;
        _tonemapSrvs[15] = autoExposure && _post is not null ? _post.ExposureSrv : _zeroExposureSrv!;
        ctx.PSSetShaderResources(0, _tonemapSrvs);
        ctx.Draw(3, 0);
        ctx.ClearState();

        if (canWriteQuery)
        {
            ctx.End(writeQuery.Ts[6]); // After PostProcess + Tonemap
            ctx.End(writeQuery.Disjoint);
            writeQuery.Issued = true;
            _queryWriteIdx = (_queryWriteIdx + 1) % QueryRingSize;
        }

        var tFrameEnd = Stopwatch.GetTimestamp();
        timings.CpuPostProcessMs = ToMs(tFrameEnd - tAfterTranslucency);
        timings.FrameCpuMs = ToMs(tFrameEnd - tFrameStart);
        _lastTimings = timings;

        _frameMsRing[_frameMsCursor] = timings.FrameCpuMs;
        _frameMsCursor = (_frameMsCursor + 1) % _frameMsRing.Length;
        if (_frameMsCount < _frameMsRing.Length) _frameMsCount++;
        var sumMs = 0.0;
        for (var k = 0; k < _frameMsCount; k++) sumMs += _frameMsRing[k];
        RollingFrameMs = sumMs / _frameMsCount;
        RollingFps = RollingFrameMs > 1e-4 ? 1000.0 / RollingFrameMs : 0.0;

        if (!ReadbackEnabled)
            return [];

        var write = _staging[_readbackFrame % _staging.Length];
        ctx.CopyResource(write, _output!);
        var read = _readbackFrame >= 2 ? _staging[(_readbackFrame - 2) % _staging.Length] : write;
        _readbackFrame++;

        var result = new byte[width * height * 4];
        var mapped = ctx.Map(read, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(mapped.DataPointer + (nint) (y * mapped.RowPitch), result, y * width * 4, width * 4);
        }
        finally
        {
            ctx.Unmap(read, 0);
        }
        return result;
    }

    private void RenderShadowDepths(ID3D11DeviceContext ctx, NativeSharedResources shared, Vector4[] cameraView, Vector3 cameraPositionForTranslation,
        List<ShadowDrawCmd> shadowDraws, IReadOnlyList<NativeLandscapeDrawItem>? landscapes, Vector3 cameraPosition, float shadowDistance, uint dirtyCascadeMask)
    {
        var shadows = _shadows!;
        var res = shadows.Resolution;
        cameraView.AsSpan().CopyTo(_cascadeViewScratch);
        var cascadeView = _cascadeViewScratch;

        for (var c = 0; c < shadows.CascadeCount; c++)
        {
            if ((dirtyCascadeMask & (1u << c)) == 0u)
                continue;

            var lightViewProj = shadows.WorldToShadowClip[c];
            WriteMatrix(cascadeView, ViewTranslatedWorldToClip, Matrix4x4.CreateTranslation(cameraPositionForTranslation) * lightViewProj);
            WriteMatrix(cascadeView, ViewWorldToClip, lightViewProj);
            UploadSpan(_view, cascadeView.AsSpan());

            var dsv = shadows.CascadeDsv(c);
            ctx.ClearDepthStencilView(dsv, DepthStencilClearFlags.Depth, 1f, 0);
            ctx.OMSetRenderTargets(_emptyRtvs, dsv);
            ctx.RSSetViewport(0, 0, res, res);
            ctx.OMSetDepthStencilState(shadows.DepthState);
            ctx.OMSetBlendState(null);
            ctx.RSSetState(shadows.Raster);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

            var texelWorld = 2f * shadows.CascadeSpheres[c].W / res;
            var minTexels = c switch { 0 => 1.0f, 1 => 1.5f, 2 => 2.5f, _ => 4.0f };
            var minRadius = texelWorld * minTexels;

            _cascadeShadowScratch.Clear();
            for (var k = 0; k < shadowDraws.Count; k++)
            {
                var draw = shadowDraws[k];
                var center = _primitiveBoundsCache[draw.Primitive];
                if (center.W < minRadius)
                    continue;
                if (!shadows.CasterAffectsCascade(c, new Vector3(center.X, center.Y, center.Z), center.W))
                    continue;
                _cascadeShadowScratch.Add(draw);
            }

            if (_cascadeShadowScratch.Count > 0)
            {
                EnsureShadowIdCapacity(_cascadeShadowScratch.Count);
                if (_shadowPrimIdsScratch.Length < _cascadeShadowScratch.Count)
                    _shadowPrimIdsScratch = new uint[Math.Max(_cascadeShadowScratch.Count, Math.Max(_shadowPrimIdsScratch.Length * 2, 256))];

                for (var k = 0; k < _cascadeShadowScratch.Count; k++)
                    _shadowPrimIdsScratch[k] = (uint) _cascadeShadowScratch[k].Primitive;
                UploadSpan(_shadowPrimitiveIds, _shadowPrimIdsScratch.AsSpan(0, _cascadeShadowScratch.Count));

                NativeMaterialPipeline? currentPipeline = null;
                NativeMesh? currentMesh = null;
                var idx = 0;
                while (idx < _cascadeShadowScratch.Count)
                {
                    var first = _cascadeShadowScratch[idx];
                    var batchCount = 1;
                    while (idx + batchCount < _cascadeShadowScratch.Count &&
                           ReferenceEquals(_cascadeShadowScratch[idx + batchCount].Pipeline, first.Pipeline) &&
                           ReferenceEquals(_cascadeShadowScratch[idx + batchCount].Mesh, first.Mesh) &&
                           _cascadeShadowScratch[idx + batchCount].Section.FirstIndex == first.Section.FirstIndex &&
                           _cascadeShadowScratch[idx + batchCount].Section.IndexCount == first.Section.IndexCount)
                    {
                        batchCount++;
                    }

                    if (!ReferenceEquals(first.Pipeline, currentPipeline))
                    {
                        currentPipeline = first.Pipeline;
                        currentMesh = null;
                        if (first.Pipeline.HasDepthOnlyDiscard)
                        {
                            first.Pipeline.BindDepthOnlyMaterial(ctx, shared);
                        }
                        else
                        {
                            first.Pipeline.BindMaterial(ctx, shared);
                            if (first.Pipeline.BlendMode != ENativeBlendMode.Masked)
                                ctx.PSSetShader(null);
                        }
                        ctx.OMSetBlendState(null);
                    }
                    if (!ReferenceEquals(first.Mesh, currentMesh))
                    {
                        currentMesh = first.Mesh;
                        if (first.Pipeline.HasDepthOnlyDiscard)
                            first.Pipeline.BindDepthOnlyMesh(ctx, shared, first.Mesh);
                        else
                            first.Pipeline.BindMesh(ctx, shared, first.Mesh);
                        ctx.IASetIndexBuffer(first.Mesh.Indices, Format.R32_UInt, 0);
                    }

                    _vbPair[0] = first.Mesh.Positions;
                    _vbPair[1] = _shadowPrimitiveIds;
                    _vbOffsets[0] = 0u;
                    _vbOffsets[1] = (uint) (idx * 4);
                    ctx.IASetVertexBuffers(0, _vbPair, _vbStrides, _vbOffsets);
                    ctx.DrawIndexedInstanced((uint) first.Section.IndexCount, (uint) batchCount, (uint) first.Section.FirstIndex, 0, 0);
                    DrawCalls++;
                    idx += batchCount;
                }
            }

            if (landscapes is { Count: > 0 })
            {
                _landscapeCastersScratch.Clear();
                for (var li = 0; li < landscapes.Count; li++)
                {
                    var l = landscapes[li];
                    var lc = Vector3.Transform(l.Tile.BoundsCenter, l.LocalToWorld);
                    var ls = MathF.Max(new Vector3(l.LocalToWorld.M11, l.LocalToWorld.M12, l.LocalToWorld.M13).Length(), 1f);
                    var lr = l.Tile.BoundsRadius * ls;
                    if (Vector3.Distance(lc, cameraPosition) > shadowDistance + lr)
                        continue;
                    if (shadows.CasterAffectsCascade(c, lc, lr))
                        _landscapeCastersScratch.Add(l with { Distance = Vector3.Distance(lc, cameraPosition) });
                }
                if (_landscapeCastersScratch.Count > 0)
                {
                    var dc = DrawCalls;
                    _landscapeRenderer.Render(ctx, lightViewProj, _landscapeCastersScratch, shadows.Raster, ref dc);
                    DrawCalls = dc;
                    ctx.OMSetDepthStencilState(shadows.DepthState);
                }
            }
        }
        ctx.OMSetRenderTargets(_emptyRtvs, null);
        ctx.RSSetState(null);
    }

    private static void FillFrustumPlanes(Matrix4x4 m, Vector4[] planes)
    {
        var c1 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c2 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c3 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c4 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        planes[0] = c4 + c1;
        planes[1] = c4 - c1;
        planes[2] = c4 + c2;
        planes[3] = c4 - c2;
        planes[4] = c4 - c3;
        for (var i = 0; i < 5; i++)
        {
            var length = new Vector3(planes[i].X, planes[i].Y, planes[i].Z).Length();
            if (length > 0)
                planes[i] /= length;
        }
    }

    private static bool IsInsideLoadedWpCell(Vector3 centerCm, IReadOnlyList<(Vector3 MinCm, Vector3 MaxCm)>? bounds)
    {
        if (bounds is null || bounds.Count == 0)
            return false;
        for (var i = 0; i < bounds.Count; i++)
        {
            var (min, max) = bounds[i];
            if (centerCm.X >= min.X && centerCm.X <= max.X &&
                centerCm.Y >= min.Y && centerCm.Y <= max.Y)
                return true;
        }
        return false;
    }

    private static bool InFrustum(Vector4[] planes, Vector3 center, float radius)
    {
        for (var i = 0; i < planes.Length; i++)
        {
            var p = planes[i];
            if (p.X * center.X + p.Y * center.Y + p.Z * center.Z + p.W < -radius)
                return false;
        }
        return true;
    }

    private static bool IsWhollyInFrustum(Vector4[] planes, Vector3 center, float radius)
    {
        for (var i = 0; i < planes.Length; i++)
        {
            var p = planes[i];
            if (p.X * center.X + p.Y * center.Y + p.Z * center.Z + p.W < radius)
                return false;
        }
        return true;
    }

    private void MeasureOutputs()
    {
        var ctx = _context;
        ctx.CopyResource(_statsEmissive!, _targets[0]);
        ctx.CopyResource(_statsNormal!, _targets[1]);
        ctx.CopyResource(_statsBase!, _targets[3]);
        var e = ctx.Map(_statsEmissive!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var n = ctx.Map(_statsNormal!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var b = ctx.Map(_statsBase!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        double emissive = 0, baseColor = 0;
        var covered = 0;
        var translucentEmissive = 0;
        unsafe
        {
            for (var y = 0; y < _height; y += 4)
                for (var x = 0; x < _width; x += 4)
                {
                    var half = (Half*) (e.DataPointer + (nint) (y * e.RowPitch + x * 8));
                    var r = (float) half[0];
                    var g = (float) half[1];
                    var bl = (float) half[2];
                    var emLum = float.IsFinite(r) && float.IsFinite(g) && float.IsFinite(bl) ? 0.2126 * r + 0.7152 * g + 0.0722 * bl : 0;

                    var normal = (Half*) (n.DataPointer + (nint) (y * n.RowPitch + x * 8));
                    var nx = (float) normal[0];
                    var ny = (float) normal[1];
                    var nz = (float) normal[2];
                    if (MathF.Abs(nx) < 1e-4f && MathF.Abs(ny) < 1e-4f && MathF.Abs(nz) < 1e-4f)
                    {
                        if (emLum > 0.002)
                        { translucentEmissive++; emissive += emLum; }
                        continue;
                    }
                    covered++;
                    emissive += emLum;
                    var color = (Half*) (b.DataPointer + (nint) (y * b.RowPitch + x * 8));
                    var cr = (float) color[0];
                    var cg = (float) color[1];
                    var cb = (float) color[2];
                    if (float.IsFinite(cr) && float.IsFinite(cg) && float.IsFinite(cb))
                        baseColor += 0.2126 * cr + 0.7152 * cg + 0.0722 * cb;
                }
        }
        ctx.Unmap(_statsEmissive!, 0);
        ctx.Unmap(_statsNormal!, 0);
        ctx.Unmap(_statsBase!, 0);

        var totalPixels = covered + translucentEmissive;
        if (totalPixels == 0)
        { OutputSummary = "no visible pixels (translucent, or fully masked out)"; return; }
        var em = emissive / totalPixels;
        var bc = covered > 0 ? baseColor / covered : 0;
        OutputSummary = (em > 0.002, bc > 0.002) switch
        {
            (true, true) => "base color + emissive",
            (true, false) => covered == 0 ? "translucent emissive" : "emissive only",
            (false, true) => "base color only",
            _ => "black (no base color, no emissive)"
        } + $"  (emissive {em:0.###}, base {bc:0.###})";
    }

    private void UploadSpan<T>(ID3D11Buffer buffer, ReadOnlySpan<T> data) where T : unmanaged
    {
        var mapped = _context.Map(buffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bytes = MemoryMarshal.AsBytes(data);
            var length = Math.Min(bytes.Length, (int) buffer.Description.ByteWidth);
            unsafe
            { bytes[..length].CopyTo(new Span<byte>((void*) mapped.DataPointer, length)); }
        }
        finally
        {
            _context.Unmap(buffer, 0);
        }
    }

    private ID3D11RasterizerState SelectRasterState(bool twoSided, bool mirrored, bool wireframe) =>
        wireframe
            ? (twoSided ? _wireCullNone : (mirrored ^ FlipCulling) ? _wireCullFront : _wireCullBack)
            : (twoSided ? _cullNone : (mirrored ^ FlipCulling) ? _cullFront : _cullBack);

    private static void WriteMatrix(Vector4[] registers, int index, Matrix4x4 m)
    {
        registers[index] = new Vector4(m.M11, m.M12, m.M13, m.M14);
        registers[index + 1] = new Vector4(m.M21, m.M22, m.M23, m.M24);
        registers[index + 2] = new Vector4(m.M31, m.M32, m.M33, m.M34);
        registers[index + 3] = new Vector4(m.M41, m.M42, m.M43, m.M44);
    }

    private void DisposeTargets()
    {
        foreach (var r in _targets)
            r.Dispose();
        foreach (var r in _rtvs)
            r.Dispose();
        foreach (var r in _targetSrvs)
            r.Dispose();
        _depthSrv?.Dispose();
        _depthTexture?.Dispose();
        _dsv?.Dispose();
        _sceneColorCopySrv?.Dispose();
        _sceneColorCopyTex?.Dispose();
        _sceneDepthCopySrv?.Dispose();
        _sceneDepthCopyTex?.Dispose();
        _ssaoHalfSrv?.Dispose();
        _ssaoHalfRtv?.Dispose();
        _ssaoHalfTex?.Dispose();
        _ssaoBlurSrv?.Dispose();
        _ssaoBlurRtv?.Dispose();
        _ssaoBlurTex?.Dispose();
        _tileLightSrv?.Dispose();
        _tileLightUav?.Dispose();
        _tileLightBuffer?.Dispose();
        _selectionMaskSrv?.Dispose();
        _selectionMaskRtv?.Dispose();
        _selectionMaskTex?.Dispose();
        _outputSrv?.Dispose();
        _outputSrv = null;
        _output?.Dispose();
        _outputRtv?.Dispose();
        foreach (var r in _staging)
            r.Dispose();
        _staging = [];
        _statsEmissive?.Dispose();
        _statsNormal?.Dispose();
        _statsBase?.Dispose();
        _targets = [];
        _rtvs = [];
        _targetSrvs = [];
        _output = null;
        _depthSrv = null;
        _sceneColorCopySrv = null;
        _sceneDepthCopySrv = null;
        _ssaoHalfSrv = null;
        _ssaoHalfRtv = null;
        _ssaoHalfTex = null;
        _ssaoBlurSrv = null;
        _ssaoBlurRtv = null;
        _ssaoBlurTex = null;
        _tileLightSrv = null;
        _tileLightUav = null;
        _tileLightBuffer = null;
        _selectionMaskSrv = null;
        _selectionMaskRtv = null;
        _selectionMaskTex = null;
    }

    public void Dispose()
    {
        DisposeTargets();
        _primitiveSrv?.Dispose();
        _primitiveBuffer?.Dispose();
        _primitiveIds?.Dispose();
        _shadowPrimitiveIds?.Dispose();
        for (var i = _owned.Count - 1; i >= 0; i--)
            _owned[i].Dispose();
    }
}
