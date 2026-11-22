using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using SkiaSharp;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UExplorer.Rendering;

public enum ENativeGizmoIconKind
{
    PointLight,
    SpotLight,
    RectLight,
    DirectionalLight,
    SkyLight,
    Audio,
    Decal,
    NiagaraVfx
}

public readonly record struct NativeBillboardDrawItem(
    ENativeGizmoIconKind Kind,
    Vector3 WorldPositionCm,
    Vector3Tint ColorTint,
    bool IsSelected,
    float AttenuationRadiusCm = 0f,
    Vector3 ForwardUnreal = default,
    Vector3 RightUnreal = default,
    Vector3 UpUnreal = default,
    float SpotOuterAngleDeg = 0f,
    float SpotInnerAngleDeg = 0f,
    float RectWidthCm = 0f,
    float RectHeightCm = 0f);

public readonly record struct Vector3Tint(float R, float G, float B)
{
    public static implicit operator Vector3Tint(Vector3 v) => new(v.X, v.Y, v.Z);
    public Vector3 ToVector3() => new(R, G, B);
}

public sealed class NativeEditorGizmoRenderer : IDisposable
{
    private const string BillboardAndWireHlsl = """
        cbuffer GizmoCb : register(b0)
        {
            float4 WorldToClipRow0;
            float4 WorldToClipRow1;
            float4 WorldToClipRow2;
            float4 WorldToClipRow3;
            float4 CameraRightAndSize; // xyz = CamRight, w = WorldHalfSizeCm
            float4 CameraUpAndSelected;// xyz = CamUp, w = IsSelected (1 or 0)
            float4 CenterPosAndTintR;  // xyz = WorldCenterCm, w = Tint.r
            float4 TintGBAndUnused;    // x = Tint.g, y = Tint.b, z = IsWirePass, w = Unused
        };

        Texture2D<float4> IconTex : register(t0);
        SamplerState LinearClamp  : register(s0);

        struct PSIn
        {
            float4 Pos   : SV_Position;
            float2 UV    : TEXCOORD0;
            float4 Color : TEXCOORD1;
        };

        float4 TransformClip(float3 worldPos)
        {
            return worldPos.xxxx * WorldToClipRow0
                 + worldPos.yyyy * WorldToClipRow1
                 + worldPos.zzzz * WorldToClipRow2
                 + WorldToClipRow3;
        }

        PSIn VSMainBillboard(uint vid : SV_VertexID)
        {
            PSIn o;
            float2 corner = float2((vid == 1 || vid == 2 || vid == 4) ? 1.0 : -1.0,
                                   (vid == 2 || vid == 4 || vid == 5) ? 1.0 : -1.0);
            float2 uv = corner * float2(0.5, -0.5) + 0.5;

            float halfSize = CameraRightAndSize.w;
            float3 worldPos = CenterPosAndTintR.xyz
                            + CameraRightAndSize.xyz * (corner.x * halfSize)
                            + CameraUpAndSelected.xyz * (corner.y * halfSize);

            o.Pos = TransformClip(worldPos);
            o.UV = uv;
            o.Color = float4(CenterPosAndTintR.w, TintGBAndUnused.x, TintGBAndUnused.y, CameraUpAndSelected.w);
            return o;
        }

        struct WireVSIn
        {
            float3 Pos   : ATTRIBUTE0;
            float4 Color : ATTRIBUTE1;
        };

        PSIn VSMainWire(WireVSIn input)
        {
            PSIn o;
            o.Pos = TransformClip(input.Pos);
            o.UV = float2(0, 0);
            o.Color = input.Color;
            return o;
        }

        float4 PSMain(PSIn i) : SV_Target0
        {
            if (TintGBAndUnused.z > 0.5)
            {
                return i.Color;
            }

            float4 tex = IconTex.Sample(LinearClamp, i.UV);
            float isSelected = i.Color.a;

            if (isSelected > 0.5)
            {
                // Sample 4 neighbors in UV space to draw classic Unreal #FF6B00 selection outline around the sprite icon
                float2 stepUV = float2(1.0 / 64.0, 1.0 / 64.0);
                float nAlpha = IconTex.Sample(LinearClamp, i.UV + float2( stepUV.x, 0)).a
                             + IconTex.Sample(LinearClamp, i.UV + float2(-stepUV.x, 0)).a
                             + IconTex.Sample(LinearClamp, i.UV + float2(0,  stepUV.y)).a
                             + IconTex.Sample(LinearClamp, i.UV + float2(0, -stepUV.y)).a;

                float3 ueOrange = float3(0.925, 0.616, 0.114);
                if (tex.a < 0.15)
                {
                    float outline = saturate(nAlpha * 0.95);
                    if (outline < 0.05) discard;
                    return float4(ueOrange, outline);
                }

                float3 litRgb = lerp(tex.rgb * i.Color.rgb, ueOrange, 0.22);
                return float4(litRgb, tex.a);
            }

            if (tex.a < 0.1) discard;
            return float4(tex.rgb * i.Color.rgb, tex.a);
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct WireVertex(Vector3 pos, Vector4 color)
    {
        public Vector3 Position = pos;
        public Vector4 Color = color;
    }

    private readonly ID3D11Device _device;
    private readonly List<IDisposable> _owned = [];
    private readonly ID3D11VertexShader _vsBillboard;
    private readonly ID3D11VertexShader _vsWire;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11InputLayout _wireLayout;
    private readonly ID3D11Buffer _cb;
    private readonly ID3D11Buffer _wireVb;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11BlendState _alphaBlend;
    private readonly ID3D11DepthStencilState _depthTestNoWrite;
    private readonly Dictionary<ENativeGizmoIconKind, ID3D11ShaderResourceView> _icons = [];

    private const int MaxWireVertices = 16384;

    private T Own<T>(T r) where T : IDisposable { _owned.Add(r); return r; }

    public NativeEditorGizmoRenderer(ID3D11Device device)
    {
        _device = device;

        var vsBillBytes = Compiler.Compile(BillboardAndWireHlsl, "VSMainBillboard", "gizmo_bill_vs", "vs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();
        var vsWireBytes = Compiler.Compile(BillboardAndWireHlsl, "VSMainWire", "gizmo_wire_vs", "vs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();
        var psBytes = Compiler.Compile(BillboardAndWireHlsl, "PSMain", "gizmo_ps", "ps_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();

        _vsBillboard = Own(device.CreateVertexShader(vsBillBytes));
        _vsWire = Own(device.CreateVertexShader(vsWireBytes));
        _ps = Own(device.CreatePixelShader(psBytes));

        _wireLayout = Own(device.CreateInputLayout(
        [
            new InputElementDescription("ATTRIBUTE", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerVertexData, 0),
            new InputElementDescription("ATTRIBUTE", 1, Format.R32G32B32A32_Float, 12, 0, InputClassification.PerVertexData, 0)
        ], vsWireBytes));

        _cb = Own(device.CreateBuffer(16 * 8, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _wireVb = Own(device.CreateBuffer((uint) (MaxWireVertices * Marshal.SizeOf<WireVertex>()), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _sampler = Own(device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue)));
        _alphaBlend = Own(device.CreateBlendState(new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha)));
        _depthTestNoWrite = Own(device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.GreaterEqual)));

        LoadAllIcons();
    }

    private void LoadAllIcons()
    {
        _icons[ENativeGizmoIconKind.PointLight] = LoadPngOrProcedural("S_LightPoint.png", ENativeGizmoIconKind.PointLight);
        _icons[ENativeGizmoIconKind.SpotLight] = LoadPngOrProcedural("S_LightSpot.png", ENativeGizmoIconKind.SpotLight);
        _icons[ENativeGizmoIconKind.RectLight] = LoadPngOrProcedural("S_LightRect.png", ENativeGizmoIconKind.RectLight);
        _icons[ENativeGizmoIconKind.DirectionalLight] = LoadPngOrProcedural("S_LightDirectional.png", ENativeGizmoIconKind.DirectionalLight);
        _icons[ENativeGizmoIconKind.SkyLight] = LoadPngOrProcedural("SkyLight.png", ENativeGizmoIconKind.SkyLight);
        _icons[ENativeGizmoIconKind.Audio] = LoadPngOrProcedural("S_AudioComponent.png", ENativeGizmoIconKind.Audio);
        _icons[ENativeGizmoIconKind.Decal] = LoadPngOrProcedural("S_DecalActorIcon.png", ENativeGizmoIconKind.Decal);
        _icons[ENativeGizmoIconKind.NiagaraVfx] = LoadPngOrProcedural("S_Emitter.png", ENativeGizmoIconKind.NiagaraVfx);
    }

    private ID3D11ShaderResourceView LoadPngOrProcedural(string fileName, ENativeGizmoIconKind kind)
    {
        try
        {
            using var stream = TryFindResourceStream(fileName);
            if (stream is not null)
            {
                using var skBmp = SKBitmap.Decode(stream);
                if (skBmp is not null)
                {
                    using var rgbaBmp = skBmp.ColorType == SKColorType.Rgba8888
                        ? skBmp
                        : skBmp.Copy(SKColorType.Rgba8888);
                    var pixels = rgbaBmp.GetPixelSpan();
                    var tex = Own(_device.CreateTexture2D(pixels, Format.R8G8B8A8_UNorm, (uint) rgbaBmp.Width, (uint) rgbaBmp.Height));
                    return Own(_device.CreateShaderResourceView(tex));
                }
            }
        }
        catch
        {
        }

        // Procedural 64x64 UE5.2 crisp SkiaSharp fallback icon for Niagara / missing files
        const int sz = 64;
        using var surface = SKSurface.Create(new SKImageInfo(sz, sz, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        if (kind == ENativeGizmoIconKind.NiagaraVfx)
        {
            using var glowPaint = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(
                    new SKPoint(32, 32), 28,
                    [new SKColor(160, 235, 255, 220), new SKColor(40, 175, 255, 110), new SKColor(0, 112, 224, 0)],
                    [0f, 0.45f, 1f], SKShaderTileMode.Clamp)
            };
            canvas.DrawCircle(32, 32, 28, glowPaint);

            using var ringPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.6f,
                Color = new SKColor(95, 210, 255, 235)
            };
            canvas.Save();
            canvas.RotateDegrees(-28, 32, 32);
            canvas.DrawOval(32, 32, 22, 10, ringPaint);
            canvas.RotateDegrees(56, 32, 32);
            canvas.DrawOval(32, 32, 22, 10, ringPaint);
            canvas.Restore();

            using var starPath = new SKPath();
            starPath.MoveTo(32, 10);
            starPath.QuadTo(34, 30, 54, 32);
            starPath.QuadTo(34, 34, 32, 54);
            starPath.QuadTo(30, 34, 10, 32);
            starPath.QuadTo(30, 30, 32, 10);
            starPath.Close();

            using var starPaint = new SKPaint { IsAntialias = true, Color = new SKColor(235, 250, 255, 255) };
            canvas.DrawPath(starPath, starPaint);

            using var dotPaint = new SKPaint { IsAntialias = true, Color = new SKColor(120, 230, 255, 255) };
            canvas.DrawCircle(16, 17, 2.8f, dotPaint);
            canvas.DrawCircle(49, 19, 2.4f, dotPaint);
            canvas.DrawCircle(47, 48, 3.0f, dotPaint);
            canvas.DrawCircle(15, 45, 2.2f, dotPaint);
        }
        else
        {
            using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(255, 235, 170, 235) };
            canvas.DrawCircle(32, 32, 16, paint);
        }

        using var pix = surface.PeekPixels();
        var fallbackTex = Own(_device.CreateTexture2D(pix.GetPixelSpan(), Format.R8G8B8A8_UNorm, sz, sz));
        return Own(_device.CreateShaderResourceView(fallbackTex));
    }

    private static Stream? TryFindResourceStream(string fileName)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidatePaths =
        [
            Path.Combine(baseDir, "Resources", "UE5", fileName),
            Path.Combine(baseDir, "Resources", fileName),
            Path.Combine(baseDir, fileName),
        ];

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
                return File.OpenRead(path);
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var match = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    return asm.GetManifestResourceStream(match);
            }
            catch
            {
            }
        }

        return null;
    }

    public unsafe void Render(
        ID3D11DeviceContext ctx,
        Matrix4x4 worldToClip,
        Vector3 cameraUnrealCm,
        Vector3 camRight,
        Vector3 camUp,
        IReadOnlyList<NativeBillboardDrawItem> billboards,
        ID3D11RasterizerState cullNone)
    {
        if (billboards.Count == 0)
            return;

        ctx.RSSetState(cullNone);
        ctx.OMSetBlendState(_alphaBlend);
        ctx.OMSetDepthStencilState(_depthTestNoWrite);
        ctx.VSSetConstantBuffer(0, _cb);
        ctx.PSSetConstantBuffer(0, _cb);
        ctx.PSSetSampler(0, _sampler);

        var cbData = new Vector4[8];
        WriteMatrix(cbData, 0, worldToClip);

        // 1. Render 3-axis wireframe attenuation spheres / cones / rect frames for selected Light & Audio components
        var wireVerts = new List<WireVertex>(512);
        foreach (var item in billboards)
        {
            if (!item.IsSelected)
                continue;

            if (item.AttenuationRadiusCm > 1f)
            {
                // Classic Unreal Light / Audio 3-axis attenuation rings (XY, XZ, YZ)
                var ringColor = item.Kind == ENativeGizmoIconKind.Audio
                    ? new Vector4(0.35f, 0.85f, 1.0f, 0.90f)
                    : new Vector4(1.0f, 0.72f, 0.15f, 0.92f);

                AddCircleRings3Axis(wireVerts, item.WorldPositionCm, item.AttenuationRadiusCm, ringColor);
            }

            if (item.Kind == ENativeGizmoIconKind.SpotLight && item.AttenuationRadiusCm > 1f && item.SpotOuterAngleDeg > 0.5f)
            {
                AddSpotLightConeWireframe(
                    wireVerts,
                    item.WorldPositionCm,
                    item.ForwardUnreal,
                    item.RightUnreal,
                    item.UpUnreal,
                    item.AttenuationRadiusCm,
                    item.SpotOuterAngleDeg,
                    item.SpotInnerAngleDeg);
            }
            else if (item.Kind == ENativeGizmoIconKind.RectLight && (item.RectWidthCm > 1f || item.RectHeightCm > 1f))
            {
                AddRectLightWireframe(
                    wireVerts,
                    item.WorldPositionCm,
                    item.ForwardUnreal,
                    item.RightUnreal,
                    item.UpUnreal,
                    MathF.Max(item.RectWidthCm, 10f),
                    MathF.Max(item.RectHeightCm, 10f));
            }
            else if (item.Kind == ENativeGizmoIconKind.DirectionalLight)
            {
                AddDirectionalLightArrow(wireVerts, item.WorldPositionCm, item.ForwardUnreal);
            }
        }

        if (wireVerts.Count > 0)
        {
            var vCount = Math.Min(wireVerts.Count, MaxWireVertices);
            var mappedVb = ctx.Map(_wireVb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var span = CollectionsMarshal.AsSpan(wireVerts)[..vCount];
                var bytes = MemoryMarshal.AsBytes(span);
                bytes.CopyTo(new Span<byte>((void*) mappedVb.DataPointer, bytes.Length));
            }
            finally
            {
                ctx.Unmap(_wireVb, 0);
            }

            cbData[7] = new Vector4(0f, 0f, 1f, 0f); // IsWirePass = 1
            UploadCb(ctx, cbData);

            ctx.IASetInputLayout(_wireLayout);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.LineList);
            ctx.IASetVertexBuffer(0, _wireVb, (uint) Marshal.SizeOf<WireVertex>(), 0);
            ctx.VSSetShader(_vsWire);
            ctx.PSSetShader(_ps);
            ctx.Draw((uint) vCount, 0);
        }

        // 2. Render camera-facing Sprite Icons for all Lights, Audio, Decals, and VFX
        ctx.IASetInputLayout(null);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vsBillboard);
        ctx.PSSetShader(_ps);

        foreach (var item in billboards)
        {
            var distCm = Vector3.Distance(item.WorldPositionCm, cameraUnrealCm);
            if (distCm > 15000f && !item.IsSelected)
                continue;
            if (distCm < 8f)
                continue; // Hide completely if camera is right inside the origin

            // Shrink faster as you get closer (< 4m) so sprites never block close-up VFX!
            var closeT = Math.Clamp(distCm / 400f, 0.03f, 1.0f);
            var worldHalfSizeCm = Math.Clamp(distCm * 0.014f * closeT, 0.4f, 65f);
            if (item.Kind == ENativeGizmoIconKind.NiagaraVfx)
                worldHalfSizeCm *= 0.65f; // Keep VFX icons extra compact
            if (item.IsSelected)
                worldHalfSizeCm *= 1.12f;

            var tint = item.ColorTint.ToVector3();
            var maxChannel = MathF.Max(tint.X, MathF.Max(tint.Y, tint.Z));
            if (maxChannel > 0.05f)
                tint = Vector3.Lerp(Vector3.One, tint / maxChannel, 0.65f);
            else
                tint = Vector3.One;

            cbData[4] = new Vector4(camRight, worldHalfSizeCm);
            cbData[5] = new Vector4(camUp, item.IsSelected ? 1f : 0f);
            cbData[6] = new Vector4(item.WorldPositionCm, tint.X);
            cbData[7] = new Vector4(tint.Y, tint.Z, 0f, 0f);
            UploadCb(ctx, cbData);

            if (_icons.TryGetValue(item.Kind, out var srv))
                ctx.PSSetShaderResource(0, srv);

            ctx.Draw(6, 0);
        }

        ctx.OMSetBlendState(null);
    }

    private static void AddCircleRings3Axis(List<WireVertex> verts, Vector3 center, float radius, Vector4 color)
    {
        const int segments = 64;
        for (var i = 0; i < segments; i++)
        {
            var a0 = (i / (float) segments) * MathF.Tau;
            var a1 = ((i + 1) / (float) segments) * MathF.Tau;
            var c0 = MathF.Cos(a0) * radius;
            var s0 = MathF.Sin(a0) * radius;
            var c1 = MathF.Cos(a1) * radius;
            var s1 = MathF.Sin(a1) * radius;

            // XY horizontal ring
            verts.Add(new WireVertex(center + new Vector3(c0, s0, 0f), color));
            verts.Add(new WireVertex(center + new Vector3(c1, s1, 0f), color));

            // XZ vertical ring
            verts.Add(new WireVertex(center + new Vector3(c0, 0f, s0), color));
            verts.Add(new WireVertex(center + new Vector3(c1, 0f, s1), color));

            // YZ vertical ring
            verts.Add(new WireVertex(center + new Vector3(0f, c0, s0), color));
            verts.Add(new WireVertex(center + new Vector3(0f, c1, s1), color));
        }
    }

    private static void AddSpotLightConeWireframe(
        List<WireVertex> verts,
        Vector3 origin,
        Vector3 forward,
        Vector3 right,
        Vector3 up,
        float length,
        float outerDeg,
        float innerDeg)
    {
        var fwd = forward.LengthSquared() > 1e-4f ? Vector3.Normalize(forward) : new Vector3(1, 0, 0);
        var rgt = right.LengthSquared() > 1e-4f ? Vector3.Normalize(right) : new Vector3(0, 1, 0);
        var u = up.LengthSquared() > 1e-4f ? Vector3.Normalize(up) : new Vector3(0, 0, 1);

        void DrawCone(float angleDeg, Vector4 col)
        {
            var rad = Math.Clamp(angleDeg, 1f, 89f) * (MathF.PI / 180f);
            var coneDist = length * MathF.Cos(rad);
            var coneRadius = length * MathF.Sin(rad);
            var diskCenter = origin + fwd * coneDist;

            const int segs = 36;
            for (var i = 0; i < segs; i++)
            {
                var a0 = (i / (float) segs) * MathF.Tau;
                var a1 = ((i + 1) / (float) segs) * MathF.Tau;
                var p0 = diskCenter + (rgt * MathF.Cos(a0) + u * MathF.Sin(a0)) * coneRadius;
                var p1 = diskCenter + (rgt * MathF.Cos(a1) + u * MathF.Sin(a1)) * coneRadius;
                verts.Add(new WireVertex(p0, col));
                verts.Add(new WireVertex(p1, col));

                if (i % 9 == 0)
                {
                    verts.Add(new WireVertex(origin, col));
                    verts.Add(new WireVertex(p0, col));
                }
            }
        }

        DrawCone(outerDeg, new Vector4(1.0f, 0.65f, 0.1f, 0.95f));
        if (innerDeg > 0.5f && innerDeg < outerDeg)
            DrawCone(innerDeg, new Vector4(0.65f, 0.88f, 1.0f, 0.85f));
    }

    private static void AddRectLightWireframe(
        List<WireVertex> verts,
        Vector3 origin,
        Vector3 forward,
        Vector3 right,
        Vector3 up,
        float width,
        float height)
    {
        var fwd = forward.LengthSquared() > 1e-4f ? Vector3.Normalize(forward) : new Vector3(1, 0, 0);
        var rgt = right.LengthSquared() > 1e-4f ? Vector3.Normalize(right) : new Vector3(0, 1, 0);
        var u = up.LengthSquared() > 1e-4f ? Vector3.Normalize(up) : new Vector3(0, 0, 1);

        var hw = width * 0.5f;
        var hh = height * 0.5f;
        var c0 = origin - rgt * hw - u * hh;
        var c1 = origin + rgt * hw - u * hh;
        var c2 = origin + rgt * hw + u * hh;
        var c3 = origin - rgt * hw + u * hh;

        var col = new Vector4(1.0f, 0.68f, 0.12f, 0.95f);
        verts.Add(new WireVertex(c0, col));
        verts.Add(new WireVertex(c1, col));
        verts.Add(new WireVertex(c1, col));
        verts.Add(new WireVertex(c2, col));
        verts.Add(new WireVertex(c2, col));
        verts.Add(new WireVertex(c3, col));
        verts.Add(new WireVertex(c3, col));
        verts.Add(new WireVertex(c0, col));

        // Direction indicator line
        verts.Add(new WireVertex(origin, col));
        verts.Add(new WireVertex(origin + fwd * MathF.Max(width, height), col));
    }

    private static void AddDirectionalLightArrow(List<WireVertex> verts, Vector3 origin, Vector3 forward)
    {
        var fwd = forward.LengthSquared() > 1e-4f ? Vector3.Normalize(forward) : new Vector3(0, 0, -1);
        var col = new Vector4(1.0f, 0.85f, 0.25f, 0.95f);
        var tip = origin + fwd * 180f;
        verts.Add(new WireVertex(origin, col));
        verts.Add(new WireVertex(tip, col));
    }

    private unsafe void UploadCb(ID3D11DeviceContext ctx, Vector4[] data)
    {
        var mapped = ctx.Map(_cb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bytes = MemoryMarshal.AsBytes(data.AsSpan());
            bytes.CopyTo(new Span<byte>((void*) mapped.DataPointer, bytes.Length));
        }
        finally
        {
            ctx.Unmap(_cb, 0);
        }
    }

    private static void WriteMatrix(Vector4[] dst, int offset, Matrix4x4 m)
    {
        dst[offset + 0] = new Vector4(m.M11, m.M12, m.M13, m.M14);
        dst[offset + 1] = new Vector4(m.M21, m.M22, m.M23, m.M24);
        dst[offset + 2] = new Vector4(m.M31, m.M32, m.M33, m.M34);
        dst[offset + 3] = new Vector4(m.M41, m.M42, m.M43, m.M44);
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
            _owned[i].Dispose();
    }
}
