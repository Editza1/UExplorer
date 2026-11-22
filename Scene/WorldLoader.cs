using System.Numerics;
using System.Runtime.CompilerServices;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Component.Landscape;
using CUE4Parse.UE4.Assets.Exports.Component.Lights;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.WorldPartition;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using UExplorer.Rendering;

namespace UExplorer.Scene;

/// <summary>
/// Loads UE4/UE5 .umap (UWorld) levels directly from CUE4Parse into UExplorer's WorldScene.
///
/// Transform rules (all properties are read through the archetype chain, cooked instances only store deltas):
/// - every USceneComponent carries RelativeLocation/Rotation/Scale3D + bAbsolute*,
/// - AttachParent may point into another actor (attached actors, child actor components),
/// - AttachSocketName on a static mesh parent applies that mesh socket's transform,
/// - streaming levels apply their LevelTransform,
/// - blueprint actors whose SCS components were not saved in the level get them from the class.
/// </summary>
public static class WorldLoader
{
    private static readonly ConditionalWeakTable<IFileProvider, string[]> CachedUmapKeys = new();

    private sealed class LevelContext
    {
        public required SceneLevel TargetLevel { get; init; }
        public required Action<string>? Log { get; init; }
        public Matrix4x4? LevelTransform { get; init; }
        public readonly Dictionary<UObject, SceneObject> Components = new(ReferenceEqualityComparer.Instance);
        public readonly List<(UObject Component, SceneObject Node, SceneObject Actor)> Pending = [];
        public Dictionary<UObject, List<UObject>> SubobjectsByOuter { get; init; } = new(ReferenceEqualityComparer.Instance);
    }

    public static void LoadWorld(UWorld world, WorldScene scene, IFileProvider provider, Action<string>? onLog = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(provider);

        scene.Clear();
        scene.WorldName = world.Name;
        scene.DiscoveredStreamingLevels.AddRange(DiscoverStreamingLevels(world, provider));
        scene.WorldPartitionCells.AddRange(DiscoverWorldPartitionCells(world, provider));
        var visitedWorlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LoadWorldRecursive(world, scene, provider, onLog, null, visitedWorlds, isRootPersistent: true, packagePathOverride: world.Owner?.Name);

        // Load non-spatially-loaded World Partition cells (always-loaded world actors)
        foreach (var cell in scene.WorldPartitionCells)
        {
            if (cell.IsSpatiallyLoaded || cell.IsHlod)
                continue;
            try
            {
                if (LoadWorldPartitionCellLevel(provider, cell, onLog) is { } lvl)
                {
                    cell.LoadedLevel = lvl;
                    cell.IsLoaded = true;
                    scene.Levels.Add(lvl);
                }
            }
            catch (Exception e)
            {
                onLog?.Invoke($"Skipped non-spatial WP cell '{cell.Name}': {e.Message}");
            }
        }

        scene.RebuildAggregates();
        onLog?.Invoke($"Loaded World '{world.Name}': {scene.Meshes.Count} meshes, {scene.Landscapes.Count} landscapes, {scene.Lights.Count} lights, {scene.Niagaras.Count} VFX, {scene.WorldPartitionCells.Count} WP cells ({scene.Levels.Count} levels)");
    }

    public static IReadOnlyList<StreamingLevelDescriptor> DiscoverStreamingLevels(UWorld persistentWorld, IFileProvider provider)
    {
        ArgumentNullException.ThrowIfNull(persistentWorld);
        ArgumentNullException.ThrowIfNull(provider);

        var result = new List<StreamingLevelDescriptor>();
        var seenObjects = new HashSet<UObject>(ReferenceEqualityComparer.Instance);

        void Collect(IEnumerable<FPackageIndex?>? indices)
        {
            if (indices is null) return;
            foreach (var slIdx in indices)
            {
                if (slIdx is null || slIdx.IsNull || !slIdx.TryLoad(out var slObj) || slObj is null)
                    continue;
                if (!seenObjects.Add(slObj))
                    continue;

                var assetPath = ExtractStreamingWorldAssetPath(slObj);
                if (string.IsNullOrWhiteSpace(assetPath))
                    continue;

                var transform = slObj.TryGetValue(out FTransform lt, "LevelTransform")
                    ? UnrealSpace.Matrix(lt)
                    : Matrix4x4.Identity;

                var isAlwaysLoaded = slObj is ULevelStreamingAlwaysLoaded or ULevelStreamingPersistent;
                var initiallyLoaded = slObj.GetOrDefault("bShouldBeLoaded", slObj.GetOrDefault("bInitiallyLoaded", isAlwaysLoaded));
                var initiallyVisible = slObj.GetOrDefault("bShouldBeVisible", slObj.GetOrDefault("bInitiallyVisible", isAlwaysLoaded));

                var resolvedKey = ResolveWorldPackageKey(provider, assetPath);
                var packagePath = resolvedKey ?? assetPath;
                var rawName = Path.GetFileNameWithoutExtension(packagePath.Split('.')[0]);
                var name = string.IsNullOrWhiteSpace(rawName) ? slObj.Name : rawName;

                result.Add(new StreamingLevelDescriptor(
                    Name: name,
                    PackagePath: packagePath,
                    LevelTransform: transform,
                    IsAlwaysLoaded: isAlwaysLoaded,
                    InitiallyLoaded: initiallyLoaded,
                    InitiallyVisible: initiallyVisible,
                    StreamingClass: slObj.ExportType,
                    IsResolvable: resolvedKey is not null));
            }
        }

        Collect(persistentWorld.StreamingLevels);

        if (persistentWorld.PersistentLevel is { IsNull: false } &&
            persistentWorld.PersistentLevel.TryLoad<ULevel>(out var pLevel) && pLevel is not null &&
            pLevel.WorldSettings is { IsNull: false } &&
            pLevel.WorldSettings.TryLoad(out var wsObj) && wsObj is not null &&
            wsObj.TryGetValue(out FPackageIndex[] wsStreaming, "StreamingLevels"))
        {
            Collect(wsStreaming);
        }

        return result;
    }

    public static IReadOnlyList<WorldPartitionCellDescriptor> DiscoverWorldPartitionCells(UWorld world, IFileProvider provider)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(provider);

        var result = new List<WorldPartitionCellDescriptor>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        UWorldPartition? wp = null;
        if (world.TryGetValue(out FPackageIndex wpWorldIdx, "WorldPartition") && !wpWorldIdx.IsNull)
            wpWorldIdx.TryLoad(out wp);
        if (wp is null &&
            world.PersistentLevel is { IsNull: false } &&
            world.PersistentLevel.TryLoad<ULevel>(out var pLevel) && pLevel is not null &&
            pLevel.WorldSettings is { IsNull: false } &&
            pLevel.WorldSettings.TryLoad(out var wsObj) && wsObj is not null &&
            wsObj.TryGetValue(out FPackageIndex wpWsIdx, "WorldPartition") && !wpWsIdx.IsNull)
        {
            wpWsIdx.TryLoad(out wp);
        }

        if (wp?.RuntimeHash is not { IsNull: false } hashIdx || !hashIdx.TryLoad<UWorldPartitionRuntimeHash>(out var hashObj) || hashObj is null)
            return result;

        void TryAddCell(UWorldPartitionRuntimeCell cellObj, bool forceSpatial = true)
        {
            if (cellObj is not UWorldPartitionRuntimeLevelStreamingCell lsc)
                return;
            if (lsc.LevelStreaming is not { IsNull: false } lsIdx || !lsIdx.TryLoad(out var lsObj) || lsObj is null)
                return;

            var assetPath = ExtractStreamingWorldAssetPath(lsObj);
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            var resolvedKey = ResolveWorldPackageKey(provider, assetPath);
            if (string.IsNullOrWhiteSpace(resolvedKey) || !seenPaths.Add(resolvedKey))
                return;

            var minCm = new Vector3(-50000f, -50000f, -50000f);
            var maxCm = new Vector3(50000f, 50000f, 50000f);
            var hierLevel = 0;
            var gridName = string.Empty;

            if (cellObj.RuntimeCellData is { IsNull: false } dataIdx && dataIdx.TryLoad<UWorldPartitionRuntimeCellData>(out var cellData) && cellData is not null)
            {
                hierLevel = cellData.HierarchicalLevel;
                gridName = cellData.GridName.IsNone ? string.Empty : cellData.GridName.Text;

                if (cellData.CellBounds is { IsValid: > 0 } cb)
                {
                    minCm = new Vector3(cb.Min.X, cb.Min.Y, cb.Min.Z);
                    maxCm = new Vector3(cb.Max.X, cb.Max.Y, cb.Max.Z);
                }
                else if (cellData.ContentBounds.IsValid > 0)
                {
                    minCm = new Vector3(cellData.ContentBounds.Min.X, cellData.ContentBounds.Min.Y, cellData.ContentBounds.Min.Z);
                    maxCm = new Vector3(cellData.ContentBounds.Max.X, cellData.ContentBounds.Max.Y, cellData.ContentBounds.Max.Z);
                }
                else if (cellData is UWorldPartitionRuntimeCellDataSpatialHash sh && sh.Extent > 0f)
                {
                    var center = new Vector3(sh.Position.X, sh.Position.Y, sh.Position.Z);
                    var ext = new Vector3(sh.Extent, sh.Extent, 100000f);
                    minCm = center - ext;
                    maxCm = center + ext;
                }
            }

            var isHlod = hierLevel > 0 ||
                         gridName.Contains("HLOD", StringComparison.OrdinalIgnoreCase) ||
                         resolvedKey.Contains("HLOD", StringComparison.OrdinalIgnoreCase);

            var rawName = Path.GetFileNameWithoutExtension(resolvedKey.Split('.')[0]);
            result.Add(new WorldPartitionCellDescriptor
            {
                Name = string.IsNullOrWhiteSpace(rawName) ? cellObj.Name : rawName,
                PackagePath = resolvedKey,
                BoundsMinCm = minCm,
                BoundsMaxCm = maxCm,
                HierarchicalLevel = hierLevel,
                GridName = gridName,
                IsSpatiallyLoaded = forceSpatial && cellObj.bIsSpatiallyLoaded,
                IsHlod = isHlod
            });
        }

        if (hashObj is UWorldPartitionRuntimeSpatialHash spatialHash)
        {
            foreach (var grid in spatialHash.StreamingGrids ?? [])
            {
                foreach (var gLevel in grid.GridLevels ?? [])
                {
                    foreach (var lCell in gLevel.LayerCells ?? [])
                    {
                        foreach (var cIdx in lCell.GridCells ?? [])
                        {
                            if (!cIdx.IsNull && cIdx.TryLoad<UWorldPartitionRuntimeCell>(out var cellObj) && cellObj is not null)
                                TryAddCell(cellObj, forceSpatial: true);
                        }
                    }
                }
            }
        }
        else if (hashObj is UWorldPartitionRuntimeHashSet hashSet)
        {
            foreach (var sd in hashSet.RuntimeStreamingData ?? [])
            {
                foreach (var cIdx in sd.NonSpatiallyLoadedCells ?? [])
                {
                    if (!cIdx.IsNull && cIdx.TryLoad<UWorldPartitionRuntimeCell>(out var cellObj) && cellObj is not null)
                        TryAddCell(cellObj, forceSpatial: false);
                }
                foreach (var cIdx in sd.SpatiallyLoadedCells ?? [])
                {
                    if (!cIdx.IsNull && cIdx.TryLoad<UWorldPartitionRuntimeCell>(out var cellObj) && cellObj is not null)
                        TryAddCell(cellObj, forceSpatial: true);
                }
            }
        }

        return result;
    }

    public static SceneLevel? LoadWorldPartitionCellLevel(IFileProvider provider, WorldPartitionCellDescriptor cell, Action<string>? onLog = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(cell);

        var subWorld = ResolveStreamingWorld(provider, cell.PackagePath, out var resolvedKey);
        if (subWorld?.PersistentLevel is not { IsNull: false } ||
            !subWorld.PersistentLevel.TryLoad<ULevel>(out var persistent) || persistent is null)
            return null;

        var level = new SceneLevel
        {
            Name = cell.Name,
            PackagePath = resolvedKey ?? cell.PackagePath,
            IsPersistent = false,
            IsLoaded = true,
            IsVisible = true,
            LevelTransform = Matrix4x4.Identity
        };

        LoadLevel(persistent, new LevelContext
        {
            TargetLevel = level,
            Log = onLog,
            LevelTransform = null
        });

        if (cell.IsHlod)
        {
            foreach (var m in level.Meshes)
                m.IsHlod = true;
        }

        return level;
    }

    public static SceneLevel LoadLevelAdditive(
        WorldScene scene,
        IFileProvider provider,
        string packagePath,
        Matrix4x4? transform = null,
        bool isPersistent = false,
        string? nameOverride = null)
        => LoadLevelAdditiveCore(scene, provider, packagePath, transform, isPersistent, nameOverride, onLog: null);

    internal static SceneLevel LoadLevelAdditiveCore(
        WorldScene scene,
        IFileProvider provider,
        string packagePath,
        Matrix4x4? transform,
        bool isPersistent,
        string? nameOverride,
        Action<string>? onLog)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(packagePath))
            throw new ArgumentException("Package path cannot be empty.", nameof(packagePath));

        var subWorld = ResolveStreamingWorld(provider, packagePath, out var resolvedKey);
        if (subWorld is null)
            throw new InvalidOperationException($"Could not resolve UWorld package '{packagePath}'.");

        if (subWorld.PersistentLevel is not { IsNull: false } ||
            !subWorld.PersistentLevel.TryLoad<ULevel>(out var persistent) || persistent is null)
            throw new InvalidOperationException($"World '{subWorld.Name}' has no valid PersistentLevel.");

        var level = new SceneLevel
        {
            Name = string.IsNullOrWhiteSpace(nameOverride) ? subWorld.Name : nameOverride,
            PackagePath = resolvedKey ?? subWorld.Owner?.Name ?? packagePath,
            IsPersistent = isPersistent,
            IsLoaded = true,
            IsVisible = true,
            LevelTransform = transform ?? Matrix4x4.Identity
        };

        var effectiveTransform = transform is { } t && t != Matrix4x4.Identity ? t : (Matrix4x4?) null;
        LoadLevel(persistent, new LevelContext
        {
            TargetLevel = level,
            Log = onLog,
            LevelTransform = effectiveTransform
        });

        if (isPersistent && string.Equals(scene.WorldName, "Untitled World", StringComparison.Ordinal))
            scene.WorldName = subWorld.Name;

        scene.Levels.Add(level);
        scene.RebuildAggregates();
        return level;
    }

    public static void UnloadLevel(WorldScene scene, SceneLevel level)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(level);

        level.Clear();
        scene.Levels.Remove(level);
        scene.RebuildAggregates();
    }

    public static void SetLevelVisible(WorldScene scene, SceneLevel level, bool visible)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(level);

        if (level.IsVisible == visible)
            return;

        level.IsVisible = visible;
        scene.RebuildAggregates();
    }

    private static string? ExtractStreamingWorldAssetPath(UObject slObj)
    {
        if (slObj.TryGetValue(out FSoftObjectPath worldAsset, "WorldAsset") && !worldAsset.AssetPathName.IsNone)
            return worldAsset.AssetPathName.Text;
        if (slObj.TryGetValue(out FName pkgName, "PackageName") && !pkgName.IsNone)
            return pkgName.Text;
        if (slObj.TryGetValue(out FName pkgToLoad, "PackageNameToLoad") && !pkgToLoad.IsNone)
            return pkgToLoad.Text;
        return null;
    }

    private static void LoadWorldRecursive(
        UWorld world,
        WorldScene scene,
        IFileProvider provider,
        Action<string>? onLog,
        Matrix4x4? parentTransform,
        HashSet<string> visitedWorlds,
        bool isRootPersistent,
        string? packagePathOverride = null)
    {
        var worldKey = world.GetPathName();
        if (!visitedWorlds.Add(worldKey))
            return;

        var sceneLevel = new SceneLevel
        {
            Name = world.Name,
            PackagePath = packagePathOverride ?? world.Owner?.Name ?? worldKey,
            IsPersistent = isRootPersistent,
            IsLoaded = true,
            IsVisible = true,
            LevelTransform = parentTransform ?? Matrix4x4.Identity
        };

        if (world.PersistentLevel is { IsNull: false } && world.PersistentLevel.TryLoad<ULevel>(out var persistent) && persistent is not null)
        {
            var effectiveTransform = parentTransform is { } pt && pt != Matrix4x4.Identity ? pt : (Matrix4x4?) null;
            LoadLevel(persistent, new LevelContext { TargetLevel = sceneLevel, Log = onLog, LevelTransform = effectiveTransform });
        }
        scene.Levels.Add(sceneLevel);

        var streamingIndices = new List<FPackageIndex>();
        if (world.StreamingLevels is { Length: > 0 } directStreaming)
            streamingIndices.AddRange(directStreaming);
        else if (world.PersistentLevel is { IsNull: false } &&
                 world.PersistentLevel.TryLoad<ULevel>(out var pLvl) && pLvl is not null &&
                 pLvl.WorldSettings is { IsNull: false } &&
                 pLvl.WorldSettings.TryLoad(out var wsObj) && wsObj is not null &&
                 wsObj.TryGetValue(out FPackageIndex[] wsStreaming, "StreamingLevels"))
            streamingIndices.AddRange(wsStreaming);

        foreach (var slIdx in streamingIndices)
        {
            if (slIdx is null || slIdx.IsNull || !slIdx.TryLoad(out var slObj) || slObj is null)
                continue;
            var assetPath = ExtractStreamingWorldAssetPath(slObj);
            if (string.IsNullOrWhiteSpace(assetPath))
                continue;

            Matrix4x4? levelTransform = slObj.TryGetValue(out FTransform lt, "LevelTransform") ? UnrealSpace.Matrix(lt) : null;
            if (parentTransform is { } pt)
                levelTransform = levelTransform is { } cur ? cur * pt : pt;

            if (ResolveStreamingWorld(provider, assetPath, out var subKey) is { } subWorld)
                LoadWorldRecursive(subWorld, scene, provider, onLog, levelTransform, visitedWorlds, isRootPersistent: false, packagePathOverride: subKey);
        }
    }

    public static string? ResolveWorldPackageKey(IFileProvider provider, string assetPath)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(assetPath))
            return null;

        var norm = assetPath.Replace('\\', '/').Trim();
        if (norm.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
        {
            if (provider.Files.ContainsKey(norm))
                return norm;
            var trimmed = norm.TrimStart('/');
            if (provider.Files.ContainsKey(trimmed))
                return trimmed;
        }

        var dot = norm.LastIndexOf('.');
        var lastSlash = norm.LastIndexOf('/');
        if (dot > lastSlash)
            norm = norm[..dot];

        var pkgPart = norm.TrimStart('/');
        if (provider.Files.ContainsKey(pkgPart + ".umap"))
            return pkgPart + ".umap";

        var firstSlash = pkgPart.IndexOf('/');
        var rootSeg = firstSlash > 0 ? pkgPart[..firstSlash] : "";
        var relSubPath = firstSlash > 0 ? pkgPart[(firstSlash + 1)..] : pkgPart;

        string expectedSuffix;
        if (rootSeg.Equals("Game", StringComparison.OrdinalIgnoreCase))
            expectedSuffix = $"/Content/{relSubPath}.umap";
        else if (rootSeg.Equals("Engine", StringComparison.OrdinalIgnoreCase))
            expectedSuffix = $"Engine/Content/{relSubPath}.umap";
        else if (!string.IsNullOrEmpty(rootSeg))
            expectedSuffix = $"/{rootSeg}/Content/{relSubPath}.umap";
        else
            expectedSuffix = $"/{relSubPath}.umap";

        var expectedNoLeading = expectedSuffix.TrimStart('/');
        var fileOnlySuffix = $"/{Path.GetFileName(pkgPart)}.umap";
        var umapKeys = CachedUmapKeys.GetValue(provider, static p =>
            p.Files.Keys.Where(k => k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)).ToArray());

        string? fallbackMatch = null;
        foreach (var k in umapKeys)
        {
            if (k.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase) ||
                k.Equals(expectedNoLeading, StringComparison.OrdinalIgnoreCase))
                return k;
            if (fallbackMatch is null && k.EndsWith(fileOnlySuffix, StringComparison.OrdinalIgnoreCase))
                fallbackMatch = k;
        }

        return fallbackMatch;
    }

    private static UWorld? ResolveStreamingWorld(IFileProvider provider, string assetPath, out string? resolvedKey)
    {
        resolvedKey = ResolveWorldPackageKey(provider, assetPath);
        if (resolvedKey is not null && provider.TryLoadPackage(resolvedKey, out var subPkg))
            return subPkg.GetExports().OfType<UWorld>().FirstOrDefault();

        if (provider.TryLoadPackageObject<UWorld>(assetPath, out var direct) && direct is not null)
        {
            resolvedKey ??= direct.Owner?.Name ?? assetPath;
            return direct;
        }

        return null;
    }

    private static bool WouldCreateCycle(SceneObject candidateParent, SceneObject child)
    {
        var cur = candidateParent;
        for (var d = 0; cur is not null && d < 64; d++, cur = cur.Parent)
        {
            if (ReferenceEquals(cur, child))
                return true;
        }
        return false;
    }

    private static void LoadLevel(ULevel level, LevelContext ctx)
    {
        var subobjects = new Dictionary<UObject, List<UObject>>(ReferenceEqualityComparer.Instance);
        if (level.Owner is { } pkg)
        {
            foreach (var exp in pkg.GetExports())
            {
                if (exp?.Outer is { } outerIdx && outerIdx.TryLoad(out var outerObj) && outerObj is not null)
                {
                    if (!subobjects.TryGetValue(outerObj, out var list))
                    {
                        list = [];
                        subobjects[outerObj] = list;
                    }
                    list.Add(exp);
                }
            }
        }
        ctx = new LevelContext
        {
            TargetLevel = ctx.TargetLevel,
            Log = ctx.Log,
            LevelTransform = ctx.LevelTransform,
            SubobjectsByOuter = subobjects
        };

        var levelRoot = ctx.LevelTransform is { } transform && transform != Matrix4x4.Identity
            ? new SceneObject { Name = ctx.TargetLevel.Name, ClassName = "LevelStreaming", Path = ctx.TargetLevel.PackagePath, OverrideLocalMatrix = transform }
            : null;
        ctx.TargetLevel.LevelRoot = levelRoot;

        var actors = new List<SceneObject>();
        foreach (var actorIdx in level.Actors)
        {
            if (actorIdx is null || actorIdx.IsNull || !actorIdx.TryLoad(out var actorObj) || actorObj is null)
                continue;
            try
            {
                ExtractPostProcessFromActor(actorObj, ctx.TargetLevel);
                if (LoadActor(actorObj, ctx) is { } actorNode)
                    actors.Add(actorNode);
            }
            catch (Exception e)
            {
                ctx.Log?.Invoke($"Skipped actor '{actorObj.Name}': {e.Message}");
            }
        }

        // wire attachments once every actor of the level exists, AttachParent can point into another actor
        foreach (var (component, node, actor) in ctx.Pending)
        {
            var parent = actor;
            if (UnrealSpace.TryGetInherited(component, "AttachParent", out FPackageIndex attach) &&
                UnrealSpace.Load(attach) is { } attachObj &&
                ctx.Components.TryGetValue(attachObj, out var found) &&
                !ReferenceEquals(found, node) &&
                !WouldCreateCycle(found, node))
            {
                parent = found;
                if (UnrealSpace.TryGetInherited(component, "AttachSocketName", out FName socket) && !socket.IsNone)
                {
                    node.AttachSocketName = socket.Text;
                    if (found is SceneMesh parentMesh && FindSocket(parentMesh.Mesh, socket.Text) is { } socketMatrix)
                        node.AttachSocketMatrix = socketMatrix;
                }
            }
            if (!WouldCreateCycle(parent, node))
            {
                node.Parent = parent;
                parent.Children.Add(node);
            }
        }

        foreach (var actor in actors)
        {
            // actors whose root got attached to another actor's component are reached through that component
            if (actor.Children.Count == 0) continue;
            if (levelRoot is not null)
            {
                actor.Parent = levelRoot;
                levelRoot.Children.Add(actor);
            }
            else
            {
                ctx.TargetLevel.RootObjects.Add(actor);
            }
        }
        if (levelRoot is not null && levelRoot.Children.Count > 0)
            ctx.TargetLevel.RootObjects.Add(levelRoot);
        ctx.Pending.Clear();
    }

    private static SceneObject? LoadActor(UObject actorObj, LevelContext ctx)
    {
        var actorNode = new SceneObject
        {
            Name = actorObj.Name,
            ClassName = actorObj.ExportType,
            Path = actorObj.GetPathName(),
            SourceObject = actorObj
        };
        var isHlod = actorObj.ExportType.Contains("LODActor", StringComparison.OrdinalIgnoreCase) ||
                     actorObj.ExportType.Contains("HLOD", StringComparison.OrdinalIgnoreCase);

        var compIndices = new List<FPackageIndex>();
        void Collect(string property)
        {
            if (actorObj.TryGetValue(out FPackageIndex single, property) && !single.IsNull) compIndices.Add(single);
            else if (actorObj.TryGetValue(out FPackageIndex[] many, property)) compIndices.AddRange(many.Where(x => x is { IsNull: false }));
        }
        Collect("RootComponent");
        Collect("OwnedComponents");
        Collect("InstanceComponents");
        Collect("BlueprintCreatedComponents");
        Collect("NiagaraComponent");
        Collect("LightComponent");
        Collect("StaticMeshComponent");

        var seen = new HashSet<UObject>(ReferenceEqualityComparer.Instance);
        var compObjects = new List<UObject>();
        foreach (var cIdx in compIndices)
        {
            if (UnrealSpace.Load(cIdx) is { } compObj && seen.Add(compObj))
                compObjects.Add(compObj);
        }
        // Also pick up cooked subobject components whose Outer is actorObj when OwnedComponents was stripped
        if (ctx.SubobjectsByOuter.TryGetValue(actorObj, out var outerSubs))
        {
            foreach (var sub in outerSubs)
            {
                if (seen.Add(sub))
                    compObjects.Add(sub);
            }
        }

        var added = 0;
        foreach (var compObj in compObjects)
        {
            ExtractPostProcessFromComponent(compObj, ctx.TargetLevel);
            if (CreateComponentNodeForLevel(compObj, ctx.TargetLevel, isHlod) is not { } node) continue;
            ctx.Components[compObj] = node;
            ctx.Pending.Add((compObj, node, actorNode));
            added++;
        }

        // blueprint actors: SCS components that were not serialized with the level instance come from the class
        if (actorObj.Class?.Load() is UBlueprintGeneratedClass bp)
        {
            var existingNames = new HashSet<string>(seen.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            // class components hang below the instance's root component (which carries the actor transform)
            SceneObject attachTo = actorNode;
            if (actorObj.TryGetValue(out FPackageIndex rootIndex, "RootComponent") && UnrealSpace.Load(rootIndex) is { } rootObj &&
                ctx.Components.TryGetValue(rootObj, out var rootNode))
                attachTo = rootNode;
            foreach (var bpNode in BlueprintComponents.BuildForLevel(bp, ctx.TargetLevel, isHlod, existingNames))
            {
                if (bpNode.Parent is null)
                {
                    bpNode.Parent = attachTo;
                    attachTo.Children.Add(bpNode);
                }
                added++;
            }
        }

        return added > 0 ? actorNode : null;
    }

    /// <summary>Scene node for a component (mesh, light, fog, niagara, plain scene component), not yet parented.</summary>
    internal static SceneObject? CreateComponentNode(UObject compObj, WorldScene? scene, bool isHlod)
        => CreateComponentNodeForLevel(compObj, scene?.GetOrCreatePersistentLevel(), isHlod);

    internal static SceneObject? CreateComponentNodeForLevel(UObject compObj, SceneLevel? level, bool isHlod)
    {
        SceneObject? node = null;

        if (compObj is UStaticMeshComponent smc || compObj.ExportType.EndsWith("StaticMeshComponent", StringComparison.Ordinal))
        {
            if (UnrealSpace.TryGetInherited(compObj, "StaticMesh", out FPackageIndex smIdx) && UnrealSpace.Load(smIdx) is UStaticMesh sm)
            {
                var meshNode = new SceneMesh
                {
                    Name = compObj.Name,
                    ClassName = compObj.ExportType,
                    Path = compObj.GetPathName(),
                    SourceObject = compObj,
                    Mesh = sm,
                    Materials = ResolveMaterialSlots(compObj, sm),
                    IsHlod = isHlod
                };
                if (compObj is UInstancedStaticMeshComponent { PerInstanceSMData: { Length: > 0 } instData })
                {
                    meshNode.InstanceLocalMatrices = new List<Matrix4x4>(instData.Length);
                    foreach (var inst in instData)
                        meshNode.InstanceLocalMatrices.Add(UnrealSpace.Matrix(inst.TransformData));
                }
                level?.Meshes.Add(meshNode);
                node = meshNode;
            }
            else
            {
                node = new SceneObject { Name = compObj.Name, ClassName = compObj.ExportType, Path = compObj.GetPathName(), SourceObject = compObj };
            }
        }
        else if (compObj is ULandscapeComponent landscapeComp)
        {
            var landNode = new SceneLandscape
            {
                Name = landscapeComp.Name,
                ClassName = landscapeComp.ExportType,
                Path = landscapeComp.GetPathName(),
                SourceObject = landscapeComp,
                SourceComponent = landscapeComp
            };
            if (landscapeComp.WeightmapLayerAllocations is { Length: > 0 } allocs)
            {
                foreach (var alloc in allocs)
                {
                    var layerName = alloc.GetLayerName();
                    var debugColor = new Vector4(0.35f, 0.55f, 0.25f, 1f);
                    if (alloc.LayerInfo is { IsNull: false } &&
                        alloc.LayerInfo.TryLoad<ULandscapeLayerInfoObject>(out var layerInfo) &&
                        layerInfo is not null)
                    {
                        if (!layerInfo.LayerName.IsNone && !string.IsNullOrWhiteSpace(layerInfo.LayerName.Text))
                            layerName = layerInfo.LayerName.Text;
                        var dc = layerInfo.LayerUsageDebugColor;
                        debugColor = new Vector4(dc.R, dc.G, dc.B, dc.A);
                    }
                    if (string.IsNullOrEmpty(layerName) || layerName == "None")
                        layerName = LandscapeSystem.VisibilityLayer;
                    landNode.Layers[layerName] = new LandscapeLayerMapping(alloc.WeightmapTextureIndex, alloc.WeightmapTextureChannel, debugColor);
                }
            }
            level?.Landscapes.Add(landNode);
            node = landNode;
        }
        else if (compObj is ULightComponentBase lightComp)
        {
            if (CreateLightNode(lightComp) is { } lightNode)
            {
                level?.Lights.Add(lightNode);
                node = lightNode;
            }
        }
        else if (compObj.ExportType.Contains("ExponentialHeightFog", StringComparison.OrdinalIgnoreCase))
        {
            var fog = new SceneHeightFog
            {
                Name = compObj.Name,
                ClassName = compObj.ExportType,
                Path = compObj.GetPathName(),
                SourceObject = compObj,
                FogDensity = UnrealSpace.GetInherited(compObj, "FogDensity", 0.02f),
                FogHeightFalloff = UnrealSpace.GetInherited(compObj, "FogHeightFalloff", 0.2f),
                FogMaxOpacity = UnrealSpace.GetInherited(compObj, "FogMaxOpacity", 1f),
                StartDistance = UnrealSpace.GetInherited(compObj, "StartDistance", 0f)
            };
            if (UnrealSpace.TryGetInherited(compObj, "FogInscatteringColor", out FLinearColor fogCol))
                fog.FogInscatteringColor = new Vector3(fogCol.R, fogCol.G, fogCol.B);
            if (level is not null) level.HeightFog ??= fog;
            node = fog;
        }
        else if (compObj.ExportType.Contains("Niagara", StringComparison.OrdinalIgnoreCase))
        {
            var niag = new NiagaraAsset(compObj);
            if (niag.MeshEmitters.Count > 0 || niag.SpriteEmitters.Count > 0 || niag.LightEmitters.Count > 0 || niag.DiscoveredEmitters.Count > 0)
            {
                var niagNode = new SceneNiagara { Name = compObj.Name, ClassName = compObj.ExportType, Path = compObj.GetPathName(), SourceObject = compObj, Asset = niag };
                level?.Niagaras.Add(niagNode);
                node = niagNode;
            }
        }
        else if (compObj is USceneComponent || compObj.ExportType.EndsWith("Component", StringComparison.Ordinal) && UnrealSpace.TryGetInherited(compObj, "RelativeLocation", out FVector _))
        {
            node = new SceneObject { Name = compObj.Name, ClassName = compObj.ExportType, Path = compObj.GetPathName(), SourceObject = compObj };
        }

        if (node is not null)
        {
            ApplyTransform(node, compObj);
            if (node is SceneLandscape sl && sl.LocationCm == Vector3.Zero && compObj is ULandscapeComponent lc && (lc.SectionBaseX != 0 || lc.SectionBaseY != 0))
                sl.LocationCm = new Vector3(lc.SectionBaseX, lc.SectionBaseY, 0f);
        }
        return node;
    }

    private static Matrix4x4? FindSocket(UStaticMesh mesh, string name)
    {
        foreach (var socketIdx in mesh.Sockets ?? [])
        {
            if (UnrealSpace.Load(socketIdx) is not { } socket) continue;
            var socketName = socket.GetOrDefault<FName>("SocketName");
            if (!socketName.Text.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var loc = socket.GetOrDefault("RelativeLocation", FVector.ZeroVector);
            var rot = socket.GetOrDefault("RelativeRotation", FRotator.ZeroRotator);
            var scl = socket.GetOrDefault("RelativeScale", FVector.OneVector);
            return UnrealSpace.Matrix(loc, rot.Quaternion(), scl);
        }
        return null;
    }

    internal static MaterialAsset?[] ResolveMaterialSlots(UObject component, UStaticMesh sm)
    {
        var baseMats = sm.Materials ?? [];
        var overMats = UnrealSpace.GetInherited(component, "OverrideMaterials", Array.Empty<FPackageIndex>());
        var count = Math.Max(baseMats.Length, overMats.Length);
        if (count == 0) return [];

        var result = new MaterialAsset?[count];
        for (var i = 0; i < count; i++)
        {
            UUnrealMaterial? mat = null;
            if (i < overMats.Length && overMats[i] is { IsNull: false } oIdx && oIdx.TryLoad<UUnrealMaterial>(out var oMat)) mat = oMat;
            if (mat is null && i < baseMats.Length && baseMats[i] is { IsNull: false } bIdx && bIdx.TryLoad<UUnrealMaterial>(out var bMat)) mat = bMat;
            if (mat is not null) result[i] = new MaterialAsset(mat);
        }
        return result;
    }

    private static void ExtractPostProcessFromActor(UObject actorObj, SceneLevel level)
    {
        if (!actorObj.ExportType.Contains("PostProcess", StringComparison.OrdinalIgnoreCase))
            return;
        if (!actorObj.GetOrDefault("bEnabled", true))
            return;
        var blendWeight = actorObj.GetOrDefault("BlendWeight", 1f);
        if (blendWeight <= 0.01f)
            return;
        var priority = actorObj.GetOrDefault("Priority", 0f) + (actorObj.GetOrDefault("bUnbound", false) ? 0f : -100f);
        if (actorObj.TryGetValue(out FStructFallback settings, "Settings") && settings is not null)
            ApplyPostProcessStruct(settings, priority, level);
    }

    private static void ExtractPostProcessFromComponent(UObject compObj, SceneLevel level)
    {
        if (!compObj.ExportType.Contains("PostProcess", StringComparison.OrdinalIgnoreCase))
            return;
        if (!UnrealSpace.GetInherited(compObj, "bEnabled", true))
            return;
        var blendWeight = UnrealSpace.GetInherited(compObj, "BlendWeight", 1f);
        if (blendWeight <= 0.01f)
            return;
        var priority = UnrealSpace.GetInherited(compObj, "Priority", 0f) + (UnrealSpace.GetInherited(compObj, "bUnbound", true) ? 0f : -100f);
        if (UnrealSpace.TryGetInherited(compObj, "Settings", out FStructFallback settings) && settings is not null)
            ApplyPostProcessStruct(settings, priority, level);
    }

    private static void ApplyPostProcessStruct(FStructFallback settings, float priority, SceneLevel level)
    {
        level.PostProcess ??= new ScenePostProcess();
        var pp = level.PostProcess;
        if (priority < pp.Priority && pp.AutoExposureBias is not null)
            return;
        pp.Priority = Math.Max(pp.Priority, priority);

        if (settings.GetOrDefault("bOverride_AutoExposureBias", false) && settings.TryGetValue(out float expBias, "AutoExposureBias"))
            pp.AutoExposureBias = Math.Clamp(expBias, -8f, 8f);
        if (settings.GetOrDefault("bOverride_AutoExposureMinBrightness", false) && settings.TryGetValue(out float minB, "AutoExposureMinBrightness"))
            pp.AutoExposureMinEV100 = minB > 0f && !settings.GetOrDefault("bOverride_AutoExposureMethod", false) ? Math.Clamp(MathF.Log2(MathF.Max(minB, 1e-4f)), -10f, 20f) : Math.Clamp(minB, -10f, 20f);
        if (settings.GetOrDefault("bOverride_AutoExposureMaxBrightness", false) && settings.TryGetValue(out float maxB, "AutoExposureMaxBrightness"))
            pp.AutoExposureMaxEV100 = maxB > 0f && !settings.GetOrDefault("bOverride_AutoExposureMethod", false) ? Math.Clamp(MathF.Log2(MathF.Max(maxB, 1e-4f)), -10f, 20f) : Math.Clamp(maxB, -10f, 20f);
        if (settings.GetOrDefault("bOverride_BloomIntensity", false) && settings.TryGetValue(out float bloom, "BloomIntensity"))
            pp.BloomIntensity = Math.Clamp(bloom, 0f, 4f);
    }

    private static SceneLight? CreateLightNode(ULightComponentBase lightComp)
    {
        var color = UnrealSpace.GetInherited(lightComp, "LightColor", lightComp.LightColor);
        var linCol = new FLinearColor(color);
        var col = new Vector3(linCol.R, linCol.G, linCol.B);
        var intensity = UnrealSpace.GetInherited(lightComp, "Intensity", lightComp.Intensity);
        var fallbackUnits = lightComp is ULocalLightComponent llc ? llc.IntensityUnits : ELightUnits.Unitless;
        var units = UnrealSpace.GetInherited(lightComp, "IntensityUnits", fallbackUnits);
        var useInvSq = UnrealSpace.GetInherited(lightComp, "bUseInverseSquaredFalloff",
            UnrealSpace.GetInherited(lightComp, "InverseSquaredFalloff", lightComp is not UPointLightComponent plc || plc.bUseInverseSquaredFalloff));
        if (!useInvSq)
            units = ELightUnits.Unitless;
        var falloffExp = UnrealSpace.GetInherited(lightComp, "LightFalloffExponent", lightComp is UPointLightComponent p2 ? p2.LightFalloffExponent : 8.0f);
        var softSrcRadius = UnrealSpace.GetInherited(lightComp, "SoftSourceRadius", lightComp is UPointLightComponent p3 ? p3.SoftSourceRadius : 0.0f);
        var srcLength = UnrealSpace.GetInherited(lightComp, "SourceLength", lightComp is UPointLightComponent p4 ? p4.SourceLength : 0.0f);
        var maxDrawDist = UnrealSpace.GetInherited(lightComp, "MaxDrawDistance", lightComp is ULightComponent lc1 ? lc1.MaxDrawDistance : 0.0f);
        var maxFadeRange = UnrealSpace.GetInherited(lightComp, "MaxDistanceFadeRange", lightComp is ULightComponent lc2 ? lc2.MaxDistanceFadeRange : 0.0f);

        SceneLight? node = lightComp switch
        {
            USpotLightComponent sp => new SceneSpotLight
            {
                AttenuationRadius = UnrealSpace.GetInherited(sp, "AttenuationRadius", sp.AttenuationRadius),
                InnerConeAngle = UnrealSpace.GetInherited(sp, "InnerConeAngle", sp.InnerConeAngle),
                OuterConeAngle = UnrealSpace.GetInherited(sp, "OuterConeAngle", sp.OuterConeAngle),
                SourceRadius = UnrealSpace.GetInherited(sp, "SourceRadius", sp.SourceRadius),
                SoftSourceRadius = softSrcRadius,
                SourceLength = srcLength,
                UseInverseSquaredFalloff = useInvSq,
                LightFalloffExponent = falloffExp,
                IntensityUnits = units,
                MaxDrawDistance = maxDrawDist,
                MaxDistanceFadeRange = maxFadeRange
            },
            UPointLightComponent pt => new ScenePointLight
            {
                AttenuationRadius = UnrealSpace.GetInherited(pt, "AttenuationRadius", pt.AttenuationRadius),
                SourceRadius = UnrealSpace.GetInherited(pt, "SourceRadius", pt.SourceRadius),
                SoftSourceRadius = softSrcRadius,
                SourceLength = srcLength,
                UseInverseSquaredFalloff = useInvSq,
                LightFalloffExponent = falloffExp,
                IntensityUnits = units,
                MaxDrawDistance = maxDrawDist,
                MaxDistanceFadeRange = maxFadeRange
            },
            URectLightComponent rc => new SceneRectLight
            {
                AttenuationRadius = UnrealSpace.GetInherited(rc, "AttenuationRadius", rc.AttenuationRadius),
                SourceWidth = UnrealSpace.GetInherited(rc, "SourceWidth", rc.SourceWidth),
                SourceHeight = UnrealSpace.GetInherited(rc, "SourceHeight", rc.SourceHeight),
                BarnDoorAngle = UnrealSpace.GetInherited(rc, "BarnDoorAngle", rc.BarnDoorAngle),
                BarnDoorLength = UnrealSpace.GetInherited(rc, "BarnDoorLength", rc.BarnDoorLength),
                UseInverseSquaredFalloff = useInvSq,
                LightFalloffExponent = falloffExp,
                IntensityUnits = units,
                MaxDrawDistance = maxDrawDist,
                MaxDistanceFadeRange = maxFadeRange
            },
            UDirectionalLightComponent => new SceneDirectionalLight(),
            _ when lightComp.ExportType.Contains("SkyLight", StringComparison.OrdinalIgnoreCase) => new SceneSkyLight(),
            _ => null
        };

        if (node is null) return null;
        node.Name = lightComp.Name;
        node.ClassName = lightComp.ExportType;
        node.Path = lightComp.GetPathName();
        node.SourceObject = lightComp;
        node.Color = col;
        node.Intensity = intensity;
        node.CastShadows = UnrealSpace.GetInherited(lightComp, "CastShadows", lightComp.CastShadows);
        return node;
    }

    /// <summary>Relative transform with each part resolved independently through the archetype chain.</summary>
    internal static void ApplyTransform(SceneObject obj, UObject component)
    {
        var loc = UnrealSpace.GetInherited(component, "RelativeLocation", FVector.ZeroVector);
        var rot = UnrealSpace.GetInherited(component, "RelativeRotation", FRotator.ZeroRotator);
        var scl = UnrealSpace.GetInherited(component, "RelativeScale3D", FVector.OneVector);
        obj.LocationCm = new Vector3(loc.X, loc.Y, loc.Z);
        obj.SetRotationFromUnreal(rot);
        obj.Scale = new Vector3(scl.X, scl.Y, scl.Z);
        obj.AbsLocation = UnrealSpace.GetInherited(component, "bAbsoluteLocation", false);
        obj.AbsRotation = UnrealSpace.GetInherited(component, "bAbsoluteRotation", false);
        obj.AbsScale = UnrealSpace.GetInherited(component, "bAbsoluteScale", false);
    }
}
