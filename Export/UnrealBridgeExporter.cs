using System.Globalization;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Textures;
using SixLabors.ImageSharp;
using UExplorer.Rendering;
using UExplorer.Scene;
using UExplorer.UI.Editors;

namespace UExplorer.Export;

/// <summary>
/// Exports Meshes, Materials, Material Instances, Niagara VFX, and Worlds (.umap) into a structured
/// Unreal Engine 5 Bridge package (<c>%LOCALAPPDATA%/UExplorer/UnrealBridge/</c>) and notifies the
/// UE5 <c>UExplorerBridge</c> plugin via local HTTP (<c>http://127.0.0.1:47291/import</c>) and a file queue.
/// </summary>
public static class UnrealBridgeExporter
{
    public const int BridgePort = 47291;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMilliseconds(750) };
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    public static string BridgeRootDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UExplorer", "UnrealBridge");

    public static string ExportsDir => Path.Combine(BridgeRootDir, "Exports");
    public static string QueueDir => Path.Combine(BridgeRootDir, "Queue");

    public sealed record ExportOutcome(bool Success, string AssetKind, string AssetName, string OutputDirectory, string ManifestPath, string Message);

    public static ExportOutcome ExportByPackagePath(IFileProvider provider, string packagePath, string? preferredName = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(packagePath))
            return new ExportOutcome(false, "Unknown", "", "", "", "Package path is empty.");

        try
        {
            if (!provider.TryLoadPackage(packagePath, out var pkg))
                return new ExportOutcome(false, "Unknown", preferredName ?? packagePath, "", "", $"Could not load package '{packagePath}'.");

            var exports = pkg.GetExports().ToList();
            UObject? primary = null;
            if (!string.IsNullOrWhiteSpace(preferredName))
                primary = exports.FirstOrDefault(e => e.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase));
            primary ??= exports.FirstOrDefault(e => e is UWorld)
                        ?? exports.FirstOrDefault(e => e is UStaticMesh or USkeletalMesh or UUnrealMaterial)
                        ?? exports.FirstOrDefault(e => e.ExportType is "NiagaraSystem" or "NiagaraEmitter")
                        ?? exports.FirstOrDefault();

            if (primary is null)
                return new ExportOutcome(false, "Unknown", preferredName ?? packagePath, "", "", $"No exportable objects found in '{packagePath}'.");

            return ExportObject(primary, provider, log);
        }
        catch (Exception ex)
        {
            return new ExportOutcome(false, "Unknown", preferredName ?? packagePath, "", "", $"Export failed: {ex.Message}");
        }
    }

    public static ExportOutcome ExportObject(UObject obj, IFileProvider? provider, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(obj);
        try
        {
            return obj switch
            {
                UStaticMesh sm => ExportStaticMesh(sm, log),
                USkeletalMesh skm => ExportSkeletalMesh(skm, log),
                UUnrealMaterial mat => ExportMaterial(new MaterialAsset(mat), log),
                UWorld world when provider is not null => ExportWorld(world, provider, log),
                _ when obj.ExportType is "NiagaraSystem" or "NiagaraEmitter" => ExportNiagara(obj, log),
                _ => new ExportOutcome(false, obj.ExportType, obj.Name, "", "", $"Unsupported asset type for Unreal export: {obj.ExportType}")
            };
        }
        catch (Exception ex)
        {
            return new ExportOutcome(false, obj.ExportType, obj.Name, "", "", $"Export failed for {obj.Name}: {ex.Message}");
        }
    }

    public static ExportOutcome ExportLoadedScene(WorldScene scene, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var safeName = SanitizeName(scene.WorldName);
        var outDir = PrepareExportDir("World", safeName);
        var worldJsonPath = Path.Combine(outDir, "world.json");
        var meshesDir = Path.Combine(outDir, "Meshes");
        var matsDir = Path.Combine(outDir, "Materials");
        var niagaraDir = Path.Combine(outDir, "Niagara");
        Directory.CreateDirectory(meshesDir);
        Directory.CreateDirectory(matsDir);
        Directory.CreateDirectory(niagaraDir);

        var exportedMeshes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var exportedMaterials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var exportedNiagaras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string EnsureMeshExported(UStaticMesh sm)
        {
            var key = sm.GetPathName();
            if (exportedMeshes.TryGetValue(key, out var existing))
                return existing;
            var mSafe = SanitizeName(sm.Name);
            var mDir = Path.Combine(meshesDir, mSafe);
            Directory.CreateDirectory(mDir);
            var mJson = WriteStaticMeshFiles(sm, mDir, matsDir, exportedMaterials);
            exportedMeshes[key] = mJson;
            return mJson;
        }

        string EnsureMaterialExported(MaterialAsset matAsset)
        {
            var key = matAsset.CookedMaterial?.GetPathName() ?? matAsset.Name;
            if (exportedMaterials.TryGetValue(key, out var existing))
                return existing;
            var mSafe = GetUniqueMaterialExportName(matAsset);
            var mDir = Path.Combine(matsDir, mSafe);
            Directory.CreateDirectory(mDir);
            var (mJson, _) = WriteMaterialFiles(matAsset, mDir);
            exportedMaterials[key] = mJson;
            return mJson;
        }

        var actors = new List<object>();
        static (Vector3 LocCm, Vector3 RotDeg, Vector3 Scl) ExtractWorldTransform(SceneObject obj, Matrix4x4 worldMat)
        {
            var locCm = obj.WorldLocationCm;
            var rotDeg = obj.RotationDeg;
            var scl = obj.Scale;

            var needsDecompose = obj.OverrideLocalMatrix is not null || obj.AttachSocketMatrix is not null;
            for (var p = obj.Parent; p is not null && !needsDecompose; p = p.Parent)
            {
                if (p.OverrideLocalMatrix is not null || p.AttachSocketMatrix is not null ||
                    p.RotationDeg != Vector3.Zero || p.Scale != Vector3.One)
                {
                    needsDecompose = true;
                }
            }
            if (needsDecompose && Matrix4x4.Decompose(worldMat, out var wScl, out var wRot, out var wTrans))
            {
                locCm = new Vector3(wTrans.X, wTrans.Z, wTrans.Y) * 100f;
                scl = new Vector3(wScl.X, wScl.Z, wScl.Y);
                var uRot = UnrealSpace.ToUnrealQuat(Quaternion.Normalize(wRot)).Rotator();
                rotDeg = new Vector3(uRot.Pitch, uRot.Yaw, uRot.Roll);
            }
            return (locCm, rotDeg, scl);
        }

        void Collect(SceneObject node)
        {
            var nodeWorld = node.WorldMatrix;
            var (wLoc, wRot, wScl) = ExtractWorldTransform(node, nodeWorld);
            var entry = new Dictionary<string, object?>
            {
                ["Name"] = node.Name,
                ["ClassName"] = node.ClassName,
                ["Path"] = node.Path,
                ["IsVisible"] = node.IsVisible,
                ["LocationCm"] = new[] { wLoc.X, wLoc.Y, wLoc.Z },
                ["RotationDeg"] = new[] { wRot.X, wRot.Y, wRot.Z },
                ["Scale"] = new[] { wScl.X, wScl.Y, wScl.Z }
            };

            switch (node)
            {
                case SceneMesh sm:
                    entry["Type"] = "StaticMesh";
                    entry["MeshPath"] = sm.Mesh.GetPathName();
                    entry["MeshName"] = sm.Mesh.Name;
                    entry["IsHlod"] = sm.IsHlod;
                    entry["MeshJson"] = EnsureMeshExported(sm.Mesh);
                    entry["Materials"] = sm.Materials.Select(m => m is not null ? EnsureMaterialExported(m) : null).ToArray();
                    entry["InstanceCount"] = sm.InstanceLocalMatrices?.Count ?? 0;
                    if (sm.InstanceLocalMatrices is { Count: > 0 } instMats)
                    {
                        var instList = new List<object>(instMats.Count);
                        foreach (var instLocal in instMats)
                        {
                            var instWorld = instLocal * nodeWorld;
                            if (Matrix4x4.Decompose(instWorld, out var iScl, out var iRot, out var iTrans))
                            {
                                var iLocCm = new Vector3(iTrans.X, iTrans.Z, iTrans.Y) * 100f;
                                var iScale = new Vector3(iScl.X, iScl.Z, iScl.Y);
                                var iURot = UnrealSpace.ToUnrealQuat(Quaternion.Normalize(iRot)).Rotator();
                                instList.Add(new
                                {
                                    LocationCm = new[] { iLocCm.X, iLocCm.Y, iLocCm.Z },
                                    RotationDeg = new[] { iURot.Pitch, iURot.Yaw, iURot.Roll },
                                    Scale = new[] { iScale.X, iScale.Y, iScale.Z }
                                });
                            }
                        }
                        entry["Instances"] = instList;
                    }
                    break;
                case SceneNiagara sn:
                    entry["Type"] = "Niagara";
                    entry["NiagaraName"] = sn.Name;
                    if (sn.SourceObject is { } nObj)
                    {
                        var nSys = NiagaraAsset.ResolveSystemAssetFromComponent(nObj) ?? nObj;
                        var nKey = nSys.GetPathName();
                        if (!exportedNiagaras.TryGetValue(nKey, out var nJson))
                        {
                            var nDir = Path.Combine(niagaraDir, SanitizeName(nSys.Name));
                            Directory.CreateDirectory(nDir);
                            nJson = WriteNiagaraFiles(nSys, nDir, meshesDir, matsDir, exportedMeshes, exportedMaterials);
                            exportedNiagaras[nKey] = nJson;
                        }
                        entry["NiagaraJson"] = nJson;
                    }
                    break;
                case LocalLightComponent ll:
                    entry["Type"] = ll.GetType().Name;
                    entry["Color"] = new[] { ll.Color.X, ll.Color.Y, ll.Color.Z };
                    entry["Intensity"] = ll.Intensity;
                    entry["IntensityUnits"] = ll.IntensityUnits.ToString();
                    entry["AttenuationRadius"] = ll.AttenuationRadius;
                    entry["UseInverseSquaredFalloff"] = ll.UseInverseSquaredFalloff;
                    if (ll is SceneSpotLight sp)
                    {
                        entry["InnerConeAngle"] = sp.InnerConeAngle;
                        entry["OuterConeAngle"] = sp.OuterConeAngle;
                    }
                    else if (ll is SceneRectLight rl)
                    {
                        entry["SourceWidth"] = rl.SourceWidth;
                        entry["SourceHeight"] = rl.SourceHeight;
                    }
                    break;
                case SceneDirectionalLight dl:
                    entry["Type"] = "DirectionalLight";
                    entry["Color"] = new[] { dl.Color.X, dl.Color.Y, dl.Color.Z };
                    entry["Intensity"] = dl.Intensity;
                    break;
                case SceneSkyLight sky:
                    entry["Type"] = "SkyLight";
                    entry["Color"] = new[] { sky.Color.X, sky.Color.Y, sky.Color.Z };
                    entry["Intensity"] = sky.Intensity;
                    break;
                case SceneLandscape sl:
                    entry["Type"] = "Landscape";
                    entry["ComponentSizeQuads"] = sl.SourceComponent.ComponentSizeQuads;
                    entry["SectionBaseX"] = sl.SourceComponent.SectionBaseX;
                    entry["SectionBaseY"] = sl.SourceComponent.SectionBaseY;
                    break;
            }

            if (entry.ContainsKey("Type"))
                actors.Add(entry);

            foreach (var child in node.Children)
                Collect(child);
        }

        foreach (var root in scene.RootObjects)
            Collect(root);

        var worldPayload = new
        {
            WorldName = scene.WorldName,
            LevelCount = scene.Levels.Count,
            ActorCount = actors.Count,
            HeightFog = scene.HeightFog is { } hf ? new
            {
                hf.FogDensity,
                hf.FogHeightFalloff,
                hf.FogMaxOpacity,
                hf.StartDistance,
                InscatteringColor = new[] { hf.FogInscatteringColor.X, hf.FogInscatteringColor.Y, hf.FogInscatteringColor.Z }
            } : null,
            Actors = actors
        };

        File.WriteAllText(worldJsonPath, JsonSerializer.Serialize(worldPayload, _jsonOpts));
        var manifestPath = WriteManifestAndQueue("World", scene.WorldName, scene.Levels.FirstOrDefault()?.PackagePath ?? scene.WorldName, outDir, new Dictionary<string, string>
        {
            ["WorldJson"] = worldJsonPath
        });
        log?.Invoke($"[Export to Unreal] Exported World '{scene.WorldName}' ({actors.Count} actors) -> {outDir}");
        return new ExportOutcome(true, "World", scene.WorldName, outDir, manifestPath, $"Exported World '{scene.WorldName}' ({actors.Count} actors)");
    }

    private static ExportOutcome ExportWorld(UWorld world, IFileProvider provider, Action<string>? log)
    {
        var tempScene = new WorldScene();
        WorldLoader.LoadWorld(world, tempScene, provider, log);
        return ExportLoadedScene(tempScene, log);
    }

    private static ExportOutcome ExportStaticMesh(UStaticMesh mesh, Action<string>? log)
    {
        var safeName = SanitizeName(mesh.Name);
        var outDir = PrepareExportDir("StaticMesh", safeName);
        var matsDir = Path.Combine(outDir, "Materials");
        Directory.CreateDirectory(matsDir);
        var exportedMats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var meshJsonPath = WriteStaticMeshFiles(mesh, outDir, matsDir, exportedMats);
        var objPath = Path.Combine(outDir, $"{safeName}.obj");

        var manifestPath = WriteManifestAndQueue("StaticMesh", mesh.Name, mesh.GetPathName(), outDir, new Dictionary<string, string>
        {
            ["ObjFile"] = objPath,
            ["MeshJson"] = meshJsonPath
        });
        log?.Invoke($"[Export to Unreal] Exported StaticMesh '{mesh.Name}' ({exportedMats.Count} materials + textures) -> {outDir}");
        return new ExportOutcome(true, "StaticMesh", mesh.Name, outDir, manifestPath, $"Exported StaticMesh '{mesh.Name}'");
    }

    private static string WriteStaticMeshFiles(UStaticMesh mesh, string outDir, string matsDir, Dictionary<string, string> exportedMaterials)
    {
        var safeName = SanitizeName(mesh.Name);
        var objPath = Path.Combine(outDir, $"{safeName}.obj");
        var meshJsonPath = Path.Combine(outDir, "mesh.json");

        var vCount = 0;
        var iCount = 0;
        string? attribsPath = null;
        try
        {
            using var dto = new StaticMeshDto(mesh, ExplorerBridge.ToExportOptions());
            if (dto.LODs.Count > 0)
            {
                var lod = dto.LODs[0];
                WriteLodToObj(lod, objPath);
                vCount = lod.Vertices.Length;
                iCount = lod.Indices.Length;

                // Extract extra UV channels (UV1+, e.g. BaseSimple_4Layers layer index in UV1.x) & VertexColors
                var extraUvs = new List<float[]>();
                var rawLod = mesh.RenderData?.LODs is { Length: > 0 } ? mesh.RenderData.LODs[0] : null;
                if (rawLod?.VertexBuffer?.UV is { Length: > 0 } rawUvs && rawUvs.Length == vCount && rawUvs[0].UV is { Length: > 1 } firstUvSet)
                {
                    for (var c = 1; c < firstUvSet.Length; c++)
                    {
                        var ch = new float[vCount * 2];
                        for (var v = 0; v < vCount; v++)
                        {
                            var uvArr = rawUvs[v].UV;
                            if (c < uvArr.Length) { ch[v * 2] = uvArr[c].U; ch[v * 2 + 1] = uvArr[c].V; }
                        }
                        extraUvs.Add(ch);
                    }
                }
                else
                {
                    foreach (var extra in lod.ExtraUvs ?? [])
                    {
                        if (extra is null || extra.Length != vCount) continue;
                        var ch = new float[vCount * 2];
                        for (var v = 0; v < vCount; v++) { ch[v * 2] = extra[v].U; ch[v * 2 + 1] = extra[v].V; }
                        extraUvs.Add(ch);
                    }
                }

                byte[]? vColors = null;
                if (lod.VertexColors is { Length: > 0 } vcSets && vcSets[0].Colors is { } srcColors && srcColors.Length == vCount)
                {
                    vColors = new byte[vCount * 4];
                    for (var v = 0; v < vCount; v++)
                    {
                        vColors[v * 4 + 0] = srcColors[v].R;
                        vColors[v * 4 + 1] = srcColors[v].G;
                        vColors[v * 4 + 2] = srcColors[v].B;
                        vColors[v * 4 + 3] = srcColors[v].A;
                    }
                }

                if (extraUvs.Count > 0 || vColors is not null)
                {
                    attribsPath = Path.Combine(outDir, "mesh_attribs.bin");
                    using var bw = new BinaryWriter(File.Create(attribsPath));
                    bw.Write(0x4D415452u); // 'MATR'
                    bw.Write(vCount);
                    bw.Write(extraUvs.Count);
                    bw.Write(vColors is not null ? 1 : 0);
                    bw.Write(iCount);
                    foreach (var ch in extraUvs)
                        foreach (var f in ch)
                            bw.Write(f);
                    if (vColors is not null)
                        bw.Write(vColors);
                    foreach (var idx in lod.Indices)
                        bw.Write(idx);
                }
            }
        }
        catch { }

        var slotMaterials = new List<object>();
        for (var i = 0; i < (mesh.Materials?.Length ?? 0); i++)
        {
            var mIdx = mesh.Materials![i];
            if (mIdx is not { IsNull: false } || !mIdx.TryLoad<UUnrealMaterial>(out var uMat) || uMat is null)
                continue;
            var matAsset = new MaterialAsset(uMat);
            var key = uMat.GetPathName();
            if (!exportedMaterials.TryGetValue(key, out var matJson))
            {
                var matSubDir = Path.Combine(matsDir, GetUniqueMaterialExportName(matAsset));
                Directory.CreateDirectory(matSubDir);
                (matJson, _) = WriteMaterialFiles(matAsset, matSubDir);
                exportedMaterials[key] = matJson;
            }
            slotMaterials.Add(new
            {
                SlotIndex = i,
                Name = GetUniqueMaterialExportName(matAsset),
                PackagePath = key,
                IsInstance = uMat is UMaterialInstance,
                IsMaterialInstanceDynamic = uMat is UMaterialInstanceDynamic,
                MaterialJson = matJson
            });
        }

        var meshPayload = new
        {
            Name = mesh.Name,
            PackagePath = mesh.GetPathName(),
            ObjFile = objPath,
            AttribsFile = attribsPath,
            VertexCount = vCount,
            IndexCount = iCount,
            Materials = slotMaterials
        };
        File.WriteAllText(meshJsonPath, JsonSerializer.Serialize(meshPayload, _jsonOpts));
        return meshJsonPath;
    }

    private static ExportOutcome ExportSkeletalMesh(USkeletalMesh mesh, Action<string>? log)
    {
        var safeName = SanitizeName(mesh.Name);
        var outDir = PrepareExportDir("SkeletalMesh", safeName);
        var meshJsonPath = Path.Combine(outDir, "mesh.json");

        var exportedMaterials = new List<object>();
        var matsDir = Path.Combine(outDir, "Materials");
        Directory.CreateDirectory(matsDir);

        for (var i = 0; i < (mesh.Materials?.Length ?? 0); i++)
        {
            var mIdx = mesh.Materials![i];
            if (mIdx is not { IsNull: false } || !mIdx.TryLoad<UUnrealMaterial>(out var uMat) || uMat is null)
                continue;
            var matAsset = new MaterialAsset(uMat);
            var matSubDir = Path.Combine(matsDir, $"{i}_{SanitizeName(matAsset.Name)}");
            Directory.CreateDirectory(matSubDir);
            var (matJson, matT3d) = WriteMaterialFiles(matAsset, matSubDir);
            exportedMaterials.Add(new
            {
                SlotIndex = i,
                Name = matAsset.Name,
                PackagePath = uMat.GetPathName(),
                IsInstance = uMat is UMaterialInstance,
                MaterialJson = matJson,
                MaterialT3D = matT3d
            });
        }

        var payload = new
        {
            Name = mesh.Name,
            PackagePath = mesh.GetPathName(),
            Materials = exportedMaterials
        };
        File.WriteAllText(meshJsonPath, JsonSerializer.Serialize(payload, _jsonOpts));

        var manifestPath = WriteManifestAndQueue("SkeletalMesh", mesh.Name, mesh.GetPathName(), outDir, new Dictionary<string, string>
        {
            ["MeshJson"] = meshJsonPath
        });
        log?.Invoke($"[Export to Unreal] Exported SkeletalMesh '{mesh.Name}' -> {outDir}");
        return new ExportOutcome(true, "SkeletalMesh", mesh.Name, outDir, manifestPath, $"Exported SkeletalMesh '{mesh.Name}'");
    }

    private static ExportOutcome ExportMaterial(MaterialAsset matAsset, Action<string>? log)
    {
        var kind = matAsset.CookedMaterial is UMaterialInstance ? "MaterialInstance" : "Material";
        var safeName = GetUniqueMaterialExportName(matAsset);
        var outDir = PrepareExportDir(kind, safeName);
        var (matJson, matT3d) = WriteMaterialFiles(matAsset, outDir);

        var manifestPath = WriteManifestAndQueue(kind, matAsset.Name, matAsset.CookedMaterial?.GetPathName() ?? matAsset.Name, outDir, new Dictionary<string, string>
        {
            ["MaterialJson"] = matJson,
            ["MaterialT3D"] = matT3d
        });
        log?.Invoke($"[Export to Unreal] Exported {kind} '{matAsset.Name}' -> {outDir}");
        return new ExportOutcome(true, kind, matAsset.Name, outDir, manifestPath, $"Exported {kind} '{matAsset.Name}'");
    }

    private static ExportOutcome ExportNiagara(UObject niagaraObj, Action<string>? log)
    {
        var safeName = SanitizeName(niagaraObj.Name);
        var outDir = PrepareExportDir("Niagara", safeName);
        var meshesDir = Path.Combine(outDir, "Meshes");
        var matsDir = Path.Combine(outDir, "Materials");
        Directory.CreateDirectory(meshesDir);
        Directory.CreateDirectory(matsDir);

        var exportedMeshes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var exportedMats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var niagaraJsonPath = WriteNiagaraFiles(niagaraObj, outDir, meshesDir, matsDir, exportedMeshes, exportedMats);

        var manifestPath = WriteManifestAndQueue("Niagara", niagaraObj.Name, niagaraObj.GetPathName(), outDir, new Dictionary<string, string>
        {
            ["NiagaraJson"] = niagaraJsonPath
        });
        log?.Invoke($"[Export to Unreal] Exported Niagara '{niagaraObj.Name}' ({exportedMeshes.Count} meshes, {exportedMats.Count} materials + textures) -> {outDir}");
        return new ExportOutcome(true, "Niagara", niagaraObj.Name, outDir, manifestPath, $"Exported Niagara '{niagaraObj.Name}'");
    }

    private static string WriteNiagaraFiles(
        UObject niagaraObj,
        string outDir,
        string meshesDir,
        string matsDir,
        Dictionary<string, string> exportedMeshes,
        Dictionary<string, string> exportedMats)
    {
        var asset = niagaraObj.ExportType is "NiagaraSystem" or "NiagaraEmitter"
            ? new NiagaraAsset(niagaraObj, niagaraObj.Name)
            : new NiagaraAsset(niagaraObj);
        var sysObj = asset.SystemAsset ?? niagaraObj;
        var niagaraJsonPath = Path.Combine(outDir, "niagara.json");
        var texDir = Path.Combine(outDir, "Textures");
        Directory.CreateDirectory(texDir);

        string? EnsureMat(MaterialAsset? mat)
        {
            if (mat is null) return null;
            var key = mat.CookedMaterial?.GetPathName() ?? mat.Name;
            if (exportedMats.TryGetValue(key, out var existing))
                return existing;
            var mDir = Path.Combine(matsDir, GetUniqueMaterialExportName(mat));
            Directory.CreateDirectory(mDir);
            var (mJson, _) = WriteMaterialFiles(mat, mDir);
            exportedMats[key] = mJson;
            return mJson;
        }

        static object[]? SampleVm(NiagaraEmitterVmSimulation? vmSim, bool isMesh)
        {
            if (vmSim is null) return null;
            var times = new[] { 0.1f, 0.5f, 1.0f };
            var result = new List<object>(times.Length);
            foreach (var t in times)
            {
                if (!vmSim.TrySimulate(t, out var parts) || parts.Length == 0)
                {
                    result.Add(new { Time = t, Count = 0, Particles = Array.Empty<object>() });
                    continue;
                }
                var take = Math.Min(parts.Length, 4);
                var pList = new List<object>(take);
                for (var i = 0; i < take; i++)
                {
                    var p = parts[i];
                    pList.Add(new
                    {
                        Pos = new[] { p.Position.X, p.Position.Y, p.Position.Z },
                        Color = new[] { p.Color.X, p.Color.Y, p.Color.Z, p.Color.W },
                        Scale = isMesh ? new[] { p.Scale.X, p.Scale.Y, p.Scale.Z } : new[] { p.SpriteSize.X, p.SpriteSize.Y, 1f },
                        Age = p.NormalizedAge,
                        Dyn = new[] { p.DynamicMaterialParameter.X, p.DynamicMaterialParameter.Y, p.DynamicMaterialParameter.Z, p.DynamicMaterialParameter.W }
                    });
                }
                result.Add(new { Time = t, Count = parts.Length, Particles = pList });
            }
            return result.ToArray();
        }

        static object? SerializeFloatCurve(NiagaraFloatCurve? curve)
        {
            if (curve is null || curve.Lut.Length == 0) return null;
            const int maxKeys = 16;
            var count = Math.Min(maxKeys, curve.Lut.Length);
            var keys = new List<object>(count);
            for (var i = 0; i < count; i++)
            {
                var t = count > 1 ? (float)i / (count - 1) : 0f;
                keys.Add(new { Time = t, Value = curve.Sample(t) });
            }
            return new { curve.Name, Keys = keys };
        }

        static object? SerializeColorCurve(NiagaraColorCurve? curve)
        {
            if (curve is null || curve.Lut.Length < 4) return null;
            const int maxKeys = 16;
            var sampleCount = curve.Lut.Length / 4;
            var count = Math.Min(maxKeys, sampleCount);
            var keys = new List<object>(count);
            for (var i = 0; i < count; i++)
            {
                var t = count > 1 ? (float)i / (count - 1) : 0f;
                var c = curve.Sample(t);
                keys.Add(new { Time = t, R = c.X, G = c.Y, B = c.Z, A = c.W });
            }
            return new { curve.Name, Keys = keys };
        }

        static (int count, System.Numerics.Vector3 posMin, System.Numerics.Vector3 posMax, System.Numerics.Vector3 velAvg, System.Numerics.Vector3 scaleAvg, float dyn3, float spriteRot) ExtractVmStats(NiagaraEmitterVmSimulation? vmSim, float sampleTime)
        {
            if (vmSim is null || !vmSim.TrySimulate(sampleTime, out var parts) || parts.Length == 0)
                return (0, System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One, 0f, 0f);
            var pMin = new System.Numerics.Vector3(float.MaxValue);
            var pMax = new System.Numerics.Vector3(float.MinValue);
            var vSum = System.Numerics.Vector3.Zero;
            var sSum = System.Numerics.Vector3.Zero;
            float d3Sum = 0f, rotSum = 0f;
            foreach (var p in parts)
            {
                pMin = System.Numerics.Vector3.Min(pMin, p.Position);
                pMax = System.Numerics.Vector3.Max(pMax, p.Position);
                vSum += p.Velocity;
                sSum += p.Scale;
                d3Sum += p.DynamicMaterialParameter.Z;
                rotSum += p.SpriteRotation;
            }
            float inv = 1f / parts.Length;
            return (parts.Length, pMin, pMax, vSum * inv, sSum * inv, d3Sum * inv, rotSum * inv);
        }

        var spriteList = new List<object>();
        foreach (var se in asset.SpriteEmitters)
        {
            string? texPath = se.SpriteTexture is not null ? TrySaveTexturePng(se.SpriteTexture, texDir) : null;
            var matJson = EnsureMat(se.Material);
            var vmSamples = SampleVm(se.VmSimulation, isMesh: false);
            var (vmCount, pMin, pMax, velAvg, _, _, spriteRotAvg) = ExtractVmStats(se.VmSimulation, MathF.Max(0.5f, se.Lifetime * 0.5f));
            var boxSize = vmCount > 1 ? pMax - pMin : System.Numerics.Vector3.Zero;
            var boxCenter = vmCount > 1 ? (pMin + pMax) * 0.5f : System.Numerics.Vector3.Zero;
            var effMax = Math.Max(se.MaxParticles, vmCount);
            var spawnRate = effMax / MathF.Max(0.1f, se.Lifetime);
            spriteList.Add(new
            {
                se.EmitterName,
                se.Enabled,
                se.LocalSpace,
                MaxParticles = effMax,
                SpawnRate = spawnRate,
                se.Lifetime,
                SpriteSize = new[] { se.SpriteSize.X * se.SizeMultiplier, se.SpriteSize.Y * se.SizeMultiplier },
                se.SizeMultiplier,
                Color = new[] { se.Color.X, se.Color.Y, se.Color.Z, se.Color.W * se.AlphaMultiplier },
                se.ColorBrightness,
                se.AlphaMultiplier,
                SpawnRadius = se.SpawnRadius,
                se.VerticalLineLength,
                se.VerticalOffsetZ,
                se.TurbulenceStrength,
                OriginOffset = new[] { se.OriginOffset.X, se.OriginOffset.Y, se.OriginOffset.Z },
                BoxSize = new[] { boxSize.X, boxSize.Y, boxSize.Z },
                BoxCenter = new[] { boxCenter.X, boxCenter.Y, boxCenter.Z },
                VelAvg = new[] { velAvg.X, velAvg.Y, velAvg.Z },
                SpriteRotation = spriteRotAvg,
                se.VelocitySpeed,
                se.GravityZ,
                se.TimeScale,
                se.IsRibbon,
                se.RibbonWidth,
                se.SubImageBlend,
                se.HasExplicitSimData,
                se.IsGpuCompute,
                PivotInUVSpace = new[] { se.PivotInUVSpace.X, se.PivotInUVSpace.Y },
                SubImageSize = new[] { se.SubImageSize.X, se.SubImageSize.Y },
                ColorCurve = SerializeColorCurve(se.ColorCurve),
                ScaleCurve = SerializeFloatCurve(se.UniformSizeCurve ?? se.ScaleFactorCurve),
                ScaleRgbCurve = SerializeFloatCurve(se.ScaleRgbCurve),
                AlphaCurve = SerializeFloatCurve(se.AlphaCurve),
                VmSamples = vmSamples,
                TextureFile = texPath,
                MaterialName = se.Material?.Name,
                MaterialJson = matJson
            });
        }

        var meshList = new List<object>();
        foreach (var me in asset.MeshEmitters)
        {
            var mKey = me.Mesh.GetPathName();
            if (!exportedMeshes.TryGetValue(mKey, out var meshJson))
            {
                var mDir = Path.Combine(meshesDir, SanitizeName(me.Mesh.Name));
                Directory.CreateDirectory(mDir);
                meshJson = WriteStaticMeshFiles(me.Mesh, mDir, matsDir, exportedMats);
                exportedMeshes[mKey] = meshJson;
            }
            var matJson = EnsureMat(me.Material);
            var vmSamples = SampleVm(me.VmSimulation, isMesh: true);
            var effMax = me.MaxParticles;
            var effScale = new[] { me.Scale.X * me.ScaleMultiplier, me.Scale.Y * me.ScaleMultiplier, me.Scale.Z * me.ScaleMultiplier };
            var posZRange = new[] { 0f, 0f };
            var dynParam3 = 0f;
            var (meCount, pMin, pMax, velAvg, scaleAvg, vmDyn3, _) = ExtractVmStats(me.VmSimulation, MathF.Max(0.5f, me.Lifetime * 0.5f));
            var boxSize = meCount > 1 ? pMax - pMin : System.Numerics.Vector3.Zero;
            var boxCenter = meCount > 1 ? (pMin + pMax) * 0.5f : System.Numerics.Vector3.Zero;
            if (meCount > 0)
            {
                effMax = Math.Max(effMax, meCount);
                if (me.Scale == System.Numerics.Vector3.One && me.ScaleMultiplier == 1.0f && scaleAvg.LengthSquared() > 1e-5f)
                    effScale = new[] { scaleAvg.X, scaleAvg.Y, scaleAvg.Z };
                if (pMax.Z > pMin.Z) posZRange = new[] { pMin.Z, pMax.Z };
                dynParam3 = vmDyn3;
            }
            var spawnRate = effMax / MathF.Max(0.1f, me.Lifetime);
            meshList.Add(new
            {
                me.EmitterName,
                me.Enabled,
                me.LocalSpace,
                MaxParticles = effMax,
                SpawnRate = spawnRate,
                me.Lifetime,
                Scale = effScale,
                StartOffset = new[] { me.StartOffset.X, me.StartOffset.Y, me.StartOffset.Z },
                PosZRange = posZRange,
                BoxSize = new[] { boxSize.X, boxSize.Y, boxSize.Z },
                BoxCenter = new[] { boxCenter.X, boxCenter.Y, boxCenter.Z },
                VelAvg = new[] { velAvg.X, velAvg.Y, velAvg.Z },
                DynamicParam3 = dynParam3,
                RotationEuler = new[] { me.RotationEuler.X, me.RotationEuler.Y, me.RotationEuler.Z },
                SpinSpeedEuler = new[] { me.SpinSpeedEuler.X, me.SpinSpeedEuler.Y, me.SpinSpeedEuler.Z },
                TravelVector = new[] { me.TravelVector.X, me.TravelVector.Y, me.TravelVector.Z },
                Color = new[] { me.Color.X, me.Color.Y, me.Color.Z, me.Color.W * me.AlphaMultiplier },
                me.GravityZ,
                me.TimeScale,
                me.PhaseSpread,
                me.FacingMode,
                me.LockedAxisEnable,
                LockedAxis = new[] { me.LockedAxis.X, me.LockedAxis.Y, me.LockedAxis.Z },
                me.LockedAxisSpace,
                ColorCurve = SerializeColorCurve(me.ColorCurve),
                ScaleCurve = SerializeFloatCurve(me.ScaleFactorCurve),
                ScaleRgbCurve = SerializeFloatCurve(me.ScaleRgbCurve),
                AlphaCurve = SerializeFloatCurve(me.AlphaCurve),
                PositionCurve = SerializeFloatCurve(me.PositionCurve),
                DynamicParam1Curve = SerializeFloatCurve(me.DynamicParam1Curve),
                DynamicParam2Curve = SerializeFloatCurve(me.DynamicParam2Curve),
                VmSamples = SampleVm(me.VmSimulation, isMesh: true),
                MeshName = me.Mesh.Name,
                MeshPath = mKey,
                MeshJson = meshJson,
                MaterialName = me.Material?.Name,
                MaterialJson = matJson
            });
        }

        var lightList = asset.LightEmitters.Select(le => new
        {
            le.EmitterName,
            le.Enabled,
            Color = new[] { le.Color.X, le.Color.Y, le.Color.Z },
            le.Intensity,
            le.DefaultRadius,
            le.RadiusScale
        }).ToList();

        var payload = new
        {
            Name = sysObj.Name,
            ClassName = sysObj.ExportType,
            PackagePath = sysObj.GetPathName(),
            SpriteEmitters = spriteList,
            MeshEmitters = meshList,
            LightEmitters = lightList
        };
        File.WriteAllText(niagaraJsonPath, JsonSerializer.Serialize(payload, _jsonOpts));
        return niagaraJsonPath;
    }

    private static (string MatJsonPath, string MatT3dPath) WriteMaterialFiles(MaterialAsset matAsset, string outDir, HashSet<string>? visitedMaterials = null)
    {
        visitedMaterials ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        visitedMaterials.Add(matAsset.CookedMaterial?.GetPathName() ?? matAsset.Name);

        var texDir = Path.Combine(outDir, "Textures");
        Directory.CreateDirectory(texDir);

        var graph = UeMaterialGraphBuilder.Build(matAsset);
        var t3d = UeMaterialGraphBuilder.ExportToUeT3D(graph);
        var t3dPath = Path.Combine(outDir, "material_nodes.t3d");
        File.WriteAllText(t3dPath, t3d);

        var exportedTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes)
        {
            if (node.PreviewTexture is { } tex && !exportedTextures.ContainsKey(tex.Name))
            {
                if (TrySaveTexturePng(tex, texDir) is { } pngPath)
                    exportedTextures[tex.Name] = pngPath;
            }
        }

        var graphNodes = new List<object>(graph.Nodes.Count);
        var graphLinks = new List<object>();

        foreach (var node in graph.Nodes)
        {
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in node.ExpressionProperties)
                props[k] = v;

            graphNodes.Add(new
            {
                node.NodeName,
                node.ExpressionClass,
                node.Title,
                node.PosX,
                node.PosY,
                TextureName = node.PreviewTexture?.Name,
                Properties = props
            });

            foreach (var inPin in node.InputPins)
            {
                foreach (var (srcNode, srcPin) in inPin.LinkedTo)
                {
                    graphLinks.Add(new
                    {
                        FromNode = srcNode.NodeName,
                        FromPin = srcPin.PinName,
                        ToNode = node.NodeName,
                        ToPin = inPin.PinName
                    });
                }
            }
        }

        if (graph.RootNode is not null)
        {
            foreach (var inPin in graph.RootNode.InputPins)
            {
                foreach (var (srcNode, srcPin) in inPin.LinkedTo)
                {
                    graphLinks.Add(new
                    {
                        FromNode = srcNode.NodeName,
                        FromPin = srcPin.PinName,
                        ToNode = "Root",
                        ToPin = inPin.PinName
                    });
                }
            }
        }

        var pixelShaderAsm = string.Empty;
        var vertexShaderAsm = string.Empty;
        var scalarParams = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var vectorParams = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        var orderedScalarParams = new List<object>();
        var orderedVectorParams = new List<object>();
        var cbVectorCount = 0;
        var psMaterialCbSlot = -1;
        var psViewCbSlot = -1;
        var psMpcCbSlot = -1;
        var vsMaterialCbSlot = -1;
        var vsViewCbSlot = -1;
        var vsMpcCbSlot = -1;
        var psMpcCbSlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var vsMpcCbSlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var psVertexFactory = string.Empty;
        var psTextureSlots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var vsTextureSlots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var psTextureParamNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var vsTextureParamNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var evaluatedCbSlots = new List<float[]>();
        var cbSlotParamNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var compoundScalarSlotKeys = new List<(string ParamName, int Slot, int Comp)>();
        var compoundVectorSlotKeys = new List<(string ParamName, int Slot)>();
        var parameterCollections = new List<object>();
        try
        {
            var shaderSets = matAsset.GetNativeShaders(validateOnDevice: false);
            var primarySet = shaderSets.FirstOrDefault(s => s.UniformSet is not null && s.Shaders.Count > 0)
                             ?? shaderSets.FirstOrDefault(s => s.Shaders.Count > 0);
            if (primarySet is not null)
            {
                // Prefer Niagara VF pixel shader when present so ParticleColor & DynamicParameter aren't constant-folded away
                var ps = primarySet.Shaders.FirstOrDefault(s =>
                             s.ShaderType.Contains("TBasePassPS", StringComparison.OrdinalIgnoreCase) &&
                             (s.VertexFactory.Contains("NiagaraMesh", StringComparison.OrdinalIgnoreCase) ||
                              s.VertexFactory.Contains("NiagaraSprite", StringComparison.OrdinalIgnoreCase) ||
                              s.VertexFactory.Contains("NiagaraRibbon", StringComparison.OrdinalIgnoreCase)))
                         ?? primarySet.Shaders.FirstOrDefault(s => s.ShaderType.Contains("TBasePassPS", StringComparison.OrdinalIgnoreCase))
                         ?? primarySet.Shaders.FirstOrDefault(s => s.ShaderType.Contains("PS", StringComparison.Ordinal));

                var (matResources, _) = matAsset.ResolveNativeMaterialResourceLayout(primarySet);
                var resourceParamNames = new string?[matResources.Length];
                if (primarySet.UniformSet is { } texUniforms)
                {
                    var texParams = texUniforms.UniformTextureParameters ?? [];
                    var virtualTypeIndex = texParams.Length switch
                    {
                        >= 6 => texParams.Length - 1,
                        5 => 4,
                        _ => -1
                    };
                    var pSlot = 0;
                    void RecordTexParamName(string? pName)
                    {
                        if (pSlot < resourceParamNames.Length && !string.IsNullOrWhiteSpace(pName) && pName != "None")
                            resourceParamNames[pSlot] = pName;
                        pSlot += 2;
                    }
                    var nonVirtualCount = virtualTypeIndex >= 0 ? virtualTypeIndex : texParams.Length;
                    for (var typeIdx = 0; typeIdx < nonVirtualCount; typeIdx++)
                    {
                        foreach (var param in texParams[typeIdx] ?? [])
                            RecordTexParamName(param.ParameterInfo?.Name.Text ?? param.ParameterName);
                    }
                    foreach (var _ in texUniforms.UniformExternalTextureParameters ?? [])
                        RecordTexParamName(null);
                    foreach (var stack in texUniforms.VTStacks ?? [])
                    {
                        pSlot++;
                        if (stack.NumLayers > 4) pSlot++;
                        pSlot++;
                    }
                    if (virtualTypeIndex >= 0 && virtualTypeIndex < texParams.Length)
                    {
                        foreach (var vtParam in texParams[virtualTypeIndex] ?? [])
                            RecordTexParamName(vtParam.ParameterInfo?.Name.Text ?? vtParam.ParameterName);
                    }
                    pSlot += 2;
                    if (pSlot != resourceParamNames.Length && texUniforms.UniformBufferLayoutInitializer?.Resources is { } layoutResources)
                    {
                        var allTex = texParams.SelectMany(x => x ?? []).ToList();
                        var nextAll = 0;
                        for (var ri = 0; ri < Math.Min(layoutResources.Length, resourceParamNames.Length); ri++)
                        {
                            var mt = layoutResources[ri].MemberType.ToString();
                            var mtByte = (byte)layoutResources[ri].MemberType;
                            if (mt.Contains("TEXTURE", StringComparison.OrdinalIgnoreCase) ||
                                mt.Contains("SRV", StringComparison.OrdinalIgnoreCase) ||
                                mtByte is 5 or 6)
                            {
                                if (nextAll < allTex.Count)
                                {
                                    var p = allTex[nextAll++];
                                    if (resourceParamNames[ri] is null)
                                    {
                                        var pName = p.ParameterInfo?.Name.Text ?? p.ParameterName;
                                        if (!string.IsNullOrWhiteSpace(pName) && pName != "None")
                                            resourceParamNames[ri] = pName;
                                    }
                                }
                            }
                        }
                    }
                }

                if (ps is { Code: not null, Bytecode.Length: > 0 })
                {
                    psVertexFactory = ps.VertexFactory ?? string.Empty;
                    pixelShaderAsm = ps.Disassembly ??= D3D11NativeShaderDevice.Disassemble(ps) ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(pixelShaderAsm))
                        File.WriteAllText(Path.Combine(outDir, "pixel_shader.asm"), pixelShaderAsm);

                    if (ps.Container is { } psContainer)
                    {
                        var psInfo = UnrealD3D11ShaderInfo.Parse(ps.Code, psContainer);
                        for (var ui = 0; ui < psInfo.UniformBufferNames.Count; ui++)
                        {
                            var uName = psInfo.UniformBufferNames[ui];
                            if (uName.Equals("View", StringComparison.OrdinalIgnoreCase))
                                psViewCbSlot = ui;
                            else if (uName.Equals("Material", StringComparison.OrdinalIgnoreCase))
                                psMaterialCbSlot = ui;
                            else if (uName.StartsWith("MaterialCollection", StringComparison.OrdinalIgnoreCase))
                            {
                                var suffix = uName.Substring("MaterialCollection".Length);
                                var mpcIdx = int.TryParse(suffix, out var parsedIdx) ? parsedIdx : 0;
                                psMpcCbSlots[$"cb{ui}"] = mpcIdx;
                                if (psMpcCbSlot < 0) psMpcCbSlot = ui;
                            }
                        }
                        foreach (var texEntry in psInfo.Textures)
                        {
                            if (texEntry.UniformBuffer >= 0 && texEntry.UniformBuffer < psInfo.UniformBufferNames.Count &&
                                psInfo.UniformBufferNames[texEntry.UniformBuffer].Equals("Material", StringComparison.OrdinalIgnoreCase) &&
                                texEntry.Resource >= 0 && texEntry.Resource < matResources.Length &&
                                matResources[texEntry.Resource] is { } texObj)
                            {
                                psTextureSlots[$"t{texEntry.Register}"] = texObj.Name;
                                if (resourceParamNames[texEntry.Resource] is { Length: > 0 } pName)
                                    psTextureParamNames[$"t{texEntry.Register}"] = pName;
                                if (!exportedTextures.ContainsKey(texObj.Name) && TrySaveTexturePng(texObj, texDir) is { } pngPath)
                                    exportedTextures[texObj.Name] = pngPath;
                            }
                        }
                    }
                }

                // Prefer a DXBC TBasePassVS (FLocalVertexFactory preferred) that binds Material uniform buffer (has WPO)
                NativeShaderEntry? vs = null;
                UnrealD3D11ShaderInfo? bestVsInfo = null;
                Dictionary<string, int> bestVsMpcSlots = new(StringComparer.OrdinalIgnoreCase);
                var bestVsScore = int.MinValue;
                foreach (var candVs in primarySet.Shaders.Where(s =>
                             s.Stage == ENativeShaderStage.Vertex &&
                             s.Code is not null &&
                             s.Bytecode is { Length: > 0 } &&
                             s.Container is { Kind: ENativeBytecodeKind.Dxbc }))
                {
                    var vInfo = UnrealD3D11ShaderInfo.Parse(candVs.Code!, candVs.Container!.Value);
                    var ubNames = vInfo.UniformBufferNames.ToList();
                    var matSlot = ubNames.FindIndex(n => n.Equals("Material", StringComparison.OrdinalIgnoreCase));
                    var viewSlot = ubNames.FindIndex(n => n.Equals("View", StringComparison.OrdinalIgnoreCase));
                    var mpcSlot = ubNames.FindIndex(n => n.StartsWith("MaterialCollection", StringComparison.OrdinalIgnoreCase));
                    var candMpcSlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (var ui = 0; ui < ubNames.Count; ui++)
                    {
                        if (ubNames[ui].StartsWith("MaterialCollection", StringComparison.OrdinalIgnoreCase))
                        {
                            var suffix = ubNames[ui].Substring("MaterialCollection".Length);
                            candMpcSlots[$"cb{ui}"] = int.TryParse(suffix, out var parsedIdx) ? parsedIdx : 0;
                        }
                    }

                    var score = 0;
                    if (matSlot >= 0) score += 100;
                    if (candVs.ShaderType.Contains("TBasePassVS", StringComparison.OrdinalIgnoreCase)) score += 40;
                    if (candVs.VertexFactory.Contains("FLocalVertexFactory", StringComparison.OrdinalIgnoreCase)) score += 20;
                    if (!candVs.VertexFactory.Contains("Particle", StringComparison.OrdinalIgnoreCase) &&
                        !candVs.VertexFactory.Contains("Niagara", StringComparison.OrdinalIgnoreCase))
                        score += 5;

                    if (score > bestVsScore)
                    {
                        bestVsScore = score;
                        vs = candVs;
                        bestVsInfo = vInfo;
                        vsMaterialCbSlot = matSlot;
                        vsViewCbSlot = viewSlot;
                        vsMpcCbSlot = mpcSlot;
                        bestVsMpcSlots = candMpcSlots;
                    }
                }
                vsMpcCbSlots = bestVsMpcSlots;
                if (vs is { Code: not null, Bytecode.Length: > 0 })
                {
                    if (bestVsInfo is not null)
                    {
                        foreach (var texEntry in bestVsInfo.Textures)
                        {
                            if (texEntry.UniformBuffer >= 0 && texEntry.UniformBuffer < bestVsInfo.UniformBufferNames.Count &&
                                bestVsInfo.UniformBufferNames[texEntry.UniformBuffer].Equals("Material", StringComparison.OrdinalIgnoreCase) &&
                                texEntry.Resource >= 0 && texEntry.Resource < matResources.Length &&
                                matResources[texEntry.Resource] is { } texObj)
                            {
                                vsTextureSlots[$"t{texEntry.Register}"] = texObj.Name;
                                if (resourceParamNames[texEntry.Resource] is { Length: > 0 } pName)
                                    vsTextureParamNames[$"t{texEntry.Register}"] = pName;
                                if (!exportedTextures.ContainsKey(texObj.Name) && TrySaveTexturePng(texObj, texDir) is { } pngPath)
                                    exportedTextures[texObj.Name] = pngPath;
                            }
                        }
                    }
                    vertexShaderAsm = vs.Disassembly ??= D3D11NativeShaderDevice.Disassemble(vs) ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(vertexShaderAsm))
                        File.WriteAllText(Path.Combine(outDir, "vertex_shader.asm"), vertexShaderAsm);
                }

                // Export exact evaluated Material constant buffer float4 slots (cbMat[0..N]) & per-slot parameter names
                if (matAsset.BuildNativeMaterialBuffer(primarySet) is { Constants.Length: >= 16 } evalBuf)
                {
                    var slotCount = evalBuf.Constants.Length / 16;
                    for (var si = 0; si < slotCount; si++)
                    {
                        var off = si * 16;
                        evaluatedCbSlots.Add(new[]
                        {
                            BitConverter.ToSingle(evalBuf.Constants, off + 0),
                            BitConverter.ToSingle(evalBuf.Constants, off + 4),
                            BitConverter.ToSingle(evalBuf.Constants, off + 8),
                            BitConverter.ToSingle(evalBuf.Constants, off + 12)
                        });
                    }

                    var uSet = primarySet.UniformSet;
                    const string compChars = "xyzw";
                    foreach (var (offset, _, trace) in evalBuf.Evaluated)
                    {
                        var slot = offset / 16;
                        var comp = (offset % 16) / 4;
                        var compChar = compChars[Math.Clamp(comp, 0, 3)];

                        string? paramName = null;
                        var isSelectionAlpha = false;

                        var mNamed = Regex.Match(trace, @"(?:Param|Vec|DVec)\[(\d+):([^\]]+)\]");
                        if (mNamed.Success)
                        {
                            paramName = mNamed.Groups[2].Value.Trim();
                        }
                        else
                        {
                            var mScalar = Regex.Match(trace, @"Scalar\[(\d+)\]");
                            if (mScalar.Success && int.TryParse(mScalar.Groups[1].Value, out var sIdx) &&
                                uSet is not null && sIdx >= 0 && sIdx < uSet.UniformScalarParameters.Length)
                            {
                                paramName = uSet.UniformScalarParameters[sIdx].ParameterInfo.Name.PlainText;
                            }
                            else
                            {
                                var mVec = Regex.Match(trace, @"Vec\[(\d+)\]");
                                if (mVec.Success && int.TryParse(mVec.Groups[1].Value, out var vIdx) &&
                                    uSet is not null && vIdx >= 0 && vIdx < uSet.UniformVectorParameters.Length)
                                {
                                    paramName = uSet.UniformVectorParameters[vIdx].ParameterInfo.Name.PlainText;
                                }
                                else
                                {
                                    var mInfo = Regex.Match(trace, @"(?:Scalar|Vec)\(([^,]+),");
                                    if (mInfo.Success)
                                        paramName = mInfo.Groups[1].Value.Trim();
                                }
                            }
                        }

                        var colonIdx = trace.IndexOf(": ", StringComparison.Ordinal);
                        var traceRhs = colonIdx >= 0 ? trace.Substring(colonIdx + 2).Trim() : trace;
                        var isSafeDivClamp = Regex.IsMatch(traceRhs,
                            @"^(?:Scalar|Param)\[[^\]]+\][^->]*(?:->\s*Swizzle\([^)]*\))?\s*->\s*Const\(0,0,0,0\)\s*->\s*Max(?:\s*->\s*Swizzle\([^)]*\))?$");
                        var isCompound = !isSafeDivClamp && (
                            traceRhs.StartsWith("Const(", StringComparison.Ordinal) ||
                            traceRhs.StartsWith("Zero", StringComparison.Ordinal) ||
                            Regex.IsMatch(traceRhs, @"\b(?:Add|Sub|Mul|Div|Fmod|Min|Max|Clamp|Sin|Cos|Tan|Asin|Acos|Atan|Atan2|Dot|Cross|Sqrt|Rcp|Length|Normalize|Saturate|Abs|Floor|Ceil|Round|Trunc|Sign|Frac|Fractional|Log2|Log10|Neg|Append)\b"));

                        var isVecSlot = offset % 16 == 0 &&
                            (trace.StartsWith("vector[", StringComparison.Ordinal) || Regex.IsMatch(trace, @"\([34]c\)|Float[34]"));

                        if (isCompound)
                        {
                            if (isVecSlot)
                            {
                                var pVecName = $"PreshaderVec_{slot}";
                                if (cbSlotParamNames.TryAdd($"{slot}", pVecName))
                                    compoundVectorSlotKeys.Add((pVecName, slot));
                                cbSlotParamNames.TryAdd($"{slot}.{compChar}", pVecName);
                            }
                            else
                            {
                                var pScalarName = $"Preshader_{slot}_{comp}";
                                if (cbSlotParamNames.TryAdd($"{slot}.{compChar}", pScalarName))
                                    compoundScalarSlotKeys.Add((pScalarName, slot, comp));
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(paramName) && paramName != "None")
                        {
                            if (paramName.Equals("SelectionColor", StringComparison.OrdinalIgnoreCase) &&
                                trace.Contains("Swizzle(n=1,3", StringComparison.OrdinalIgnoreCase))
                            {
                                isSelectionAlpha = true;
                            }
                            var finalName = isSelectionAlpha ? "SelectionColor.a" : paramName;
                            if (offset % 16 == 0 && !isSelectionAlpha && isVecSlot)
                            {
                                cbSlotParamNames.TryAdd($"{slot}", finalName);
                            }
                            cbSlotParamNames.TryAdd($"{slot}.{compChar}", finalName);
                        }
                    }
                }

                // Export referenced MaterialParameterCollections (MPC) ordered by shader ParameterCollections GUIDs
                var mpcInfos = new List<CUE4Parse.UE4.Assets.Objects.FStructFallback>();
                for (var node = matAsset; node != null; node = node.Parent)
                {
                    var c = node.CookedMaterial?.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback?>("CachedExpressionData");
                    if (c?.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("ParameterCollectionInfos") is { Length: > 0 } found)
                        mpcInfos.AddRange(found);
                }
                if (mpcInfos.Count == 0 && matAsset.RootMaterial() is { } rootMpcMat)
                {
                    var c = rootMpcMat.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback?>("CachedExpressionData");
                    if (c?.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("ParameterCollectionInfos") is { Length: > 0 } found)
                        mpcInfos.AddRange(found);
                }
                var orderedMpcInfos = new List<CUE4Parse.UE4.Assets.Objects.FStructFallback>();
                var mpcGuids = primarySet.UniformSet?.ParameterCollections ?? [];
                for (var gi = 0; gi < mpcGuids.Length; gi++)
                {
                    var matched = mpcInfos.FirstOrDefault(x => x.GetOrDefault<CUE4Parse.UE4.Objects.Core.Misc.FGuid>("StateId").Equals(mpcGuids[gi]))
                                  ?? (gi < mpcInfos.Count ? mpcInfos[gi] : null);
                    if (matched is not null)
                        orderedMpcInfos.Add(matched);
                }
                foreach (var info in mpcInfos)
                {
                    if (!orderedMpcInfos.Contains(info))
                        orderedMpcInfos.Add(info);
                }
                var exportedMpcNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var info in orderedMpcInfos)
                {
                    if (info.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FPackageIndex?>("ParameterCollection") is { IsNull: false } idx &&
                        idx.TryLoad<UObject>(out var mpcObj) &&
                        exportedMpcNames.Add(mpcObj.Name))
                    {
                        var mpcScalars = new List<object>();
                        foreach (var s in mpcObj.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("ScalarParameters") ?? [])
                        {
                            var pName = s.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("ParameterName").Text;
                            var pVal = s.GetOrDefault<float>("DefaultValue");
                            var pId = s.GetOrDefault<CUE4Parse.UE4.Objects.Core.Misc.FGuid>("Id").ToString();
                            mpcScalars.Add(new { Name = pName, Value = pVal, Id = pId });
                        }
                        var mpcVectors = new List<object>();
                        foreach (var v in mpcObj.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("VectorParameters") ?? [])
                        {
                            var pName = v.GetOrDefault<CUE4Parse.UE4.Objects.UObject.FName>("ParameterName").Text;
                            var pCol = v.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FLinearColor>("DefaultValue");
                            var pId = v.GetOrDefault<CUE4Parse.UE4.Objects.Core.Misc.FGuid>("Id").ToString();
                            mpcVectors.Add(new { Name = pName, Value = new[] { pCol.R, pCol.G, pCol.B, pCol.A }, Id = pId });
                        }
                        var stateId = mpcObj.GetOrDefault<CUE4Parse.UE4.Objects.Core.Misc.FGuid>("StateId").ToString();
                        var mpcJsonPath = Path.Combine(outDir, SanitizeName(mpcObj.Name) + ".mpc.json");
                        var mpcPayload = new
                        {
                            Name = mpcObj.Name,
                            PackagePath = mpcObj.GetPathName(),
                            StateId = stateId,
                            ScalarParameters = mpcScalars,
                            VectorParameters = mpcVectors
                        };
                        File.WriteAllText(mpcJsonPath, JsonSerializer.Serialize(mpcPayload, _jsonOpts));
                        parameterCollections.Add(new { Name = mpcObj.Name, MpcJson = mpcJsonPath });
                    }
                }

                if (primarySet.UniformSet is { } uniforms)
                {
                    cbVectorCount = uniforms.UniformVectorPreshaders.Length;
                    foreach (var sp in uniforms.UniformScalarParameters)
                    {
                        var name = sp.ParameterInfo.Name.PlainText;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            scalarParams[name] = sp.DefaultValue;
                            orderedScalarParams.Add(new { Name = name, Value = sp.DefaultValue });
                        }
                    }
                    foreach (var vp in uniforms.UniformVectorParameters)
                    {
                        var name = vp.ParameterInfo.Name.PlainText;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            var rgba = new[] { vp.DefaultValue.R, vp.DefaultValue.G, vp.DefaultValue.B, vp.DefaultValue.A };
                            vectorParams[name] = rgba;
                            orderedVectorParams.Add(new { Name = name, Value = rgba });
                        }
                    }
                }
            }
        }
        catch { }

        // If this is a Material Instance WITHOUT its own static-permutation shader map, recursively export its parent material (so UE5 creates a real UMaterialInstanceConstant)
        var hasOwnStaticPermutation = matAsset.CookedMaterial is UMaterialInstance miPerm &&
                                      miPerm.LoadedMaterialResources is { Count: > 0 };
        string? parentMaterialJson = null;
        var instanceScalarOverrides = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var instanceVectorOverrides = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        var instanceTextureOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (matAsset.CookedMaterial is UMaterialInstance mi && !hasOwnStaticPermutation)
        {
            if (matAsset.Parent is { } parentMat)
            {
                var parentRootDir = Directory.GetParent(outDir)?.FullName ?? outDir;
                var parentDir = Path.Combine(parentRootDir, GetUniqueMaterialExportName(parentMat));
                var expectedParentJson = Path.Combine(parentDir, "material.json");
                var parentKey = parentMat.CookedMaterial?.GetPathName() ?? parentMat.Name;
                if (visitedMaterials.Contains(parentKey))
                {
                    parentMaterialJson = File.Exists(expectedParentJson) ? expectedParentJson : null;
                }
                else
                {
                    Directory.CreateDirectory(parentDir);
                    var (pJson, _) = WriteMaterialFiles(parentMat, parentDir, visitedMaterials);
                    parentMaterialJson = pJson;
                }
            }

            var miScalars = mi is UMaterialInstanceConstant mic
                ? mic.ScalarParameterValues
                : mi.GetOrDefault("ScalarParameterValues", Array.Empty<FScalarParameterValue>());
            foreach (var sp in miScalars)
            {
                var pName = sp.ParameterInfo?.Name.Text ?? sp.Name;
                if (!string.IsNullOrWhiteSpace(pName) && pName != "None")
                    instanceScalarOverrides[pName] = sp.ParameterValue;
            }

            var miVectors = mi is UMaterialInstanceConstant micV
                ? micV.VectorParameterValues
                : mi.GetOrDefault("VectorParameterValues", Array.Empty<FVectorParameterValue>());
            foreach (var vp in miVectors)
            {
                var pName = vp.ParameterInfo?.Name.Text ?? vp.Name;
                if (!string.IsNullOrWhiteSpace(pName) && pName != "None" && vp.ParameterValue is { } col)
                    instanceVectorOverrides[pName] = new[] { col.R, col.G, col.B, col.A };
            }
            foreach (var dv in mi.GetOrDefault<CUE4Parse.UE4.Assets.Objects.FStructFallback[]>("DoubleVectorParameterValues", []))
            {
                var info = dv.GetOrDefault<FMaterialParameterInfo?>("ParameterInfo");
                var pName = info?.Name.Text;
                if (string.IsNullOrWhiteSpace(pName) || pName == "None") continue;
                var val = dv.GetOrDefault<CUE4Parse.UE4.Objects.Core.Math.FVector4>("ParameterValue");
                instanceVectorOverrides[pName] = new[] { val.X, val.Y, val.Z, val.W };
            }

            var miTextures = mi is UMaterialInstanceConstant micT
                ? micT.TextureParameterValues
                : mi.GetOrDefault("TextureParameterValues", Array.Empty<FTextureParameterValue>());
            foreach (var tp in miTextures)
            {
                var pName = tp.ParameterInfo?.Name.Text ?? tp.Name;
                if (string.IsNullOrWhiteSpace(pName) || pName == "None") continue;
                if (tp.ParameterValue?.Load<UTexture>() is { } texObj)
                {
                    instanceTextureOverrides[pName] = texObj.Name;
                    if (!exportedTextures.ContainsKey(texObj.Name) && TrySaveTexturePng(texObj, texDir) is { } pngPath)
                        exportedTextures[texObj.Name] = pngPath;
                }
            }

            // Override compound preshader constants with this MI's exact evaluated values
            foreach (var (pName, slot, comp) in compoundScalarSlotKeys)
            {
                if (slot >= 0 && slot < evaluatedCbSlots.Count && comp >= 0 && comp < 4)
                    instanceScalarOverrides[pName] = evaluatedCbSlots[slot][comp];
            }
            foreach (var (pName, slot) in compoundVectorSlotKeys)
            {
                if (slot >= 0 && slot < evaluatedCbSlots.Count)
                {
                    var s = evaluatedCbSlots[slot];
                    instanceVectorOverrides[pName] = new[] { s[0], s[1], s[2], s[3] };
                }
            }
        }

        var exportName = GetUniqueMaterialExportName(matAsset);
        var payload = new
        {
            Name = exportName,
            OriginalName = matAsset.Name,
            PackagePath = matAsset.CookedMaterial?.GetPathName() ?? matAsset.Name,
            IsMaterialInstance = matAsset.CookedMaterial is UMaterialInstance && !hasOwnStaticPermutation,
            IsMaterialInstanceDynamic = matAsset.CookedMaterial is UMaterialInstanceDynamic,
            ParentName = matAsset.Parent?.Name,
            ParentPath = matAsset.Parent?.CookedMaterial?.GetPathName(),
            ParentMaterialJson = parentMaterialJson,
            InstanceScalarOverrides = instanceScalarOverrides,
            InstanceVectorOverrides = instanceVectorOverrides,
            InstanceTextureOverrides = instanceTextureOverrides,
            BlendMode = matAsset.BlendMode.ToString(),
            ShadingModel = matAsset.ShadingModel.ToString(),
            MaterialDomain = matAsset.MaterialDomain.ToString(),
            TwoSided = matAsset.TwoSided,
            T3dFile = t3dPath,
            PixelShaderAsm = pixelShaderAsm,
            VertexShaderAsm = vertexShaderAsm,
            ScalarParameters = scalarParams,
            VectorParameters = vectorParams,
            CbVectorCount = cbVectorCount,
            PsMaterialCbSlot = psMaterialCbSlot,
            PsViewCbSlot = psViewCbSlot,
            PsMpcCbSlot = psMpcCbSlot,
            PsMpcCbSlots = psMpcCbSlots,
            VsMaterialCbSlot = vsMaterialCbSlot,
            VsViewCbSlot = vsViewCbSlot,
            VsMpcCbSlot = vsMpcCbSlot,
            VsMpcCbSlots = vsMpcCbSlots,
            PsVertexFactory = psVertexFactory,
            PsTextureSlots = psTextureSlots,
            VsTextureSlots = vsTextureSlots,
            PsTextureParamNames = psTextureParamNames,
            VsTextureParamNames = vsTextureParamNames,
            EvaluatedCbSlots = evaluatedCbSlots,
            CbSlotParamNames = cbSlotParamNames,
            ParameterCollections = parameterCollections,
            OrderedScalarParams = orderedScalarParams,
            OrderedVectorParams = orderedVectorParams,
            Textures = exportedTextures,
            GraphNodes = graphNodes,
            GraphLinks = graphLinks,
            NodeCount = graph.Nodes.Count + 1,
            CommentGroups = graph.Comments.Select(c => c.Text).ToArray()
        };

        var jsonPath = Path.Combine(outDir, "material.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(payload, _jsonOpts));
        return (jsonPath, t3dPath);
    }

    private static void WriteLodToObj(MeshLodDto<MeshVertex> lod, string objPath)
    {
        var sb = new StringBuilder(lod.Vertices.Length * 48);
        sb.AppendLine("# Exported by UExplorer Unreal Bridge");
        foreach (var v in lod.Vertices)
        {
            // Negate Y to convert UE left-handed coordinates into right-handed OBJ space
            // so UE5's OBJ importer flips Y back without inverting face winding/normals.
            sb.AppendFormat(CultureInfo.InvariantCulture, "v {0:0.######} {1:0.######} {2:0.######}\n", v.Position.X, -v.Position.Y, v.Position.Z);
        }
        foreach (var v in lod.Vertices)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "vt {0:0.######} {1:0.######}\n", v.Uv.U, 1f - v.Uv.V);
        }
        foreach (var v in lod.Vertices)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "vn {0:0.######} {1:0.######} {2:0.######}\n", v.Normal.X, -v.Normal.Y, v.Normal.Z);
        }

        foreach (var sec in lod.Sections)
        {
            sb.AppendLine($"usemtl MaterialSlot_{sec.MaterialIndex}");
            var first = sec.FirstIndex;
            var count = sec.NumFaces * 3;
            for (var i = 0; i + 2 < count && first + i + 2 < lod.Indices.Length; i += 3)
            {
                var i0 = lod.Indices[first + i] + 1;
                var i1 = lod.Indices[first + i + 1] + 1;
                var i2 = lod.Indices[first + i + 2] + 1;
                sb.AppendLine($"f {i0}/{i0}/{i0} {i1}/{i1}/{i1} {i2}/{i2}/{i2}");
            }
        }
        File.WriteAllText(objPath, sb.ToString());
    }

    private static string? TrySaveTexturePng(UTexture tex, string texDir)
    {
        try
        {
            var safe = SanitizeName(tex.Name);
            var outPath = Path.Combine(texDir, $"{safe}.png");
            if (File.Exists(outPath))
                return outPath;
            var decoded = tex.Decode(2048, ExplorerBridge.ToExportOptions());
            if (decoded is null)
                return null;
            using var img = decoded.ToImage();
            img.SaveAsPng(outPath);
            return outPath;
        }
        catch
        {
            return null;
        }
    }

    private static string PrepareExportDir(string kind, string safeName)
    {
        Directory.CreateDirectory(ExportsDir);
        Directory.CreateDirectory(QueueDir);
        var dir = Path.Combine(ExportsDir, $"{kind}_{safeName}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteManifestAndQueue(string kind, string assetName, string packagePath, string outDir, Dictionary<string, string> files)
    {
        var manifestPath = Path.Combine(outDir, "manifest.json");
        var manifest = new
        {
            SchemaVersion = 1,
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            AssetKind = kind,
            AssetName = assetName,
            PackagePath = packagePath,
            OutputDirectory = outDir,
            Files = files
        };
        var json = JsonSerializer.Serialize(manifest, _jsonOpts);
        File.WriteAllText(manifestPath, json);

        var queueFile = Path.Combine(QueueDir, $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{kind}_{SanitizeName(assetName)}.json");
        File.WriteAllText(queueFile, json);

        // Non-blocking ping to the UE5 UExplorerBridge plugin if it is running
        _ = Task.Run(async () =>
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _http.PostAsync($"http://127.0.0.1:{BridgePort}/import", content);
            }
            catch
            {
                // UE5 plugin not running yet; manifest remains queued in QueueDir
            }
        });

        return manifestPath;
    }

    private static string GetUniqueMaterialExportName(MaterialAsset matAsset)
    {
        var baseName = SanitizeName(matAsset.Name);
        if (matAsset.CookedMaterial is UMaterialInstanceDynamic ||
            matAsset.Name.StartsWith("MaterialInstanceDynamic", StringComparison.OrdinalIgnoreCase))
        {
            var path = matAsset.CookedMaterial?.GetPathName() ?? matAsset.Name;
            uint hash = 2166136261u;
            foreach (var ch in path)
            {
                hash ^= ch;
                hash *= 16777619u;
            }
            return $"{baseName}_{hash:X8}";
        }
        return baseName;
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Asset";
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
