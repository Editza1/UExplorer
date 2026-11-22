using System.Numerics;
using System.Runtime.InteropServices;
using CUE4Parse.FileProvider;
using UExplorer.Scene;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace UExplorer.Rendering;

/// <summary>
/// Standalone lightweight 128x128 DX11 renderer dedicated to Content Browser material thumbnails
/// and headless thumbnail diagnostics. Supports both cooked SM5 GBuffer materials (!pipe.IsForward)
/// and forward translucent/additive materials (pipe.IsForward) with zero shadow-map, SSAO, TAA,
/// bloom, LTC, or full-scene shader compilation overhead.
/// </summary>
public sealed class NativeThumbnailRenderer : IDisposable
{
    public const int DefaultSize = 128;
    private const int PrimitiveStride = 36;
    private const int ViewRegisters = 256;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly List<IDisposable> _owned = [];
    private readonly int _size;

    private readonly ID3D11Buffer _viewCb;
    private readonly ID3D11Buffer _translucentBasePassCb;
    private readonly ID3D11Buffer _primitiveBuffer;
    private readonly ID3D11ShaderResourceView _primitiveSrv;
    private readonly ID3D11Buffer _primitiveIdVb;
    private readonly ID3D11Buffer _thumbCb;

    private readonly ID3D11Texture2D[] _basePassTex = new ID3D11Texture2D[6];
    private readonly ID3D11RenderTargetView[] _rtvs = new ID3D11RenderTargetView[6];
    private readonly ID3D11ShaderResourceView[] _srvs = new ID3D11ShaderResourceView[6];
    private readonly ID3D11Texture2D _depthTex;
    private readonly ID3D11DepthStencilView _dsv;
    private readonly ID3D11ShaderResourceView _depthSrv;
    private readonly ID3D11Texture2D _sceneColorCopyTex;
    private readonly ID3D11ShaderResourceView _sceneColorCopySrv;
    private readonly ID3D11Texture2D _sceneDepthCopyTex;
    private readonly ID3D11ShaderResourceView _sceneDepthCopySrv;
    private readonly ID3D11Texture2D _outputTex;
    private readonly ID3D11RenderTargetView _outputRtv;
    private readonly ID3D11ShaderResourceView _outputSrv;
    private ID3D11Texture2D? _stagingTex;

    private readonly ID3D11RasterizerState _cullBack;
    private readonly ID3D11RasterizerState _cullNone;
    private readonly ID3D11DepthStencilState _depthWrite;
    private readonly ID3D11DepthStencilState _depthRead;
    private readonly ID3D11DepthStencilState _depthEqualRead;

    private readonly ID3D11VertexShader _fullscreenVs;
    private readonly ID3D11PixelShader _deferredLitPs;
    private readonly ID3D11PixelShader _forwardTonemapPs;
    private readonly ID3D11ShaderResourceView _preintegratedGfSrv;
    private readonly NativeSharedResources _shared;
    private readonly NativeMeshLods _defaultSphere;

    private readonly Vector4[] _viewScratch = new Vector4[ViewRegisters];
    private readonly Vector4[] _transScratch = new Vector4[256];
    private readonly Vector4[] _primScratch = new Vector4[PrimitiveStride];
    private readonly Vector4[] _thumbCbScratch = new Vector4[8];

    public int Size => _size;
    public bool ReadbackEnabled { get; set; }

    private T Own<T>(T res) where T : IDisposable { _owned.Add(res); return res; }

    public NativeThumbnailRenderer(D3D11NativeShaderDevice host, int size = DefaultSize)
    {
        _device = host.Device;
        _context = host.Context;
        _size = Math.Clamp(size, 64, 256);

        _viewCb = Own(_device.CreateBuffer(ViewRegisters * 16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _translucentBasePassCb = Own(_device.CreateBuffer(256 * 16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _thumbCb = Own(_device.CreateBuffer(8 * 16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _primitiveBuffer = Own(_device.CreateBuffer(new BufferDescription((uint) (PrimitiveStride * 16), BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.BufferStructured, 16)));
        _primitiveSrv = Own(_device.CreateShaderResourceView(_primitiveBuffer, new ShaderResourceViewDescription(_primitiveBuffer, Format.Unknown, 0, PrimitiveStride)));
        _primitiveIdVb = Own(_device.CreateBuffer(new uint[] { 0u }.AsSpan(), BindFlags.VertexBuffer));

        Format[] baseFormats =
        [
            Format.R16G16B16A16_Float, // [0] Emissive / Forward HDR
            Format.R16G16B16A16_Float, // [1] GBufferA WorldNormal
            Format.R8G8B8A8_UNorm,     // [2] GBufferB Metallic/Specular/Roughness/ShadingModelID
            Format.R8G8B8A8_UNorm,     // [3] GBufferC BaseColor/AO
            Format.R8G8B8A8_UNorm,     // [4] GBufferD CustomData
            Format.R8G8B8A8_UNorm,     // [5] GBufferE CustomData
        ];
        for (var i = 0; i < 6; i++)
        {
            _basePassTex[i] = Own(_device.CreateTexture2D(new Texture2DDescription(baseFormats[i], (uint) _size, (uint) _size, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource)));
            _rtvs[i] = Own(_device.CreateRenderTargetView(_basePassTex[i]));
            _srvs[i] = Own(_device.CreateShaderResourceView(_basePassTex[i]));
        }

        _depthTex = Own(_device.CreateTexture2D(new Texture2DDescription(Format.R32_Typeless, (uint) _size, (uint) _size, 1, 1, BindFlags.DepthStencil | BindFlags.ShaderResource)));
        _dsv = Own(_device.CreateDepthStencilView(_depthTex, new DepthStencilViewDescription(_depthTex, DepthStencilViewDimension.Texture2D, Format.D32_Float)));
        _depthSrv = Own(_device.CreateShaderResourceView(_depthTex, new ShaderResourceViewDescription(_depthTex, ShaderResourceViewDimension.Texture2D, Format.R32_Float, 0, 1)));

        _sceneColorCopyTex = Own(_device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, (uint) _size, (uint) _size, 1, 1, BindFlags.ShaderResource)));
        _sceneColorCopySrv = Own(_device.CreateShaderResourceView(_sceneColorCopyTex));
        _sceneDepthCopyTex = Own(_device.CreateTexture2D(new Texture2DDescription(Format.R32_Typeless, (uint) _size, (uint) _size, 1, 1, BindFlags.ShaderResource)));
        _sceneDepthCopySrv = Own(_device.CreateShaderResourceView(_sceneDepthCopyTex, new ShaderResourceViewDescription(_sceneDepthCopyTex, ShaderResourceViewDimension.Texture2D, Format.R32_Float, 0, 1)));

        _outputTex = Own(_device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint) _size, (uint) _size, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource)));
        _outputRtv = Own(_device.CreateRenderTargetView(_outputTex));
        _outputSrv = Own(_device.CreateShaderResourceView(_outputTex));

        // System LUTs & default textures
        var zeroVectors = new Vector4[256];
        var blacks = new Dictionary<EDxbcResourceKind, ID3D11ShaderResourceView>();
        var zeroStructured = Own(_device.CreateBuffer(zeroVectors.AsSpan(), new BufferDescription((uint) (zeroVectors.Length * 16), BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16)));
        blacks[EDxbcResourceKind.Structured] = Own(_device.CreateShaderResourceView(zeroStructured, new ShaderResourceViewDescription(zeroStructured, Format.Unknown, 0, (uint) zeroVectors.Length)));
        var zeroTyped = Own(_device.CreateBuffer(zeroVectors.AsSpan(), BindFlags.ShaderResource));
        blacks[EDxbcResourceKind.Buffer] = Own(_device.CreateShaderResourceView(zeroTyped, new ShaderResourceViewDescription(zeroTyped, Format.R32G32B32A32_Float, 0, (uint) zeroVectors.Length)));
        var zeroUIntTyped = Own(_device.CreateBuffer(zeroVectors.AsSpan(), BindFlags.ShaderResource));
        var zeroUIntBufferSrv = Own(_device.CreateShaderResourceView(zeroUIntTyped, new ShaderResourceViewDescription(zeroUIntTyped, Format.R32G32B32A32_UInt, 0, (uint) zeroVectors.Length)));
        var black2D = Own(_device.CreateTexture2D(new uint[] { 0 }.AsSpan(), Format.R8G8B8A8_UNorm, 1, 1));
        blacks[EDxbcResourceKind.Texture2D] = Own(_device.CreateShaderResourceView(black2D));
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

        var cubeData = new uint[] { 0xFFD0C4B4, 0xFFD0C4B4, 0xFFD0C4B4, 0xFFD0C4B4, 0xFFE8E2D8, 0xFF4A4846 };
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

        var (noise2DTex, noise2DSrv, noise3DTex, noise3DSrv) = NativeSystemTextures.CreateNoiseTextures(_device);
        Own(noise2DTex); Own(noise2DSrv);
        Own(noise3DTex); Own(noise3DSrv);

        _shared = new NativeSharedResources
        {
            View = _viewCb,
            TranslucentBasePass = _translucentBasePassCb,
            PrimitiveData = _primitiveSrv,
            Zeros = Own(_device.CreateBuffer(new byte[16384].AsSpan(), BindFlags.ConstantBuffer)),
            White2D = whiteSrv,
            NeutralYCoCg2D = Own(_device.CreateShaderResourceView(neutralYCoCg)),
            FlatNormal2D = Own(_device.CreateShaderResourceView(flatNormal)),
            DefaultUInt2D = Own(_device.CreateShaderResourceView(zeroUIntTex)),
            DefaultUIntBuffer = zeroUIntBufferSrv,
            DefaultUInt2DArray = zeroUIntArraySrv,
            DefaultUInt3D = zeroUInt3DSrv,
            DefaultCube = defaultCubeSrv,
            AmbientVolume3D = blacks[EDxbcResourceKind.Texture3D],
            Noise2D = noise2DSrv,
            NoiseVolume3D = noise3DSrv,
            SceneColorCopy = _sceneColorCopySrv,
            SceneDepthCopy = _sceneDepthCopySrv,
            PreIntegratedGF = _preintegratedGfSrv,
            BlackDefaults = blacks,
            Wrap = Own(_device.CreateSamplerState(new SamplerDescription(Filter.Anisotropic, TextureAddressMode.Wrap, 0, 4, ComparisonFunction.Never, 0, float.MaxValue))),
            Clamp = Own(_device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue)))
        };

        _cullBack = Own(_device.CreateRasterizerState(new RasterizerDescription(CullMode.Back, FillMode.Solid) { FrontCounterClockwise = true, DepthClipEnable = true }));
        _cullNone = Own(_device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { FrontCounterClockwise = true, DepthClipEnable = true }));
        _depthWrite = Own(_device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.GreaterEqual)));
        _depthRead = Own(_device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.GreaterEqual)));
        _depthEqualRead = Own(_device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.Equal)));

        _fullscreenVs = Own(_device.CreateVertexShader(Compiler.Compile(ThumbnailHlsl, "VSThumb", "thumb_vs", "vs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray()));
        _deferredLitPs = Own(_device.CreatePixelShader(Compiler.Compile(ThumbnailHlsl, "PSThumbnailDeferredLit", "thumb_def_ps", "ps_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray()));
        _forwardTonemapPs = Own(_device.CreatePixelShader(Compiler.Compile(ThumbnailHlsl, "PSThumbnailForwardTonemap", "thumb_fwd_ps", "ps_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray()));

        _defaultSphere = Own(NativeMeshLods.CreateProceduralSphere(_device, radiusCm: 50f, stacks: 24, slices: 36));
    }

    public static NativeMeshLods CreateSphereMesh(ID3D11Device device, float radiusCm = 50f) =>
        NativeMeshLods.CreateProceduralSphere(device, radiusCm);

    public static float ComputeFramedCameraDistance(float tightBoundsRadiusCm, float vFovRad, float fillFraction = 0.72f) =>
        NativeMeshLods.ComputeFramedCameraDistance(tightBoundsRadiusCm, vFovRad, fillFraction);

    public ID3D11ShaderResourceView? RenderMaterialToThumbnail(
        MaterialAsset mat,
        NativeMaterialPipeline pipe,
        NativeMeshLods? meshOverride = null,
        IFileProvider? provider = null)
    {
        if (!RenderMaterialInternal(mat, pipe, meshOverride, provider))
            return null;

        using var copyTex = _device.CreateTexture2D(new Texture2DDescription(
            Format.R8G8B8A8_UNorm,
            (uint) _size,
            (uint) _size,
            1,
            1,
            BindFlags.ShaderResource,
            ResourceUsage.Default));
        _context.CopyResource(copyTex, _outputTex);
        return _device.CreateShaderResourceView(copyTex);
    }

    public byte[] RenderMaterialCapture(
        MaterialAsset mat,
        NativeMaterialPipeline pipe,
        NativeMeshLods? meshOverride = null,
        IFileProvider? provider = null)
    {
        if (!RenderMaterialInternal(mat, pipe, meshOverride, provider))
            return [];

        _stagingTex ??= Own(_device.CreateTexture2D(new Texture2DDescription(
            Format.R8G8B8A8_UNorm,
            (uint) _size,
            (uint) _size,
            1,
            1,
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read)));

        _context.CopyResource(_stagingTex, _outputTex);
        var result = new byte[_size * _size * 4];
        var mapped = _context.Map(_stagingTex, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            for (var y = 0; y < _size; y++)
                Marshal.Copy(mapped.DataPointer + (nint) (y * mapped.RowPitch), result, y * _size * 4, _size * 4);
        }
        finally
        {
            _context.Unmap(_stagingTex, 0);
        }
        return result;
    }

    private bool RenderMaterialInternal(
        MaterialAsset mat,
        NativeMaterialPipeline pipe,
        NativeMeshLods? meshOverride,
        IFileProvider? provider)
    {
        var meshLods = meshOverride ?? _defaultSphere;
        var mesh = meshLods.Lods[0];
        var ctx = _context;

        // Keep live MI parameters synced before drawing
        pipe.EnsureParametersSynced(_device, ctx, provider, mat);

        // Deterministic BoundsRadius camera framing
        const float vFovRad = 45f * MathF.PI / 180f;
        const float near = 0.05f;
        var f = 1f / MathF.Tan(vFovRad * 0.5f);
        var dist = NativeMeshLods.ComputeFramedCameraDistance(mesh.TightBoundsRadius, vFovRad, 0.72f);

        var centerMeters = new Vector3(mesh.BoundsCenter.X, mesh.BoundsCenter.Z, mesh.BoundsCenter.Y) * 0.01f;
        const float yaw = 0.55f;
        const float pitch = 0.30f;
        var cosP = MathF.Cos(pitch);
        var camMeters = centerMeters + new Vector3(
            MathF.Sin(yaw) * cosP * dist,
            MathF.Sin(pitch) * dist,
            MathF.Cos(yaw) * cosP * dist);

        var view = Matrix4x4.CreateLookAt(camMeters, centerMeters, Vector3.UnitY);
        var proj = new Matrix4x4(f, 0, 0, 0, 0, f, 0, 0, 0, 0, 0, -1, 0, 0, near, 0);
        var unrealToMeters = new Matrix4x4(0.01f, 0, 0, 0, 0, 0, 0.01f, 0, 0, 0.01f, 0, 0, 0, 0, 0, 1);
        var worldToClip = unrealToMeters * view * proj;
        var camUnrealCm = new Vector3(camMeters.X, camMeters.Z, camMeters.Y) * 100f;

        var translatedWorldToClip = Matrix4x4.CreateTranslation(camUnrealCm) * worldToClip;
        Matrix4x4.Invert(translatedWorldToClip, out var clipToTranslatedWorld);
        Matrix4x4.Invert(worldToClip, out var clipToWorld);
        var screenToClip = new Matrix4x4(2f / _size, 0, 0, 0, 0, -2f / _size, 0, 0, 0, 0, 1, 0, -1, 1, 0, 1);
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
            translatedWorldToClip = Matrix4x4.CreateTranslation(camUnrealCm) * worldToClip;
            Matrix4x4.Invert(translatedWorldToClip, out clipToTranslatedWorld);
            Matrix4x4.Invert(worldToClip, out clipToWorld);
            svPosToTranslatedWorld = screenToClip * clipToTranslatedWorld;
            viewToClip = viewToTranslatedWorld * translatedWorldToClip;
        }
        var nearCm = MathF.Max(MathF.Abs(viewToClip.M43), 0.1f);
        Matrix4x4.Invert(viewToClip, out var clipToView);
        var screenToWorld = screenToClip * clipToWorld;

        // Lighting rig: Key + Rim + Sky environment
        var keyDir = Vector3.Normalize(new Vector3(0.45f, -0.60f, 0.66f));
        var keyColor = new Vector3(1.0f, 0.97f, 0.92f);
        const float keyIntensity = MathF.PI * 1.10f;
        var rimDir = Vector3.Normalize(new Vector3(-0.65f, 0.55f, 0.48f));
        var rimColor = new Vector3(0.52f, 0.68f, 0.95f);
        const float rimIntensity = MathF.PI * 0.45f;
        var skyUpper = new Vector3(0.28f, 0.34f, 0.44f);
        var skyLower = new Vector3(0.09f, 0.10f, 0.12f);
        const float skyIntensity = 0.85f;

        Array.Clear(_viewScratch);
        WriteMatrix(_viewScratch, 0, translatedWorldToClip);
        WriteMatrix(_viewScratch, 4, worldToClip);
        WriteMatrix(_viewScratch, 8, clipToWorld);
        WriteMatrix(_viewScratch, 12, translatedWorldToView);
        WriteMatrix(_viewScratch, 16, viewToTranslatedWorld);
        WriteMatrix(_viewScratch, 24, viewToClip);
        WriteMatrix(_viewScratch, 32, clipToView);
        WriteMatrix(_viewScratch, 36, clipToTranslatedWorld);
        WriteMatrix(_viewScratch, 44, svPosToTranslatedWorld);
        WriteMatrix(_viewScratch, 48, screenToWorld);
        WriteMatrix(_viewScratch, 52, svPosToTranslatedWorld);
        _viewScratch[56] = new Vector4(camFwd, 0);
        _viewScratch[57] = new Vector4(camUp, 0);
        _viewScratch[58] = new Vector4(camRight, 0);
        _viewScratch[60] = new Vector4(camFwd, 0);
        _viewScratch[65] = new Vector4(0f, 0f, 1f / nearCm, -1e-7f);
        _viewScratch[66] = new Vector4(0.5f, -0.5f, 0.5f, 0.5f);
        _viewScratch[67] = new Vector4(camUnrealCm, 1);
        _viewScratch[68] = new Vector4(0, 0, 0, 1);
        _viewScratch[69] = new Vector4(camUnrealCm, 1);
        _viewScratch[70] = new Vector4(-camUnrealCm, 0);
        WriteMatrix(_viewScratch, 91, translatedWorldToClip);
        _viewScratch[113] = new Vector4(-camUnrealCm, 0);
        _viewScratch[129] = new Vector4(0, 0, _size, _size);
        _viewScratch[130] = new Vector4(_size, _size, 1f / _size, 1f / _size);
        _viewScratch[131] = new Vector4(_size, _size, 1f / _size, 1f / _size);
        _viewScratch[132] = new Vector4(0, 0, 1, 1);
        _viewScratch[134] = Vector4.One;
        _viewScratch[135] = Vector4.One;
        _viewScratch[136] = new Vector4(0, 0, 0, 1);
        _viewScratch[137] = new Vector4(0, 0, 0, 1);
        _viewScratch[138] = new Vector4(0, 0, 0, 1);
        _viewScratch[139] = new Vector4(0, 1, 0, 0);
        _viewScratch[140] = new Vector4(0, 0, 0, 1);
        _viewScratch[141] = Vector4.One;
        _viewScratch[142] = new Vector4(1f / 60f, 0f, 0.5f, 0.5f);
        _viewScratch[143] = new Vector4(1f / 60f, 0f, 1f, 1f);
        _viewScratch[144] = new Vector4(1f, 0f, 1f, 1f);
        _viewScratch[145] = new Vector4(1f, pipe.IsForward ? 0.55f : 0f, 1f, 1f);
        _viewScratch[152] = Vector4.Zero;
        _viewScratch[154] = Vector4.Zero;
        _viewScratch[157] = new Vector4(skyUpper * skyIntensity, 1f);
        _viewScratch[161] = new Vector4(keyDir, 0f);
        _viewScratch[165] = new Vector4(keyColor * keyIntensity, 1f);
        _viewScratch[179] = new Vector4(1f, 0f, 1f, 1f);
        _viewScratch[195] = new Vector4(1f, 1f, 4f, 1f);
        _viewScratch[210] = new Vector4(0f, 0f, 0f, 1f);
        _viewScratch[226] = new Vector4(0.04f, 0.08f, 0.04f, 0.04f);
        Upload(_viewCb, _viewScratch.AsSpan());

        Array.Clear(_transScratch);
        var oneUintBits = BitConverter.Int32BitsToSingle(1);
        var channelMaskBits = BitConverter.Int32BitsToSingle(0xFF02);
        _transScratch[0] = new Vector4(0, 0, oneUintBits, 0);
        _transScratch[1] = new Vector4(oneUintBits, oneUintBits, oneUintBits, 0);
        _transScratch[3] = new Vector4(0.001f, 0f, 1f, 1f);
        _transScratch[4] = new Vector4(keyDir, 0);
        _transScratch[5] = new Vector4(keyColor * keyIntensity, 1f);
        _transScratch[6] = new Vector4(channelMaskBits, 0, 0, 1f);
        _transScratch[107] = new Vector4(0f, 0.001f, 0f, 0f);
        _transScratch[108] = new Vector4(0f, 0.001f, 0f, 0f);
        _transScratch[109] = new Vector4(skyUpper * skyIntensity, 1f);
        _transScratch[110] = new Vector4(0f, 0f, 0f, 1e9f);
        _transScratch[111] = new Vector4(0f, 0f, 0f, -1f);
        _transScratch[135] = new Vector4(0.5f, -0.5f, 0.5f, 0.5f);
        Upload(_translucentBasePassCb, _transScratch.AsSpan());

        Array.Clear(_primScratch);
        WriteMatrix(_primScratch, 0, Matrix4x4.Identity);
        _primScratch[4] = Vector4.One;
        _primScratch[5] = new Vector4(mesh.BoundsCenter, mesh.BoundsRadius);
        WriteMatrix(_primScratch, 6, Matrix4x4.Identity);
        WriteMatrix(_primScratch, 10, Matrix4x4.Identity);
        WriteMatrix(_primScratch, 14, Matrix4x4.Identity);
        var halfExt = new Vector3(MathF.Max(mesh.BoundsRadius, 100f));
        _primScratch[19] = new Vector4(halfExt, 1f);
        _primScratch[20] = new Vector4(1f, 1f, 1f, 0f);
        _primScratch[21] = new Vector4(mesh.BoundsCenter - halfExt, 0f);
        _primScratch[22] = new Vector4(mesh.BoundsCenter + halfExt, 0f);
        var maskBits = BitConverter.Int32BitsToSingle(0x7FF);
        _primScratch[23] = new Vector4(maskBits, maskBits, maskBits, maskBits);
        Upload(_primitiveBuffer, _primScratch.AsSpan());

        ctx.ClearState();
        ctx.RSSetViewport(0, 0, _size, _size);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.ClearDepthStencilView(_dsv, DepthStencilClearFlags.Depth, 0f, 0);

        if (!pipe.IsForward)
        {
            for (var i = 0; i < 6; i++)
                ctx.ClearRenderTargetView(_rtvs[i], new Color4(0, 0, 0, 0));

            if (pipe.NeedsEarlyZMaskPass)
            {
                ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), _dsv);
                ctx.OMSetDepthStencilState(_depthWrite);
                ctx.OMSetBlendState(null);
                pipe.BindDepthOnlyMaterial(ctx, _shared);
                pipe.BindDepthOnlyMesh(ctx, _shared, mesh);
                ctx.IASetIndexBuffer(mesh.Indices, Format.R32_UInt, 0);
                ctx.IASetVertexBuffers(0, [mesh.Positions, _primitiveIdVb], [12u, 4u], [0u, 0u]);
                ctx.RSSetState(pipe.TwoSided ? _cullNone : _cullBack);
                foreach (var sec in mesh.Sections)
                    ctx.DrawIndexedInstanced((uint) sec.IndexCount, 1, (uint) sec.FirstIndex, 0, 0);
            }

            if (pipe.IsRvtShader)
                ctx.OMSetRenderTargets([_rtvs[0], _rtvs[3], _rtvs[1]], _dsv);
            else
                ctx.OMSetRenderTargets(_rtvs, _dsv);

            ctx.OMSetDepthStencilState(pipe.NeedsEarlyZMaskPass ? _depthEqualRead : _depthWrite);
            ctx.OMSetBlendState(null);
            pipe.BindMaterial(ctx, _shared);
            pipe.BindMesh(ctx, _shared, mesh);
            ctx.IASetIndexBuffer(mesh.Indices, Format.R32_UInt, 0);
            ctx.IASetVertexBuffers(0, [mesh.Positions, _primitiveIdVb], [12u, 4u], [0u, 0u]);
            ctx.RSSetState(pipe.TwoSided ? _cullNone : _cullBack);
            foreach (var sec in mesh.Sections)
                ctx.DrawIndexedInstanced((uint) sec.IndexCount, 1, (uint) sec.FirstIndex, 0, 0);

            // Lightweight deferred lit + ACES/sRGB resolve pass
            _thumbCbScratch[0] = new Vector4(keyDir, keyIntensity);
            _thumbCbScratch[1] = new Vector4(keyColor, 0f);
            _thumbCbScratch[2] = new Vector4(rimDir, rimIntensity);
            _thumbCbScratch[3] = new Vector4(rimColor, 0f);
            _thumbCbScratch[4] = new Vector4(skyUpper * skyIntensity, 1f);
            _thumbCbScratch[5] = new Vector4(skyLower * skyIntensity, 0f);
            _thumbCbScratch[6] = new Vector4(camUnrealCm, 1f);
            _thumbCbScratch[7] = new Vector4(_size, _size, 1f / _size, 1f / _size);
            Upload(_thumbCb, _thumbCbScratch.AsSpan());

            ctx.ClearState();
            ctx.OMSetRenderTargets(_outputRtv, null);
            ctx.RSSetViewport(0, 0, _size, _size);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.RSSetState(_cullNone);
            ctx.VSSetShader(_fullscreenVs);
            ctx.PSSetShader(_deferredLitPs);
            ctx.PSSetConstantBuffer(0, _thumbCb);
            ctx.PSSetConstantBuffer(1, _viewCb);
            ctx.PSSetSampler(0, _shared.Clamp);
            ctx.PSSetShaderResources(0, [_srvs[0], _srvs[1], _srvs[2], _srvs[3], _srvs[4], _srvs[5], _depthSrv, _preintegratedGfSrv]);
            ctx.Draw(3, 0);
            ctx.ClearState();
        }
        else
        {
            // Forward translucent / additive / modulate thumbnail
            ctx.ClearRenderTargetView(_rtvs[0], new Color4(0.024f, 0.028f, 0.036f, 1.0f));
            ctx.CopyResource(_sceneColorCopyTex, _basePassTex[0]);
            ctx.CopyResource(_sceneDepthCopyTex, _depthTex);

            ctx.OMSetRenderTargets(_rtvs[0], _dsv);
            ctx.OMSetDepthStencilState(_depthRead);
            ctx.OMSetBlendState(pipe.Blend);
            pipe.BindMaterial(ctx, _shared);
            pipe.BindMesh(ctx, _shared, mesh);
            ctx.IASetIndexBuffer(mesh.Indices, Format.R32_UInt, 0);
            ctx.IASetVertexBuffers(0, [mesh.Positions, _primitiveIdVb], [12u, 4u], [0u, 0u]);
            ctx.RSSetState(pipe.TwoSided ? _cullNone : _cullBack);
            foreach (var sec in mesh.Sections)
                ctx.DrawIndexedInstanced((uint) sec.IndexCount, 1, (uint) sec.FirstIndex, 0, 0);

            ctx.ClearState();
            ctx.OMSetRenderTargets(_outputRtv, null);
            ctx.RSSetViewport(0, 0, _size, _size);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.RSSetState(_cullNone);
            ctx.VSSetShader(_fullscreenVs);
            ctx.PSSetShader(_forwardTonemapPs);
            ctx.PSSetShaderResource(0, _srvs[0]);
            ctx.Draw(3, 0);
            ctx.ClearState();
        }

        return true;
    }

    private void Upload<T>(ID3D11Buffer buffer, ReadOnlySpan<T> data) where T : unmanaged
    {
        var mapped = _context.Map(buffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bytes = MemoryMarshal.AsBytes(data);
            var length = Math.Min(bytes.Length, (int) buffer.Description.ByteWidth);
            unsafe
            {
                bytes[..length].CopyTo(new Span<byte>((void*) mapped.DataPointer, length));
            }
        }
        finally
        {
            _context.Unmap(buffer, 0);
        }
    }

    private static void WriteMatrix(Vector4[] registers, int index, Matrix4x4 m)
    {
        registers[index] = new Vector4(m.M11, m.M12, m.M13, m.M14);
        registers[index + 1] = new Vector4(m.M21, m.M22, m.M23, m.M24);
        registers[index + 2] = new Vector4(m.M31, m.M32, m.M33, m.M34);
        registers[index + 3] = new Vector4(m.M41, m.M42, m.M43, m.M44);
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
            _owned[i].Dispose();
        _owned.Clear();
    }

    private const string ThumbnailHlsl = """
        #define PI 3.1415926535897932f

        struct V { float4 Pos : SV_Position; };
        V VSThumb(uint id : SV_VertexID)
        {
            V o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.Pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        cbuffer ThumbCb : register(b0)
        {
            float4 KeyDirAndIntensity;
            float4 KeyColor;
            float4 RimDirAndIntensity;
            float4 RimColor;
            float4 SkyUpper;
            float4 SkyLower;
            float4 CamPos;
            float4 SizeAndInv;
        };

        cbuffer ViewCb : register(b1)
        {
            float4 ViewRegs[256];
        };

        SamplerState LinearClamp : register(s0);
        Texture2D<float4> SceneColor      : register(t0);
        Texture2D<float4> GBufferA        : register(t1);
        Texture2D<float4> GBufferB        : register(t2);
        Texture2D<float4> GBufferC        : register(t3);
        Texture2D<float4> GBufferD        : register(t4);
        Texture2D<float4> GBufferE        : register(t5);
        Texture2D<float>  DepthTex        : register(t6);
        Texture2D<float2> PreIntegratedGF : register(t7);

        float3 SafeNormalize(float3 v, float3 fb)
        {
            float l = dot(v, v);
            return l > 1e-8f ? v * rsqrt(l) : fb;
        }

        float3 UnpackYCoCg(float4 ycocg)
        {
            float scale = 1.0f / (ycocg.z * 31.875f + 1.0f);
            float Co = (ycocg.x - 0.5019608f) * scale;
            float Cg = (ycocg.y - 0.5019608f) * scale;
            float Y  = ycocg.w;
            return saturate(float3(Y + Co - Cg, Y + Cg, Y - Co - Cg));
        }

        float3 FilmToneMapFast(float3 c)
        {
            c = max(c, 0.0f);
            const float a = 2.51f;
            const float b = 0.03f;
            const float c1 = 2.43f;
            const float d = 0.59f;
            const float e = 0.14f;
            return saturate((c * (a * c + b)) / (c * (c1 * c + d) + e));
        }

        float3 LinearToSrgbBranchless(float3 lin)
        {
            lin = max(6.10352e-5f, lin);
            return min(lin * 12.92f, pow(max(lin, 0.00313067f), 1.0f / 2.4f) * 1.055f - 0.055f);
        }

        float D_GGX(float a2, float NoH)
        {
            float d = (NoH * a2 - NoH) * NoH + 1.0f;
            return a2 / max(PI * d * d, 1e-6f);
        }

        float Vis_SmithJointApprox(float a2, float NoV, float NoL)
        {
            float a = sqrt(a2);
            float visV = NoL * (NoV * (1.0f - a) + a);
            float visL = NoV * (NoL * (1.0f - a) + a);
            return 0.5f / max(visV + visL, 1e-5f);
        }

        float3 F_Schlick(float3 f0, float VoH)
        {
            float fc = pow(1.0f - VoH, 5.0f);
            return f0 + (1.0f - f0) * fc;
        }

        float3 EvalDirLight(float3 N, float3 V, float3 L, float3 lightCol, float lightInt, float3 diffCol, float3 specCol, float roughness)
        {
            float NoL = saturate(dot(N, L));
            if (NoL <= 0.0f) return 0.0f;
            float NoV = saturate(abs(dot(N, V)) + 1e-4f);
            float3 H = SafeNormalize(V + L, N);
            float NoH = saturate(dot(N, H));
            float VoH = saturate(dot(V, H));
            float a = roughness * roughness;
            float a2 = max(a * a, 1e-4f);
            float D = D_GGX(a2, NoH);
            float Vis = Vis_SmithJointApprox(a2, NoV, NoL);
            float3 F = F_Schlick(specCol, VoH);
            float3 diff = diffCol * (1.0f / PI);
            float3 spec = (D * Vis) * F;
            return (diff + spec) * lightCol * (lightInt * NoL);
        }

        float4 PSThumbnailDeferredLit(V i) : SV_Target
        {
            int3 p = int3(i.Pos.xy, 0);
            float4 emissive = SceneColor.Load(p);
            float4 a = GBufferA.Load(p);
            float4 b = GBufferB.Load(p);
            float4 c = GBufferC.Load(p);
            float depth = DepthTex.Load(p);

            if (any(isnan(emissive.rgb)) || any(isinf(emissive.rgb)))
                emissive.rgb = 0.0f;

            // Studio dark gradient backdrop
            if (dot(a.xyz, a.xyz) < 1e-4f)
            {
                float gy = saturate(i.Pos.y * SizeAndInv.w);
                float3 bg = lerp(float3(0.015f, 0.017f, 0.022f), float3(0.028f, 0.032f, 0.042f), gy);
                float3 col = LinearToSrgbBranchless(FilmToneMapFast(bg + emissive.rgb));
                return float4(col, 1.0f);
            }

            bool isYCoCgRvt = (b.a > 0.45f && b.a < 0.55f);
            uint rawByteA = (uint) round(saturate(b.a) * 255.0f);
            uint shadingModel = rawByteA & 0xFu;

            float3 N, baseColor;
            float metallic, specular, roughness, ao;
            if (isYCoCgRvt)
            {
                float2 nXY = a.xy * 2.0f - 1.0f;
                N = SafeNormalize(float3(nXY, sqrt(saturate(1.0f - dot(nXY, nXY)))), float3(0, 0, 1));
                baseColor = UnpackYCoCg(c);
                metallic = 0.0f;
                specular = saturate(b.r);
                roughness = clamp(b.g, 0.04f, 1.0f);
                ao = 1.0f;
                shadingModel = 1u;
            }
            else if (shadingModel == 15u)
            {
                float3 packedN = float3(a.xy, b.b);
                N = SafeNormalize(packedN * (255.0f / 127.0f) - 1.0f, float3(0, 0, 1));
                baseColor = saturate(c.rgb);
                metallic = 0.0f;
                specular = saturate(b.r);
                roughness = clamp(b.g, 0.04f, 1.0f);
                ao = 1.0f;
                shadingModel = 1u;
            }
            else
            {
                N = SafeNormalize(a.xyz * 2.0f - 1.0f, float3(0, 0, 1));
                metallic = saturate(b.r);
                specular = saturate(b.g);
                roughness = clamp(b.b, 0.04f, 1.0f);
                baseColor = saturate(c.rgb);
                ao = c.a > 1e-4f ? saturate(c.a) : 1.0f;
                if (shadingModel == 0u && dot(baseColor, baseColor) > 1e-4f)
                    shadingModel = 1u;
            }

            if (shadingModel == 0u)
            {
                return float4(LinearToSrgbBranchless(FilmToneMapFast(emissive.rgb)), 1.0f);
            }

            float4 worldH = i.Pos.xxxx * ViewRegs[44] + i.Pos.yyyy * ViewRegs[45] + depth.xxxx * ViewRegs[46] + ViewRegs[47];
            float3 twp = worldH.xyz / max(worldH.w, 1e-6f);
            float3 V = SafeNormalize(-twp, float3(0, 0, 1));
            float NoV = saturate(abs(dot(N, V)) + 1e-4f);

            float3 specColor = lerp((0.08f * specular).xxx, baseColor, metallic);
            float3 diffColor = baseColor * (1.0f - metallic);

            float3 keyLit = EvalDirLight(N, V, normalize(KeyDirAndIntensity.xyz), KeyColor.rgb, KeyDirAndIntensity.w, diffColor, specColor, roughness);
            float3 rimLit = EvalDirLight(N, V, normalize(RimDirAndIntensity.xyz), RimColor.rgb, RimDirAndIntensity.w, diffColor, specColor, roughness);

            // Sky hemisphere IBL + PreIntegratedGF
            float h = N.z * 0.5f + 0.5f;
            float3 skyIrrad = lerp(SkyLower.rgb, SkyUpper.rgb, h * h * (3.0f - 2.0f * h));
            float3 indirectDiff = skyIrrad * diffColor * ao;

            float3 R = reflect(-V, N);
            float rh = R.z * 0.5f + 0.5f;
            float3 skyRefl = lerp(SkyLower.rgb, SkyUpper.rgb * 1.4f, smoothstep(0.15f, 0.85f, rh));
            float2 gf = PreIntegratedGF.SampleLevel(LinearClamp, float2(NoV, roughness), 0);
            float3 envBrdf = specColor * gf.x + saturate(50.0f * specColor.g) * gf.y;
            float3 indirectSpec = skyRefl * envBrdf * ao;

            float3 total = emissive.rgb + keyLit + rimLit + indirectDiff + indirectSpec;
            if (any(isnan(total)) || any(isinf(total)))
                total = baseColor * 0.5f + emissive.rgb;

            return float4(LinearToSrgbBranchless(FilmToneMapFast(total)), 1.0f);
        }

        float4 PSThumbnailForwardTonemap(V i) : SV_Target
        {
            int3 p = int3(i.Pos.xy, 0);
            float4 c = SceneColor.Load(p);
            if (any(isnan(c.rgb)) || any(isinf(c.rgb)))
                c.rgb = 0.0f;
            return float4(LinearToSrgbBranchless(FilmToneMapFast(c.rgb)), 1.0f);
        }
        """;
}
