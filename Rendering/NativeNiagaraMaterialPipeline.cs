using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Serilog;
using UExplorer.Scene;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UExplorer.Rendering;

/// <summary>
/// Evaluates genuine cooked UE Niagara pixel shaders (FNiagaraSpriteVertexFactory* / FNiagaraMeshVertexFactory* / FLocalVertexFactory)
/// paired with an ISGN-matched DX11 Adapter Vertex Shader, MaterialAsset preshaders, MaterialCollection* UBs, and shared scene textures.
/// </summary>
public sealed class NativeNiagaraMaterialPipeline : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly List<IDisposable> _owned = [];
    private readonly UnrealD3D11ShaderInfo _psInfo;
    private readonly Dictionary<int, EDxbcResourceKind> _psDeclarations;
    private readonly Dictionary<int, bool> _psUintResources;
    private readonly ID3D11VertexShader? _cookedVs;
    private readonly byte[]? _vsBytecode;
    private readonly UnrealD3D11ShaderInfo? _vsInfo;
    private readonly Dictionary<int, EDxbcResourceKind>? _vsDeclarations;
    private readonly Dictionary<int, bool>? _vsUintResources;
    private readonly Dictionary<(int UvChannels, bool HasColors), ID3D11InputLayout> _cookedInputLayouts = new();
    private readonly Dictionary<string, ID3D11Buffer> _namedBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ID3D11ShaderResourceView?[] _materialTextures2D;
    private readonly ID3D11ShaderResourceView?[] _materialTexturesCube;
    private readonly bool[] _materialTexturesClamp;
    private readonly IReadOnlyList<ESamplerSourceMode?>? _materialSamplerSources;
    private readonly ID3D11Buffer _materialCb;
    private readonly ID3D11Buffer? _niagaraMeshVfCb;
    private readonly ID3D11Buffer? _niagaraSpriteVfCb;
    private readonly ID3D11Buffer? _meshLooseCb;
    private readonly ID3D11Buffer? _primitiveIdentityCb;
    private readonly ID3D11ShaderResourceView? _dummyFloatSrv;
    private readonly ID3D11ShaderResourceView? _dummyIntSrv;

    public ID3D11VertexShader AdapterVertexShader { get; }
    public ID3D11InputLayout? AdapterInputLayout { get; }
    public ID3D11PixelShader PixelShader { get; }
    public ID3D11BlendState Blend { get; }
    public ENativeBlendMode BlendMode { get; }
    public bool TwoSided { get; }
    public bool IsMesh { get; }
    public bool UsesCookedVertexShader => _cookedVs is not null;
    public bool NeedsSceneColor { get; }
    public bool NeedsSceneDepth { get; }
    public string VertexFactory { get; }

    private enum Source : byte
    {
        Fixed, View, TranslucentBasePass, Zeros, Primitive, NiagaraMeshVF, NiagaraSpriteVF, NiagaraFloatData, SceneColor, SceneDepth,
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

    private readonly object _syncLock = new();
    private Stage? _vertexStage;
    private Stage? _pixelStage;

    private sealed record CachedAdapterVs(ID3D11VertexShader Vs, ID3D11InputLayout? InputLayout);
    private static readonly ConcurrentDictionary<string, CachedAdapterVs> _adapterVsCache = new(StringComparer.Ordinal);

    private T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }

    private NativeNiagaraMaterialPipeline(
        ID3D11Device device,
        NativeShaderEntry ps,
        NativeShaderEntry? vs,
        byte[] materialConstants,
        IReadOnlyList<UTexture?> materialResources,
        ENativeBlendMode blendMode,
        bool twoSided,
        IReadOnlyDictionary<string, byte[]>? namedBuffers,
        IReadOnlyList<ESamplerSourceMode?>? materialSamplerSources,
        bool isMesh,
        string vertexFactory)
    {
        if (ps.Code is null || ps.Container is not { } psContainer)
            throw new InvalidOperationException("Niagara material pixel shader missing bytecode");

        _device = device;
        IsMesh = isMesh;
        VertexFactory = vertexFactory;
        BlendMode = blendMode;
        TwoSided = twoSided;
        _materialSamplerSources = materialSamplerSources;

        try
        {
            _psInfo = UnrealD3D11ShaderInfo.Parse(ps.Code, psContainer);
            var psBytecode = ps.Bytecode.ToArray();
            _psDeclarations = DxbcResourceDeclarations.ReadWithTypes(psBytecode, out _psUintResources);
            var needsSceneColor = UsesTranslucentResource(_psInfo, 67);
            var needsSceneDepth = UsesTranslucentResource(_psInfo, 36);

            PixelShader = Own(device.CreatePixelShader(psBytecode));
            var isLocalVf = vertexFactory.Contains("LocalVertexFactory", StringComparison.OrdinalIgnoreCase);
            var adapter = GetOrCreateAdapterVs(device, psBytecode, isMesh, isLocalVf);
            AdapterVertexShader = adapter.Vs;
            AdapterInputLayout = adapter.InputLayout;

            if (vs is { Code: { } vsCode, Container: { } vsContainer })
            {
                var vsBytecode = vs.Bytecode.ToArray();
                _vsBytecode = vsBytecode;
                _vsInfo = UnrealD3D11ShaderInfo.Parse(vsCode, vsContainer);
                needsSceneColor |= UsesTranslucentResource(_vsInfo, 67);
                needsSceneDepth |= UsesTranslucentResource(_vsInfo, 36);
                _vsDeclarations = DxbcResourceDeclarations.ReadWithTypes(vsBytecode, out _vsUintResources);
                _cookedVs = Own(device.CreateVertexShader(vsBytecode));

                var looseData = new byte[64];
                BitConverter.TryWriteBytes(looseData.AsSpan(16, 4), -1); // SortedIndicesOffset = -1
                _meshLooseCb = Own(device.CreateBuffer(looseData.AsSpan(), BindFlags.ConstantBuffer));

                var primData = new float[20];
                primData[0] = 1f; primData[5] = 1f; primData[10] = 1f; primData[15] = 1f; // Identity LocalToWorld
                primData[16] = 1f; primData[17] = 1f; primData[18] = 1f; primData[19] = 1f; // InvScale=(1,1,1), DetSign=1
                _primitiveIdentityCb = Own(device.CreateBuffer(MemoryMarshal.AsBytes(primData.AsSpan()), BindFlags.ConstantBuffer));

                var dummyFloatBuf = Own(device.CreateBuffer(new float[4].AsSpan(), BindFlags.ShaderResource));
                _dummyFloatSrv = Own(device.CreateShaderResourceView(dummyFloatBuf, new ShaderResourceViewDescription(dummyFloatBuf, Format.R32_Float, 0, 4)));
                var dummyIntBuf = Own(device.CreateBuffer(new int[4].AsSpan(), BindFlags.ShaderResource));
                _dummyIntSrv = Own(device.CreateShaderResourceView(dummyIntBuf, new ShaderResourceViewDescription(dummyIntBuf, Format.R32_SInt, 0, 4)));
            }
            NeedsSceneColor = needsSceneColor;
            NeedsSceneDepth = needsSceneDepth;

            var material = new byte[Math.Max(256, (materialConstants.Length + 15) / 16 * 16)];
            materialConstants.CopyTo(material, 0);
            _materialCb = Own(device.CreateBuffer(material.AsSpan(), BindFlags.ConstantBuffer));

            foreach (var (name, data) in namedBuffers ?? new Dictionary<string, byte[]>())
            {
                var padded = new byte[Math.Max(16, (data.Length + 15) / 16 * 16)];
                data.CopyTo(padded, 0);
                _namedBuffers[name] = Own(device.CreateBuffer(padded.AsSpan(), BindFlags.ConstantBuffer));
            }

            if (UsesCookedVertexShader || _psInfo.UniformBufferNames.Contains("NiagaraMeshVF"))
                _niagaraMeshVfCb = Own(device.CreateBuffer(432, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            if (_psInfo.UniformBufferNames.Contains("NiagaraSpriteVF"))
            {
                var spriteVfData = new byte[80];
                BitConverter.TryWriteBytes(spriteVfData.AsSpan(60, 4), 0xFu);
                BitConverter.TryWriteBytes(spriteVfData.AsSpan(76, 4), 0xFu);
                _niagaraSpriteVfCb = Own(device.CreateBuffer(spriteVfData.AsSpan(), BindFlags.ConstantBuffer));
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
        catch
        {
            Dispose();
            throw;
        }
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

    public static NativeNiagaraMaterialPipeline? Create(ID3D11Device device, MaterialAsset node, bool isMesh, out string? error)
    {
        error = null;
        static int VfPriority(string vf, bool mesh)
        {
            if (mesh)
            {
                return vf switch
                {
                    "FNiagaraMeshVertexFactoryEx" => 0,
                    "FNiagaraMeshVertexFactory" => 1,
                    _ when vf.Contains("NiagaraMeshVertexFactory", StringComparison.OrdinalIgnoreCase) => 2,
                    "FMeshParticleVertexFactory" => 3,
                    "FLocalVertexFactory" => 4,
                    _ when vf.Contains("LocalVertexFactory", StringComparison.OrdinalIgnoreCase) => 5,
                    _ => 99
                };
            }
            return vf switch
            {
                "FNiagaraSpriteVertexFactoryEx" => 0,
                "FNiagaraSpriteVertexFactory" => 1,
                _ when vf.Contains("NiagaraSpriteVertexFactory", StringComparison.OrdinalIgnoreCase) => 2,
                "FGPUSpriteVertexFactory" => 3,
                "FParticleSpriteVertexFactory" => 4,
                "FLocalVertexFactory" => 5,
                _ when vf.Contains("LocalVertexFactory", StringComparison.OrdinalIgnoreCase) => 6,
                _ => 99
            };
        }

        static int PolicyPriority(NativeShaderEntry x) => x.ShaderType switch
        {
            "TBasePassPSFNoLightMapPolicy" when x.Pipeline.Length == 0 => 0,
            "TBasePassPSFNoLightMapPolicySkylight" when x.Pipeline.Length == 0 => 1,
            "TBasePassPSFNoLightMapPolicy" => 2,
            "TBasePassPSFNoLightMapPolicySkylight" => 3,
            _ => 4
        };

        for (var shaderSource = node; shaderSource != null; shaderSource = shaderSource.Parent)
        {
            var sets = shaderSource.GetNativeShaders();
            foreach (var set in sets)
            {
                if (set.UniformSet is null)
                    continue;

                var candidates = set.Shaders
                    .Where(x => x.Status == ENativeShaderStatus.Created &&
                                x.Code is not null && x.Container is not null &&
                                x.ShaderType.StartsWith("TBasePassPS", StringComparison.Ordinal) &&
                                VfPriority(x.VertexFactory, isMesh) < 99)
                    .OrderBy(x => VfPriority(x.VertexFactory, isMesh))
                    .ThenBy(PolicyPriority)
                    .ThenBy(x => x.Pipeline.Length)
                    .ThenBy(x => x.ShaderType, StringComparer.Ordinal)
                    .ToList();

                foreach (var ps in candidates)
                {
                    var buffer = node.BuildNativeMaterialBuffer(set);
                    if (buffer is null)
                        continue;

                    var (resources, samplerSources) = node.ResolveNativeMaterialResourceLayout(set);
                    var collections = node.ResolveNativeParameterCollections(set);
                    NativeShaderEntry? vs = null;
                    if (isMesh && ps.VertexFactory.Contains("NiagaraMeshVertexFactory", StringComparison.OrdinalIgnoreCase))
                    {
                        var expectedVs = "TBasePassVS" + ps.ShaderType["TBasePassPS".Length..];
                        vs = set.Shaders.FirstOrDefault(x =>
                                 x.Status == ENativeShaderStatus.Created &&
                                 x.Code is not null && x.Container is not null &&
                                 x.VertexFactory == ps.VertexFactory &&
                                 x.Pipeline == ps.Pipeline &&
                                 x.ShaderType == expectedVs)
                             ?? set.Shaders.FirstOrDefault(x =>
                                 x.Status == ENativeShaderStatus.Created &&
                                 x.Code is not null && x.Container is not null &&
                                 x.VertexFactory == ps.VertexFactory &&
                                 x.ShaderType.StartsWith("TBasePassVS", StringComparison.Ordinal));
                    }
                    try
                    {
                        return new NativeNiagaraMaterialPipeline(
                            device,
                            ps,
                            vs,
                            buffer.Constants,
                            resources,
                            (ENativeBlendMode) (int) node.BlendMode,
                            node.TwoSided,
                            collections,
                            samplerSources,
                            isMesh,
                            ps.VertexFactory);
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                        Log.Debug(ex, "Niagara material pipeline build failed for {Mat} ({Vf})", node.Name, ps.VertexFactory);
                    }
                }
            }
        }

        error ??= $"No compatible particle BasePassPS found for {node.Name} (isMesh={isMesh})";
        return null;
    }

    private static CachedAdapterVs GetOrCreateAdapterVs(ID3D11Device device, byte[] psBytecode, bool isMesh, bool isLocalVf)
    {
        var isgn = DxbcResourceDeclarations.ReadInputSignature(psBytecode);
        var elements = isgn
            .Where(el => el.SystemValueType == 0 && !el.SemanticName.StartsWith("SV_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(el => el.Register)
            .ToList();

        var sigKey = new StringBuilder();
        sigKey.Append(device.NativePointer.ToInt64()).Append('|')
              .Append(isMesh ? 'M' : 'S').Append('|')
              .Append(isLocalVf ? 'L' : 'N');
        foreach (var el in elements)
            sigKey.Append('|').Append(el.Register).Append(':').Append(el.SemanticName).Append(el.SemanticIndex).Append(':').Append(el.ComponentType);

        return _adapterVsCache.GetOrAdd(sigKey.ToString(), _ => CompileAdapterVs(device, elements, isMesh, isLocalVf));
    }

    private static CachedAdapterVs CompileAdapterVs(ID3D11Device device, List<DxbcInputElement> elements, bool isMesh, bool isLocalVf)
    {
        var sb = new StringBuilder();
        if (isMesh)
        {
            sb.AppendLine("""
                cbuffer MeshAdapterCb : register(b0)
                {
                    float4 WorldToClipRow0;
                    float4 WorldToClipRow1;
                    float4 WorldToClipRow2;
                    float4 WorldToClipRow3;
                    uint   UvChannelCount;
                    uint3  _Pad;
                };
                struct MeshInstance
                {
                    float4 Row0;
                    float4 Row1;
                    float4 Row2;
                    float4 Row3;
                    float4 Color;
                    float4 DynParam0;
                    float4 Meta;
                };
                StructuredBuffer<MeshInstance> Instances : register(t0);
                Buffer<float2> MeshUVs : register(t1);
                struct VSIn
                {
                    float3 Pos : ATTRIBUTE0;
                    uint   Vid : SV_VertexID;
                    uint   Iid : SV_InstanceID;
                };
                """);
        }
        else
        {
            sb.AppendLine("""
                cbuffer SpriteAdapterCb : register(b0)
                {
                    float4 WorldToClipRow0;
                    float4 WorldToClipRow1;
                    float4 WorldToClipRow2;
                    float4 WorldToClipRow3;
                    float4 CameraRight;
                    float4 CameraUp;
                    float4 LocalToWorldRow0;
                    float4 LocalToWorldRow1;
                    float4 LocalToWorldRow2;
                    float4 LocalToWorldRow3;
                    float4 SubImageInfo;
                };
                StructuredBuffer<float4> VmParticles : register(t0);
                struct VSIn
                {
                    uint Vid : SV_VertexID;
                    uint Iid : SV_InstanceID;
                };
                """);
        }

        sb.AppendLine("struct VSOut {");
        for (var i = 0; i < elements.Count; i++)
        {
            var el = elements[i];
            var noInterp = el.ComponentType is 1 or 2 ||
                           el.SemanticName.Equals("PARTICLE_RANDOM", StringComparison.OrdinalIgnoreCase) ||
                           (el.SemanticName.Equals("COLOR", StringComparison.OrdinalIgnoreCase) && el.SemanticIndex is 1 or 2);
            var typeName = el.ComponentType switch
            {
                1 => "uint4",
                2 => "int4",
                _ => "float4"
            };
            sb.Append("    ");
            if (noInterp) sb.Append("nointerpolation ");
            sb.AppendLine($"{typeName} f{i} : {el.SemanticName}{el.SemanticIndex};");
        }
        sb.AppendLine("    float4 SvPos : SV_Position;");
        sb.AppendLine("};");

        sb.AppendLine("VSOut VSMain(VSIn input) {");
        sb.AppendLine("    VSOut o;");
        if (isMesh)
        {
            sb.AppendLine("""
                    MeshInstance inst = Instances[input.Iid];
                    float3 worldPos = (input.Pos.xxxx * inst.Row0
                                     + input.Pos.yyyy * inst.Row1
                                     + input.Pos.zzzz * inst.Row2
                                     + inst.Row3).xyz;
                    o.SvPos = worldPos.xxxx * WorldToClipRow0
                            + worldPos.yyyy * WorldToClipRow1
                            + worldPos.zzzz * WorldToClipRow2
                            + WorldToClipRow3;
                    uint stride = max(UvChannelCount, 1u);
                    float2 uv0 = MeshUVs.Load((int)(input.Vid * stride + 0u));
                    float2 uv1 = stride > 1u ? MeshUVs.Load((int)(input.Vid * stride + 1u)) : uv0;
                """);
        }
        else
        {
            sb.AppendLine("""
                    float4 p0 = VmParticles[input.Iid * 4 + 0];
                    float4 p1 = VmParticles[input.Iid * 4 + 1];
                    float4 p2 = VmParticles[input.Iid * 4 + 2];
                    float4 p3 = VmParticles[input.Iid * 4 + 3];
                    float2 corner = float2((input.Vid == 1 || input.Vid == 2 || input.Vid == 4) ? 1.0 : -1.0,
                                           (input.Vid == 2 || input.Vid == 4 || input.Vid == 5) ? 1.0 : -1.0);
                    float2 rawUV = corner * float2(0.5, -0.5) + 0.5;
                    float2 centered = float2(rawUV.x - SubImageInfo.z, SubImageInfo.w - rawUV.y);
                    float s = sin(p2.y);
                    float c = cos(p2.y);
                    float2 scaled = centered * float2(p0.w, p2.x);
                    float2 rotOff = float2(scaled.x * c - scaled.y * s, scaled.x * s + scaled.y * c);
                    float3 worldCenter = (p0.xxxx * LocalToWorldRow0
                                        + p0.yyyy * LocalToWorldRow1
                                        + p0.zzzz * LocalToWorldRow2
                                        + LocalToWorldRow3).xyz;
                    float3 worldPos = worldCenter + CameraRight.xyz * rotOff.x + CameraUp.xyz * rotOff.y;
                    o.SvPos = worldPos.xxxx * WorldToClipRow0
                            + worldPos.yyyy * WorldToClipRow1
                            + worldPos.zzzz * WorldToClipRow2
                            + WorldToClipRow3;
                    float subU = max(SubImageInfo.x, 1.0);
                    float subV = max(SubImageInfo.y, 1.0);
                    float idx = floor(max(p2.z, 0.0));
                    float2 uv = (rawUV + float2(fmod(idx, subU), floor(idx / subU))) / float2(subU, subV);
                """);
        }

        for (var i = 0; i < elements.Count; i++)
        {
            var el = elements[i];
            var expr = MapOutputExpression(el, isMesh, isLocalVf);
            sb.AppendLine($"    o.f{i} = {expr};");
        }
        sb.AppendLine("    return o;");
        sb.AppendLine("}");

        var vsBytes = Compiler.Compile(sb.ToString(), "VSMain", isMesh ? "niagara_adapter_mesh_vs" : "niagara_adapter_sprite_vs", "vs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();
        var vs = device.CreateVertexShader(vsBytes);
        ID3D11InputLayout? il = null;
        if (isMesh)
        {
            il = device.CreateInputLayout(
            [
                new InputElementDescription("ATTRIBUTE", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerVertexData, 0)
            ], vsBytes);
        }
        return new CachedAdapterVs(vs, il);
    }

    private static string MapOutputExpression(DxbcInputElement el, bool isMesh, bool isLocalVf)
    {
        if (el.ComponentType == 1)
            return "uint4(0, 0, 0, 0)";
        if (el.ComponentType == 2)
            return "int4(0, 0, 0, 0)";

        var sem = el.SemanticName.ToUpperInvariant();
        if (sem == "TEXCOORD")
        {
            return el.SemanticIndex switch
            {
                0 when isMesh => "float4(uv0, uv1)",
                0 when isLocalVf => "float4(uv, uv)",
                0 => "p1",
                1 when isMesh => "float4(uv1, 0.0, 0.0)",
                1 => "float4(uv, 0.0, 0.0)",
                2 when !isMesh => "p3",
                7 => "float4(0.0, 0.0, 0.0, 1.0)", // VertexFog (zero inscatter, transmittance 1.0)
                _ => "float4(0.0, 0.0, 0.0, 0.0)"
            };
        }
        if (sem == "COLOR")
        {
            return el.SemanticIndex switch
            {
                0 or 1 => isMesh ? "inst.Color" : "p1",
                2 => isMesh ? "inst.DynParam0" : "p3",
                _ => "float4(0.0, 0.0, 0.0, 0.0)"
            };
        }
        if (sem == "PARTICLE_RANDOM")
            return isMesh ? "float4(inst.Meta.x, 0.0, 0.0, 0.0)" : "float4(p2.w, 0.0, 0.0, 0.0)";

        return "float4(0.0, 0.0, 0.0, 0.0)";
    }

    private ID3D11InputLayout GetOrCreateCookedInputLayout(int uvChannels, bool hasColors)
    {
        lock (_cookedInputLayouts)
        {
            if (_cookedInputLayouts.TryGetValue((uvChannels, hasColors), out var cached))
                return cached;

            var sig = DxbcResourceDeclarations.ReadInputSignature(_vsBytecode!);
            var descs = new List<InputElementDescription>();
            foreach (var el in sig)
            {
                if (el.SystemValueType != 0 || el.SemanticName.StartsWith("SV_", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (el.SemanticName.Equals("ATTRIBUTE", StringComparison.OrdinalIgnoreCase))
                {
                    switch (el.SemanticIndex)
                    {
                        case 0:
                            descs.Add(new InputElementDescription("ATTRIBUTE", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerVertexData, 0));
                            break;
                        case 1:
                            descs.Add(new InputElementDescription("ATTRIBUTE", 1, Format.R8G8B8A8_SNorm, 0, 1, InputClassification.PerVertexData, 0));
                            break;
                        case 2:
                            descs.Add(new InputElementDescription("ATTRIBUTE", 2, Format.R8G8B8A8_SNorm, 4, 1, InputClassification.PerVertexData, 0));
                            break;
                        case 3:
                            descs.Add(new InputElementDescription("ATTRIBUTE", 3, Format.R8G8B8A8_UNorm, 0, 3, InputClassification.PerVertexData, 0));
                            break;
                        case >= 4 and <= 11:
                            var uvByteOffset = Math.Clamp(el.SemanticIndex - 4, 0, Math.Max(0, uvChannels - 1)) * 8;
                            descs.Add(new InputElementDescription("ATTRIBUTE", (uint) el.SemanticIndex, Format.R32G32_Float, (uint) uvByteOffset, 2, InputClassification.PerVertexData, 0));
                            break;
                        default:
                            var fallbackFmt = el.ComponentType switch
                            {
                                1 => Format.R32G32B32A32_UInt,
                                2 => Format.R32G32B32A32_SInt,
                                _ => Format.R32G32B32A32_Float
                            };
                            descs.Add(new InputElementDescription("ATTRIBUTE", (uint) el.SemanticIndex, fallbackFmt, 0, 0, InputClassification.PerVertexData, 0));
                            break;
                    }
                }
                else
                {
                    var fallbackFmt = el.ComponentType switch
                    {
                        1 => Format.R32G32B32A32_UInt,
                        2 => Format.R32G32B32A32_SInt,
                        _ => Format.R32G32B32A32_Float
                    };
                    descs.Add(new InputElementDescription(el.SemanticName, (uint) el.SemanticIndex, fallbackFmt, 0, 0, InputClassification.PerVertexData, 0));
                }
            }

            if (descs.Count == 0)
                descs.Add(new InputElementDescription("ATTRIBUTE", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerVertexData, 0));

            var il = Own(_device.CreateInputLayout(descs.ToArray(), _vsBytecode!));
            _cookedInputLayouts[(uvChannels, hasColors)] = il;
            return il;
        }
    }

    private Stage BuildStage(NativeSharedResources shared, UnrealD3D11ShaderInfo info, Dictionary<int, EDxbcResourceKind> declarations, Dictionary<int, bool> uintResources, bool isVertex)
    {
        var stage = new Stage();
        var buffers = new List<BufferBinding>();
        for (var slot = 0; slot < info.UniformBufferNames.Count; slot++)
        {
            var ubName = info.UniformBufferNames[slot];
            buffers.Add(ubName switch
            {
                "" when isVertex && _meshLooseCb is not null => new BufferBinding((uint) slot, Source.Fixed, _meshLooseCb),
                "View" => new BufferBinding((uint) slot, Source.View, null),
                "TranslucentBasePass" => new BufferBinding((uint) slot, Source.TranslucentBasePass, null),
                "Primitive" => _primitiveIdentityCb is not null
                    ? new BufferBinding((uint) slot, Source.Fixed, _primitiveIdentityCb)
                    : new BufferBinding((uint) slot, Source.Zeros, null),
                "Material" => new BufferBinding((uint) slot, Source.Fixed, _materialCb),
                "NiagaraMeshVF" => new BufferBinding((uint) slot, Source.NiagaraMeshVF, _niagaraMeshVfCb),
                "NiagaraSpriteVF" => new BufferBinding((uint) slot, Source.NiagaraSpriteVF, _niagaraSpriteVfCb),
                var other when _namedBuffers.TryGetValue(other, out var named) => new BufferBinding((uint) slot, Source.Fixed, named),
                _ => new BufferBinding((uint) slot, Source.Zeros, null)
            });
        }
        stage.Buffers = buffers.ToArray();

        string Name(int ub) => ub < info.UniformBufferNames.Count ? info.UniformBufferNames[ub] : string.Empty;
        var views = new Dictionary<int, ViewBinding>();
        var srvSet = info.ShaderResourceViews.ToHashSet();
        var niagaraFloatAssigned = false;

        foreach (var e in info.ShaderResourceViews.Concat(info.Textures))
        {
            var register = (uint) e.Register;
            var ubName = Name(e.UniformBuffer);
            var declKind = declarations.GetValueOrDefault(e.Register, EDxbcResourceKind.Texture2D);
            var isUint = uintResources.GetValueOrDefault(e.Register);
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
                ("NiagaraMeshVF", _) when declKind == EDxbcResourceKind.Buffer && isUint => new ViewBinding(register, Source.Fixed, _dummyIntSrv),
                ("NiagaraMeshVF", _) when declKind == EDxbcResourceKind.Buffer && !niagaraFloatAssigned => MarkFloatAssigned(register, ref niagaraFloatAssigned),
                ("NiagaraMeshVF", _) when declKind == EDxbcResourceKind.Buffer => new ViewBinding(register, Source.Fixed, _dummyFloatSrv),
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
        foreach (var (register, kind) in declarations.OrderBy(kv => kv.Key))
        {
            if (!views.ContainsKey(register))
            {
                if (isVertex && kind == EDxbcResourceKind.Buffer)
                {
                    if (uintResources.GetValueOrDefault(register))
                        views[register] = new ViewBinding((uint) register, Source.Fixed, _dummyIntSrv);
                    else if (!niagaraFloatAssigned)
                        views[register] = MarkFloatAssigned((uint) register, ref niagaraFloatAssigned);
                    else
                        views[register] = new ViewBinding((uint) register, Source.Fixed, _dummyFloatSrv);
                    continue;
                }
                var regUint = uintResources.GetValueOrDefault(register);
                if (isVertex && !regUint && kind == EDxbcResourceKind.Texture3D && shared.AmbientVolume3D is not null)
                {
                    views[register] = new ViewBinding((uint) register, Source.AmbientVolume3D, null);
                    continue;
                }
                if (regUint && kind is EDxbcResourceKind.Buffer or EDxbcResourceKind.Texture2D or EDxbcResourceKind.Texture2DArray or EDxbcResourceKind.Texture3D)
                    views[register] = FallbackBinding((uint) register, kind, true);
                else if (shared.BlackDefaults.ContainsKey(kind))
                    views[register] = new ViewBinding((uint) register, Source.BlackDefault, null, kind);
            }
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

        static ViewBinding MarkFloatAssigned(uint reg, ref bool assigned)
        {
            assigned = true;
            return new ViewBinding(reg, Source.NiagaraFloatData, null);
        }

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

    private unsafe void UpdateMeshVfCb(
        ID3D11DeviceContext ctx,
        bool hasDynamicParams,
        uint particleStride = 0,
        uint facingMode = 0,
        bool lockedAxisEnable = false,
        Vector3 lockedAxis = default,
        uint lockedAxisSpace = 0)
    {
        if (_niagaraMeshVfCb is null)
            return;

        Span<byte> buf = stackalloc byte[432];
        buf.Clear();

        static void WInt(Span<byte> b, int off, int val) => BitConverter.TryWriteBytes(b.Slice(off, 4), val);
        static void WUInt(Span<byte> b, int off, uint val) => BitConverter.TryWriteBytes(b.Slice(off, 4), val);
        static void WFlt(Span<byte> b, int off, float val) => BitConverter.TryWriteBytes(b.Slice(off, 4), val);
        static void WVec4(Span<byte> b, int off, Vector4 v)
        {
            BitConverter.TryWriteBytes(b.Slice(off, 4), v.X);
            BitConverter.TryWriteBytes(b.Slice(off + 4, 4), v.Y);
            BitConverter.TryWriteBytes(b.Slice(off + 8, 4), v.Z);
            BitConverter.TryWriteBytes(b.Slice(off + 12, 4), v.W);
        }

        // MeshScale = (1, 1, 1) at cb4[2].xyz (+32)
        WFlt(buf, 32, 1f); WFlt(buf, 36, 1f); WFlt(buf, 40, 1f);
        // SubImageSize = (1, 1, 1, 1) at cb4[3] (+48)
        WVec4(buf, 48, Vector4.One);
        // DeltaSeconds at cb4[4].z (+72), MaterialParamValidMask at cb4[4].w (+76)
        WFlt(buf, 72, 1f / 60f);
        var mask = hasDynamicParams ? 0xFu : 0u;
        WUInt(buf, 76, mask);

        if (particleStride > 0)
        {
            // SoA float buffer layout: Pos[0..2], Color[3..6], Rot[7..10], Scale[11..13], DynParam[14..17], NormAge[18], MatRandom[19], Vel[20..22]
            WInt(buf, 80, 0);   // PositionDataOffset
            WInt(buf, 84, -1);  // PrevPositionDataOffset
            WInt(buf, 88, 20);  // VelocityDataOffset
            WInt(buf, 92, -1);  // PrevVelocityDataOffset

            WInt(buf, 96, 3);   // ColorDataOffset
            WInt(buf, 100, 7);  // RotationDataOffset
            WInt(buf, 104, -1); // PrevRotationDataOffset
            WInt(buf, 108, 11); // ScaleDataOffset

            WInt(buf, 112, -1); // PrevScaleDataOffset
            WInt(buf, 116, hasDynamicParams ? 14 : -1); // MaterialParamDataOffset
            WInt(buf, 120, -1); // MaterialParam1DataOffset
            WInt(buf, 124, -1); // MaterialParam2DataOffset

            WInt(buf, 128, -1); // MaterialParam3DataOffset
            WInt(buf, 132, 18); // NormalizedAgeDataOffset
            WInt(buf, 136, -1); // SubImageDataOffset
            WInt(buf, 140, 19); // MaterialRandomDataOffset

            WInt(buf, 144, -1); // CameraOffsetDataOffset
            WInt(buf, 148, -1); // PrevCameraOffsetDataOffset
        }
        else
        {
            for (var off = 80; off <= 148; off += 4)
                WInt(buf, off, -1);
        }

        // Defaults at cb4[10..23] (+160..+383)
        WVec4(buf, 160, new Vector4(0, 0, 0, 1)); // DefaultPos
        WVec4(buf, 176, new Vector4(0, 0, 0, 1)); // DefaultPrevPos
        WVec4(buf, 224, Vector4.One);             // DefaultColor
        WVec4(buf, 240, Vector4.UnitW);           // DefaultRotation (identity quat)
        WVec4(buf, 256, Vector4.UnitW);           // DefaultPrevRotation
        WVec4(buf, 272, new Vector4(1, 1, 1, 0)); // DefaultScale
        WVec4(buf, 288, new Vector4(1, 1, 1, 0)); // DefaultPrevScale
        WVec4(buf, 304, Vector4.One);             // DefaultDynamicMaterialParameter0
        WVec4(buf, 320, Vector4.One);             // DefaultDynamicMaterialParameter1
        WVec4(buf, 336, Vector4.One);             // DefaultDynamicMaterialParameter2
        WVec4(buf, 352, Vector4.One);             // DefaultDynamicMaterialParameter3

        // cb4[24] (+384): DefaultPrevCamOffset, SubImageBlendMode, FacingMode, bLockedAxisEnable
        WUInt(buf, 392, facingMode);
        WUInt(buf, 396, lockedAxisEnable ? 1u : 0u);
        // cb4[25] (+400): LockedAxis, LockedAxisSpace
        var la = lockedAxis.LengthSquared() > 1e-6f ? Vector3.Normalize(lockedAxis) : Vector3.UnitZ;
        WVec4(buf, 400, new Vector4(la, BitConverter.UInt32BitsToSingle(lockedAxisSpace)));
        // cb4[26].x (+416): NiagaraFloatDataStride
        WUInt(buf, 416, particleStride);

        var mapped = ctx.Map(_niagaraMeshVfCb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            buf.CopyTo(new Span<byte>((void*) mapped.DataPointer, 432));
        }
        finally { ctx.Unmap(_niagaraMeshVfCb, 0); }
    }

    public void BindMesh(
        ID3D11DeviceContext ctx,
        NativeSharedResources shared,
        NativeMesh mesh,
        ID3D11ShaderResourceView floatDataSrv,
        int particleStride,
        bool hasDynamicParams,
        uint facingMode,
        bool lockedAxisEnable,
        Vector3 lockedAxis,
        uint lockedAxisSpace)
    {
        Stage vs, ps;
        lock (_syncLock)
        {
            vs = _vertexStage ??= BuildStage(shared, _vsInfo!, _vsDeclarations!, _vsUintResources!, isVertex: true);
            ps = _pixelStage ??= BuildStage(shared, _psInfo, _psDeclarations, _psUintResources, isVertex: false);
        }
        UpdateMeshVfCb(ctx, hasDynamicParams, (uint) particleStride, facingMode, lockedAxisEnable, lockedAxis, lockedAxisSpace);

        ctx.IASetInputLayout(GetOrCreateCookedInputLayout(mesh.UvChannelCount, mesh.HasVertexColors));
        ctx.VSSetShader(_cookedVs);
        ctx.PSSetShader(PixelShader);
        ctx.OMSetBlendState(Blend);

        BindStageResources(ctx, shared, vs, isVertex: true, floatDataSrv);
        BindStageResources(ctx, shared, ps, isVertex: false, floatDataSrv);
    }

    public void Bind(ID3D11DeviceContext ctx, NativeSharedResources shared, bool hasDynamicParams)
    {
        Stage ps;
        lock (_syncLock)
        {
            ps = _pixelStage ??= BuildStage(shared, _psInfo, _psDeclarations, _psUintResources, isVertex: false);
        }
        if (_niagaraMeshVfCb is not null)
            UpdateMeshVfCb(ctx, hasDynamicParams);

        ctx.IASetInputLayout(AdapterInputLayout);
        ctx.VSSetShader(AdapterVertexShader);
        ctx.PSSetShader(PixelShader);
        ctx.OMSetBlendState(Blend);

        BindStageResources(ctx, shared, ps, isVertex: false, null);
    }

    private static void BindStageResources(
        ID3D11DeviceContext ctx,
        NativeSharedResources shared,
        Stage stage,
        bool isVertex,
        ID3D11ShaderResourceView? floatDataSrv)
    {
        foreach (var b in stage.Buffers)
        {
            var buffer = b.Source switch
            {
                Source.View => shared.View,
                Source.TranslucentBasePass => shared.TranslucentBasePass,
                Source.Zeros => shared.Zeros,
                Source.NiagaraMeshVF => b.Buffer ?? shared.Zeros,
                Source.NiagaraSpriteVF => b.Buffer ?? shared.Zeros,
                _ => b.Buffer ?? shared.Zeros
            };
            if (isVertex)
                ctx.VSSetConstantBuffer(b.Slot, buffer);
            else
                ctx.PSSetConstantBuffer(b.Slot, buffer);
        }

        foreach (var v in stage.Views)
        {
            var view = v.Source switch
            {
                Source.Primitive => shared.PrimitiveData,
                Source.SceneColor => shared.SceneColorCopy,
                Source.SceneDepth => shared.SceneDepthCopy,
                Source.NiagaraFloatData => floatDataSrv ?? v.View!,
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
                if (isVertex)
                    ctx.VSSetShaderResource(v.Register, view);
                else
                    ctx.PSSetShaderResource(v.Register, view);
            }
        }

        foreach (var sampler in stage.Samplers)
        {
            var s = sampler.Clamp ? shared.Clamp : shared.Wrap;
            if (isVertex)
                ctx.VSSetSampler(sampler.Register, s);
            else
                ctx.PSSetSampler(sampler.Register, s);
        }
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
            _owned[i].Dispose();
        _owned.Clear();
    }
}
