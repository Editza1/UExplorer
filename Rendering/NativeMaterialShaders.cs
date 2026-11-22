using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Shaders;

namespace UExplorer.Rendering;

public enum ENativeShaderStatus
{
    /// <summary>Bytecode extracted, not validated on a device yet.</summary>
    Extracted,
    /// <summary>D3D11 accepted the bytecode (CreateXShader succeeded).</summary>
    Created,
    /// <summary>DXIL / SM6, needs a D3D12 device.</summary>
    RequiresD3D12,
    /// <summary>Shader map references code that could not be found or decoded.</summary>
    Missing,
    /// <summary>The device rejected the bytecode.</summary>
    Rejected
}

public sealed class NativeShaderEntry
{
    public required string ShaderType { get; init; }
    public required string VertexFactory { get; init; }
    public required string Pipeline { get; init; }
    public required int ResourceIndex { get; init; }
    public byte[]? Code { get; init; }
    public DxbcContainer? Container { get; init; }

    /// <summary>Unreal-side binding metadata from the shader map (uniform buffer slots, loose parameters...).</summary>
    public string? UnrealBindings { get; init; }
    public string? Disassembly { get; set; }

    public ENativeShaderStatus Status { get; set; }
    public string? Error { get; set; }
    public IReadOnlyList<NativeShaderBinding> Bindings { get; set; } = [];
    public IReadOnlyList<NativeConstantBuffer> ConstantBuffers { get; set; } = [];

    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[NativeShader] {Stage} | {ShaderType} | {VertexFactory}{(Pipeline.Length > 0 ? $" | pipeline {Pipeline}" : "")}");
        sb.AppendLine($"status={Status} resourceIndex={ResourceIndex} bytecode={Bytecode.Length} bytes" +
                      (Container is { } c ? $" {c.Kind} {c.MajorVersion}.{c.MinorVersion} (blob {Code?.Length} bytes, container at {c.Offset})" : ""));
        if (Error is not null) sb.AppendLine($"error: {Error}");
        foreach (var buffer in ConstantBuffers)
        {
            sb.AppendLine($"cbuffer {buffer.Name} size={buffer.Size}");
            foreach (var (name, offset, size) in buffer.Variables)
                sb.AppendLine($"  +{offset} {size} {name}");
        }
        foreach (var binding in Bindings)
            sb.AppendLine($"bind {binding.Type} {binding.Name} slot={binding.Slot} count={binding.Count}");
        if (ConstantBuffers.Count == 0 && Bindings.Count == 0) sb.AppendLine("(no D3D reflection data: RDEF stripped)");
        if (UnrealBindings is not null) sb.Append(UnrealBindings);
        if (Code is not null && Container is { } container)
        {
            sb.AppendLine($"ue header ({container.Offset} bytes): {Convert.ToHexString(Code, 0, container.Offset)}");
            var tail = container.Offset + container.Length;
            sb.AppendLine($"ue tail ({Code.Length - tail} bytes): {Convert.ToHexString(Code, tail, Code.Length - tail)}");
        }
        return sb.ToString();
    }

    public ReadOnlyMemory<byte> Bytecode => Container is { } c && Code is not null ? c.Slice(Code) : ReadOnlyMemory<byte>.Empty;
    public ENativeShaderStage Stage => Container?.Stage ?? ENativeShaderStage.Unknown;
}

public readonly record struct NativeShaderBinding(string Name, string Type, int Slot, int Count);
public readonly record struct NativeConstantBuffer(string Name, int Size, IReadOnlyList<(string Name, int Offset, int Size)> Variables);

/// <summary>
/// Every native shader a cooked material carries for one shader platform (usually SP_PCD3D_SM5 and/or SM6),
/// resolved from either inline code or the shared shader libraries.
/// </summary>
public sealed class NativeMaterialShaders
{
    public string Platform { get; private init; } = string.Empty;
    public string Source { get; private init; } = string.Empty;
    public IReadOnlyList<NativeShaderEntry> Shaders { get; private init; } = [];
    public string? Problem { get; private init; }
    public string? Warning { get; private init; }
    public string? UniformsReport { get; private init; }
    public FUniformExpressionSet? UniformSet { get; private init; }

    public static IReadOnlyList<NativeMaterialShaders> Resolve(UMaterialInterface material, IFileProvider provider)
    {
        if (material.LoadedMaterialResources is not { Count: > 0 } resources)
        {
            var hint = provider.ReadShaderMaps
                ? "This material has no inline shader maps in its package (it may inherit them from its parent, or the game strips them)."
                : "Shader maps were not read: set ReadShaderMaps = true on the file provider before loading packages.";
            return [new NativeMaterialShaders { Problem = hint }];
        }

        var results = new List<NativeMaterialShaders>();
        foreach (var resource in resources)
        {
            if (resource.LoadedShaderMap is not { } map) continue;
            var content = map.Content as FMaterialShaderMapContent;

            Func<int, byte[]?> load;
            string source;
            int codeCount;
            if (map.Code is { } inline)
            {
                source = "inline";
                load = index => LoadInline(inline, index);
                codeCount = Math.Max(inline.ShaderEntries.Length, inline.ShaderCodeResources.Length);
            }
            else if (map.ResourceHash is { } hash && NativeShaderLibrary.For(provider).TryGetShaderMap(hash, out var fromLibrary, out var count, out var library))
            {
                source = library;
                load = fromLibrary;
                codeCount = count;
            }
            else
            {
                results.Add(new NativeMaterialShaders
                {
                    Platform = map.ShaderPlatform.ToString(),
                    Problem = $"Shared shader map {map.ResourceHash} was not found in any shader library."
                });
                continue;
            }

            var entries = new List<NativeShaderEntry>();
            if (map.ContentError is null && content is not null)
            {
                Collect(content, "Material", "", load, entries);
                foreach (var mesh in content.OrderedMeshShaderMaps ?? [])
                    Collect(mesh, Name(mesh.VertexFactoryTypeName.Hash), "", load, entries);
            }
            else
            {
                // the shader map layout of this game is not understood, so shader types / vertex factories are
                // unknown; the bytecode itself is still addressable by resource index
                for (var i = 0; i < codeCount; i++)
                    entries.Add(Create(i, "(unknown type)", "(unknown VF)", string.Empty, load));
            }

            // when the content failed to parse its platform field was never read (the enum default is SM5),
            // so describe the set by what the bytecode actually is
            var dxbc = entries.Count(x => x.Container?.Kind == ENativeBytecodeKind.Dxbc);
            var dxil = entries.Count(x => x.Container?.Kind == ENativeBytecodeKind.Dxil);
            var platform = map.ContentError is null && content is not null
                ? content.ShaderPlatform.ToString()
                : dxil > 0 && dxbc == 0 ? "D3D12 (DXIL, platform unknown)"
                : dxbc > 0 && dxil == 0 ? "D3D11 (DXBC, platform unknown)"
                : $"mixed {dxbc} DXBC / {dxil} DXIL (platform unknown)";

            if (map.ContentError is { } contentError)
                Serilog.Log.Warning("Shader map layout not parsed for {Material} ({Platform}): {Error}", material.Name, platform, contentError);

            results.Add(new NativeMaterialShaders
            {
                Platform = platform,
                Source = source,
                Shaders = entries,
                UniformSet = map.ContentError is null ? content?.MaterialCompilationOutput?.UniformExpressionSet : null,
                UniformsReport = content is not null && map.ContentError is null ? DescribeUniforms(material, content) : null,
                Warning = map.ContentError is { } error ? $"Shader map layout not parsed ({error}); shader types are unknown, listing raw bytecode by index." : null
            });
        }

        return results;
    }

    private static void Collect(FShaderMapContent content, string vertexFactory, string _, Func<int, byte[]?> load, List<NativeShaderEntry> entries)
    {
        foreach (var shader in content.Shaders)
            entries.Add(Create(shader, vertexFactory, string.Empty, load));

        foreach (var pipeline in content.ShaderPipelines)
        foreach (var shader in pipeline.Shaders)
        {
            if (shader is null) continue;
            entries.Add(Create(shader, vertexFactory, Name(pipeline.TypeName.Hash), load));
        }
    }

    private static NativeShaderEntry Create(FShader shader, string vertexFactory, string pipeline, Func<int, byte[]?> load)
    {
        var entry = Create(shader.ResourceIndex, Name(shader.Type.Hash), shader.VFType.Hash != 0 ? Name(shader.VFType.Hash) : vertexFactory, pipeline, load);
        try
        {
            return new NativeShaderEntry
            {
                ShaderType = entry.ShaderType, VertexFactory = entry.VertexFactory, Pipeline = entry.Pipeline,
                ResourceIndex = entry.ResourceIndex, Code = entry.Code, Container = entry.Container,
                Status = entry.Status, Error = entry.Error, UnrealBindings = DescribeUnreal(shader)
            };
        }
        catch
        {
            return entry;
        }
    }

    private static string DescribeUnreal(FShader shader)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"target=0x{System.Runtime.CompilerServices.Unsafe.As<FShaderTarget, uint>(ref shader.Target):X} instructions={shader.NumInstructions}");
        for (var i = 0; i < shader.UniformBufferParameterStructs.Length; i++)
        {
            var slot = i < shader.UniformBufferParameters.Length ? shader.UniformBufferParameters[i].BaseIndex.ToString() : "?";
            sb.AppendLine($"ue uniformBuffer {Name(shader.UniformBufferParameterStructs[i].Hash)} cb={slot}");
        }
        var map = shader.ParameterMapInfo;
        if (map is not null)
        {
            foreach (var ub in map.UniformBuffers ?? []) sb.AppendLine($"ue map.uniformBuffer base={ub.BaseIndex} size={ub.Size}");
            foreach (var t in map.TextureSamplers ?? []) sb.AppendLine($"ue map.textureSampler base={t.BaseIndex} size={t.Size}");
            foreach (var t in map.SRVs ?? []) sb.AppendLine($"ue map.srv base={t.BaseIndex} size={t.Size}");
            foreach (var lb in map.LooseParameterBuffers ?? [])
            {
                sb.AppendLine($"ue map.looseBuffer cb={lb.BaseIndex} size={lb.Size}");
                foreach (var lp in lb.Parameters ?? []) sb.AppendLine($"    loose offset={lp.BaseIndex} size={lp.Size}");
            }
        }
        var bindings = shader.Bindings;
        if (bindings is not null)
        {
            foreach (var p in bindings.Parameters ?? []) sb.AppendLine($"ue bind.param buffer={p.BufferIndex} base={p.BaseIndex} structOffset={p.ByteOffset} size={p.ByteSize}");
            foreach (var r in bindings.ResourceParameters ?? []) sb.AppendLine($"ue bind.resource structOffset={r.ByteOffset} base={r.BaseIndex} type={(byte) r.BaseType}");
            foreach (var r in bindings.ParameterReferences ?? []) sb.AppendLine($"ue bind.structRef buffer={r.BufferIndex} structOffset={r.ByteOffset}");
        }
        return sb.ToString();
    }

    private static NativeShaderEntry Create(int resourceIndex, string shaderType, string vf, string pipeline, Func<int, byte[]?> load)
    {
        byte[]? code;
        try { code = load(resourceIndex); }
        catch { code = null; }
        DxbcContainer? container = code is not null && DxbcContainer.TryFind(code, out var found) ? found : null;

        return new NativeShaderEntry
        {
            ShaderType = shaderType,
            VertexFactory = vf,
            Pipeline = pipeline,
            ResourceIndex = resourceIndex,
            Code = code,
            Container = container,
            Status = container switch
            {
                null => ENativeShaderStatus.Missing,
                { Kind: ENativeBytecodeKind.Dxil } => ENativeShaderStatus.RequiresD3D12,
                _ => ENativeShaderStatus.Extracted
            },
            Error = code is null ? "bytecode not found" : container is null ? "no DXBC container in shader code (not a D3D shader platform?)" : null
        };
    }

    private static byte[]? LoadInline(FShaderMapResourceCode code, int index)
    {
        if (index < 0) return null;
        if (index < code.ShaderEntries.Length)
        {
            var entry = code.ShaderEntries[index];
            return NativeShaderLibrary.Decompress(entry.Code, entry.UncompressedSize);
        }
        if (index < code.ShaderCodeResources.Length)
        {
            var resource = code.ShaderCodeResources[index];
            return NativeShaderLibrary.Decompress(resource.Code.Data, resource.Header.UncompressedSize);
        }
        return null;
    }

    private static string DescribeUniforms(UMaterialInterface material, FMaterialShaderMapContent content)
    {
        var sb = new System.Text.StringBuilder();
        var set = content.MaterialCompilationOutput?.UniformExpressionSet;
        sb.AppendLine($"[MaterialUniforms] {material.Name} platform={content.ShaderPlatform}");
        if (set is null) return sb.AppendLine("no uniform expression set").ToString();

        sb.AppendLine($"vectorPreshaders={set.UniformVectorPreshaders.Length} scalarPreshaders={set.UniformScalarPreshaders.Length} preshaderBytes={set.UniformPreshaderData?.Data?.Length ?? 0}");
        foreach (var h in set.UniformVectorPreshaders) sb.AppendLine($"  vec preshader @{h.OpcodeOffset} len {h.OpcodeSize}");
        foreach (var h in set.UniformScalarPreshaders) sb.AppendLine($"  scalar preshader @{h.OpcodeOffset} len {h.OpcodeSize}");
        if (set.UniformPreshaderData?.Data is { Length: > 0 } data) sb.AppendLine($"  preshader data: {Convert.ToHexString(data)}");
        if (set.UniformPreshaderData?.Names is { Length: > 0 } names) sb.AppendLine($"  preshader names: {string.Join(", ", names.Select(x => x.Text))}");
        foreach (var p in set.UniformScalarParameters) sb.AppendLine($"scalar {p.ParameterInfo?.Name.Text ?? p.ParameterName} default={p.DefaultValue}");
        foreach (var p in set.UniformVectorParameters) sb.AppendLine($"vector {p.ParameterInfo?.Name.Text ?? p.ParameterName} default={p.DefaultValue}");
        for (var type = 0; type < set.UniformTextureParameters.Length; type++)
        foreach (var p in set.UniformTextureParameters[type] ?? [])
            sb.AppendLine($"texture[type {type}] {p.ParameterInfo?.Name.Text ?? p.ParameterName} textureIndex={p.TextureIndex} sampler={p.SamplerSource}");
        foreach (var vt in set.VTStacks ?? []) sb.AppendLine($"vtstack layers={vt.NumLayers}");
        foreach (var guid in set.ParameterCollections ?? []) sb.AppendLine($"parameterCollection {guid}");

        if (set.UniformBufferLayoutInitializer is { } ub)
        {
            sb.AppendLine($"uniformBuffer name={ub.Name} size={ub.ConstantBufferSize} slot={ub.StaticSlot}");
            foreach (var r in ub.Resources ?? []) sb.AppendLine($"  resource +{r.MemberOffset} {r.MemberType}");
        }

        if (material is UMaterial root)
        {
            for (var i = 0; i < root.ReferencedTextures.Count; i++)
                sb.AppendLine($"referencedTexture[{i}] {root.ReferencedTextures[i]?.Name ?? "null"}");
        }

        return sb.ToString();
    }

    private static string Name(ulong hash) => HashedNamesProvider.TryGetEntry(hash, out var name) && name is not null ? name : $"0x{hash:X16}";
}
