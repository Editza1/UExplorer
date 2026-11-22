using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Serilog;



using UExplorer.Scene;
namespace UExplorer.Rendering;

public static class NativeAssetCache
{
    private sealed class Pending<T>(Task<T?> task) where T : class
    {
        public Task<T?> Task { get; } = task;
        public string? Error;
        public DateTime Created { get; } = DateTime.UtcNow;
    }

    private static readonly Dictionary<string, Pending<NativeMeshLods>> _meshes = [];
    private static readonly ConditionalWeakTable<SceneLandscape, Pending<NativeLandscapeTile>> _landscapeTiles = [];
    private static readonly SemaphoreSlim _throttle = new(Math.Clamp(Environment.ProcessorCount / 2, 4, 8));
    private static readonly ConditionalWeakTable<MaterialAsset, Pending<NativeMaterialPipeline>> _pipelines = [];
    private static readonly ConcurrentDictionary<string, byte> _dumpedMaterialNames = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConditionalWeakTable<MaterialAsset, Pending<NativeNiagaraMaterialPipeline>> _niagaraSpritePipelines = [];
    private static readonly ConditionalWeakTable<MaterialAsset, Pending<NativeNiagaraMaterialPipeline>> _niagaraMeshPipelines = [];
    public static bool EnableDiskDumps = false;
    public static readonly string LogsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    public static readonly string DumpDirectory = Path.Combine(AppContext.BaseDirectory, "dump");

    public static void DumpNiagaraMaterialOnce(MaterialAsset node)
    {
        if (!EnableDiskDumps || !_dumpedMaterialNames.TryAdd(node.Name + "_Niagara", 1))
            return;
        _ = Task.Run(() =>
        {
            try
            {
                var sets = node.GetNativeShaders();
                var set = sets.FirstOrDefault(x => x.UniformSet is not null) ?? sets.FirstOrDefault();
                if (set is null)
                    return;

                var safeName = string.Concat(node.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var matDumpDir = Path.Combine(DumpDirectory, safeName);
                Directory.CreateDirectory(matDumpDir);

                foreach (var nShader in set.Shaders.Where(x =>
                             (x.VertexFactory.Contains("Niagara") || x.VertexFactory.Contains("Particle")) &&
                             (x.ShaderType.StartsWith("TBasePassVS") || x.ShaderType.StartsWith("TBasePassPS"))))
                {
                    nShader.Disassembly ??= D3D11NativeShaderDevice.Disassemble(nShader);
                    var prefix = nShader.ShaderType.StartsWith("TBasePassVS", StringComparison.Ordinal) ? "VS" : "PS";
                    File.WriteAllText(Path.Combine(matDumpDir, $"{prefix}_{nShader.ShaderType}_{nShader.VertexFactory}.asm"),
                        nShader.Describe() + "\n" + nShader.Disassembly);
                }
            }
            catch { }
        });
    }

    public static void DumpLandscapeMaterialOnce(MaterialAsset node)
    {
        if (!EnableDiskDumps || !_dumpedMaterialNames.TryAdd(node.Name, 1))
            return;
        _ = Task.Run(() =>
        {
            try
            {
                var sets = node.GetNativeShaders();
                var set = sets.FirstOrDefault(x => x.UniformSet is not null) ?? sets.FirstOrDefault();
                var vs = set?.Shaders.FirstOrDefault(x => x.ShaderType.StartsWith("TBasePassVS"));
                var ps = set?.Shaders.FirstOrDefault(x => x.ShaderType.StartsWith("TBasePassPS"));
                var buf = set is not null ? node.BuildNativeMaterialBuffer(set) : null;
                var res = set is not null ? node.ResolveNativeMaterialResources(set) : null;
                DumpMaterial(node, set, vs, ps, buf, res, null, null);

                if (set?.Shaders.FirstOrDefault(x => x.ShaderType.Contains("TVirtualTexturePSBaseColorNormalSpecular")) is { } rvtPs)
                {
                    var safeName = string.Concat(node.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                    var matDumpDir = Path.Combine(DumpDirectory, safeName);
                    Directory.CreateDirectory(matDumpDir);
                    rvtPs.Disassembly ??= D3D11NativeShaderDevice.Disassemble(rvtPs);
                    File.WriteAllText(Path.Combine(matDumpDir, $"PS_{rvtPs.ShaderType}.asm"), rvtPs.Describe() + "\n" + rvtPs.Disassembly);
                }
            }
            catch { }
        });
    }

    public static NativeLandscapeTile? GetLandscapeTile(D3D11NativeShaderDevice device, SceneLandscape component, bool allowQueue = true)
    {
        Pending<NativeLandscapeTile> pending;
        lock (_landscapeTiles)
        {
            if (!_landscapeTiles.TryGetValue(component, out pending!))
            {
                if (!allowQueue)
                    return null;
                pending = new Pending<NativeLandscapeTile>(Task.Run<NativeLandscapeTile?>(async () =>
                {
                    await _throttle.WaitAsync();
                    try
                    { return NativeLandscapeRenderer.BuildTile(device.Device, component); }
                    catch (Exception e) { Log.Warning(e, "Native landscape failed: {Name}", component.Name); return null; }
                    finally { _throttle.Release(); }
                }));
                _landscapeTiles.Add(component, pending);
            }
        }
        return pending.Task.IsCompletedSuccessfully ? pending.Task.Result : null;
    }

    public static NativeMeshLods? GetMesh(D3D11NativeShaderDevice device, UStaticMesh mesh, bool allowQueue = true)
    {
        var key = mesh.GetPathName();
        lock (_meshes)
        {
            if (!_meshes.TryGetValue(key, out var pending))
            {
                if (!allowQueue)
                    return null;
                pending = new Pending<NativeMeshLods>(Task.Run<NativeMeshLods?>(async () =>
                {
                    await _throttle.WaitAsync();
                    try
                    { return NativeMeshLods.FromStaticMesh(device.Device, mesh); }
                    catch (Exception e) { Log.Warning(e, "Native renderer: mesh {Mesh} failed", mesh.Name); return null; }
                    finally { _throttle.Release(); }
                }));
                _meshes[key] = pending;
            }
            return pending.Task.IsCompletedSuccessfully ? pending.Task.Result : null;
        }
    }

    public static NativeMaterialPipeline? GetPipeline(D3D11NativeShaderDevice device, MaterialAsset node, out string? status, bool allowQueue = true)
    {
        Pending<NativeMaterialPipeline> pending;
        lock (_pipelines)
        {
            if (!_pipelines.TryGetValue(node, out pending!))
            {
                if (!allowQueue)
                {
                    status = "queued";
                    return null;
                }
                Pending<NativeMaterialPipeline>? created = null;
                created = new Pending<NativeMaterialPipeline>(Task.Run<NativeMaterialPipeline?>(async () =>
                {
                    await _throttle.WaitAsync();
                    try
                    { return CreatePipeline(device, node); }
                    catch (Exception e)
                    {
                        created!.Error = e.Message;
                        SnooperDiagnostics.LogError("Material", node.Name, e.ToString());
                        return null;
                    }
                    finally { _throttle.Release(); }
                }));
                pending = created;
                _pipelines.Add(node, pending);
            }
        }

        status = pending.Task.IsCompleted ? pending.Error : "compiling...";
        return pending.Task.IsCompletedSuccessfully ? pending.Task.Result : null;
    }

    internal static NativeMaterialPipeline? BuildPipelineSync(D3D11NativeShaderDevice device, MaterialAsset node, out string? error)
    {
        try
        {
            error = null;
            return CreatePipeline(device, node);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static NativeNiagaraMaterialPipeline? GetNiagaraPipeline(
        D3D11NativeShaderDevice device,
        MaterialAsset node,
        bool isMesh,
        out string? status,
        bool allowQueue = true)
    {
        var table = isMesh ? _niagaraMeshPipelines : _niagaraSpritePipelines;
        Pending<NativeNiagaraMaterialPipeline> pending;
        lock (table)
        {
            if (!table.TryGetValue(node, out pending!))
            {
                if (!allowQueue)
                {
                    status = "queued";
                    return null;
                }
                Pending<NativeNiagaraMaterialPipeline>? created = null;
                created = new Pending<NativeNiagaraMaterialPipeline>(Task.Run<NativeNiagaraMaterialPipeline?>(async () =>
                {
                    await _throttle.WaitAsync();
                    try
                    {
                        var pipe = NativeNiagaraMaterialPipeline.Create(device.Device, node, isMesh, out var err);
                        if (pipe is null)
                            created!.Error = err;
                        return pipe;
                    }
                    catch (Exception e)
                    {
                        created!.Error = e.Message;
                        SnooperDiagnostics.LogError("NiagaraMaterial", node.Name, e.ToString());
                        return null;
                    }
                    finally { _throttle.Release(); }
                }));
                pending = created;
                table.Add(node, pending);
            }
        }

        status = pending.Task.IsCompleted ? pending.Error : "compiling...";
        return pending.Task.IsCompletedSuccessfully ? pending.Task.Result : null;
    }

    internal static NativeNiagaraMaterialPipeline? BuildNiagaraPipelineSync(
        D3D11NativeShaderDevice device,
        MaterialAsset node,
        bool isMesh,
        out string? error)
    {
        try
        {
            return NativeNiagaraMaterialPipeline.Create(device.Device, node, isMesh, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static void Forget(MaterialAsset node)
    {
        lock (_pipelines)
            _pipelines.Remove(node);
        lock (_niagaraSpritePipelines)
            _niagaraSpritePipelines.Remove(node);
        lock (_niagaraMeshPipelines)
            _niagaraMeshPipelines.Remove(node);
        _dumpedMaterialNames.TryRemove(node.Name, out _);
    }

    private static NativeMaterialPipeline CreatePipeline(D3D11NativeShaderDevice device, MaterialAsset node)
    {
        static int VfPriority(string vf) => vf switch
        {
            "FLocalVertexFactory" => 0,
            _ when vf.Contains("LocalVertexFactory") => 1,
            _ when vf.Contains("NiagaraMeshVertexFactory") => 2,
            _ when vf.Contains("NiagaraSpriteVertexFactory") => 3,
            _ => 99
        };

        for (var shaderSource = node; shaderSource != null; shaderSource = shaderSource.Parent)
        {
            var sets = shaderSource.GetNativeShaders();
            foreach (var set in sets)
            {
                if (set.UniformSet is null)
                    continue;
                bool Usable(NativeShaderEntry x) => x.Status == ENativeShaderStatus.Created && VfPriority(x.VertexFactory) < 99;
                var pixels = set.Shaders.Where(x => Usable(x) && x.ShaderType.StartsWith("TBasePassPS"))
                    .OrderBy(x => VfPriority(x.VertexFactory))
                    .ThenBy(x => x.Pipeline.Length)
                    .ToList();
                var vertices = set.Shaders.Where(x => Usable(x) && x.ShaderType.StartsWith("TBasePassVS"))
                    .OrderBy(x => VfPriority(x.VertexFactory))
                    .ThenBy(x => x.Pipeline.Length)
                    .ToList();

                var policyOrder = new[] { "FNoLightMapPolicy", "FNoLightMapPolicySkylight" }
                    .Concat(pixels.Select(x => x.ShaderType["TBasePassPS".Length..]))
                    .Distinct();

                foreach (var vfGroup in pixels.GroupBy(x => x.VertexFactory).OrderBy(g => VfPriority(g.Key)))
                {
                    var vfName = vfGroup.Key;
                    var vfPixels = vfGroup.ToList();
                    var vfVertices = vertices.Where(v => v.VertexFactory == vfName).ToList();
                    if (vfVertices.Count == 0)
                        continue;

                    foreach (var policy in policyOrder)
                    {
                        var ps = vfPixels.FirstOrDefault(x => x.ShaderType == "TBasePassPS" + policy && x.Pipeline.Length == 0)
                                 ?? vfPixels.FirstOrDefault(x => x.ShaderType == "TBasePassPS" + policy);
                        var vs = vfVertices.FirstOrDefault(x => x.ShaderType == "TBasePassVS" + policy && x.Pipeline.Length == 0)
                                 ?? vfVertices.FirstOrDefault(x => x.ShaderType == "TBasePassVS" + policy)
                                 ?? vfVertices.FirstOrDefault();
                        if (ps is null || vs is null)
                            continue;

                        var buffer = node.BuildNativeMaterialBuffer(set) ?? throw new InvalidOperationException("material buffer could not be built");

                        var isRvtShader = false;
                        if (buffer.UsesRuntimeVirtualTexture)
                        {
                            var rvtPs = set.Shaders.FirstOrDefault(x => Usable(x) && x.ShaderType == "TVirtualTexturePSBaseColorNormalSpecular")
                                        ?? set.Shaders.FirstOrDefault(x => Usable(x) && x.ShaderType == "TVirtualTexturePSBaseColor");
                            var rvtVs = set.Shaders.FirstOrDefault(x => Usable(x) && x.ShaderType == "TVirtualTextureVSBaseColorNormalSpecular")
                                        ?? set.Shaders.FirstOrDefault(x => Usable(x) && x.ShaderType == "TVirtualTextureVSBaseColor");
                            if (rvtPs is not null && rvtVs is not null)
                            {
                                ps = rvtPs;
                                vs = rvtVs;
                                isRvtShader = true;
                            }
                        }

                        var (resources, samplerSources) = node.ResolveNativeMaterialResourceLayout(set);
                        var collections = node.ResolveNativeParameterCollections(set);
                        try
                        {
                            var pipeline = new NativeMaterialPipeline(device.Device, vs, ps, buffer.Constants, resources,
                                (ENativeBlendMode) (int) node.BlendMode, node.TwoSided, collections, node.IsEmissiveDriven, isRvtShader, samplerSources,
                                !isRvtShader ? set.Shaders.FirstOrDefault(x => Usable(x) && x.VertexFactory == vfName && x.ShaderType == "TDepthOnlyVS<false>" && x.Pipeline == "DepthNoColorOutputPipeline") ?? set.Shaders.FirstOrDefault(x => Usable(x) && x.VertexFactory == vfName && x.ShaderType.StartsWith("TDepthOnlyVS")) : null,
                                !isRvtShader ? set.Shaders.FirstOrDefault(x => Usable(x) && x.VertexFactory == vfName && x.ShaderType == "FDepthOnlyPS<false>" && x.Pipeline == "DepthNoColorOutputPipeline") ?? set.Shaders.FirstOrDefault(x => Usable(x) && x.VertexFactory == vfName && x.ShaderType.StartsWith("FDepthOnlyPS")) : null,
                                shaderSet: set,
                                initialParameterVersion: node.ParameterVersion,
                                isVolumeDomain: node.MaterialDomain == CUE4Parse.UE4.Assets.Exports.Material.EMaterialDomain.MD_Volume);

                            if (EnableDiskDumps && _dumpedMaterialNames.TryAdd(node.Name, 1))
                                _ = Task.Run(() => DumpMaterial(node, set, vs, ps, buffer, resources, pipeline, null));
                            return pipeline;
                        }
                        catch (Exception ex)
                        {
                            if (EnableDiskDumps && _dumpedMaterialNames.TryAdd(node.Name, 1))
                                _ = Task.Run(() => DumpMaterial(node, set, vs, ps, buffer, resources, null, ex.ToString()));
                            throw;
                        }
                    }
                }
            }
        }

        var initialSets = node.GetNativeShaders();
        var problem = initialSets.Select(x => x.Problem ?? x.Warning).FirstOrDefault(x => x is not null);
        if (EnableDiskDumps && _dumpedMaterialNames.TryAdd(node.Name, 1))
            _ = Task.Run(() => DumpMaterial(node, initialSets.FirstOrDefault(), null, null, null, null, null, problem ?? "no TBasePassVS/PS pair"));
        throw new InvalidOperationException(problem ?? "no TBasePassVS/PS pair for FLocalVertexFactory that D3D11 accepted");
    }

    private static void DumpMaterial(
        MaterialAsset node,
        NativeMaterialShaders? set,
        NativeShaderEntry? vs,
        NativeShaderEntry? ps,
        NativeMaterialUniforms.Result? buffer,
        UTexture?[]? resources,
        NativeMaterialPipeline? pipeline,
        string? error)
    {
        try
        {
            var safeName = string.Concat(node.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            Directory.CreateDirectory(LogsDirectory);
            var matDumpDir = Path.Combine(DumpDirectory, safeName);
            Directory.CreateDirectory(matDumpDir);

            var sb = new StringBuilder();
            sb.AppendLine($"=== Material: {node.Name} ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
            var chain = new List<string>();
            for (var curr = node; curr != null; curr = curr.Parent)
                chain.Add(curr.Name);
            sb.AppendLine($"Hierarchy: {string.Join(" -> ", chain)}");
            sb.AppendLine($"BlendMode: {node.BlendMode} | ShadingModel: {node.ShadingModel} | Domain: {node.MaterialDomain} | TwoSided: {node.TwoSided} | IsEmissiveDriven: {node.IsEmissiveDriven}");
            sb.AppendLine($"Detected View DiffuseOverride cb0 register: {NativeSceneRenderer.DetectedDiffuseOverrideReg} (PreViewTranslation cb0[{NativeSceneRenderer.DetectedPreViewTranslationReg}])");

            if (error is not null)
            {
                sb.AppendLine();
                sb.AppendLine("!!! PIPELINE ERROR !!!");
                sb.AppendLine(error);
            }

            if (pipeline is not null)
            {
                sb.AppendLine();
                sb.AppendLine("--- Pipeline Binding Description ---");
                sb.AppendLine(pipeline.Description);
            }

            if (set?.UniformsReport is { } uReport)
            {
                sb.AppendLine();
                sb.AppendLine("--- Uniform Expression Set ---");
                sb.AppendLine(uReport);
            }

            if (buffer is not null)
            {
                sb.AppendLine();
                sb.AppendLine("--- Evaluated Material Constant Buffer ---");
                sb.AppendLine(buffer.Describe());
            }

            if (resources is not null)
            {
                sb.AppendLine();
                sb.AppendLine($"--- Resolved Material Textures ({resources.Length} slots) ---");
                for (var i = 0; i < resources.Length; i++)
                    sb.AppendLine($"  Resource[{i}] = {resources[i]?.GetPathName() ?? "null"} (sRGB={resources[i]?.SRGB}, Compression={resources[i]?.CompressionSettings})");
            }

            if (vs is not null)
            {
                vs.Disassembly ??= D3D11NativeShaderDevice.Disassemble(vs);
                File.WriteAllText(Path.Combine(matDumpDir, $"VS_{vs.ShaderType}.asm"), vs.Describe() + "\n" + vs.Disassembly);
            }

            if (ps is not null)
            {
                ps.Disassembly ??= D3D11NativeShaderDevice.Disassemble(ps);
                File.WriteAllText(Path.Combine(matDumpDir, $"PS_{ps.ShaderType}.asm"), ps.Describe() + "\n" + ps.Disassembly);
            }

            if (set is not null)
            {
                var allShadersSb = new StringBuilder();
                foreach (var s in set.Shaders)
                {
                    allShadersSb.AppendLine("========================================");
                    allShadersSb.AppendLine(s.Describe());
                }
                File.WriteAllText(Path.Combine(matDumpDir, "all_shaders.txt"), allShadersSb.ToString());
            }

            File.WriteAllText(Path.Combine(matDumpDir, "summary.txt"), sb.ToString());
            File.WriteAllText(Path.Combine(LogsDirectory, $"{safeName}.log"), sb.ToString());
            SnooperDiagnostics.WriteMaterialLog(node.Name, sb.ToString());
            if (error is not null)
                SnooperDiagnostics.LogError("MaterialPipeline", node.Name, error);
        }
        catch
        {
        }
    }
}
