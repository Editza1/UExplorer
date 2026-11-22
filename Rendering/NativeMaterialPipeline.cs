using System.Runtime.InteropServices;
using CUE4Parse.FileProvider;
using CUE4Parse_Conversion.Textures;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Serilog;
using SixLabors.ImageSharp.PixelFormats;

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

using UExplorer.Scene;
namespace UExplorer.Rendering;

public enum ENativeBlendMode { Opaque, Masked, Translucent, Additive, Modulate, AlphaComposite, AlphaHoldout }

public sealed class NativeSharedResources
{
    public required ID3D11Buffer View { get; init; }
    public required ID3D11Buffer TranslucentBasePass { get; init; }
    public required ID3D11ShaderResourceView PrimitiveData { get; init; }
    public required ID3D11Buffer Zeros { get; init; }
    public required ID3D11ShaderResourceView White2D { get; init; }
    public required ID3D11ShaderResourceView NeutralYCoCg2D { get; init; }
    public required ID3D11ShaderResourceView FlatNormal2D { get; init; }
    public required ID3D11ShaderResourceView DefaultUInt2D { get; init; }
    public required ID3D11ShaderResourceView DefaultUIntBuffer { get; init; }
    public required ID3D11ShaderResourceView DefaultUInt2DArray { get; init; }
    public required ID3D11ShaderResourceView DefaultUInt3D { get; init; }
    public required ID3D11ShaderResourceView DefaultCube { get; init; }
    public required ID3D11ShaderResourceView AmbientVolume3D { get; init; }
    public ID3D11ShaderResourceView? Noise2D { get; init; }
    public ID3D11ShaderResourceView? NoiseVolume3D { get; init; }
    public required ID3D11ShaderResourceView SceneColorCopy { get; init; }
    public required ID3D11ShaderResourceView SceneDepthCopy { get; init; }
    public ID3D11ShaderResourceView? PreIntegratedGF { get; init; }
    public required IReadOnlyDictionary<EDxbcResourceKind, ID3D11ShaderResourceView> BlackDefaults { get; init; }
    public required ID3D11SamplerState Wrap { get; init; }
    public required ID3D11SamplerState Clamp { get; init; }
}

public sealed class NativeMaterialPipeline : IDisposable
{
    private static int _nextPipelineId;
    public int PipelineId { get; } = Interlocked.Increment(ref _nextPipelineId);

    private readonly List<IDisposable> _owned = [];
    private readonly UnrealD3D11ShaderInfo _vsInfo, _psInfo;
    private readonly Dictionary<int, EDxbcResourceKind> _vsDeclarations, _psDeclarations;
    private readonly Dictionary<int, bool> _vsUintResources, _psUintResources;
    private readonly UnrealD3D11ShaderInfo? _depthVsInfo, _depthPsInfo;
    private readonly Dictionary<int, EDxbcResourceKind>? _depthVsDeclarations, _depthPsDeclarations;
    private readonly Dictionary<int, bool>? _depthVsUintResources, _depthPsUintResources;
    private readonly Dictionary<string, ID3D11Buffer> _namedBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ID3D11ShaderResourceView?[] _materialTextures2D;
    private readonly ID3D11ShaderResourceView?[] _materialTexturesCube;
    private readonly bool[] _materialTexturesClamp;
    private IReadOnlyList<ESamplerSourceMode?>? _materialSamplerSources;
    private readonly ID3D11Buffer _material;
    private readonly NativeMaterialShaders? _shaderSet;
    private readonly object _syncLock = new();
    private long _syncedParameterVersion;

    public long SyncedParameterVersion => Volatile.Read(ref _syncedParameterVersion);
    public bool NeedsSceneColor { get; }
    public bool NeedsSceneDepth { get; }
    public ID3D11VertexShader VertexShader { get; }
    public ID3D11PixelShader PixelShader { get; }
    public ID3D11InputLayout InputLayout { get; }
    public ID3D11VertexShader? DepthOnlyVertexShader { get; }
    public ID3D11PixelShader? DepthOnlyPixelShader { get; }
    public ID3D11InputLayout? DepthOnlyInputLayout { get; }
    public bool HasDepthOnlyDiscard { get; }
    public bool NeedsEarlyZMaskPass { get; }
    public ID3D11BlendState? Blend { get; }
    public ENativeBlendMode BlendMode { get; }
    public bool IsForward { get; }
    public bool IsRvtShader { get; }
    public bool IsVolumeDomain { get; }
    public bool TwoSided { get; }
    public string Description { get; }

    private T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }

    public NativeMaterialPipeline(ID3D11Device device, NativeShaderEntry vs, NativeShaderEntry ps, byte[] materialConstants,
        IReadOnlyList<UTexture?> materialResources, ENativeBlendMode blendMode, bool twoSided, IReadOnlyDictionary<string, byte[]>? namedBuffers,
        bool isEmissiveDriven = false, bool isRvtShader = false, IReadOnlyList<ESamplerSourceMode?>? materialSamplerSources = null,
        NativeShaderEntry? depthVs = null, NativeShaderEntry? depthPs = null,
        NativeMaterialShaders? shaderSet = null, long initialParameterVersion = 0, bool isVolumeDomain = false)
    {
        _shaderSet = shaderSet;
        _syncedParameterVersion = initialParameterVersion;
        _materialSamplerSources = materialSamplerSources;
        IsVolumeDomain = isVolumeDomain;
        if (vs.Code is null || vs.Container is not { } vsContainer || ps.Code is null || ps.Container is not { } psContainer)
            throw new InvalidOperationException("missing bytecode");

        _vsInfo = UnrealD3D11ShaderInfo.Parse(vs.Code, vsContainer);
        _psInfo = UnrealD3D11ShaderInfo.Parse(ps.Code, psContainer);
        NeedsSceneColor = UsesTranslucentResource(_vsInfo, 67) || UsesTranslucentResource(_psInfo, 67);
        NeedsSceneDepth = UsesTranslucentResource(_vsInfo, 36) || UsesTranslucentResource(_psInfo, 36);
        var vsBytecode = vs.Bytecode.ToArray();
        var psBytecode = ps.Bytecode.ToArray();
        _vsDeclarations = DxbcResourceDeclarations.ReadWithTypes(vsBytecode, out _vsUintResources);
        _psDeclarations = DxbcResourceDeclarations.ReadWithTypes(psBytecode, out _psUintResources);
        var outputs = DxbcResourceDeclarations.OutputCount(psBytecode);
        var basePassHasDiscard = DxbcResourceDeclarations.HasDiscard(psBytecode);

        BlendMode = blendMode;
        TwoSided = twoSided;
        IsRvtShader = isRvtShader;
        IsForward = !isRvtShader && (outputs <= 2 || blendMode is ENativeBlendMode.Translucent or ENativeBlendMode.Additive or ENativeBlendMode.Modulate or ENativeBlendMode.AlphaComposite);

        VertexShader = Own(device.CreateVertexShader(vsBytecode));
        PixelShader = Own(device.CreatePixelShader(psBytecode));
        InputLayout = Own(CreateCompatibleInputLayout(device, vsBytecode));

        if (!IsForward && !IsRvtShader &&
            depthVs is { Code: not null, Container: { } dvsContainer } &&
            depthPs is { Code: not null, Container: { } dpsContainer })
        {
            var dvsBytecode = depthVs.Bytecode.ToArray();
            var dpsBytecode = depthPs.Bytecode.ToArray();
            var depthHasDiscard = DxbcResourceDeclarations.HasDiscard(dpsBytecode);
            if (depthHasDiscard || blendMode == ENativeBlendMode.Masked)
            {
                try
                {
                    _depthVsInfo = UnrealD3D11ShaderInfo.Parse(depthVs.Code, dvsContainer);
                    _depthPsInfo = UnrealD3D11ShaderInfo.Parse(depthPs.Code, dpsContainer);
                    _depthVsDeclarations = DxbcResourceDeclarations.ReadWithTypes(dvsBytecode, out _depthVsUintResources);
                    _depthPsDeclarations = DxbcResourceDeclarations.ReadWithTypes(dpsBytecode, out _depthPsUintResources);
                    DepthOnlyVertexShader = Own(device.CreateVertexShader(dvsBytecode));
                    DepthOnlyPixelShader = Own(device.CreatePixelShader(dpsBytecode));
                    DepthOnlyInputLayout = Own(CreateCompatibleInputLayout(device, dvsBytecode));
                    HasDepthOnlyDiscard = true;
                    NeedsEarlyZMaskPass = !basePassHasDiscard;
                }
                catch (Exception e)
                {
                    Log.Debug(e, "DepthOnly prepass shader creation skipped for {Vs}/{Ps}", depthVs.ShaderType, depthPs.ShaderType);
                }
            }
        }

        var material = new byte[Math.Max(256, (materialConstants.Length + 15) / 16 * 16)];
        materialConstants.CopyTo(material, 0);
        _material = Own(device.CreateBuffer(material.AsSpan(),
            new BufferDescription((uint) material.Length, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write)));

        foreach (var (name, data) in namedBuffers ?? new Dictionary<string, byte[]>())
        {
            var padded = new byte[Math.Max(16, (data.Length + 15) / 16 * 16)];
            data.CopyTo(padded, 0);
            _namedBuffers[name] = Own(device.CreateBuffer(padded.AsSpan(), BindFlags.ConstantBuffer));
        }

        _materialTextures2D = new ID3D11ShaderResourceView?[materialResources.Count];
        _materialTexturesCube = new ID3D11ShaderResourceView?[materialResources.Count];
        _materialTexturesClamp = new bool[materialResources.Count];
        for (var i = 0; i < materialResources.Count; i++)
        {
            if (materialResources[i] is { } texture)
            {
                _materialTextures2D[i] = NativeTextureCache.Get(device, texture, asCube: false);
                _materialTexturesCube[i] = NativeTextureCache.Get(device, texture, asCube: true);
                _materialTexturesClamp[i] = texture is UTexture2D t2d &&
                    (t2d.AddressX == TextureAddress.TA_Clamp || t2d.AddressY == TextureAddress.TA_Clamp);
            }
        }

        if (IsForward)
        {
            var (src, dst) = blendMode switch
            {
                ENativeBlendMode.Additive => (Vortice.Direct3D11.Blend.One, Vortice.Direct3D11.Blend.One),
                ENativeBlendMode.Modulate => (Vortice.Direct3D11.Blend.DestinationColor, Vortice.Direct3D11.Blend.Zero),
                ENativeBlendMode.AlphaComposite => (Vortice.Direct3D11.Blend.One, Vortice.Direct3D11.Blend.InverseSourceAlpha),
                ENativeBlendMode.Translucent => (Vortice.Direct3D11.Blend.SourceAlpha, Vortice.Direct3D11.Blend.InverseSourceAlpha),
                _ => (Vortice.Direct3D11.Blend.One, Vortice.Direct3D11.Blend.Zero)
            };
            Blend = Own(device.CreateBlendState(new BlendDescription(src, dst, Vortice.Direct3D11.Blend.One, Vortice.Direct3D11.Blend.InverseSourceAlpha)));
        }

        Description = $"{vs.ShaderType} + {ps.ShaderType} | {blendMode}{(twoSided ? " two-sided" : "")} | {(IsRvtShader ? "RVT-direct" : IsForward ? "forward" : "deferred")} ({outputs} outputs)\n" +
                      $"VS:\n{_vsInfo.Describe()}{Declared(_vsDeclarations)}PS:\n{_psInfo.Describe()}{Declared(_psDeclarations)}";
    }

    private static bool UsesTranslucentResource(UnrealD3D11ShaderInfo info, int resourceIndex)
    {
        foreach (var e in info.ShaderResourceViews)
        {
            if (e.Resource == resourceIndex &&
                e.UniformBuffer >= 0 && e.UniformBuffer < info.UniformBufferNames.Count &&
                info.UniformBufferNames[e.UniformBuffer] == "TranslucentBasePass")
                return true;
        }
        foreach (var e in info.Textures)
        {
            if (e.Resource == resourceIndex &&
                e.UniformBuffer >= 0 && e.UniformBuffer < info.UniformBufferNames.Count &&
                info.UniformBufferNames[e.UniformBuffer] == "TranslucentBasePass")
                return true;
        }
        return false;
    }

    private static string Declared(Dictionary<int, EDxbcResourceKind> declarations)
        => string.Concat(declarations.OrderBy(x => x.Key).Select(x => $"declared t{x.Key}: {x.Value}\n"));

    private enum Source : byte
    {
        Fixed, View, TranslucentBasePass, Zeros, LocalVF, Primitive, TexCoords, Positions, Tangents, Colors, SceneColor, SceneDepth,
        White2D, NeutralYCoCg2D, DefaultUInt2D, DefaultUIntBuffer, DefaultUInt2DArray, DefaultUInt3D, DefaultCube, AmbientVolume3D, Noise2D, NoiseVolume3D, PreIntegratedGF, BlackDefault, Black2DOrWhite
    }
    private readonly record struct BufferBinding(uint Slot, Source Source, ID3D11Buffer? Buffer);
    private readonly record struct ViewBinding(uint Register, Source Source, ID3D11ShaderResourceView? View, EDxbcResourceKind BlackKind = EDxbcResourceKind.Texture2D);
    private readonly record struct SamplerBinding(uint Register, bool Clamp);
    private sealed class Stage
    {
        public BufferBinding[] Buffers = [];
        public ViewBinding[] Views = [];
        public SamplerBinding[] Samplers = [];
    }
    private Stage? _vertexStage, _pixelStage;
    private Stage? _depthVertexStage, _depthPixelStage;

    private static ID3D11InputLayout CreateCompatibleInputLayout(ID3D11Device device, byte[] vsBytecode)
    {
        var sig = DxbcResourceDeclarations.ReadInputSignature(vsBytecode);
        var descs = new List<InputElementDescription>();
        foreach (var el in sig)
        {
            if (el.SystemValueType != 0 || el.SemanticName.StartsWith("SV_", StringComparison.OrdinalIgnoreCase))
                continue;

            if (el.SemanticName.Equals("ATTRIBUTE", StringComparison.OrdinalIgnoreCase) && el.SemanticIndex == 0)
            {
                var fmt = el.ComponentType == 1 ? Format.R32G32B32_UInt : (el.ComponentType == 2 ? Format.R32G32B32_SInt : Format.R32G32B32_Float);
                descs.Add(new InputElementDescription(el.SemanticName, (uint) el.SemanticIndex, fmt, 0, 0, InputClassification.PerVertexData, 0));
            }
            else if (el.SemanticName.Equals("ATTRIBUTE", StringComparison.OrdinalIgnoreCase) && el.SemanticIndex == 13)
            {
                var fmt = el.ComponentType == 2 ? Format.R32_SInt : (el.ComponentType == 3 ? Format.R32_Float : Format.R32_UInt);
                descs.Add(new InputElementDescription(el.SemanticName, (uint) el.SemanticIndex, fmt, 0, 1, InputClassification.PerInstanceData, 1));
            }
            else
            {
                // Auxiliary attributes (e.g., NiagaraMeshVF ATTRIBUTE1..4 or tangent streams) map safely from slot 0
                var scalarFmt = el.ComponentType switch
                {
                    1 => el.Mask switch { 1 => Format.R32_UInt, 3 => Format.R32G32_UInt, 7 => Format.R32G32B32_UInt, _ => Format.R32G32B32A32_UInt },
                    2 => el.Mask switch { 1 => Format.R32_SInt, 3 => Format.R32G32_SInt, 7 => Format.R32G32B32_SInt, _ => Format.R32G32B32A32_SInt },
                    _ => el.Mask switch { 1 => Format.R32_Float, 3 => Format.R32G32_Float, 7 => Format.R32G32B32_Float, _ => Format.R32G32B32A32_Float }
                };
                descs.Add(new InputElementDescription(el.SemanticName, (uint) el.SemanticIndex, scalarFmt, 0, 0, InputClassification.PerVertexData, 0));
            }
        }

        if (descs.Count == 0)
        {
            descs.Add(new InputElementDescription("ATTRIBUTE", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerVertexData, 0));
        }

        return device.CreateInputLayout(descs.ToArray(), vsBytecode);
    }

    private Stage BuildStage(NativeSharedResources shared, UnrealD3D11ShaderInfo info, Dictionary<int, EDxbcResourceKind> declarations, Dictionary<int, bool> isUintResource)
    {
        var stage = new Stage();
        var buffers = new List<BufferBinding>();
        for (var slot = 0; slot < info.UniformBufferNames.Count; slot++)
        {
            buffers.Add(info.UniformBufferNames[slot] switch
            {
                "View" => new BufferBinding((uint) slot, Source.View, null),
                "TranslucentBasePass" => new BufferBinding((uint) slot, Source.TranslucentBasePass, null),
                "LocalVF" => new BufferBinding((uint) slot, Source.LocalVF, null),
                "Material" => new BufferBinding((uint) slot, Source.Fixed, _material),
                var other when _namedBuffers.TryGetValue(other, out var named) => new BufferBinding((uint) slot, Source.Fixed, named),
                _ => new BufferBinding((uint) slot, Source.Zeros, null)
            });
        }
        stage.Buffers = buffers.ToArray();

        string Name(int ub) => ub < info.UniformBufferNames.Count ? info.UniformBufferNames[ub] : string.Empty;
        var views = new Dictionary<int, ViewBinding>();
        var srvSet = info.ShaderResourceViews.ToHashSet();

        foreach (var e in info.ShaderResourceViews.Concat(info.Textures))
        {
            var register = (uint) e.Register;
            var ubName = Name(e.UniformBuffer);
            var declKind = declarations.GetValueOrDefault(e.Register, EDxbcResourceKind.Texture2D);
            var isUint = isUintResource.GetValueOrDefault(e.Register);
            var isMaterialSrv = ubName == "Material" && srvSet.Contains(e);

            ViewBinding? binding = (ubName, e.Resource) switch
            {
                ("View", 48) => new ViewBinding(register, Source.Primitive, null),
                ("View", _) when declKind == EDxbcResourceKind.Structured => new ViewBinding(register, Source.Primitive, null),
                ("View", _) when declKind == EDxbcResourceKind.TextureCube => new ViewBinding(register, Source.DefaultCube, null),
                ("View", 58) when !isUint && declKind == EDxbcResourceKind.Texture3D => new ViewBinding(register, Source.NoiseVolume3D, null),
                ("View", _) when !isUint && declKind == EDxbcResourceKind.Texture3D => new ViewBinding(register, Source.AmbientVolume3D, null),
                ("View", 56) when !isUint && declKind == EDxbcResourceKind.Texture2D => new ViewBinding(register, Source.Black2DOrWhite, null),
                ("View", >= 60 and <= 66) when !isUint && declKind == EDxbcResourceKind.Texture2D => new ViewBinding(register, Source.Noise2D, null),
                ("View", _) when !isUint && declKind == EDxbcResourceKind.Texture2D => new ViewBinding(register, Source.PreIntegratedGF, null),
                ("TranslucentBasePass", 24) when !isUint && declKind == EDxbcResourceKind.Texture2D => new ViewBinding(register, Source.PreIntegratedGF, null),
                ("TranslucentBasePass", 36) when !isUint && declKind == EDxbcResourceKind.Texture2D => new ViewBinding(register, Source.SceneDepth, null),
                ("TranslucentBasePass", 67) when !isUint && declKind == EDxbcResourceKind.Texture2D => new ViewBinding(register, Source.SceneColor, null),
                ("TranslucentBasePass", _) when declKind == EDxbcResourceKind.TextureCube => new ViewBinding(register, Source.DefaultCube, null),
                ("TranslucentBasePass", _) when !isUint && declKind == EDxbcResourceKind.Texture3D => new ViewBinding(register, Source.AmbientVolume3D, null),
                ("TranslucentBasePass", _) when !isUint && declKind == EDxbcResourceKind.Texture2D => new ViewBinding(register, Source.White2D, null),
                ("LocalVF", 0) => new ViewBinding(register, Source.TexCoords, null),
                ("LocalVF", 1 or 2) => new ViewBinding(register, Source.Positions, null),
                ("LocalVF", 3) => new ViewBinding(register, Source.Tangents, null),
                ("LocalVF", 4) => new ViewBinding(register, Source.Colors, null),
                ("Material", _) when isUint => FallbackBinding(register, declKind, true),
                ("Material", var r) when declKind == EDxbcResourceKind.TextureCube && (r < _materialTexturesCube.Length ? _materialTexturesCube[r] : null) is { } cubeTex => new ViewBinding(register, Source.Fixed, cubeTex),
                ("Material", _) when declKind == EDxbcResourceKind.TextureCube => new ViewBinding(register, Source.DefaultCube, null),
                ("Material", var r) when (r < _materialTextures2D.Length ? _materialTextures2D[r] : null) is { } texture => new ViewBinding(register, Source.Fixed, texture),
                ("Material", _) when isMaterialSrv && declKind is EDxbcResourceKind.Texture2D => new ViewBinding(register, Source.NeutralYCoCg2D, null),
                ("Material", _) when declKind is EDxbcResourceKind.Texture2D => new ViewBinding(register, Source.White2D, null),
                _ => null
            };
            views[e.Register] = binding ?? FallbackBinding(register, declKind, isUint);
        }
        foreach (var (register, kind) in declarations)
            if (!views.ContainsKey(register))
            {
                var regUint = isUintResource.GetValueOrDefault(register);
                if (regUint && kind is EDxbcResourceKind.Buffer or EDxbcResourceKind.Texture2D or EDxbcResourceKind.Texture2DArray or EDxbcResourceKind.Texture3D)
                    views[register] = FallbackBinding((uint) register, kind, true);
                else if (shared.BlackDefaults.ContainsKey(kind))
                    views[register] = new ViewBinding((uint) register, Source.BlackDefault, null, kind);
            }
        stage.Views = views.Values.Where(x => x.Source != Source.Fixed || x.View is not null).ToArray();

        stage.Samplers = info.Samplers.Select(e =>
        {
            var ubName = Name(e.UniformBuffer);
            bool clamp;
            if (ubName == "Material" && _materialSamplerSources is not null && e.Resource >= 0 && e.Resource < _materialSamplerSources.Count && _materialSamplerSources[e.Resource] is { } mode)
            {
                clamp = mode == ESamplerSourceMode.SSM_Clamp_WorldGroupSettings ||
                        (mode == ESamplerSourceMode.SSM_FromTextureAsset &&
                         e.Resource < _materialTexturesClamp.Length && _materialTexturesClamp[e.Resource]);
            }
            else
            {
                clamp = ubName != "Material" && e.Register > 7;
            }
            return new SamplerBinding((uint) e.Register, clamp);
        }).ToArray();
        return stage;

        static ViewBinding FallbackBinding(uint reg, EDxbcResourceKind kind, bool uintRes) => uintRes
            ? kind switch
            {
                EDxbcResourceKind.Buffer => new ViewBinding(reg, Source.DefaultUIntBuffer, null),
                EDxbcResourceKind.Texture2D => new ViewBinding(reg, Source.DefaultUInt2D, null),
                EDxbcResourceKind.Texture2DArray => new ViewBinding(reg, Source.DefaultUInt2DArray, null),
                EDxbcResourceKind.Texture3D => new ViewBinding(reg, Source.DefaultUInt3D, null),
                _ => new ViewBinding(reg, Source.BlackDefault, null, kind)
            }
            : new ViewBinding(reg, Source.BlackDefault, null, kind);
    }

    public void BindMaterial(ID3D11DeviceContext ctx, NativeSharedResources shared)
    {
        Stage vs, ps;
        lock (_syncLock)
        {
            vs = _vertexStage ??= BuildStage(shared, _vsInfo, _vsDeclarations, _vsUintResources);
            ps = _pixelStage ??= BuildStage(shared, _psInfo, _psDeclarations, _psUintResources);
        }
        ctx.IASetInputLayout(InputLayout);
        ctx.VSSetShader(VertexShader);
        ctx.PSSetShader(PixelShader);
        BindStage(ctx, shared, null, vs, vertex: true, meshOnly: false);
        BindStage(ctx, shared, null, ps, vertex: false, meshOnly: false);
    }

    public void BindMesh(ID3D11DeviceContext ctx, NativeSharedResources shared, NativeMesh mesh)
    {
        Stage? vs, ps;
        lock (_syncLock)
        {
            vs = _vertexStage;
            ps = _pixelStage;
        }
        if (vs is null || ps is null) return;
        BindStage(ctx, shared, mesh, vs, vertex: true, meshOnly: true);
        BindStage(ctx, shared, mesh, ps, vertex: false, meshOnly: true);
    }

    public void BindDepthOnlyMaterial(ID3D11DeviceContext ctx, NativeSharedResources shared)
    {
        if (DepthOnlyVertexShader is null || DepthOnlyPixelShader is null || DepthOnlyInputLayout is null)
            return;
        Stage dvs, dps;
        lock (_syncLock)
        {
            dvs = _depthVertexStage ??= BuildStage(shared, _depthVsInfo!, _depthVsDeclarations!, _depthVsUintResources!);
            dps = _depthPixelStage ??= BuildStage(shared, _depthPsInfo!, _depthPsDeclarations!, _depthPsUintResources!);
        }
        ctx.IASetInputLayout(DepthOnlyInputLayout);
        ctx.VSSetShader(DepthOnlyVertexShader);
        ctx.PSSetShader(DepthOnlyPixelShader);
        BindStage(ctx, shared, null, dvs, vertex: true, meshOnly: false);
        BindStage(ctx, shared, null, dps, vertex: false, meshOnly: false);
    }

    public void BindDepthOnlyMesh(ID3D11DeviceContext ctx, NativeSharedResources shared, NativeMesh mesh)
    {
        Stage? dvs, dps;
        lock (_syncLock)
        {
            dvs = _depthVertexStage;
            dps = _depthPixelStage;
        }
        if (dvs is null || dps is null)
            return;
        BindStage(ctx, shared, mesh, dvs, vertex: true, meshOnly: true);
        BindStage(ctx, shared, mesh, dps, vertex: false, meshOnly: true);
    }

    private static void BindStage(ID3D11DeviceContext ctx, NativeSharedResources shared, NativeMesh? mesh, Stage stage, bool vertex, bool meshOnly)
    {
        foreach (var b in stage.Buffers)
        {
            var isMesh = b.Source == Source.LocalVF;
            if (isMesh != meshOnly)
                continue;
            var buffer = b.Source switch
            {
                Source.View => shared.View,
                Source.TranslucentBasePass => shared.TranslucentBasePass,
                Source.Zeros => shared.Zeros,
                Source.LocalVF => mesh!.LocalVF,
                _ => b.Buffer!
            };
            if (vertex)
                ctx.VSSetConstantBuffer(b.Slot, buffer);
            else
                ctx.PSSetConstantBuffer(b.Slot, buffer);
        }

        foreach (var v in stage.Views)
        {
            var isMesh = v.Source is Source.TexCoords or Source.Positions or Source.Tangents or Source.Colors;
            if (isMesh != meshOnly)
                continue;
            var view = v.Source switch
            {
                Source.Primitive => shared.PrimitiveData,
                Source.SceneColor => shared.SceneColorCopy,
                Source.SceneDepth => shared.SceneDepthCopy,
                Source.TexCoords => mesh!.TexCoords,
                Source.Positions => mesh!.PositionsSrv,
                Source.Tangents => mesh!.Tangents,
                Source.Colors => mesh!.Colors,
                Source.White2D => shared.White2D,
                Source.NeutralYCoCg2D => shared.NeutralYCoCg2D,
                Source.DefaultUInt2D => shared.DefaultUInt2D,
                Source.DefaultUIntBuffer => shared.DefaultUIntBuffer,
                Source.DefaultUInt2DArray => shared.DefaultUInt2DArray,
                Source.DefaultUInt3D => shared.DefaultUInt3D,
                Source.DefaultCube => shared.DefaultCube,
                Source.AmbientVolume3D => shared.AmbientVolume3D,
                Source.Noise2D => shared.Noise2D ?? shared.White2D,
                Source.NoiseVolume3D => shared.NoiseVolume3D ?? shared.AmbientVolume3D,
                Source.PreIntegratedGF => shared.PreIntegratedGF ?? shared.White2D,
                Source.BlackDefault => shared.BlackDefaults.GetValueOrDefault(v.BlackKind),
                Source.Black2DOrWhite => shared.BlackDefaults.GetValueOrDefault(EDxbcResourceKind.Texture2D) ?? shared.White2D,
                _ => v.View!
            };
            if (view is not null)
            {
                if (vertex)
                    ctx.VSSetShaderResource(v.Register, view);
                else
                    ctx.PSSetShaderResource(v.Register, view);
            }
        }

        if (meshOnly)
            return;
        foreach (var sampler in stage.Samplers)
        {
            var state = sampler.Clamp ? shared.Clamp : shared.Wrap;
            if (vertex)
                ctx.VSSetSampler(sampler.Register, state);
            else
                ctx.PSSetSampler(sampler.Register, state);
        }
    }

    public unsafe void UpdateMaterialConstants(ID3D11DeviceContext ctx, byte[] constants)
    {
        if (constants is null)
            return;
        var size = (int) _material.Description.ByteWidth;
        var copyLen = Math.Min(size, constants.Length);
        var mapped = ctx.Map(_material, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var dst = new Span<byte>((void*) mapped.DataPointer, size);
            constants.AsSpan(0, copyLen).CopyTo(dst);
            if (copyLen < size)
                dst[copyLen..].Clear();
        }
        finally
        {
            ctx.Unmap(_material, 0);
        }
    }

    public bool UpdateTextureOverrides(ID3D11Device device, IFileProvider? provider, MaterialAsset mat)
    {
        lock (_syncLock)
            return UpdateTextureOverridesLocked(device, provider, mat);
    }

    private bool UpdateTextureOverridesLocked(ID3D11Device device, IFileProvider? provider, MaterialAsset mat)
    {
        var shaderSet = _shaderSet ?? mat.GetNativeShaders(validateOnDevice: false).FirstOrDefault(s => s.UniformSet is not null);
        if (shaderSet is null)
            return false;

        var (resources, samplerSources) = mat.ResolveNativeMaterialResourceLayout(shaderSet, provider);
        var count = Math.Min(_materialTextures2D.Length, resources.Length);
        var changed = false;

        for (var i = 0; i < count; i++)
        {
            ID3D11ShaderResourceView? srv2D = null;
            ID3D11ShaderResourceView? srvCube = null;
            var clamp = false;
            if (resources[i] is { } tex)
            {
                srv2D = NativeTextureCache.Get(device, tex, asCube: false);
                srvCube = NativeTextureCache.Get(device, tex, asCube: true);
                clamp = tex is UTexture2D t2d &&
                    (t2d.AddressX == TextureAddress.TA_Clamp || t2d.AddressY == TextureAddress.TA_Clamp);
            }

            if (!ReferenceEquals(_materialTextures2D[i], srv2D) ||
                !ReferenceEquals(_materialTexturesCube[i], srvCube) ||
                _materialTexturesClamp[i] != clamp)
            {
                _materialTextures2D[i] = srv2D;
                _materialTexturesCube[i] = srvCube;
                _materialTexturesClamp[i] = clamp;
                changed = true;
            }
        }

        _materialSamplerSources = samplerSources;
        if (changed)
        {
            _vertexStage = null;
            _pixelStage = null;
            _depthVertexStage = null;
            _depthPixelStage = null;
        }
        return changed;
    }

    public bool EnsureParametersSynced(ID3D11Device device, ID3D11DeviceContext ctx, IFileProvider? provider, MaterialAsset mat)
    {
        var targetVersion = mat.ParameterVersion;
        if (Volatile.Read(ref _syncedParameterVersion) == targetVersion)
            return false;

        lock (_syncLock)
        {
            targetVersion = mat.ParameterVersion;
            if (Volatile.Read(ref _syncedParameterVersion) == targetVersion)
                return false;

            UpdateTextureOverridesLocked(device, provider, mat);
            var shaderSet = _shaderSet ?? mat.GetNativeShaders(validateOnDevice: false).FirstOrDefault(s => s.UniformSet is not null);
            if (shaderSet is not null && mat.BuildNativeMaterialBuffer(shaderSet) is { } buf)
                UpdateMaterialConstants(ctx, buf.Constants);

            Volatile.Write(ref _syncedParameterVersion, targetVersion);
            return true;
        }
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
            _owned[i].Dispose();
    }
}

public sealed class PreparedTextureData
{
    public Format Format { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int MipCount { get; init; }
    public bool IsCube { get; init; }
    public bool IsFloat { get; init; }
    public bool IsClamp { get; init; }
    public Rgba32[][]? RgbaSubresources { get; init; }
    public Half[][]? HalfSubresources { get; init; }
    public int[] SubresourceWidths { get; init; } = [];
}

public static class NativeTextureCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<ID3D11ShaderResourceView?>> _cache = new();
    public static int MaxTextureSize = 512;

    public static ID3D11ShaderResourceView? Get(ID3D11Device device, UTexture texture, bool asCube = false, bool forceLinearUncompressed = false)
    {
        var key = $"{texture.GetPathName()}|cube={asCube}|lin={forceLinearUncompressed}";
        return _cache.GetOrAdd(key, _ => new Lazy<ID3D11ShaderResourceView?>(() => Load(device, texture, asCube, forceLinearUncompressed), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public static PreparedTextureData? PrepareCpu(UTexture texture, bool asCube = false, bool forceLinearUncompressed = false, int? maxSizeOverride = null)
    {
        try
        {
            var maxMip = maxSizeOverride ?? (forceLinearUncompressed ? 2048 : Math.Min(ExplorerBridge.MaxTextureMipSize, MaxTextureSize));
            var decoded = texture.Decode(maxMip, ExplorerBridge.ToExportOptions());
            if (decoded is null)
                return null;

            var isClamp = texture is UTexture2D t2d &&
                (t2d.AddressX == TextureAddress.TA_Clamp || t2d.AddressY == TextureAddress.TA_Clamp);

            if (decoded.PixelFormat == EPixelFormat.PF_FloatRGBA && decoded.Data.Length >= decoded.Width * decoded.Height * 8)
            {
                return PrepareFloatRgba(decoded, asCube, isClamp);
            }

            using var image = decoded.ToImage();
            var pixels = new Rgba32[image.Width * image.Height];
            image.CopyPixelDataTo(pixels);

            var isNormal = texture.CompressionSettings == TextureCompressionSettings.TC_Normalmap;
            var format = !forceLinearUncompressed && !isNormal && texture.SRGB ? Format.R8G8B8A8_UNorm_SRgb : Format.R8G8B8A8_UNorm;

            if (asCube)
            {
                var faceSize = Math.Max(1, Math.Min(image.Width, image.Height));
                var hasSixStackedFaces = image.Height >= faceSize * 6;
                var faceMips = new List<(Rgba32[] Pixels, int Width, int Height)>[6];
                for (var f = 0; f < 6; f++)
                {
                    var facePixels = new Rgba32[faceSize * faceSize];
                    var yBase = hasSixStackedFaces ? f * faceSize : 0;
                    for (var y = 0; y < faceSize; y++)
                        for (var x = 0; x < faceSize; x++)
                            facePixels[y * faceSize + x] = pixels[(yBase + y) * image.Width + x];
                    faceMips[f] = BuildCpuMipChain(facePixels, faceSize, faceSize, isNormal: false);
                }

                var mipCount = faceMips[0].Count;
                var subs = new Rgba32[6 * mipCount][];
                var widths = new int[6 * mipCount];
                for (var f = 0; f < 6; f++)
                {
                    for (var m = 0; m < mipCount; m++)
                    {
                        var idx = f * mipCount + m;
                        subs[idx] = faceMips[f][m].Pixels;
                        widths[idx] = faceMips[f][m].Width;
                    }
                }
                return new PreparedTextureData
                {
                    Format = format,
                    Width = faceSize,
                    Height = faceSize,
                    MipCount = mipCount,
                    IsCube = true,
                    IsFloat = false,
                    IsClamp = isClamp,
                    RgbaSubresources = subs,
                    SubresourceWidths = widths
                };
            }

            var isNoiseOrDither = texture.Name.Contains("Noise", StringComparison.OrdinalIgnoreCase) ||
                                  texture.Name.Contains("Dither", StringComparison.OrdinalIgnoreCase) ||
                                  (image.Width <= 64 && image.Height <= 64 && texture.CompressionSettings == TextureCompressionSettings.TC_Grayscale);

            if (!forceLinearUncompressed && !isNoiseOrDither && image.Width >= 4 && image.Height >= 4)
            {
                var mipMaps = BuildCpuMipChain(pixels, image.Width, image.Height, isNormal);
                var subs = new Rgba32[mipMaps.Count][];
                var widths = new int[mipMaps.Count];
                for (var m = 0; m < mipMaps.Count; m++)
                {
                    subs[m] = mipMaps[m].Pixels;
                    widths[m] = mipMaps[m].Width;
                }
                return new PreparedTextureData
                {
                    Format = format,
                    Width = image.Width,
                    Height = image.Height,
                    MipCount = mipMaps.Count,
                    IsCube = false,
                    IsFloat = false,
                    IsClamp = isClamp,
                    RgbaSubresources = subs,
                    SubresourceWidths = widths
                };
            }

            return new PreparedTextureData
            {
                Format = format,
                Width = image.Width,
                Height = image.Height,
                MipCount = 1,
                IsCube = false,
                IsFloat = false,
                IsClamp = isClamp,
                RgbaSubresources = [pixels],
                SubresourceWidths = [image.Width]
            };
        }
        catch (Exception e)
        {
            Log.Warning(e, "Native renderer: could not prepare {Texture}", texture.Name);
            return null;
        }
    }

    public static ID3D11ShaderResourceView? UploadPrepared(ID3D11Device device, PreparedTextureData cpu)
    {
        if (cpu.Width <= 0 || cpu.Height <= 0 || cpu.MipCount <= 0)
            return null;

        try
        {
            if (cpu.IsFloat && cpu.HalfSubresources is { } halfSubs)
            {
                var desc = cpu.IsCube
                    ? new Texture2DDescription(cpu.Format, (uint) cpu.Width, (uint) cpu.Height, 6, (uint) cpu.MipCount, BindFlags.ShaderResource, ResourceUsage.Immutable, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.TextureCube)
                    : new Texture2DDescription(cpu.Format, (uint) cpu.Width, (uint) cpu.Height, 1, (uint) cpu.MipCount, BindFlags.ShaderResource, ResourceUsage.Immutable);

                var handles = new GCHandle[halfSubs.Length];
                var subresources = new SubresourceData[halfSubs.Length];
                try
                {
                    for (var i = 0; i < halfSubs.Length; i++)
                    {
                        handles[i] = GCHandle.Alloc(halfSubs[i], GCHandleType.Pinned);
                        subresources[i] = new SubresourceData(handles[i].AddrOfPinnedObject(), (uint) (cpu.SubresourceWidths[i] * 8));
                    }
                    using var tex = device.CreateTexture2D(desc, subresources);
                    var dim = cpu.IsCube ? ShaderResourceViewDimension.TextureCube : ShaderResourceViewDimension.Texture2D;
                    return device.CreateShaderResourceView(tex, new ShaderResourceViewDescription(tex, dim, cpu.Format, 0, (uint) cpu.MipCount));
                }
                finally
                {
                    for (var i = 0; i < handles.Length; i++)
                        if (handles[i].IsAllocated)
                            handles[i].Free();
                }
            }

            if (cpu.RgbaSubresources is { } rgbaSubs)
            {
                if (!cpu.IsCube && cpu.MipCount == 1)
                {
                    using var tex = device.CreateTexture2D(rgbaSubs[0].AsSpan(), cpu.Format, (uint) cpu.Width, (uint) cpu.Height);
                    return device.CreateShaderResourceView(tex);
                }

                var desc = cpu.IsCube
                    ? new Texture2DDescription(cpu.Format, (uint) cpu.Width, (uint) cpu.Height, 6, (uint) cpu.MipCount, BindFlags.ShaderResource, ResourceUsage.Immutable, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.TextureCube)
                    : new Texture2DDescription(cpu.Format, (uint) cpu.Width, (uint) cpu.Height, 1, (uint) cpu.MipCount, BindFlags.ShaderResource, ResourceUsage.Immutable);

                var handles = new GCHandle[rgbaSubs.Length];
                var subresources = new SubresourceData[rgbaSubs.Length];
                try
                {
                    for (var i = 0; i < rgbaSubs.Length; i++)
                    {
                        handles[i] = GCHandle.Alloc(rgbaSubs[i], GCHandleType.Pinned);
                        subresources[i] = new SubresourceData(handles[i].AddrOfPinnedObject(), (uint) (cpu.SubresourceWidths[i] * 4));
                    }
                    using var tex = device.CreateTexture2D(desc, subresources);
                    var dim = cpu.IsCube ? ShaderResourceViewDimension.TextureCube : ShaderResourceViewDimension.Texture2D;
                    return device.CreateShaderResourceView(tex, new ShaderResourceViewDescription(tex, dim, cpu.Format, 0, (uint) cpu.MipCount));
                }
                finally
                {
                    for (var i = 0; i < handles.Length; i++)
                        if (handles[i].IsAllocated)
                            handles[i].Free();
                }
            }

            return null;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Native renderer: could not upload prepared texture ({W}x{H})", cpu.Width, cpu.Height);
            return null;
        }
    }

    private static ID3D11ShaderResourceView? Load(ID3D11Device device, UTexture texture, bool asCube, bool forceLinearUncompressed)
    {
        var prepared = PrepareCpu(texture, asCube, forceLinearUncompressed);
        return prepared is not null ? UploadPrepared(device, prepared) : null;
    }

    private static PreparedTextureData PrepareFloatRgba(CTexture decoded, bool asCube, bool isClamp)
    {
        var halfData = new Half[decoded.Width * decoded.Height * 4];
        Buffer.BlockCopy(decoded.Data, 0, halfData, 0, halfData.Length * 2);

        if (asCube)
        {
            var faceSize = Math.Max(1, Math.Min(decoded.Width, decoded.Height));
            var hasSixStackedFaces = decoded.Height >= faceSize * 6;
            var faceMips = new List<(Half[] Pixels, int Width, int Height)>[6];
            for (var f = 0; f < 6; f++)
            {
                var facePixels = new Half[faceSize * faceSize * 4];
                var yBase = hasSixStackedFaces ? f * faceSize : 0;
                for (var y = 0; y < faceSize; y++)
                {
                    Buffer.BlockCopy(
                        halfData,
                        ((yBase + y) * decoded.Width) * 8,
                        facePixels,
                        (y * faceSize) * 8,
                        faceSize * 8);
                }
                faceMips[f] = BuildHalfMipChain(facePixels, faceSize, faceSize);
            }

            var mipCount = faceMips[0].Count;
            var subs = new Half[6 * mipCount][];
            var widths = new int[6 * mipCount];
            for (var f = 0; f < 6; f++)
            {
                for (var m = 0; m < mipCount; m++)
                {
                    var idx = f * mipCount + m;
                    subs[idx] = faceMips[f][m].Pixels;
                    widths[idx] = faceMips[f][m].Width;
                }
            }
            return new PreparedTextureData
            {
                Format = Format.R16G16B16A16_Float,
                Width = faceSize,
                Height = faceSize,
                MipCount = mipCount,
                IsCube = true,
                IsFloat = true,
                IsClamp = isClamp,
                HalfSubresources = subs,
                SubresourceWidths = widths
            };
        }
        else
        {
            var mips = BuildHalfMipChain(halfData, decoded.Width, decoded.Height);
            var subs = new Half[mips.Count][];
            var widths = new int[mips.Count];
            for (var m = 0; m < mips.Count; m++)
            {
                subs[m] = mips[m].Pixels;
                widths[m] = mips[m].Width;
            }
            return new PreparedTextureData
            {
                Format = Format.R16G16B16A16_Float,
                Width = decoded.Width,
                Height = decoded.Height,
                MipCount = mips.Count,
                IsCube = false,
                IsFloat = true,
                IsClamp = isClamp,
                HalfSubresources = subs,
                SubresourceWidths = widths
            };
        }
    }

    private static List<(Half[] Pixels, int Width, int Height)> BuildHalfMipChain(Half[] basePixels, int width, int height)
    {
        var chain = new List<(Half[] Pixels, int Width, int Height)>(10)
        {
            (basePixels, width, height)
        };

        var prev = basePixels;
        var prevW = width;
        var prevH = height;

        while (prevW > 1 || prevH > 1)
        {
            var nextW = Math.Max(1, prevW >> 1);
            var nextH = Math.Max(1, prevH >> 1);
            var next = new Half[nextW * nextH * 4];

            for (var y = 0; y < nextH; y++)
            {
                var y0 = Math.Min(prevH - 1, y * 2);
                var y1 = Math.Min(prevH - 1, y * 2 + 1);
                for (var x = 0; x < nextW; x++)
                {
                    var x0 = Math.Min(prevW - 1, x * 2);
                    var x1 = Math.Min(prevW - 1, x * 2 + 1);

                    var i00 = (y0 * prevW + x0) * 4;
                    var i10 = (y0 * prevW + x1) * 4;
                    var i01 = (y1 * prevW + x0) * 4;
                    var i11 = (y1 * prevW + x1) * 4;
                    var dst = (y * nextW + x) * 4;

                    for (var c = 0; c < 4; c++)
                    {
                        var avg = ((float) prev[i00 + c] + (float) prev[i10 + c] + (float) prev[i01 + c] + (float) prev[i11 + c]) * 0.25f;
                        next[dst + c] = (Half) avg;
                    }
                }
            }

            chain.Add((next, nextW, nextH));
            prev = next;
            prevW = nextW;
            prevH = nextH;
        }

        return chain;
    }

    private static List<(Rgba32[] Pixels, int Width, int Height)> BuildCpuMipChain(Rgba32[] basePixels, int width, int height, bool isNormal)
    {
        var chain = new List<(Rgba32[] Pixels, int Width, int Height)>(10)
        {
            (basePixels, width, height)
        };

        var prev = basePixels;
        var prevW = width;
        var prevH = height;

        while (prevW > 1 || prevH > 1)
        {
            var nextW = Math.Max(1, prevW >> 1);
            var nextH = Math.Max(1, prevH >> 1);
            var next = new Rgba32[nextW * nextH];

            for (var y = 0; y < nextH; y++)
            {
                var y0 = Math.Min(prevH - 1, y * 2);
                var y1 = Math.Min(prevH - 1, y * 2 + 1);
                for (var x = 0; x < nextW; x++)
                {
                    var x0 = Math.Min(prevW - 1, x * 2);
                    var x1 = Math.Min(prevW - 1, x * 2 + 1);

                    var p00 = prev[y0 * prevW + x0];
                    var p10 = prev[y0 * prevW + x1];
                    var p01 = prev[y1 * prevW + x0];
                    var p11 = prev[y1 * prevW + x1];

                    if (isNormal)
                    {
                        var nx = (p00.R + p10.R + p01.R + p11.R) * 0.25f / 127.5f - 1f;
                        var ny = (p00.G + p10.G + p01.G + p11.G) * 0.25f / 127.5f - 1f;
                        var nz = MathF.Sqrt(MathF.Max(0f, 1f - nx * nx - ny * ny));
                        var len = MathF.Sqrt(MathF.Max(1e-6f, nx * nx + ny * ny + nz * nz));
                        nx /= len;
                        ny /= len;
                        nz /= len;
                        next[y * nextW + x] = new Rgba32(
                            (byte) Math.Clamp((int) ((nx * 0.5f + 0.5f) * 255f), 0, 255),
                            (byte) Math.Clamp((int) ((ny * 0.5f + 0.5f) * 255f), 0, 255),
                            (byte) Math.Clamp((int) ((nz * 0.5f + 0.5f) * 255f), 0, 255),
                            255);
                    }
                    else
                    {
                        next[y * nextW + x] = new Rgba32(
                            (byte) ((p00.R + p10.R + p01.R + p11.R + 2) >> 2),
                            (byte) ((p00.G + p10.G + p01.G + p11.G + 2) >> 2),
                            (byte) ((p00.B + p10.B + p01.B + p11.B + 2) >> 2),
                            (byte) ((p00.A + p10.A + p01.A + p11.A + 2) >> 2));
                    }
                }
            }

            chain.Add((next, nextW, nextH));
            prev = next;
            prevW = nextW;
            prevH = nextH;
        }

        return chain;
    }
}
