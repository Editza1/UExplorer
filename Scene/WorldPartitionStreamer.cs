using System.Collections.Concurrent;
using System.Numerics;
using CUE4Parse.FileProvider;

namespace UExplorer.Scene;

/// <summary>
/// Asynchronous camera-proximity World Partition streamer.
/// Streams in actual detail cells (meshes, blueprints, landscapes) near the camera on background threads,
/// unloads distant detail cells beyond hysteresis distance, and lets HLODs cover the unloaded far area
/// without freezing the UI thread.
/// </summary>
public sealed class WorldPartitionStreamer
{
    private const int MaxConcurrentLoads = 3;
    private const int MaxIntegratePerFrame = 2;
    private const float ReevaluateMoveThresholdCm = 1500f;

    private readonly ConcurrentQueue<(WorldPartitionCellDescriptor Cell, SceneLevel Level)> _completed = new();
    private readonly List<WorldPartitionCellDescriptor> _candidates = new(64);
    private readonly List<(Vector3 MinCm, Vector3 MaxCm)> _loadedActualBounds = new(32);

    private WorldScene? _boundScene;
    private Vector3 _lastEvalCamCm = new(float.NaN);
    private int _frameCounter;
    private int _inFlightCount;
    private int _epoch;

    public float LoadRadiusMeters { get; set; } = 500f;
    public bool Enabled { get; set; } = true;

    public int InFlightCount => Volatile.Read(ref _inFlightCount);
    public int LoadedCellCount { get; private set; }
    public int TotalCellCount => _boundScene?.WorldPartitionCells.Count ?? 0;

    public void Reset(WorldScene? scene)
    {
        Interlocked.Increment(ref _epoch);
        while (_completed.TryDequeue(out _)) { }
        _boundScene = scene;
        _lastEvalCamCm = new Vector3(float.NaN);
        _frameCounter = 0;
        LoadedCellCount = 0;
    }

    public IReadOnlyList<(Vector3 MinCm, Vector3 MaxCm)> GetLoadedActualCellBounds() => _loadedActualBounds;

    public void Update(WorldScene scene, IFileProvider? provider, Vector3 cameraUnrealCm, Action<string>? onLog = null)
    {
        if (!ReferenceEquals(_boundScene, scene))
            Reset(scene);

        if (provider is null || scene.WorldPartitionCells.Count == 0)
        {
            _loadedActualBounds.Clear();
            return;
        }

        // 1. Integrate up to MaxIntegratePerFrame completed cells on UI thread
        var sceneChanged = false;
        var integrated = 0;
        while (integrated < MaxIntegratePerFrame && _completed.TryDequeue(out var item))
        {
            var (cell, lvl) = item;
            cell.IsLoading = false;
            if (!scene.WorldPartitionCells.Contains(cell))
                continue;

            cell.LoadedLevel = lvl;
            cell.IsLoaded = true;
            if (!scene.Levels.Contains(lvl))
                scene.Levels.Add(lvl);
            sceneChanged = true;
            integrated++;
        }

        if (!Enabled)
        {
            if (sceneChanged)
                scene.RebuildAggregates();
            RebuildLoadedBounds(scene);
            return;
        }

        // 2. Throttle spatial evaluation to every 12 frames or 15m camera movement
        _frameCounter++;
        var movedSq = float.IsNaN(_lastEvalCamCm.X)
            ? float.MaxValue
            : Vector3.DistanceSquared(cameraUnrealCm, _lastEvalCamCm);

        if (movedSq >= ReevaluateMoveThresholdCm * ReevaluateMoveThresholdCm || (_frameCounter % 12) == 0)
        {
            _lastEvalCamCm = cameraUnrealCm;
            var loadRadiusCm = MathF.Max(10000f, LoadRadiusMeters * 100f);
            var unloadRadiusCm = loadRadiusCm * 1.45f;
            var hlodLoadRadiusCm = MathF.Max(loadRadiusCm * 5.0f, 350000f);

            _candidates.Clear();
            var cells = scene.WorldPartitionCells;
            var loadedCount = 0;

            for (var i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                if (!c.IsSpatiallyLoaded)
                {
                    if (c.IsLoaded) loadedCount++;
                    continue;
                }

                var distCm = DistanceToAabbCm(cameraUnrealCm, c.BoundsMinCm, c.BoundsMaxCm);

                if (c.IsLoaded)
                {
                    loadedCount++;
                    // Unload distant actual detail cells to keep memory & draw calls lean
                    if (!c.IsHlod && distCm > unloadRadiusCm && c.LoadedLevel is { } oldLvl && !IsLevelSelected(scene, oldLvl))
                    {
                        c.IsLoaded = false;
                        c.LoadedLevel = null;
                        oldLvl.Clear();
                        scene.Levels.Remove(oldLvl);
                        sceneChanged = true;
                        loadedCount--;
                    }
                    continue;
                }

                if (c.IsLoading)
                    continue;

                var threshold = c.IsHlod ? hlodLoadRadiusCm : loadRadiusCm;
                if (distCm <= threshold)
                    _candidates.Add(c);
            }

            LoadedCellCount = loadedCount;

            if (_candidates.Count > 0 && Volatile.Read(ref _inFlightCount) < MaxConcurrentLoads)
            {
                // Prioritize actual detail cells closest to the camera, then HLODs
                _candidates.Sort((a, b) =>
                {
                    var hlodCmp = a.IsHlod.CompareTo(b.IsHlod);
                    if (hlodCmp != 0) return hlodCmp;
                    var da = DistanceToAabbCm(cameraUnrealCm, a.BoundsMinCm, a.BoundsMaxCm);
                    var db = DistanceToAabbCm(cameraUnrealCm, b.BoundsMinCm, b.BoundsMaxCm);
                    return da.CompareTo(db);
                });

                for (var i = 0; i < _candidates.Count && Volatile.Read(ref _inFlightCount) < MaxConcurrentLoads; i++)
                {
                    var cell = _candidates[i];
                    cell.IsLoading = true;
                    Interlocked.Increment(ref _inFlightCount);
                    var captureEpoch = _epoch;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            if (Volatile.Read(ref _epoch) != captureEpoch)
                            {
                                cell.IsLoading = false;
                                return;
                            }
                            var lvl = WorldLoader.LoadWorldPartitionCellLevel(provider, cell, onLog);
                            if (lvl is not null && Volatile.Read(ref _epoch) == captureEpoch)
                                _completed.Enqueue((cell, lvl));
                            else
                                cell.IsLoading = false;
                        }
                        catch
                        {
                            cell.IsLoading = false;
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _inFlightCount);
                        }
                    });
                }
            }
        }

        if (sceneChanged)
            scene.RebuildAggregates();

        RebuildLoadedBounds(scene);
    }

    private void RebuildLoadedBounds(WorldScene scene)
    {
        _loadedActualBounds.Clear();
        var cells = scene.WorldPartitionCells;
        for (var i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            if (c.IsSpatiallyLoaded && !c.IsHlod && c.IsLoaded)
                _loadedActualBounds.Add((c.BoundsMinCm, c.BoundsMaxCm));
        }
    }

    private static float DistanceToAabbCm(Vector3 p, Vector3 min, Vector3 max)
    {
        var clamped = Vector3.Clamp(p, min, max);
        return Vector3.Distance(p, clamped);
    }

    private static bool IsLevelSelected(WorldScene scene, SceneLevel level)
    {
        if (scene.SelectedObject is not { } sel)
            return false;
        return ReferenceEquals(scene.FindOwningLevel(sel), level);
    }
}
