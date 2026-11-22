using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using ImGuiNET;
using ImGuizmoNET;
using UExplorer.Config;
using UExplorer.Platform;
using UExplorer.Rendering;
using UExplorer.Scene;
using UExplorer.Services;
using UExplorer.UI.Commands;
using UExplorer.UI.Menus;
using UExplorer.UI.Panels;
using Vortice.Direct3D11;

namespace UExplorer.UI;

internal static class DockHelper
{
    private const string Lib = "cimgui";
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern uint igDockBuilderAddNode(uint nodeId, int flags);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void igDockBuilderRemoveNode(uint nodeId);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr igDockBuilderGetNode(uint nodeId);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void igDockBuilderSetNodeSize(uint nodeId, Vector2 size);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern uint igDockBuilderSplitNode(uint nodeId, ImGuiDir dir, float ratio, out uint atDir, out uint opp);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void igDockBuilderDockWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint nodeId);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void igDockBuilderFinish(uint nodeId);

    public static bool HasNode(uint id) => igDockBuilderGetNode(id) != IntPtr.Zero;

    public static uint LeftViewportNodeId { get; private set; }
    public static uint BottomDockNodeId { get; private set; }

    public static void BuildUe52Layout(uint dockId, Vector2 size)
    {
        igDockBuilderRemoveNode(dockId);
        igDockBuilderAddNode(dockId, 1 << 10);
        igDockBuilderSetNodeSize(dockId, size);

        // UE 5.2 LevelEditor_Layout_v1.8:
        // Left/Center 75% (Viewport, tab bar hidden), Right 25% -> Top-Right 40% (Outliner + World Settings), Bottom-Right 60% (Details).
        // Content Browser & Output Log remain collapsed at startup.
        igDockBuilderSplitNode(dockId, ImGuiDir.Right, 0.25f, out var right, out var left);
        igDockBuilderSplitNode(right, ImGuiDir.Down, 0.60f, out var details, out var topRight);
        LeftViewportNodeId = left;
        BottomDockNodeId = 0;

        igDockBuilderDockWindow("Viewport", left);
        igDockBuilderDockWindow("Outliner", topRight);
        igDockBuilderDockWindow("World Settings", topRight);
        igDockBuilderDockWindow("Details", details);
        igDockBuilderFinish(dockId);
    }

    public static void DockBottomWindow(uint dockId, string windowName, float ratio = 0.28f)
    {
        if (!HasNode(dockId)) return;
        if (BottomDockNodeId != 0 && HasNode(BottomDockNodeId))
        {
            igDockBuilderDockWindow(windowName, BottomDockNodeId);
            igDockBuilderFinish(dockId);
            return;
        }
        var target = LeftViewportNodeId != 0 && HasNode(LeftViewportNodeId) ? LeftViewportNodeId : dockId;
        igDockBuilderSplitNode(target, ImGuiDir.Down, ratio, out var bottom, out var top);
        if (LeftViewportNodeId != 0 && target == LeftViewportNodeId)
        {
            LeftViewportNodeId = top;
            igDockBuilderDockWindow("Viewport", top);
        }
        BottomDockNodeId = bottom;
        igDockBuilderDockWindow(windowName, bottom);
        igDockBuilderFinish(dockId);
    }
}

public sealed class Ue52MainEditor : IDisposable
{
    private enum CameraPreset { Perspective, Top, Bottom, Left, Right, Front, Back }

    private readonly Dx11AppHost _host;
    private readonly UexManifest _manifest;
    private readonly GameMountService _mountService;
    private WorldScene _scene = new();
    private readonly Ue52ContentBrowser _contentBrowser = new();
    private readonly Ue52AssetPreviewWindow _assetPreview = new();
    private readonly UExplorerSettingsTab _settingsTab;
    private readonly EditorCommandHistory _commandHistory = new(maxDepth: 64);
    private readonly EditorClipboard _clipboard = new();
    private readonly EditorContextMenus _contextMenus = new();
    private readonly LevelsPanel _levelsPanel = new();
    private readonly WorldPartitionStreamer _wpStreamer = new();
    private readonly EditorMenuHost _menuHost;
    private readonly List<string> _logs = [];
    private readonly object _logLock = new();

    private NativeSceneRenderer? _sceneRenderer;
    private readonly CancellationTokenSource _disposeCts = new();
    private volatile bool _disposed;
    private bool _rendererInitStarted;
    private int _frameCount;
    private int _mountGeneration;
    private int _worldGeneration;
    private string? _mountStage;
    private float? _mountProgress;
    private TimeSpan _mountElapsed;
    private uint _dockSpaceId;
    private bool _layoutBuilt;
    private bool _focusOutlinerNextFrame;
    private volatile bool _loadingWorld;
    private string _loadingWorldName = string.Empty;
    private WorldScene? _pendingScene;
    private bool _focusViewportNextFrame;
    private SceneLevel? _activeSpawnLevel;

    // Viewport drag-and-drop & live material hover preview state
    private string? _hoverDragPath;
    private UObject? _hoverDragAsset;
    private MaterialAsset? _hoverMaterialPreview;
    private (SceneMesh? Mesh, int Slot) _hoverMaterialTarget;
    private UStaticMesh? _cachedBasicSphere;
    private bool _searchedBasicSphere;

    // Frame-budgeted GPU cache keys so opening large worlds never freezes the UI thread
    private readonly RetainedSceneDrawCache _retainedSceneCache = new();
    private readonly Dictionary<UStaticMesh, NativeMeshLods?> _frameMeshCache = [];
    private readonly Dictionary<MaterialAsset, NativeMaterialPipeline?> _framePipeCache = [];

    // Startup layout state (UE 5.2 LevelEditor_Layout_v1.8: Content Browser & Output Log collapsed)
    private bool _showContentBrowser = false;
    private bool _showContentDrawer = false;
    private bool _focusDrawerOnOpen = false;
    private bool _closeDrawerAfterDrag = false;
    private bool _showOutliner = true;
    private bool _showWorldSettings = true;
    private bool _showDetails = true;
    private bool _showLevels = false;
    private bool _showOutputLog = false;
    private bool _viewportMaximized = false;
    private bool _showHelpModal = false;
    private bool _gameView = false;

    // Status bar Content Drawer button bounds (prevents instant close when clicking the toggle button)
    private Vector2 _drawerToggleBtnMin;
    private Vector2 _drawerToggleBtnMax;

    // UE 5.2 Camera (meters, Y-up, reversed-Z)
    private Vector3 _camPos = new(0f, 3f, 8f);
    private float _camYaw = 0f;
    private float _camPitch = -0.22f;
    private float _camSpeed = 12f;
    private CameraPreset _camPreset = CameraPreset.Perspective;
    private float _orthoWidthMeters = 24f;

    // UE 5.2 Gizmo & Snapping state
    private bool _selectModeOnly = false;
    private OPERATION _gizmoOp = OPERATION.TRANSLATE;
    private MODE _gizmoMode = MODE.WORLD;
    private bool _surfaceSnap = false;
    private bool _snapT, _snapR, _snapS;
    private float _gridSnapStepCm = 10f;
    private float _angleSnapStepDeg = 10f;
    private float _scaleSnapStep = 0.25f;

    // Search & Rename state
    private string _outlinerSearch = string.Empty;
    private string _detailsSearch = string.Empty;
    private string _wsSearch = string.Empty;
    private SceneObject? _outlinerCtxTarget;
    private bool _openOutlinerItemCtx;
    private SceneObject? _renamingObject;
    private string _renameBuffer = string.Empty;
    private bool _focusRenameNextFrame;
    private bool _expandAllOutliner;
    private bool _collapseAllOutliner;

    // Undo coalescing state
    private SceneObject? _gizmoDragTarget;
    private TransformSnapshot _gizmoStartSnapshot;
    private bool _wasUsingGizmo;

    private string? _detailsUndoKey;
    private TransformSnapshot _detailsStartTransform;
    private LightSnapshot _detailsStartLight;
    private WorldSettingsState? _wsStartSnapshot;
    private string? _detailsNameEditKey;
    private string _detailsNameEditBuf = string.Empty;

    private static readonly Matrix4x4 UnrealToMeters = new(0.01f, 0, 0, 0, 0, 0, 0.01f, 0, 0, 0.01f, 0, 0, 0, 0, 0, 1);

    private sealed class ActionProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    public Ue52MainEditor(Dx11AppHost host, UexManifest manifest, GameMountService mountService)
    {
        _host = host;
        _manifest = manifest;
        _mountService = mountService;
        _settingsTab = new UExplorerSettingsTab(
            manifest,
            mountService,
            OnGameRemounted,
            _host.InvokeOnUiThread,
            p =>
            {
                if (_disposed) return;
                _mountStage = p.Stage;
                _mountProgress = p.Progress;
                _mountElapsed = p.Elapsed;
            });

        _menuHost = new EditorMenuHost
        {
            Scene = _scene,
            History = _commandHistory,
            Clipboard = _clipboard,
            GetActiveSpawnLevel = GetActiveSpawnLevel,
            SetActiveSpawnLevel = lvl => { _activeSpawnLevel = lvl; AddLog($"Active level set to '{lvl.Name}'."); },
            GetProvider = () => _mountService.Provider,
            FocusObject = FocusCameraOnObject,
            GoHereCm = posCm =>
            {
                var target = new Vector3(posCm.X, posCm.Z, posCm.Y) * 0.01f;
                var fwd = GetCameraForward();
                _camPos = target - fwd * 4f;
            },
            SnapCameraToObject = FocusCameraOnObject,
            SnapObjectToCamera = obj =>
            {
                var before = TransformSnapshot.Capture(obj);
                var camUn = new Vector3(_camPos.X, _camPos.Z, _camPos.Y) * 100f;
                obj.LocationCm = camUn;
                obj.OverrideLocalMatrix = null;
                var after = TransformSnapshot.Capture(obj);
                _commandHistory.RecordExecuted(new TransformCommand(_scene, obj, before, after, "Snap Object to Camera"));
                _scene.MarkDirty();
            },
            SnapObjectToFloor = SnapObjectToFloor,
            BeginOutlinerRename = BeginOutlinerRename,
            FindInContentBrowser = path =>
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    _contentBrowser.SelectAndRevealAsset(path);
                    if (!_showContentBrowser)
                    {
                        _showContentDrawer = true;
                        _focusDrawerOnOpen = true;
                    }
                    else
                    {
                        ImGui.SetWindowFocus("Content Browser");
                    }
                }
            },
            OpenAssetEditor = obj =>
            {
                if (_mountService.Provider is { } prov)
                    _assetPreview.Open(obj, prov);
            },
            ExportToUnrealByPackagePath = (pkgPath, assetName) =>
            {
                if (_mountService.Provider is not { } prov)
                {
                    AddLog("[Export to Unreal] No mounted game provider.");
                    return;
                }
                AddLog($"[Export to Unreal] Queuing '{assetName}' ({pkgPath})...");
                _ = Task.Run(() =>
                {
                    var res = UExplorer.Export.UnrealBridgeExporter.ExportByPackagePath(prov, pkgPath, assetName, AddLog);
                    if (!res.Success)
                        AddLog($"[Export to Unreal] {res.Message}");
                });
            },
            ExportToUnrealObject = obj =>
            {
                var prov = _mountService.Provider;
                AddLog($"[Export to Unreal] Exporting '{obj.Name}' ({obj.ExportType})...");
                _ = Task.Run(() =>
                {
                    var res = UExplorer.Export.UnrealBridgeExporter.ExportObject(obj, prov, AddLog);
                    if (!res.Success)
                        AddLog($"[Export to Unreal] {res.Message}");
                });
            },
            ExportCurrentWorldToUnreal = () =>
            {
                AddLog($"[Export to Unreal] Exporting loaded world '{_scene.WorldName}'...");
                var res = UExplorer.Export.UnrealBridgeExporter.ExportLoadedScene(_scene, AddLog);
                if (!res.Success)
                    AddLog($"[Export to Unreal] {res.Message}");
            },
            OpenWorldByPackagePath = path =>
            {
                if (_mountService.Provider is { } prov &&
                    LoadPrimaryAssetFromPackage(prov, path) is UWorld w)
                {
                    _showContentDrawer = false;
                    StartLoadWorld(prov, w);
                }
            },
            AddStreamingSubLevel = path =>
            {
                if (_mountService.Provider is { } prov)
                {
                    _showLevels = true;
                    _levelsPanel.LoadStreamingSubLevelAsync(
                        _scene,
                        prov,
                        path,
                        null,
                        Path.GetFileNameWithoutExtension(path),
                        _host,
                        () => Volatile.Read(ref _mountGeneration),
                        () => Volatile.Read(ref _worldGeneration),
                        () => _disposed,
                        OnLevelMutated,
                        AddLog);
                }
            },
            SpawnAssetIntoLevel = path =>
            {
                if (_mountService.Provider is { } prov &&
                    LoadPrimaryAssetFromPackage(prov, path) is { } asset)
                {
                    var camUn = new Vector3(_camPos.X, _camPos.Z, _camPos.Y) * 100f;
                    HandleViewportAssetDrop(prov, asset, default, camUn, GetCameraForward());
                }
            },
            ToggleLevelVisibility = lvl =>
            {
                WorldLoader.SetLevelVisible(_scene, lvl, !lvl.IsVisible);
                OnLevelMutated();
            },
            UnloadLevel = lvl =>
            {
                if (lvl.IsPersistent) return;
                if (ReferenceEquals(_activeSpawnLevel, lvl)) _activeSpawnLevel = null;
                _commandHistory.PruneForLevel(lvl);
                WorldLoader.UnloadLevel(_scene, lvl);
                OnLevelMutated();
            },
            ExpandAllOutliner = () => _expandAllOutliner = true,
            CollapseAllOutliner = () => _collapseAllOutliner = true,
            Log = AddLog,
            PlacePrimitiveShape = PlacePrimitiveShape,
            PlaceLightActor = PlaceLightActor,
            PasteAt = PasteAt,
            CutSelected = CutSelected,
            CopySelected = CopySelected,
            DuplicateSelected = DuplicateSelected,
            DeleteSelected = DeleteSelected
        };

        _contentBrowser.MenuHost = _menuHost;

        Ue52StyleAndIcons.ApplyStarshipTheme();
        Ue52StyleAndIcons.InitIcons(host);
        AddLog($"UExplorer initialized (DirectX 11). {mountService.Status}");
    }

    public void StartInitialMount()
    {
        if (_disposed || string.IsNullOrWhiteSpace(_manifest.GameDirectory))
            return;

        _mountStage = "Starting background mount...";
        _mountProgress = 0.02f;
        _mountElapsed = TimeSpan.Zero;
        var progress = new ActionProgress<MountProgress>(p =>
        {
            _host.InvokeOnUiThread(() =>
            {
                if (_disposed) return;
                _mountStage = p.Stage;
                _mountProgress = p.Progress;
                _mountElapsed = p.Elapsed;
            });
        });

        _ = Task.Run(async () =>
        {
            var res = await _mountService.MountAsync(_manifest, progress, _disposeCts.Token).ConfigureAwait(false);
            _host.InvokeOnUiThread(() =>
            {
                if (_disposed)
                    return;
                _mountStage = null;
                _mountProgress = null;
                if (res.Error is null && res.Provider is not null)
                    OnGameRemounted();
                else if (!string.IsNullOrWhiteSpace(res.Error))
                    AddLog(res.Error);
            });
        });
    }

    private void OnGameRemounted()
    {
        Interlocked.Increment(ref _mountGeneration);
        Interlocked.Increment(ref _worldGeneration);
        Interlocked.Exchange(ref _pendingScene, null);
        _commandHistory.Clear();
        _clipboard.Clear();
        _activeSpawnLevel = null;
        _contentBrowser.Reset();
        _scene.Clear();
        _retainedSceneCache.Clear();
        _frameMeshCache.Clear();
        _framePipeCache.Clear();
        _sceneRenderer?.ResetTimings();
        _hoverDragPath = null;
        _hoverDragAsset = null;
        _hoverMaterialPreview = null;
        _hoverMaterialTarget = default;
        _cachedBasicSphere = null;
        _searchedBasicSphere = false;
        _host.Window.Text = $"UExplorer - Unreal Engine 5.2 ({_manifest.Name})";
        AddLog(_mountService.Status);
    }

    private void OnLevelMutated()
    {
        _sceneRenderer?.ResetTimings();
    }

    public void AddLog(string msg)
    {
        lock (_logLock)
        {
            _logs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            if (_logs.Count > 500) _logs.RemoveAt(0);
        }
    }

    public void DrawFrame(float dt)
    {
        if (Interlocked.Exchange(ref _pendingScene, null) is { } readyScene)
        {
            try
            {
                Interlocked.Increment(ref _worldGeneration);
                _scene.Clear();
                CopyWorldSceneData(readyScene, _scene);
                _commandHistory.Clear();
                _activeSpawnLevel = _scene.Levels.FirstOrDefault(l => l.IsPersistent) ?? _scene.Levels.FirstOrDefault();
                _retainedSceneCache.Clear();
                _wpStreamer.Reset(_scene);
                _frameMeshCache.Clear();
                _framePipeCache.Clear();
                _sceneRenderer?.ResetTimings();
                _hoverMaterialTarget = default;
                FocusCameraOnScene(_scene);
            }
            catch (Exception ex)
            {
                AddLog($"Failed to activate loaded world: {ex.Message}");
            }
        }

        HandleGlobalShortcuts();
        DrawMainMenuBar();

        var vp = ImGui.GetMainViewport();
        const float toolbarH = 36f;
        const float statusH = 32f;
        var menuH = ImGui.GetFrameHeight();

        DrawUe52MainToolbar(vp.WorkPos.X, vp.Pos.Y + menuH, vp.WorkSize.X, toolbarH);

        // Fullscreen dockspace host tucked neatly between UE5 Main Toolbar and 32px Bottom Status Bar
        var dockPos = new Vector2(vp.Pos.X, vp.Pos.Y + menuH + toolbarH);
        var dockSize = new Vector2(vp.Size.X, Math.Max(100f, vp.Size.Y - menuH - toolbarH - statusH));
        ImGui.SetNextWindowPos(dockPos);
        ImGui.SetNextWindowSize(dockSize);
        ImGui.SetNextWindowViewport(vp.ID);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        const ImGuiWindowFlags hostFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize |
                                           ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus |
                                           ImGuiWindowFlags.NoDocking;
        ImGui.Begin("##Ue52DockHost", hostFlags);
        ImGui.PopStyleVar(3);

        _dockSpaceId = ImGui.GetID("Ue52DockSpace_v1_8");
        ImGui.DockSpace(_dockSpaceId, Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);
        if (!_layoutBuilt || !DockHelper.HasNode(_dockSpaceId))
        {
            _layoutBuilt = true;
            _focusOutlinerNextFrame = true;
            DockHelper.BuildUe52Layout(_dockSpaceId, dockSize);
        }
        ImGui.End();

        if (_viewportMaximized)
        {
            ImGui.SetNextWindowPos(dockPos);
            ImGui.SetNextWindowSize(dockSize);
            DrawViewport(dt, isMaximizedOverride: true);
        }
        else
        {
            DrawViewport(dt, isMaximizedOverride: false);
            if (_showOutliner) DrawOutliner();
            if (_showWorldSettings) DrawWorldSettings();
            if (_showDetails) DrawDetails();
            if (_showContentBrowser) DrawContentBrowser();
            if (_showLevels)
            {
                _levelsPanel.Draw(
                    ref _showLevels,
                    _scene,
                    _mountService.Provider,
                    _host,
                    _commandHistory,
                    ref _activeSpawnLevel,
                    () => Volatile.Read(ref _mountGeneration),
                    () => Volatile.Read(ref _worldGeneration),
                    () => _disposed,
                    OnLevelMutated,
                    AddLog);
            }
            if (_showOutputLog) DrawOutputLog();
        }

        if (_focusOutlinerNextFrame)
        {
            _focusOutlinerNextFrame = false;
            ImGui.SetWindowFocus("Outliner");
        }
        if (_focusViewportNextFrame)
        {
            _focusViewportNextFrame = false;
            ImGui.SetWindowFocus("Viewport");
        }

        if (_showContentDrawer)
            DrawContentDrawer(vp, statusH);
        else
            _closeDrawerAfterDrag = false;

        DrawUe52StatusBar(vp.Pos.X, vp.Pos.Y + vp.Size.Y - statusH, vp.Size.X, statusH);

        _settingsTab.Draw();
        DrawHelpModal();
        if (_mountService.Provider is { } prov)
            _assetPreview.DrawAll(prov);

        _frameCount++;
    }

    private static void CopyWorldSceneData(WorldScene src, WorldScene dst)
    {
        dst.Clear();
        dst.WorldName = src.WorldName;
        dst.DiscoveredStreamingLevels.AddRange(src.DiscoveredStreamingLevels);
        dst.WorldPartitionCells.AddRange(src.WorldPartitionCells);
        dst.Levels.AddRange(src.Levels);
        dst.WorldSettings.CopyFrom(src.WorldSettings);
        dst.RebuildAggregates();
    }

    private void HandleGlobalShortcuts()
    {
        var io = ImGui.GetIO();
        var ctrl = io.KeyCtrl;
        var shift = io.KeyShift;

        if (!io.WantTextInput && ctrl && ImGui.IsKeyPressed(ImGuiKey.Space, repeat: false))
        {
            if (_showContentBrowser)
            {
                ImGui.SetWindowFocus("Content Browser");
            }
            else
            {
                _showContentDrawer = !_showContentDrawer;
                _focusDrawerOnOpen = _showContentDrawer;
            }
            return;
        }

        if (_showContentDrawer && ImGui.IsKeyPressed(ImGuiKey.Escape, repeat: false))
        {
            _showContentDrawer = false;
            _closeDrawerAfterDrag = false;
            return;
        }

        if (io.WantTextInput)
            return;

        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Z, repeat: false))
        {
            if (shift) { if (_commandHistory.Redo()) AddLog($"Redo: {_commandHistory.NextUndoDescription}"); }
            else { if (_commandHistory.Undo()) AddLog($"Undo: {_commandHistory.NextRedoDescription}"); }
            return;
        }
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Y, repeat: false))
        {
            if (_commandHistory.Redo()) AddLog($"Redo: {_commandHistory.NextUndoDescription}");
            return;
        }
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.X, repeat: false)) { CutSelected(); return; }
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.C, repeat: false)) { CopySelected(); return; }
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.V, repeat: false)) { PasteAt(null); return; }
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.D, repeat: false)) { DuplicateSelected(); return; }

        if (ImGui.IsMouseDown(ImGuiMouseButton.Right))
            return;

        if (ImGui.IsKeyPressed(ImGuiKey.Delete, repeat: false))
            DeleteSelected();
        else if (ImGui.IsKeyPressed(ImGuiKey.F2, repeat: false) && _scene.SelectedObject is { } selRen && IsEditable(selRen))
            BeginOutlinerRename(selRen);
        else if (ImGui.IsKeyPressed(ImGuiKey.H, repeat: false) && _scene.SelectedObject is { } selVis && IsEditable(selVis))
            _commandHistory.Execute(new VisibilityCommand(_scene, [(selVis, selVis.IsVisible, !selVis.IsVisible)], selVis.IsVisible ? "Hide Selected" : "Show Selected"));
        else if (ImGui.IsKeyPressed(ImGuiKey.End, repeat: false) && _scene.SelectedObject is { } selFloor && IsEditable(selFloor))
            SnapObjectToFloor(selFloor);
        else if (ImGui.IsKeyPressed(ImGuiKey.G, repeat: false))
        {
            _gameView = !_gameView;
            RenderSettings.ShowEditorSprites = !_gameView;
        }
    }

    private void DrawMainMenuBar()
    {
        if (!ImGui.BeginMainMenuBar()) return;

        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("Open Level..."))
            {
                _showContentDrawer = true;
                _focusDrawerOnOpen = true;
            }
            if (ImGui.MenuItem("Add Streaming Level..."))
                _showLevels = true;
            ImGui.Separator();
            if (ImGui.MenuItem("Load Game Manifest (.uexmanif)...")) _settingsTab.LoadManifestDialog();
            if (ImGui.MenuItem("Save Game Manifest (.uexmanif)...")) _settingsTab.SaveManifestDialog();
            ImGui.Separator();
            if (ImGui.MenuItem("Unload Current World"))
            {
                Interlocked.Increment(ref _worldGeneration);
                _scene.Clear();
                _commandHistory.Clear();
                _activeSpawnLevel = null;
                _sceneRenderer?.ResetTimings();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Exit")) _host.RequestExit();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Edit"))
        {
            var undoLabel = _commandHistory.CanUndo ? $"Undo {_commandHistory.NextUndoDescription}" : "Undo";
            var redoLabel = _commandHistory.CanRedo ? $"Redo {_commandHistory.NextRedoDescription}" : "Redo";
            if (ImGui.MenuItem(undoLabel, "Ctrl+Z", false, _commandHistory.CanUndo)) _commandHistory.Undo();
            if (ImGui.MenuItem(redoLabel, "Ctrl+Y", false, _commandHistory.CanRedo)) _commandHistory.Redo();
            ImGui.Separator();
            var hasEditable = _scene.SelectedObject is { } sel && IsEditable(sel);
            if (ImGui.MenuItem("Cut", "Ctrl+X", false, hasEditable)) CutSelected();
            if (ImGui.MenuItem("Copy", "Ctrl+C", false, hasEditable)) CopySelected();
            if (ImGui.MenuItem("Paste", "Ctrl+V", false, _clipboard.HasItems)) PasteAt(null);
            if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, hasEditable)) DuplicateSelected();
            if (ImGui.MenuItem("Delete", "Del", false, hasEditable)) DeleteSelected();
            if (ImGui.MenuItem("Rename", "F2", false, hasEditable)) BeginOutlinerRename(_scene.SelectedObject!);
            ImGui.Separator();
            if (ImGui.MenuItem("Project Settings...")) _settingsTab.IsOpen = true;
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Window"))
        {
            if (ImGui.MenuItem("Content Drawer", "Ctrl+Space", _showContentDrawer))
            {
                _showContentDrawer = !_showContentDrawer;
                _focusDrawerOnOpen = _showContentDrawer;
            }
            if (ImGui.MenuItem("Content Browser", null, _showContentBrowser))
            {
                _showContentBrowser = !_showContentBrowser;
                if (_showContentBrowser && _layoutBuilt)
                    DockHelper.DockBottomWindow(_dockSpaceId, "Content Browser");
            }
            ImGui.Separator();
            ImGui.MenuItem("Outliner", null, ref _showOutliner);
            ImGui.MenuItem("World Settings", null, ref _showWorldSettings);
            ImGui.MenuItem("Details", null, ref _showDetails);
            ImGui.MenuItem("Levels", null, ref _showLevels);
            if (ImGui.MenuItem("Output Log", null, _showOutputLog))
            {
                _showOutputLog = !_showOutputLog;
                if (_showOutputLog && _layoutBuilt)
                    DockHelper.DockBottomWindow(_dockSpaceId, "Output Log");
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Reset UE 5.2 Layout"))
            {
                _viewportMaximized = false;
                _showOutliner = true;
                _showWorldSettings = true;
                _showDetails = true;
                _showContentBrowser = false;
                _showOutputLog = false;
                _showContentDrawer = false;
                _layoutBuilt = false;
            }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Tools"))
        {
            if (ImGui.MenuItem("Project Settings (Game / AES / Mappings)...")) _settingsTab.IsOpen = true;
            if (ImGui.MenuItem("Rendering & Scalability Settings...")) _settingsTab.OpenRenderingSection();
            ImGui.Separator();
            ImGui.MenuItem("Show Perf Overlay", null, ref RenderSettings.ShowPerfOverlay);
            if (ImGui.MenuItem("Reset Renderer Timings")) _sceneRenderer?.ResetTimings();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Build"))
        {
            if (ImGui.MenuItem("Rebuild Scene Draw Cache"))
            {
                _retainedSceneCache.Clear();
                _scene.MarkDirty();
                AddLog("Scene draw cache rebuilt.");
            }
            if (ImGui.MenuItem("Reload Shaders / Timings"))
                _sceneRenderer?.ResetTimings();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Select"))
        {
            if (ImGui.MenuItem("Select First Actor", null, false, _scene.RootObjects.Count > 0))
                _scene.Select(_scene.RootObjects[0]);
            if (ImGui.MenuItem("Clear Selection", null, false, _scene.SelectedObject is not null))
                _scene.Select(null);
            if (ImGui.MenuItem("Focus Selected", "F", false, _scene.SelectedObject is not null))
                FocusCameraOnObject(_scene.SelectedObject!);
            ImGui.EndMenu();
        }

        var actorMenuLabel = _scene.SelectedObject is { } actorSel ? $"Actor ({actorSel.Name})" : "Actor";
        if (ImGui.BeginMenu($"{actorMenuLabel}##ActorMenu"))
        {
            var editable = _scene.SelectedObject is { } aSel && IsEditable(aSel);
            if (ImGui.MenuItem("Focus Selected", "F", false, _scene.SelectedObject is not null))
                FocusCameraOnObject(_scene.SelectedObject!);
            if (ImGui.MenuItem("Snap to Floor", "End", false, editable))
                SnapObjectToFloor(_scene.SelectedObject!);
            if (ImGui.MenuItem("Toggle Visibility", "H", false, editable))
            {
                var target = _scene.SelectedObject!;
                _commandHistory.Execute(new VisibilityCommand(_scene, [(target, target.IsVisible, !target.IsVisible)], target.IsVisible ? "Hide Selected" : "Show Selected"));
            }
            if (ImGui.MenuItem("Reset Transform", null, false, editable))
            {
                var target = _scene.SelectedObject!;
                var before = TransformSnapshot.Capture(target);
                var after = new TransformSnapshot(Vector3.Zero, Vector3.Zero, Quaternion.Identity, Vector3.One, null);
                _commandHistory.Execute(new TransformCommand(_scene, target, before, after, "Reset Transform"));
            }
            ImGui.Separator();
            EditorContextMenus.DrawPlaceActorSubmenu(_menuHost, null);
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Help"))
        {
            if (ImGui.MenuItem("Controls & Shortcuts...")) _showHelpModal = true;
            if (ImGui.MenuItem("About UExplorer")) AddLog("UExplorer — DirectX 11 + ImGui UE 5.2 Asset & Level Explorer");
            ImGui.EndMenu();
        }

        // Right-aligned UE5 Project/Game Badge Pill
        var badgeText = _mountService.IsMounting
            ? "Mounting..."
            : (_mountService.IsMounted ? $"{_manifest.Name} | {_manifest.EngineVersion} | {_mountService.Provider!.Files.Count:N0} files" : "No Game Mounted");
        var dotCol = _mountService.IsMounting
            ? Ue52StyleAndIcons.StyleColors.Primary
            : (_mountService.IsMounted ? Ue52StyleAndIcons.StyleColors.Success : Ue52StyleAndIcons.StyleColors.Warning);
        var textSz = ImGui.CalcTextSize(badgeText);
        var pillW = textSz.X + 28f;
        var winW = ImGui.GetWindowWidth();
        var badgeX = Math.Max(ImGui.GetCursorPosX() + 12f, winW - pillW - 10f);
        ImGui.SameLine(badgeX);
        var pMin = ImGui.GetCursorScreenPos() + new Vector2(0f, 2f);
        var pMax = pMin + new Vector2(pillW, ImGui.GetFrameHeight() - 4f);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pMin, pMax, ImGui.GetColorU32(Ue52StyleAndIcons.StyleColors.Header), 8f);
        dl.AddRect(pMin, pMax, ImGui.GetColorU32(Ue52StyleAndIcons.StyleColors.InputOutline), 8f);
        var dotCenter = new Vector2(pMin.X + 10f, (pMin.Y + pMax.Y) * 0.5f);
        dl.AddCircleFilled(dotCenter, 4f, ImGui.GetColorU32(dotCol));
        dl.AddText(new Vector2(pMin.X + 19f, pMin.Y + (pMax.Y - pMin.Y - textSz.Y) * 0.5f), ImGui.GetColorU32(Ue52StyleAndIcons.StyleColors.Foreground), badgeText);
        ImGui.Dummy(new Vector2(pillW, ImGui.GetFrameHeight()));

        ImGui.EndMainMenuBar();
    }

    private void DrawUe52MainToolbar(float x, float y, float w, float h)
    {
        ImGui.SetNextWindowPos(new Vector2(x, y));
        ImGui.SetNextWindowSize(new Vector2(w, h));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 4f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Ue52StyleAndIcons.StyleColors.Panel);
        const ImGuiWindowFlags barFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                                          ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking;
        if (ImGui.Begin("##Ue52MainToolbar", barFlags))
        {
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##TbSave", Ue52IconKind.Save, string.Empty, false, "Save Game Manifest (.uexmanif)", iconSize: 20f))
                _settingsTab.SaveManifestDialog();

            ImGui.SameLine(0f, 8f);
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("|");
            ImGui.SameLine(0f, 8f);

            if (Ue52StyleAndIcons.ToolbarIconTextButton("##TbSelMode", Ue52IconKind.SelectCursor, "Selection Mode", true, "Editor Mode", iconSize: 20f, hasDropdown: true))
                ImGui.OpenPopup("##TbModePopup");
            if (ImGui.BeginPopup("##TbModePopup"))
            {
                if (ImGui.MenuItem("Selection Mode", null, true)) { }
                ImGui.MenuItem("Game View (Hide Sprites)", "G", ref _gameView);
                RenderSettings.ShowEditorSprites = !_gameView;
                ImGui.EndPopup();
            }

            ImGui.SameLine(0f, 6f);
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##TbAdd", Ue52IconKind.AddContent, "Add", false, "Quickly Add Actors or Open Content Drawer", iconSize: 20f, hasDropdown: true))
                ImGui.OpenPopup("##TbAddPopup");
            if (ImGui.BeginPopup("##TbAddPopup"))
            {
                EditorContextMenus.DrawPlaceActorSubmenu(_menuHost, null);
                ImGui.Separator();
                if (ImGui.MenuItem("Open Content Drawer", "Ctrl+Space"))
                {
                    _showContentDrawer = true;
                    _focusDrawerOnOpen = true;
                }
                ImGui.EndPopup();
            }

            ImGui.SameLine(0f, 6f);
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##TbLevels", Ue52IconKind.Levels, "Levels", _showLevels, "Open Levels Streaming Panel", iconSize: 20f))
                _showLevels = !_showLevels;

            ImGui.SameLine(0f, 6f);
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##TbPlay", Ue52IconKind.Play, "Preview", _gameView, "Toggle Game View Preview (G)", iconSize: 20f))
            {
                _gameView = !_gameView;
                RenderSettings.ShowEditorSprites = !_gameView;
            }

            ImGui.SameLine(0f, 16f);
            ImGui.AlignTextToFramePadding();
            if (_loadingWorld)
                ImGui.TextColored(Ue52StyleAndIcons.StyleColors.AccentBlue, $"Loading World '{_loadingWorldName}'...");
            else if (!string.IsNullOrEmpty(_scene.WorldName))
                ImGui.TextDisabled($"Level: {_scene.WorldName}");

            // Right-aligned Settings combo button
            const string settingsLabel = "Settings";
            var settingsW = ImGui.CalcTextSize(settingsLabel).X + 46f;
            var rightX = Math.Max(ImGui.GetCursorPosX() + 12f, w - settingsW - 10f);
            ImGui.SameLine(rightX);
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##TbSettings", Ue52IconKind.Settings, settingsLabel, _settingsTab.IsOpen, "Editor & Rendering Settings", iconSize: 20f, hasDropdown: true))
                ImGui.OpenPopup("##TbSettingsPopup");
            if (ImGui.BeginPopup("##TbSettingsPopup"))
            {
                if (ImGui.MenuItem("Project Settings...")) _settingsTab.IsOpen = true;
                if (ImGui.MenuItem("World Settings", null, _showWorldSettings))
                {
                    _showWorldSettings = true;
                    ImGui.SetWindowFocus("World Settings");
                }
                ImGui.Separator();
                UExplorerSettingsTab.DrawQuickPerfAndScalabilityItems(() => _settingsTab.OpenRenderingSection());
                ImGui.Separator();
                if (ImGui.MenuItem("Load .uexmanif...")) _settingsTab.LoadManifestDialog();
                if (ImGui.MenuItem("Save .uexmanif...")) _settingsTab.SaveManifestDialog();
                ImGui.EndPopup();
            }
        }
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    private void DrawUe52StatusBar(float x, float y, float w, float h)
    {
        ImGui.SetNextWindowPos(new Vector2(x, y));
        ImGui.SetNextWindowSize(new Vector2(w, h));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 4f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Ue52StyleAndIcons.StyleColors.Panel);
        ImGui.PushStyleColor(ImGuiCol.Border, Ue52StyleAndIcons.StyleColors.WindowBorder);
        const ImGuiWindowFlags barFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                                          ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking;
        if (ImGui.Begin("##Ue52StatusBar", barFlags))
        {
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##SbDrawer", Ue52IconKind.ContentDrawer, "Content Drawer", _showContentDrawer, "Toggle Content Drawer (Ctrl+Space)", iconSize: 16f))
            {
                if (_showContentBrowser)
                    ImGui.SetWindowFocus("Content Browser");
                else
                {
                    _showContentDrawer = !_showContentDrawer;
                    _focusDrawerOnOpen = _showContentDrawer;
                }
            }
            _drawerToggleBtnMin = ImGui.GetItemRectMin();
            _drawerToggleBtnMax = ImGui.GetItemRectMax();

            ImGui.SameLine(0f, 6f);
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##SbOutputLog", Ue52IconKind.OutputLog, "Output Log", _showOutputLog, "Toggle Output Log", iconSize: 16f))
            {
                _showOutputLog = !_showOutputLog;
                if (_showOutputLog && _layoutBuilt)
                    DockHelper.DockBottomWindow(_dockSpaceId, "Output Log");
            }

            ImGui.SameLine(0f, 6f);
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##SbLevels", Ue52IconKind.Levels, "Levels", _showLevels, "Toggle Levels Panel", iconSize: 16f))
                _showLevels = !_showLevels;

            ImGui.SameLine(0f, 14f);
            ImGui.AlignTextToFramePadding();

            if (_mountService.IsMounting)
            {
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, Ue52StyleAndIcons.StyleColors.Primary);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3f);
                ImGui.ProgressBar(_mountProgress ?? 0.1f, new Vector2(110f, 14f), string.Empty);
                ImGui.PopStyleColor();
                ImGui.SameLine(0f, 8f);
                var stage = _mountStage ?? _mountService.Status;
                var sec = _mountElapsed.TotalSeconds > 0.05 ? $" ({_mountElapsed.TotalSeconds:F1}s)" : string.Empty;
                ImGui.TextColored(Ue52StyleAndIcons.StyleColors.AccentBlue, $"{stage}{sec}");
            }
            else if (_loadingWorld)
            {
                ImGui.TextColored(Ue52StyleAndIcons.StyleColors.AccentBlue, $"Loading '{_loadingWorldName}'...");
            }
            else
            {
                ImGui.TextDisabled(_mountService.IsMounted
                    ? $"Mounted: {_manifest.Name} ({_manifest.EngineVersion})  |  {_mountService.Provider!.Files.Count:N0} files"
                    : "No game mounted - Open Settings to select Game Directory, AES Keys & .usmap");
            }

            // Right-aligned scene counters + clickable FPS/ms readout
            var countsText = $"Actors {_scene.RootObjects.Count}  Meshes {_scene.Meshes.Count}  Lights {_scene.Lights.Count}  VFX {_scene.Niagaras.Count}";
            var fpsText = _sceneRenderer is not null && _sceneRenderer.RollingFps > 0.1
                ? $"{_sceneRenderer.RollingFps:F0} FPS ({_sceneRenderer.RollingFrameMs:F1} ms)"
                : "-- FPS";
            var countsW = ImGui.CalcTextSize(countsText).X;
            var fpsW = ImGui.CalcTextSize(fpsText).X + 14f;
            var totalRightW = countsW + fpsW + 18f;
            var rightStart = Math.Max(ImGui.GetCursorPosX() + 12f, w - totalRightW - 8f);
            ImGui.SameLine(rightStart);
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(countsText);
            ImGui.SameLine(0f, 10f);
            if (ImGui.SmallButton($"{fpsText}##SbPerf"))
                RenderSettings.ShowPerfOverlay = !RenderSettings.ShowPerfOverlay;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Click to toggle CPU/GPU Pass Timings Overlay");
        }
        ImGui.End();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
    }

    private void DrawViewport(float dt, bool isMaximizedOverride)
    {
        var windowFlags = ImGuiWindowFlags.NoCollapse;
        if (isMaximizedOverride)
            windowFlags |= ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoDocking;

        if (!isMaximizedOverride)
        {
            var winClass = new ImGuiWindowClass { DockNodeFlagsOverrideSet = ImGuiDockNodeFlags.AutoHideTabBar };
            unsafe { ImGui.SetNextWindowClass(&winClass); }
        }

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var open = ImGui.Begin("Viewport", windowFlags);
        ImGui.PopStyleVar();
        if (!open) { ImGui.End(); return; }

        var avail = ImGui.GetContentRegionAvail();
        var w = Math.Max(64, (int) (avail.X * RenderSettings.ResolutionScale));
        var h = Math.Max(64, (int) (avail.Y * RenderSettings.ResolutionScale));
        var imgPos = ImGui.GetCursorScreenPos();
        var viewportImageHovered = false;
        var toolbarHovered = false;

        var device = D3D11NativeShaderDevice.Shared;
        if (device is not null && _sceneRenderer is null)
        {
            if (!_rendererInitStarted && _frameCount >= 1)
            {
                _rendererInitStarted = true;
                _ = Task.Run(() =>
                {
                    NativeSceneRenderer? built = null;
                    try
                    {
                        if (_disposeCts.IsCancellationRequested)
                            return;
                        built = new NativeSceneRenderer(device);
                        if (_disposed || !_host.TryInvokeOnUiThread(() =>
                            {
                                if (_disposed)
                                    built.Dispose();
                                else
                                    _sceneRenderer = built;
                            }))
                        {
                            built.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        built?.Dispose();
                        AddLog($"Failed to initialize DX11 renderer: {ex.Message}");
                    }
                });
            }

            var dummySize = Vector2.Max(avail, Vector2.One);
            ImGui.Dummy(dummySize);
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(imgPos, imgPos + dummySize, ImGui.GetColorU32(new Vector4(0.06f, 0.06f, 0.065f, 1f)));
            var statusLine = "Initializing DirectX 11 Renderer...";
            var subLine = _mountService.IsMounting
                ? (_mountStage ?? _mountService.Status)
                : (_mountService.IsMounted ? "Compiling deferred & post-process shaders..." : "Waiting for game mount...");
            var sz1 = ImGui.CalcTextSize(statusLine);
            var sz2 = ImGui.CalcTextSize(subLine);
            var center = imgPos + dummySize * 0.5f;
            dl.AddText(center - new Vector2(sz1.X * 0.5f, sz1.Y + 4f), ImGui.GetColorU32(new Vector4(0.88f, 0.90f, 0.93f, 1f)), statusLine);
            dl.AddText(center - new Vector2(sz2.X * 0.5f, -4f), ImGui.GetColorU32(new Vector4(0.35f, 0.65f, 0.98f, 1f)), subLine);
        }
        else if (device is not null && _sceneRenderer is not null)
        {
            var forward = GetCameraForward();
            var upVec = _camPreset is CameraPreset.Top or CameraPreset.Bottom ? -Vector3.UnitZ : Vector3.UnitY;
            var view = Matrix4x4.CreateLookAt(_camPos, _camPos + forward, upVec);
            var aspect = Math.Max(0.1f, avail.X / Math.Max(1f, avail.Y));
            const float near = 0.05f;
            var f = aspect / MathF.Tan(RenderSettings.FieldOfViewDeg * 0.5f * MathF.PI / 180f);
            var isOrtho = _camPreset != CameraPreset.Perspective;
            Matrix4x4 proj;
            Matrix4x4 gizmoProj;
            if (isOrtho)
            {
                var orthoH = _orthoWidthMeters / aspect;
                // Reversed-Z orthographic projection (z = near -> 1, z = far -> 0)
                const float far = 20000f;
                var invRange = 1f / (far - near);
                proj = new Matrix4x4(
                    2f / _orthoWidthMeters, 0, 0, 0,
                    0, 2f / orthoH, 0, 0,
                    0, 0, invRange, 0,
                    0, 0, far * invRange, 1);
                gizmoProj = Matrix4x4.CreateOrthographic(_orthoWidthMeters, orthoH, near, 100000f);
            }
            else
            {
                proj = new Matrix4x4(f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, 0, -1, 0, 0, near, 0);
                gizmoProj = Matrix4x4.CreatePerspectiveFieldOfView(Math.Clamp(2f * MathF.Atan(1f / f), 0.05f, 3.0f), aspect, near, 100000f);
            }

            var worldToClip = UnrealToMeters * view * proj;
            var camUn = new Vector3(_camPos.X, _camPos.Z, _camPos.Y) * 100f;

            _host.SyncInterval = RenderSettings.VSync ? 1 : 0;
            _sceneRenderer.HlodTransitionDistance = RenderSettings.HlodTransitionDistanceMeters * 100f;
            _sceneRenderer.MinScreenSize = RenderSettings.MinScreenSize;
            _sceneRenderer.LodScreenBias = RenderSettings.LodScreenBias;

            _wpStreamer.Update(_scene, _mountService.Provider, camUn, AddLog);
            _retainedSceneCache.Update(device, _scene);
            _retainedSceneCache.SyncMaterialParameters(device, _mountService.Provider);

            (SceneMesh? Mesh, int Slot, NativeMaterialPipeline? Pipeline) hoverOverride = default;
            if (_hoverMaterialTarget.Mesh is { } hoverMesh && _hoverMaterialPreview is { } hoverMat)
            {
                if (!_framePipeCache.TryGetValue(hoverMat, out var hPipe) || hPipe is null)
                {
                    hPipe = NativeAssetCache.GetPipeline(device, hoverMat, out _);
                    if (hPipe is not null) _framePipeCache[hoverMat] = hPipe;
                }
                hPipe?.EnsureParametersSynced(device.Device, device.Context, _mountService.Provider, hoverMat);
                if (hPipe is not null)
                    hoverOverride = (hoverMesh, _hoverMaterialTarget.Slot, hPipe);
            }

            var niagaraSprites = new List<NativeNiagaraSpriteBatch>();
            var niagaraMeshes = new List<NativeNiagaraMeshBatch>();
            var billboards = new List<NativeBillboardDrawItem>();
            var dynamicLights = new List<SceneLight>(_scene.Lights.Count + 8);
            dynamicLights.AddRange(_scene.Lights);

            var niagaraCullCm = RenderSettings.CullDistanceMeters * 100f;
            var camFwdUn = new Vector3(forward.X, forward.Z, forward.Y);
            camFwdUn = camFwdUn.LengthSquared() > 1e-6f ? Vector3.Normalize(camFwdUn) : Vector3.UnitX;
            _vfxOcclusionFrame++;
            foreach (var sn in _scene.Niagaras)
            {
                if (!sn.IsVisibleRecursive) continue;
                var vfxPosCm = sn.WorldLocationCm;
                var toVfx = vfxPosCm - camUn;
                var distCm = toVfx.Length();
                if (distCm > niagaraCullCm) continue;

                // Fast frustum cone check (with generous 550cm effect radius margin)
                if (distCm > 550f && !sn.IsSelectedRecursive)
                {
                    var dir = toVfx / distCm;
                    if (Vector3.Dot(dir, camFwdUn) < -0.35f)
                        continue;
                    if (IsVfxOccludedByWalls(sn, camUn, vfxPosCm, distCm))
                        continue;
                }

                var snWorld = UnrealSpace.ToUnrealLocalToWorld(sn.WorldMatrix);
                foreach (var me in sn.Asset.MeshEmitters)
                {
                    if (!me.Enabled) continue;
                    if (!_frameMeshCache.TryGetValue(me.Mesh, out var nm) || nm is null)
                    {
                        nm = NativeAssetCache.GetMesh(device, me.Mesh);
                        if (nm is not null) _frameMeshCache[me.Mesh] = nm;
                    }
                    if (nm is null) continue;
                    ID3D11ShaderResourceView? meshTexSrv = null;
                    if (me.Material is { } mMat &&
                        (mMat.TryGetTexture(out var dTex, EMaterialTextureKind.Diffuse) || mMat.TryGetTexture(out dTex, EMaterialTextureKind.Emissive)))
                    {
                        meshTexSrv = NativeTextureCache.Get(device.Device, dTex);
                    }
                    var mPipe = me.Material is not null ? NativeAssetCache.GetNiagaraPipeline(device, me.Material, isMesh: true, out _) : null;
                    niagaraMeshes.Add(new NativeNiagaraMeshBatch(me, nm, snWorld, meshTexSrv, sn.GetSim(me), mPipe));
                }
                foreach (var se in sn.Asset.SpriteEmitters)
                {
                    if (!se.Enabled) continue;
                    var srv = se.SpriteTexture is not null ? NativeTextureCache.Get(device.Device, se.SpriteTexture) : null;
                    var sPipe = se.Material is not null ? NativeAssetCache.GetNiagaraPipeline(device, se.Material, isMesh: false, out _) : null;
                    niagaraSprites.Add(new NativeNiagaraSpriteBatch(se, snWorld, srv, sn.GetSim(se), sPipe));
                }
                foreach (var le in sn.Asset.LightEmitters)
                {
                    if (!le.Enabled) continue;
                    dynamicLights.Add(new ScenePointLight
                    {
                        Name = le.EmitterName,
                        LocationCm = sn.WorldLocationCm,
                        Parent = null,
                        Color = le.Color,
                        Intensity = le.Intensity,
                        AttenuationRadius = le.DefaultRadius
                    });
                }
                if (RenderSettings.ShowEditorSprites)
                    billboards.Add(new NativeBillboardDrawItem(ENativeGizmoIconKind.NiagaraVfx, sn.WorldLocationCm, new Vector3Tint(0.35f, 0.85f, 1.0f), sn.IsSelectedRecursive));
            }

            if (RenderSettings.ShowEditorSprites)
            {
                foreach (var l in _scene.Lights)
                {
                    if (!l.IsVisible) continue;
                    var kind = l switch
                    {
                        ScenePointLight => ENativeGizmoIconKind.PointLight,
                        SceneSpotLight => ENativeGizmoIconKind.SpotLight,
                        SceneRectLight => ENativeGizmoIconKind.RectLight,
                        SceneDirectionalLight => ENativeGizmoIconKind.DirectionalLight,
                        SceneSkyLight => ENativeGizmoIconKind.SkyLight,
                        _ => ENativeGizmoIconKind.PointLight
                    };
                    billboards.Add(new NativeBillboardDrawItem(kind, l.WorldLocationCm, new Vector3Tint(l.Color.X, l.Color.Y, l.Color.Z), l.IsSelectedRecursive));
                }
            }

            List<NativeLandscapeDrawItem>? landscapes = null;
            if (_scene.Landscapes.Count > 0)
            {
                landscapes = new List<NativeLandscapeDrawItem>(_scene.Landscapes.Count);
                for (var li = 0; li < _scene.Landscapes.Count; li++)
                {
                    var land = _scene.Landscapes[li];
                    if (!land.IsVisible || !land.IsVisibleRecursive)
                        continue;
                    if (NativeAssetCache.GetLandscapeTile(device, land) is { } tile)
                        landscapes.Add(new NativeLandscapeDrawItem(tile, UnrealSpace.ToUnrealLocalToWorld(land.WorldMatrix), 0f));
                }
            }

            _sceneRenderer.FlipCulling = RenderSettings.FlipCulling;
            _sceneRenderer.Features = RenderSettings.ViewportFeatures();
            _sceneRenderer.Render(w, h, worldToClip, camUn, 0.5f * f, Array.Empty<NativeDrawItem>(), (ENativePreviewMode) RenderSettings.PreviewMode,
                RenderSettings.AutoExposure ? 1.0f : RenderSettings.Exposure,
                new Vector3(0.4f, 0.3f, 0.85f), measure: false, landscapes: landscapes, niagaraSprites: niagaraSprites, niagaraMeshes: niagaraMeshes,
                sceneLights: dynamicLights, editorBillboards: billboards, heightFog: _scene.HeightFog, postProcess: _scene.PostProcess,
                retainedCache: _retainedSceneCache, worldSettings: _scene.WorldSettings, hoverOverride: hoverOverride,
                loadedWpCellBounds: _wpStreamer.GetLoadedActualCellBounds());

            _hoverMaterialTarget = default;

            if (_sceneRenderer.OutputSrv is { } outSrv)
            {
                ImGui.SetNextItemAllowOverlap();
                ImGui.Image(outSrv.NativePointer, avail);
                viewportImageHovered = ImGui.IsItemHovered();
                HandleViewportDragDropTarget(imgPos, avail, view, gizmoProj, camUn, forward);
            }

            // UE 5.2 Overlay Toolbar (Left Pill + Right Pill)
            toolbarHovered = DrawViewportOverlayToolbar(imgPos, avail);

            // UE5 Viewport HUD overlays (stats below left pill, perf below right pill, 3D axis triad bottom-left)
            var odl = ImGui.GetWindowDrawList();
            {
                var visibleMeshes = _retainedSceneCache.VisibleMeshCount;
                var loadedMeshes = _retainedSceneCache.LoadedMeshCount;
                var distinctMeshes = _retainedSceneCache.DistinctMeshCount;
                var lines = new List<(string Text, uint Col)>
                {
                    ($"{_scene.WorldName}", ImGui.GetColorU32(new Vector4(0.93f, 0.93f, 0.93f, 1f))),
                    ($"Actors {_scene.RootObjects.Count}   Meshes {visibleMeshes}   Lights {_scene.Lights.Count}   VFX {_scene.Niagaras.Count}" +
                     (_wpStreamer.TotalCellCount > 0 ? $"   WP {_wpStreamer.LoadedCellCount}/{_wpStreamer.TotalCellCount}" + (_wpStreamer.InFlightCount > 0 ? " (streaming)" : "") : ""),
                        ImGui.GetColorU32(new Vector4(0.70f, 0.72f, 0.76f, 1f))),
                };
                if (_loadingWorld)
                    lines.Add(($"Loading level '{_loadingWorldName}'...", ImGui.GetColorU32(new Vector4(0.25f, 0.62f, 1f, 1f))));
                else if (visibleMeshes > 0 && loadedMeshes < distinctMeshes)
                    lines.Add(($"Streaming meshes {loadedMeshes}/{distinctMeshes}...",
                        ImGui.GetColorU32(new Vector4(0.25f, 0.62f, 1f, 1f))));
                else if (_scene.Meshes.Count == 0 && !string.IsNullOrEmpty(_scene.WorldName) && _scene.WorldName != "Untitled World")
                    lines.Add(("No static meshes found in this level (World Partition / external actors may not be loaded)",
                        ImGui.GetColorU32(new Vector4(0.95f, 0.65f, 0.25f, 1f))));

                var op = imgPos + new Vector2(10f, 44f);
                var boxW = lines.Max(l => ImGui.CalcTextSize(l.Text).X) + 16f;
                var boxH = lines.Count * ImGui.GetTextLineHeightWithSpacing() + 8f;
                odl.AddRectFilled(op, op + new Vector2(boxW, boxH), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.45f)), 4f);
                for (var li = 0; li < lines.Count; li++)
                    odl.AddText(op + new Vector2(8f, 4f + li * ImGui.GetTextLineHeightWithSpacing()), lines[li].Col, lines[li].Text);

                if (RenderSettings.ShowPerfOverlay)
                {
                    var t = _sceneRenderer.LastTimings;
                    string[] perfLines =
                    [
                        $"FPS {_sceneRenderer.RollingFps:F1} ({_sceneRenderer.RollingFrameMs:F2} ms)  Draws {_sceneRenderer.DrawCalls}  Vis {_sceneRenderer.VisibleCount}  Lights {_sceneRenderer.ActiveLocalLights}",
                        $"CPU Cull {t.CpuCullSortMs:F2} | GBuffer {t.CpuGBufferMs:F2} | Shad {t.CpuShadowsMs:F2} | SSAO {t.CpuSsaoMs:F2} | Lit {t.CpuLightCullAndLightingMs:F2} | Trans {t.CpuTranslucencyMs:F2} | Post {t.CpuPostProcessMs:F2}",
                        t.GpuValid
                            ? $"GPU Total {t.GpuTotalMs:F2} | GBuf {t.GpuGBufferMs:F2} | Shad {t.GpuShadowsMs:F2} | SSAO {t.GpuSsaoMs:F2} | Lit {t.GpuLightCullAndLightingMs:F2} | Trans {t.GpuTranslucencyMs:F2} | Post {t.GpuPostProcessMs:F2}"
                            : "GPU Timings: warming up..."
                    ];
                    var pBoxW = perfLines.Max(l => ImGui.CalcTextSize(l).X) + 16f;
                    var pBoxH = perfLines.Length * ImGui.GetTextLineHeightWithSpacing() + 8f;
                    var pp = imgPos + new Vector2(Math.Max(10f, avail.X - pBoxW - 10f), 44f);
                    odl.AddRectFilled(pp, pp + new Vector2(pBoxW, pBoxH), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.55f)), 4f);
                    var pCol = ImGui.GetColorU32(new Vector4(0.45f, 0.90f, 0.55f, 1f));
                    for (var pi = 0; pi < perfLines.Length; pi++)
                        odl.AddText(pp + new Vector2(8f, 4f + pi * ImGui.GetTextLineHeightWithSpacing()), pCol, perfLines[pi]);
                }

                // Bottom-left 3D axis triad (UE5 style)
                var ax = imgPos + new Vector2(28f, avail.Y - 28f);
                var fwdN = Vector3.Normalize(forward);
                var rightN = Vector3.Normalize(Vector3.Cross(fwdN, upVec));
                var upN = Vector3.Cross(rightN, fwdN);
                void Axis(Vector3 dirMeters, uint col, string label)
                {
                    var sx = Vector3.Dot(dirMeters, rightN);
                    var sy = -Vector3.Dot(dirMeters, upN);
                    var end = ax + new Vector2(sx, sy) * 18f;
                    odl.AddLine(ax, end, col, 2f);
                    odl.AddText(end + new Vector2(2f, -7f), col, label);
                }
                Axis(new Vector3(1, 0, 0), ImGui.GetColorU32(new Vector4(0.90f, 0.25f, 0.25f, 1f)), "X");
                Axis(new Vector3(0, 0, 1), ImGui.GetColorU32(new Vector4(0.35f, 0.85f, 0.30f, 1f)), "Y");
                Axis(new Vector3(0, 1, 0), ImGui.GetColorU32(new Vector4(0.30f, 0.55f, 1.00f, 1f)), "Z");
            }

            var effectiveHovered = viewportImageHovered && !toolbarHovered;
            UpdateCameraInput(effectiveHovered, dt, forward);

            var viewProj = view * gizmoProj;
            if (_scene.SelectedObject is { } selected)
                DrawSelectionWireframes(selected, viewProj, imgPos, avail);

            var gizmoActive = false;
            if (!_selectModeOnly && _scene.SelectedObject is { } sel && IsEditable(sel))
            {
                ImGuizmo.SetOrthographic(isOrtho);
                ImGuizmo.SetDrawlist(ImGui.GetWindowDrawList());
                ImGuizmo.SetRect(imgPos.X, imgPos.Y, avail.X, avail.Y);
                var mat = sel.WorldMatrix;
                var v = view;
                var p = gizmoProj;
                var snap = _gizmoOp switch
                {
                    OPERATION.TRANSLATE when _snapT => new Vector3(_gridSnapStepCm * 0.01f),
                    OPERATION.ROTATE when _snapR => new Vector3(_angleSnapStepDeg),
                    OPERATION.SCALE when _snapS => new Vector3(_scaleSnapStep),
                    _ => Vector3.Zero
                };
                var manipulated = snap == Vector3.Zero
                    ? ImGuizmo.Manipulate(ref v.M11, ref p.M11, _gizmoOp, _gizmoMode, ref mat.M11)
                    : ImGuizmo.Manipulate(ref v.M11, ref p.M11, _gizmoOp, _gizmoMode, ref mat.M11, ref Unsafe.NullRef<float>(), ref snap.X);

                var usingNow = ImGuizmo.IsUsing();
                if (usingNow && (!_wasUsingGizmo || !ReferenceEquals(_gizmoDragTarget, sel)))
                {
                    _detailsUndoKey = null;
                    _gizmoDragTarget = sel;
                    _gizmoStartSnapshot = TransformSnapshot.Capture(sel);
                }
                if (manipulated)
                {
                    sel.SetWorldMatrix(mat);
                    if (_surfaceSnap && _gizmoOp == OPERATION.TRANSLATE)
                        SnapObjectToFloorInternal(sel, excludeSelected: true);
                    _scene.MarkDirty();
                }
                if (!usingNow && _wasUsingGizmo && ReferenceEquals(_gizmoDragTarget, sel))
                {
                    var endSnap = TransformSnapshot.Capture(sel);
                    if (endSnap != _gizmoStartSnapshot)
                        _commandHistory.RecordExecuted(new TransformCommand(_scene, sel, _gizmoStartSnapshot, endSnap, "Gizmo Transform"));
                    _gizmoDragTarget = null;
                }
                _wasUsingGizmo = usingNow;
                gizmoActive = ImGuizmo.IsOver() || usingNow;
            }
            else if (_wasUsingGizmo)
            {
                _wasUsingGizmo = false;
                _gizmoDragTarget = null;
            }

            if (effectiveHovered && !gizmoActive && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsMouseDown(ImGuiMouseButton.Right))
            {
                var picked = PickDetailed(ImGui.GetMousePos(), imgPos, avail, view, gizmoProj, includeBillboards: true).Hit;
                _scene.Select(picked is null ? null : ActorOf(picked, ImGui.GetIO().KeyAlt));
            }

            _contextMenus.HandleViewportRightClick(effectiveHovered, gizmoActive, mousePos =>
            {
                var pick = PickDetailed(mousePos, imgPos, avail, view, gizmoProj, includeBillboards: true);
                var locCm = pick.Hit is not null
                    ? pick.HitLocationCm
                    : new Vector3(_camPos.X, _camPos.Z, _camPos.Y) * 100f + new Vector3(forward.X, forward.Z, forward.Y) * 400f;
                return new ViewportMenuPick(pick.Hit, locCm);
            });
            _contextMenus.DrawViewportContextMenus(_menuHost);
        }

        ImGui.End();
    }

    private bool DrawViewportOverlayToolbar(Vector2 imgPos, Vector2 avail)
    {
        var anyHovered = false;
        const float pillH = 28f;

        // Left Pill Group: [≡ Options] [Perspective v] [Lit v] [Show v]
        var leftPos = imgPos + new Vector2(8f, 6f);
        var presetLabel = _camPreset.ToString();
        var viewModeLabel = ((ENativePreviewMode) RenderSettings.PreviewMode) switch
        {
            ENativePreviewMode.Lit => "Lit",
            ENativePreviewMode.Emissive => "Unlit",
            ENativePreviewMode.Wireframe => "Wireframe",
            ENativePreviewMode.BaseColor => "BaseColor",
            ENativePreviewMode.Normal => "Normal",
            ENativePreviewMode.MetallicSpecularRoughness => "Metallic/Rough",
            _ => "Lit"
        };
        var leftW = 34f
                    + (ImGui.CalcTextSize(presetLabel).X + 21f)
                    + (ImGui.CalcTextSize(viewModeLabel).X + 21f)
                    + (ImGui.CalcTextSize("Show").X + 21f)
                    + 12f;

        Ue52StyleAndIcons.BeginViewportPillStyle();
        ImGui.SetCursorScreenPos(leftPos);
        if (ImGui.BeginChild("##VpLeftPill", new Vector2(MathF.Min(leftW, avail.X * 0.48f), pillH), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##VpOpt", Ue52IconKind.OptionsMenu, string.Empty, false, "Viewport Options", iconSize: 16f))
                ImGui.OpenPopup("##VpOptionsPopup");
            if (ImGui.BeginPopup("##VpOptionsPopup"))
            {
                UExplorerSettingsTab.DrawQuickPerfAndScalabilityItems(() => _settingsTab.OpenRenderingSection());
                ImGui.Separator();
                ImGui.SetNextItemWidth(130f);
                ImGui.SliderFloat("Field of View", ref RenderSettings.FieldOfViewDeg, 30f, 150f, "%.0f deg");
                ImGui.SetNextItemWidth(130f);
                if (ImGui.SliderFloat("Resolution Scale", ref RenderSettings.ResolutionScale, 0.25f, 1f, "%.2f"))
                    RenderSettings.ScalabilityPreset = 4;
                if (ImGui.MenuItem("Game View", "G", _gameView))
                {
                    _gameView = !_gameView;
                    RenderSettings.ShowEditorSprites = !_gameView;
                }
                ImGui.EndPopup();
            }

            ImGui.SameLine();
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##VpPersp", Ue52IconKind.GenericAsset, presetLabel, false, "Camera Projection & Orientation", iconSize: 0f, hasDropdown: true))
                ImGui.OpenPopup("##VpPerspPopup");
            if (ImGui.BeginPopup("##VpPerspPopup"))
            {
                if (ImGui.MenuItem("Perspective", null, _camPreset == CameraPreset.Perspective)) SetCameraPreset(CameraPreset.Perspective);
                ImGui.Separator();
                if (ImGui.MenuItem("Top (Ortho)", null, _camPreset == CameraPreset.Top)) SetCameraPreset(CameraPreset.Top);
                if (ImGui.MenuItem("Bottom (Ortho)", null, _camPreset == CameraPreset.Bottom)) SetCameraPreset(CameraPreset.Bottom);
                if (ImGui.MenuItem("Left (Ortho)", null, _camPreset == CameraPreset.Left)) SetCameraPreset(CameraPreset.Left);
                if (ImGui.MenuItem("Right (Ortho)", null, _camPreset == CameraPreset.Right)) SetCameraPreset(CameraPreset.Right);
                if (ImGui.MenuItem("Front (Ortho)", null, _camPreset == CameraPreset.Front)) SetCameraPreset(CameraPreset.Front);
                if (ImGui.MenuItem("Back (Ortho)", null, _camPreset == CameraPreset.Back)) SetCameraPreset(CameraPreset.Back);
                ImGui.EndPopup();
            }

            ImGui.SameLine();
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##VpLit", Ue52IconKind.GenericAsset, viewModeLabel, false, "Viewport View Mode", iconSize: 0f, hasDropdown: true))
                ImGui.OpenPopup("##VpLitPopup");
            if (ImGui.BeginPopup("##VpLitPopup"))
            {
                if (ImGui.MenuItem("Lit", null, RenderSettings.PreviewMode == (int) ENativePreviewMode.Lit)) RenderSettings.PreviewMode = (int) ENativePreviewMode.Lit;
                if (ImGui.MenuItem("Unlit", null, RenderSettings.PreviewMode == (int) ENativePreviewMode.Emissive)) RenderSettings.PreviewMode = (int) ENativePreviewMode.Emissive;
                if (ImGui.MenuItem("Wireframe", null, RenderSettings.PreviewMode == (int) ENativePreviewMode.Wireframe)) RenderSettings.PreviewMode = (int) ENativePreviewMode.Wireframe;
                ImGui.Separator();
                if (ImGui.MenuItem("BaseColor", null, RenderSettings.PreviewMode == (int) ENativePreviewMode.BaseColor)) RenderSettings.PreviewMode = (int) ENativePreviewMode.BaseColor;
                if (ImGui.MenuItem("Normal", null, RenderSettings.PreviewMode == (int) ENativePreviewMode.Normal)) RenderSettings.PreviewMode = (int) ENativePreviewMode.Normal;
                if (ImGui.MenuItem("Metallic / Specular / Roughness", null, RenderSettings.PreviewMode == (int) ENativePreviewMode.MetallicSpecularRoughness)) RenderSettings.PreviewMode = (int) ENativePreviewMode.MetallicSpecularRoughness;
                ImGui.EndPopup();
            }

            ImGui.SameLine();
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##VpShow", Ue52IconKind.GenericAsset, "Show", false, "Show Flags", iconSize: 0f, hasDropdown: true))
                ImGui.OpenPopup("##VpShowPopup");
            if (ImGui.BeginPopup("##VpShowPopup"))
            {
                Ue52StyleAndIcons.PushCheckbox(RenderSettings.ShowEditorSprites);
                if (ImGui.Checkbox("Editor Sprites", ref RenderSettings.ShowEditorSprites)) _gameView = !RenderSettings.ShowEditorSprites;
                Ue52StyleAndIcons.PopCheckbox();

                var skyOn = _scene.WorldSettings.SkyEnabled;
                Ue52StyleAndIcons.PushCheckbox(skyOn);
                if (ImGui.Checkbox("Sky / Atmosphere", ref skyOn))
                {
                    var before = _scene.WorldSettings.Clone();
                    _scene.WorldSettings.SkyEnabled = skyOn;
                    _commandHistory.RecordExecuted(new WorldSettingsCommand(_scene, before, _scene.WorldSettings, "Toggle Sky"));
                    _scene.MarkDirty();
                }
                Ue52StyleAndIcons.PopCheckbox();

                var fogOn = _scene.WorldSettings.FogEnabled;
                Ue52StyleAndIcons.PushCheckbox(fogOn);
                if (ImGui.Checkbox("Height Fog", ref fogOn))
                {
                    var before = _scene.WorldSettings.Clone();
                    _scene.WorldSettings.FogEnabled = fogOn;
                    _commandHistory.RecordExecuted(new WorldSettingsCommand(_scene, before, _scene.WorldSettings, "Toggle Fog"));
                    _scene.MarkDirty();
                }
                Ue52StyleAndIcons.PopCheckbox();

                Ue52StyleAndIcons.PushCheckbox(RenderSettings.DynamicShadows);
                ImGui.Checkbox("Dynamic Shadows", ref RenderSettings.DynamicShadows);
                Ue52StyleAndIcons.PopCheckbox();

                Ue52StyleAndIcons.PushCheckbox(RenderSettings.ScreenSpaceAO);
                ImGui.Checkbox("Screen Space AO", ref RenderSettings.ScreenSpaceAO);
                Ue52StyleAndIcons.PopCheckbox();

                Ue52StyleAndIcons.PushCheckbox(RenderSettings.Bloom);
                ImGui.Checkbox("Bloom", ref RenderSettings.Bloom);
                Ue52StyleAndIcons.PopCheckbox();

                Ue52StyleAndIcons.PushCheckbox(RenderSettings.TemporalAA);
                ImGui.Checkbox("Temporal AA", ref RenderSettings.TemporalAA);
                Ue52StyleAndIcons.PopCheckbox();

                ImGui.EndPopup();
            }
        }
        anyHovered |= ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup | ImGuiHoveredFlags.ChildWindows);
        ImGui.EndChild();
        Ue52StyleAndIcons.EndViewportPillStyle();

        // Right Pill Group: [Select|W|E|R] [World/Local] [Surface] [Grid v] [Angle v] [Scale v] [Cam N] [Max]
        var camLabel = $"{_camSpeed:0}";
        var gridLabel = $"{_gridSnapStepCm:0}";
        var angleLabel = $"{_angleSnapStepDeg:0}°";
        var scaleLabel = $"{_scaleSnapStep:0.##}";
        var rightW = 28f * 5f + (28f + ImGui.CalcTextSize(gridLabel).X + 12f) + (28f + ImGui.CalcTextSize(angleLabel).X + 12f) + (28f + ImGui.CalcTextSize(scaleLabel).X + 12f) + (28f + ImGui.CalcTextSize(camLabel).X) + 30f + 22f;
        var actualRightW = MathF.Min(rightW, MathF.Max(220f, avail.X - 16f));
        var rightPos = imgPos + new Vector2(MathF.Max(8f, avail.X - actualRightW - 8f), 6f);

        Ue52StyleAndIcons.BeginViewportPillStyle();
        ImGui.SetCursorScreenPos(rightPos);
        if (ImGui.BeginChild("##VpRightPill", new Vector2(actualRightW, pillH), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##VpSel", Ue52IconKind.SelectCursor, string.Empty, _selectModeOnly, "Select Only", iconSize: 15f))
                _selectModeOnly = true;
            ImGui.SameLine();
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##VpT", Ue52IconKind.Translate, string.Empty, !_selectModeOnly && _gizmoOp == OPERATION.TRANSLATE, "Translate (W)", iconSize: 15f))
            { _selectModeOnly = false; _gizmoOp = OPERATION.TRANSLATE; }
            ImGui.SameLine();
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##VpR", Ue52IconKind.Rotate, string.Empty, !_selectModeOnly && _gizmoOp == OPERATION.ROTATE, "Rotate (E)", iconSize: 15f))
            { _selectModeOnly = false; _gizmoOp = OPERATION.ROTATE; }
            ImGui.SameLine();
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##VpS", Ue52IconKind.Scale, string.Empty, !_selectModeOnly && _gizmoOp == OPERATION.SCALE, "Scale (R)", iconSize: 15f))
            { _selectModeOnly = false; _gizmoOp = OPERATION.SCALE; }

            ImGui.SameLine();
            var isW = _gizmoMode == MODE.WORLD;
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##VpCoord", isW ? Ue52IconKind.WorldSpace : Ue52IconKind.LocalSpace, string.Empty, isW, isW ? "World Coordinate System" : "Local Coordinate System", iconSize: 15f))
                _gizmoMode = isW ? MODE.LOCAL : MODE.WORLD;

            ImGui.SameLine();
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##VpSurfSnap", Ue52IconKind.PlaneShape, string.Empty, _surfaceSnap, "Surface Snap", iconSize: 15f))
                _surfaceSnap = !_surfaceSnap;

            ImGui.SameLine();
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##VpSnapG", Ue52IconKind.SnapGrid, gridLabel, _snapT, "Snap to Grid (Click to toggle, right-click for step)", iconSize: 14f, hasDropdown: true))
                _snapT = !_snapT;
            if (ImGui.BeginPopupContextItem("##VpSnapGStep"))
            {
                foreach (var step in new[] { 1f, 5f, 10f, 50f, 100f, 500f, 1000f })
                {
                    if (ImGui.MenuItem($"{step:0} cm", null, MathF.Abs(_gridSnapStepCm - step) < 0.01f))
                    { _gridSnapStepCm = step; _snapT = true; }
                }
                ImGui.EndPopup();
            }

            ImGui.SameLine();
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##VpSnapA", Ue52IconKind.SnapAngle, angleLabel, _snapR, "Rotation Snap (Right-click for step)", iconSize: 14f, hasDropdown: true))
                _snapR = !_snapR;
            if (ImGui.BeginPopupContextItem("##VpSnapAStep"))
            {
                foreach (var deg in new[] { 5f, 10f, 15f, 30f, 45f, 90f })
                {
                    if (ImGui.MenuItem($"{deg:0}°", null, MathF.Abs(_angleSnapStepDeg - deg) < 0.01f))
                    { _angleSnapStepDeg = deg; _snapR = true; }
                }
                ImGui.EndPopup();
            }

            ImGui.SameLine();
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##VpSnapS", Ue52IconKind.SnapScale, scaleLabel, _snapS, "Scale Snap (Right-click for step)", iconSize: 14f, hasDropdown: true))
                _snapS = !_snapS;
            if (ImGui.BeginPopupContextItem("##VpSnapSStep"))
            {
                foreach (var sc in new[] { 0.0625f, 0.125f, 0.25f, 0.5f, 1.0f })
                {
                    if (ImGui.MenuItem($"{sc:0.####}", null, MathF.Abs(_scaleSnapStep - sc) < 0.001f))
                    { _scaleSnapStep = sc; _snapS = true; }
                }
                ImGui.EndPopup();
            }

            ImGui.SameLine();
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##VpCamSpd", Ue52IconKind.CameraSpeed, camLabel, false, "Camera Speed", iconSize: 15f))
                ImGui.OpenPopup("##VpCamSpdPopup");
            if (ImGui.BeginPopup("##VpCamSpdPopup"))
            {
                ImGui.TextUnformatted("Camera Speed (m/s)");
                ImGui.SetNextItemWidth(140f);
                ImGui.SliderFloat("##CamSpdSlider", ref _camSpeed, 0.5f, 120f, "%.1f m/s");
                ImGui.EndPopup();
            }

            ImGui.SameLine();
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##VpMax", _viewportMaximized ? Ue52IconKind.Restore : Ue52IconKind.Maximize, string.Empty, _viewportMaximized, _viewportMaximized ? "Restore Layout" : "Maximize Viewport", iconSize: 14f))
                _viewportMaximized = !_viewportMaximized;
        }
        anyHovered |= ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup | ImGuiHoveredFlags.ChildWindows);
        ImGui.EndChild();
        Ue52StyleAndIcons.EndViewportPillStyle();

        return anyHovered || ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId);
    }

    private Vector3 GetCameraForward() => new(
        MathF.Sin(_camYaw) * MathF.Cos(_camPitch),
        MathF.Sin(_camPitch),
        -MathF.Cos(_camYaw) * MathF.Cos(_camPitch));

    private void SetCameraPreset(CameraPreset preset)
    {
        _camPreset = preset;
        switch (preset)
        {
            case CameraPreset.Top: _camYaw = 0f; _camPitch = -1.56f; break;
            case CameraPreset.Bottom: _camYaw = 0f; _camPitch = 1.56f; break;
            case CameraPreset.Front: _camYaw = 0f; _camPitch = 0f; break;
            case CameraPreset.Back: _camYaw = MathF.PI; _camPitch = 0f; break;
            case CameraPreset.Left: _camYaw = -MathF.PI * 0.5f; _camPitch = 0f; break;
            case CameraPreset.Right: _camYaw = MathF.PI * 0.5f; _camPitch = 0f; break;
        }
    }

    private readonly record struct ViewportPickResult(SceneObject? Hit, Vector3 HitLocationCm, int MaterialSlot);

    private unsafe void HandleViewportDragDropTarget(Vector2 imgPos, Vector2 avail, Matrix4x4 view, Matrix4x4 proj, Vector3 camUn, Vector3 forwardMeters)
    {
        if (_mountService.Provider is not { } prov || !ImGui.BeginDragDropTarget())
            return;

        var payload = ImGui.AcceptDragDropPayload(Ue52Widgets.AssetDragPayloadType, ImGuiDragDropFlags.AcceptBeforeDelivery);
        if (payload.NativePtr != null && payload.Data != IntPtr.Zero && payload.DataSize > 0)
        {
            var draggedPath = System.Text.Encoding.UTF8.GetString((byte*) payload.Data, payload.DataSize);
            if (!string.Equals(_hoverDragPath, draggedPath, StringComparison.OrdinalIgnoreCase))
            {
                _hoverDragPath = draggedPath;
                _hoverDragAsset = LoadPrimaryAssetFromPackage(prov, draggedPath);
                _hoverMaterialPreview = _hoverDragAsset is UUnrealMaterial uMat ? new MaterialAsset(uMat) : null;
            }

            var pick = PickDetailed(ImGui.GetMousePos(), imgPos, avail, view, proj, includeBillboards: false);
            if (!payload.IsDelivery())
            {
                if (_hoverDragAsset is UUnrealMaterial hoverMat && pick.Hit is SceneMesh hoverMesh)
                {
                    var slot = Math.Max(0, pick.MaterialSlot);
                    _hoverMaterialTarget = (hoverMesh, slot);
                    ImGui.SetTooltip($"Assign '{hoverMat.Name}' -> {hoverMesh.Name} [Element {slot}]");
                }
                else if (_hoverDragAsset is not null)
                {
                    ImGui.SetTooltip($"Drop '{_hoverDragAsset.Name}' ({_hoverDragAsset.ExportType}) into level");
                }
            }
            else if (_hoverDragAsset is not null)
            {
                _hoverMaterialTarget = default;
                HandleViewportAssetDrop(prov, _hoverDragAsset, pick, camUn, forwardMeters);
            }
        }

        ImGui.EndDragDropTarget();
    }

    private void HandleViewportAssetDrop(IFileProvider prov, UObject asset, ViewportPickResult pick, Vector3 camUn, Vector3 forwardMeters)
    {
        var fwdUn = new Vector3(forwardMeters.X, forwardMeters.Z, forwardMeters.Y);
        fwdUn = fwdUn.LengthSquared() > 1e-6f ? Vector3.Normalize(fwdUn) : Vector3.UnitX;
        var dropLocCm = pick.Hit is SceneMesh ? pick.HitLocationCm : camUn + fwdUn * 400f;
        var targetLevel = GetActiveSpawnLevel();

        if (asset is UStaticMesh sm)
        {
            var node = new SceneMesh
            {
                Name = MakeUniqueName(targetLevel, sm.Name),
                ClassName = "StaticMeshActor",
                Path = sm.GetPathName(),
                SourceObject = sm,
                LocationCm = dropLocCm,
                Mesh = sm,
                Materials = WorldLoader.ResolveMaterialSlots(sm, sm)
            };
            _commandHistory.Execute(new SpawnObjectsCommand(_scene, targetLevel, [node], description: $"Place {sm.Name}"));
            AddLog($"Placed StaticMesh '{sm.Name}' at ({dropLocCm.X:F0}, {dropLocCm.Y:F0}, {dropLocCm.Z:F0}).");
        }
        else if (TryExtractBlueprintClass(asset) is { } bp)
        {
            var baseName = bp.Name.EndsWith("_C", StringComparison.Ordinal) ? bp.Name[..^2] : bp.Name;
            var actor = new SceneObject
            {
                Name = MakeUniqueName(targetLevel, baseName),
                ClassName = bp.Name,
                Path = bp.GetPathName(),
                SourceObject = bp,
                LocationCm = dropLocCm
            };
            foreach (var comp in BlueprintComponents.Build(bp, null, isHlod: false))
            {
                comp.Parent = actor;
                actor.Children.Add(comp);
            }
            _commandHistory.Execute(new SpawnObjectsCommand(_scene, targetLevel, [actor], description: $"Place {actor.Name}"));
            AddLog($"Placed Blueprint '{actor.Name}' at ({dropLocCm.X:F0}, {dropLocCm.Y:F0}, {dropLocCm.Z:F0}).");
        }
        else if (asset.ExportType.Contains("Niagara", StringComparison.OrdinalIgnoreCase))
        {
            var niag = new NiagaraAsset(asset, asset.Name);
            var node = new SceneNiagara
            {
                Name = MakeUniqueName(targetLevel, asset.Name),
                ClassName = "NiagaraActor",
                Path = asset.GetPathName(),
                SourceObject = asset,
                LocationCm = dropLocCm,
                Asset = niag
            };
            _commandHistory.Execute(new SpawnObjectsCommand(_scene, targetLevel, [node], description: $"Place {asset.Name}"));
            AddLog($"Placed Niagara '{asset.Name}' at ({dropLocCm.X:F0}, {dropLocCm.Y:F0}, {dropLocCm.Z:F0}).");
        }
        else if (asset is UUnrealMaterial uMat)
        {
            if (pick.Hit is SceneMesh hitMesh)
            {
                var slot = Math.Max(0, pick.MaterialSlot);
                var oldMats = hitMesh.Materials.ToArray();
                var newMats = hitMesh.Materials.ToArray();
                if (slot >= newMats.Length)
                    Array.Resize(ref newMats, slot + 1);
                newMats[slot] = new MaterialAsset(uMat);
                _commandHistory.Execute(new MaterialSlotCommand(_scene, hitMesh, oldMats, newMats, $"Assign {uMat.Name}"));
                _scene.Select(ActorOf(hitMesh, ImGui.GetIO().KeyAlt));
                AddLog($"Assigned Material '{uMat.Name}' to {hitMesh.Name} [Element {slot}].");
            }
            else if (GetBasicSphere(prov) is { } sphereSm)
            {
                var matSlotCount = Math.Max(1, sphereSm.Materials?.Length ?? 1);
                var matArr = new MaterialAsset?[matSlotCount];
                var ma = new MaterialAsset(uMat);
                for (var s = 0; s < matSlotCount; s++) matArr[s] = ma;
                var sphereLocCm = camUn + fwdUn * 300f;
                var node = new SceneMesh
                {
                    Name = MakeUniqueName(targetLevel, $"Sphere_{uMat.Name}"),
                    ClassName = "StaticMeshActor",
                    Path = sphereSm.GetPathName(),
                    SourceObject = sphereSm,
                    LocationCm = sphereLocCm,
                    Mesh = sphereSm,
                    Materials = matArr
                };
                _commandHistory.Execute(new SpawnObjectsCommand(_scene, targetLevel, [node], description: $"Place Sphere_{uMat.Name}"));
                AddLog($"Spawned preview sphere with Material '{uMat.Name}'.");
            }
            else
            {
                _assetPreview.Open(uMat, prov);
            }
        }
        else if (asset is UWorld world)
        {
            StartLoadWorld(prov, world);
        }
        else
        {
            _assetPreview.Open(asset, prov);
        }
    }

    private static UBlueprintGeneratedClass? TryExtractBlueprintClass(UObject asset)
    {
        if (asset is UBlueprintGeneratedClass bpgc)
            return bpgc;
        if (asset.TryGetValue(out FPackageIndex genIdx, "GeneratedClass") && genIdx.TryLoad<UBlueprintGeneratedClass>(out var loadedBp))
            return loadedBp;
        return null;
    }

    private UStaticMesh? GetBasicSphere(IFileProvider prov)
    {
        if (_searchedBasicSphere)
            return _cachedBasicSphere;
        _searchedBasicSphere = true;
        if (prov.TryLoadPackageObject<UStaticMesh>("Engine/Content/BasicShapes/Sphere.Sphere", out var sphere))
            _cachedBasicSphere = sphere;
        return _cachedBasicSphere;
    }

    private static UObject? LoadPrimaryAssetFromPackage(IFileProvider prov, string packagePath)
    {
        try
        {
            if (!prov.TryLoadPackage(packagePath, out var pkg))
                return null;
            var shortName = Path.GetFileNameWithoutExtension(packagePath);
            var exports = pkg.GetExports().ToList();
            return exports.FirstOrDefault(e => e.Name.Equals(shortName, StringComparison.OrdinalIgnoreCase))
                   ?? exports.FirstOrDefault(e => e is UBlueprintGeneratedClass)
                   ?? exports.FirstOrDefault(e => e is UWorld or UStaticMesh or UUnrealMaterial)
                   ?? exports.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static SceneObject ActorOf(SceneObject node, bool component)
    {
        if (component) return node;
        var current = node;
        while (current.Parent is { } parent && !string.Equals(parent.ClassName, "LevelStreaming", StringComparison.Ordinal))
            current = parent;
        return current;
    }

    private static bool ToScreen(Vector3 meters, Matrix4x4 viewProj, Vector2 origin, Vector2 size, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(meters, 1f), viewProj);
        screen = default;
        if (clip.W <= 1e-4f) return false;
        screen = origin + new Vector2((clip.X / clip.W * 0.5f + 0.5f) * size.X, (0.5f - clip.Y / clip.W * 0.5f) * size.Y);
        return true;
    }

    private ViewportPickResult PickDetailed(Vector2 mouse, Vector2 origin, Vector2 size, Matrix4x4 view, Matrix4x4 proj, bool includeBillboards = true)
    {
        var viewProj = view * proj;
        if (includeBillboards && RenderSettings.ShowEditorSprites)
        {
            SceneObject? best = null;
            var bestDistance = 16f;
            foreach (var obj in _scene.Lights.Cast<SceneObject>().Concat(_scene.Niagaras))
            {
                if (!obj.IsVisibleRecursive) continue;
                if (!ToScreen(obj.WorldMatrix.Translation, viewProj, origin, size, out var screen)) continue;
                var distance = Vector2.Distance(screen, mouse);
                if (distance < bestDistance) { bestDistance = distance; best = obj; }
            }
            if (best is not null) return new ViewportPickResult(best, best.WorldLocationCm, -1);
        }

        if (!Matrix4x4.Invert(viewProj, out var inverse)) return default;
        var ndc = new Vector2((mouse.X - origin.X) / size.X * 2f - 1f, 1f - (mouse.Y - origin.Y) / size.Y * 2f);
        var nearPoint = Vector4.Transform(new Vector4(ndc, 0f, 1f), inverse);
        var farPoint = Vector4.Transform(new Vector4(ndc, 1f, 1f), inverse);
        var rayOrigin = UnrealSpace.ToUnrealPosition(new Vector3(nearPoint.X, nearPoint.Y, nearPoint.Z) / nearPoint.W);
        var rayEnd = UnrealSpace.ToUnrealPosition(new Vector3(farPoint.X, farPoint.Y, farPoint.Z) / farPoint.W);
        var rayDir = Vector3.Normalize(rayEnd - rayOrigin);

        SceneObject? hit = null;
        var hitLocationCm = Vector3.Zero;
        var hitTriFirstIndex = -1;
        NativeMesh? hitNativeMesh = null;
        var hitDistance = float.MaxValue;
        foreach (var sm in _scene.Meshes)
        {
            if (sm.IsHlod || !sm.IsVisibleRecursive) continue;
            if ((!_retainedSceneCache.TryGetCachedMesh(sm.Mesh, out var lods) && !_frameMeshCache.TryGetValue(sm.Mesh, out lods)) || lods is null || lods.Lods.Length == 0) continue;
            var mesh = lods.Lods[0];
            IEnumerable<Matrix4x4> worlds = sm.InstanceLocalMatrices is { Count: > 0 } instances
                ? instances.Select(im => im * sm.WorldMatrix)
                : [sm.WorldMatrix];
            foreach (var worldMeters in worlds)
            {
                var localToWorld = UnrealSpace.ToUnrealLocalToWorld(worldMeters);
                var scale = MathF.Max(new Vector3(localToWorld.M11, localToWorld.M12, localToWorld.M13).Length(),
                    MathF.Max(new Vector3(localToWorld.M21, localToWorld.M22, localToWorld.M23).Length(), new Vector3(localToWorld.M31, localToWorld.M32, localToWorld.M33).Length()));
                var center = Vector3.Transform(mesh.BoundsCenter, localToWorld);
                if (!RaySphere(rayOrigin, rayDir, center, mesh.BoundsRadius * scale, out var sphereT) || sphereT > hitDistance) continue;
                if (!Matrix4x4.Invert(localToWorld, out var worldToLocal)) continue;

                var localOrigin = Vector3.Transform(rayOrigin, worldToLocal);
                var localDir = Vector3.TransformNormal(rayDir, worldToLocal);
                var positions = mesh.CpuPositions;
                var indices = mesh.CpuIndices;
                for (var i = 0; i + 2 < indices.Length; i += 3)
                {
                    var i0 = indices[i];
                    var i1 = indices[i + 1];
                    var i2 = indices[i + 2];
                    if (i0 >= positions.Length || i1 >= positions.Length || i2 >= positions.Length) continue;
                    if (!RayTriangle(localOrigin, localDir, positions[i0], positions[i1], positions[i2], out var t)) continue;
                    var worldHit = Vector3.Transform(localOrigin + localDir * t, localToWorld);
                    var distance = Vector3.Distance(rayOrigin, worldHit);
                    if (distance < hitDistance)
                    {
                        hitDistance = distance;
                        hit = sm;
                        hitLocationCm = worldHit;
                        hitTriFirstIndex = i;
                        hitNativeMesh = mesh;
                    }
                }
            }
        }

        var hitSlot = 0;
        if (hitNativeMesh is not null && hitTriFirstIndex >= 0)
        {
            foreach (var sec in hitNativeMesh.Sections)
            {
                if (hitTriFirstIndex >= sec.FirstIndex && hitTriFirstIndex < sec.FirstIndex + sec.IndexCount)
                {
                    hitSlot = sec.MaterialIndex;
                    break;
                }
            }
        }
        return new ViewportPickResult(hit, hitLocationCm, hitSlot);
    }

    private static bool RaySphere(Vector3 origin, Vector3 dir, Vector3 center, float radius, out float t)
    {
        var oc = origin - center;
        var b = Vector3.Dot(oc, dir);
        var c = oc.LengthSquared() - radius * radius;
        var discriminant = b * b - c;
        t = 0;
        if (discriminant < 0) return false;
        t = MathF.Max(0, -b - MathF.Sqrt(discriminant));
        return c <= 0 || -b > 0;
    }

    private static bool RayTriangle(Vector3 origin, Vector3 dir, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0;
        var e1 = b - a; var e2 = c - a;
        var p = Vector3.Cross(dir, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f) return false;
        var inv = 1f / det;
        var s = origin - a;
        var u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(dir, q) * inv;
        if (v < 0 || u + v > 1) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > 0;
    }

    private static void DrawSelectionWireframes(SceneObject selected, Matrix4x4 viewProj, Vector2 origin, Vector2 size)
    {
        var lights = new List<SceneLight>();
        void Collect(SceneObject obj)
        {
            if (obj is SceneLight light) lights.Add(light);
            foreach (var child in obj.Children) Collect(child);
        }
        Collect(selected);
        if (lights.Count == 0) return;

        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(origin, origin + size, true);
        var radiusColor = ImGui.GetColorU32(new Vector4(0.62f, 0.78f, 1f, 0.9f));
        var outerColor = ImGui.GetColorU32(new Vector4(0.95f, 0.85f, 0.35f, 0.95f));
        var innerColor = ImGui.GetColorU32(new Vector4(0.95f, 0.85f, 0.35f, 0.45f));

        void Polyline(IEnumerable<Vector3> points, uint color, bool closed)
        {
            Vector2? previous = null, first = null;
            foreach (var p in points)
            {
                if (!ToScreen(p, viewProj, origin, size, out var screen)) { previous = null; continue; }
                if (previous is { } prev) dl.AddLine(prev, screen, color, 1.5f);
                previous = screen;
                first ??= screen;
            }
            if (closed && previous is { } last && first is { } start) dl.AddLine(last, start, color, 1.5f);
        }

        IEnumerable<Vector3> Circle(Vector3 center, Vector3 axisA, Vector3 axisB, float radius, int segments = 64)
        {
            for (var i = 0; i < segments; i++)
            {
                var angle = i / (float) segments * MathF.Tau;
                yield return center + (axisA * MathF.Cos(angle) + axisB * MathF.Sin(angle)) * radius;
            }
        }

        foreach (var light in lights)
        {
            var world = light.WorldMatrix;
            var position = world.Translation;
            var forward = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, world));
            var right = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, world));
            var up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, world));

            switch (light)
            {
                case SceneSpotLight spot:
                {
                    var radius = spot.AttenuationRadius * 0.01f;
                    foreach (var (angleDeg, color) in new[] { (spot.OuterConeAngle, outerColor), (spot.InnerConeAngle, innerColor) })
                    {
                        var angle = float.DegreesToRadians(Math.Clamp(angleDeg, 0.1f, 89f));
                        var capCenter = position + forward * radius * MathF.Cos(angle);
                        var capRadius = radius * MathF.Sin(angle);
                        Polyline(Circle(capCenter, right, up, capRadius), color, true);
                        for (var i = 0; i < 8; i++)
                        {
                            var a = i / 8f * MathF.Tau;
                            Polyline([position, capCenter + (right * MathF.Cos(a) + up * MathF.Sin(a)) * capRadius], color, false);
                        }
                    }
                    Polyline(Circle(position, forward, right, radius, 48).Where(p => Vector3.Dot(p - position, forward) >= 0), radiusColor, false);
                    break;
                }
                case LocalLightComponent local:
                {
                    var radius = local.AttenuationRadius * 0.01f;
                    Polyline(Circle(position, Vector3.UnitX, Vector3.UnitY, radius), radiusColor, true);
                    Polyline(Circle(position, Vector3.UnitX, Vector3.UnitZ, radius), radiusColor, true);
                    Polyline(Circle(position, Vector3.UnitY, Vector3.UnitZ, radius), radiusColor, true);
                    if (light is SceneRectLight rect)
                    {
                        var w = rect.SourceWidth * 0.005f; var h = rect.SourceHeight * 0.005f;
                        Polyline([position - right * w - up * h, position + right * w - up * h, position + right * w + up * h, position - right * w + up * h], outerColor, true);
                    }
                    break;
                }
                case SceneDirectionalLight:
                {
                    var tip = position + forward * 2f;
                    Polyline([position, tip], outerColor, false);
                    Polyline([tip, tip - forward * 0.35f + right * 0.15f], outerColor, false);
                    Polyline([tip, tip - forward * 0.35f - right * 0.15f], outerColor, false);
                    break;
                }
            }
        }
        dl.PopClipRect();
    }

    private void UpdateCameraInput(bool hovered, float dt, Vector3 forward)
    {
        var io = ImGui.GetIO();
        if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Right))
        {
            if (_camPreset != CameraPreset.Perspective)
            {
                var right = Vector3.Normalize(Vector3.Cross(forward, -Vector3.UnitZ));
                var up = Vector3.Cross(right, forward);
                var panScale = _orthoWidthMeters * 0.0015f;
                _camPos -= right * io.MouseDelta.X * panScale;
                _camPos += up * io.MouseDelta.Y * panScale;
            }
            else
            {
                _camYaw += io.MouseDelta.X * 0.004f;
                _camPitch = Math.Clamp(_camPitch - io.MouseDelta.Y * 0.004f, -1.5f, 1.5f);

                var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
                var speed = _camSpeed * (ImGui.IsKeyDown(ImGuiKey.ModShift) ? 3f : 1f) * dt;
                if (ImGui.IsKeyDown(ImGuiKey.W)) _camPos += forward * speed;
                if (ImGui.IsKeyDown(ImGuiKey.S)) _camPos -= forward * speed;
                if (ImGui.IsKeyDown(ImGuiKey.D)) _camPos += right * speed;
                if (ImGui.IsKeyDown(ImGuiKey.A)) _camPos -= right * speed;
                if (ImGui.IsKeyDown(ImGuiKey.E)) _camPos += Vector3.UnitY * speed;
                if (ImGui.IsKeyDown(ImGuiKey.Q)) _camPos -= Vector3.UnitY * speed;
            }
        }
        else if (hovered && !io.WantTextInput && !ImGui.IsMouseDown(ImGuiMouseButton.Right))
        {
            if (ImGui.IsKeyPressed(ImGuiKey.W)) { _selectModeOnly = false; _gizmoOp = OPERATION.TRANSLATE; }
            if (ImGui.IsKeyPressed(ImGuiKey.E)) { _selectModeOnly = false; _gizmoOp = OPERATION.ROTATE; }
            if (ImGui.IsKeyPressed(ImGuiKey.R)) { _selectModeOnly = false; _gizmoOp = OPERATION.SCALE; }
            if (ImGui.IsKeyPressed(ImGuiKey.F) && _scene.SelectedObject is { } sel)
                FocusCameraOnObject(sel);
        }

        if (hovered && MathF.Abs(io.MouseWheel) > 0.001f)
        {
            if (_camPreset != CameraPreset.Perspective)
                _orthoWidthMeters = Math.Clamp(_orthoWidthMeters * MathF.Pow(0.88f, io.MouseWheel), 1f, 500f);
            else
                _camSpeed = Math.Clamp(_camSpeed * MathF.Pow(1.15f, io.MouseWheel), 0.5f, 200f);
        }
    }

    private void DrawOutliner()
    {
        if (!ImGui.Begin("Outliner", ref _showOutliner, ImGuiWindowFlags.NoCollapse)) { ImGui.End(); return; }

        Ue52Widgets.SearchBox("##OutSearch", "Search...", ref _outlinerSearch, -70f);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled($"{_scene.RootObjects.Count} actors");

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                                           ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV |
                                           ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("##OutlinerTable", 3, tableFlags, ImGui.GetContentRegionAvail()))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Vis", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 24f);
            ImGui.TableSetupColumn("Item Label", ImGuiTableColumnFlags.WidthStretch, 0.66f);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthStretch, 0.34f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < _scene.RootObjects.Count; i++)
            {
                var root = _scene.RootObjects[i];
                if (!string.IsNullOrEmpty(_outlinerSearch) &&
                    !root.Name.Contains(_outlinerSearch, StringComparison.OrdinalIgnoreCase) &&
                    !root.ClassName.Contains(_outlinerSearch, StringComparison.OrdinalIgnoreCase))
                    continue;

                DrawOutlinerRow(root);
            }

            var suppressEmptyCtx = _openOutlinerItemCtx || ImGui.IsPopupOpen("##OutlinerItemCtx") || ImGui.IsAnyItemHovered();
            if (_openOutlinerItemCtx)
            {
                _openOutlinerItemCtx = false;
                ImGui.OpenPopup("##OutlinerItemCtx");
            }
            _contextMenus.DrawOutlinerItemContextMenu(_menuHost, _outlinerCtxTarget);
            _contextMenus.DrawOutlinerEmptyContextMenu(_menuHost, suppressEmptyCtx);
            ImGui.EndTable();
        }
        _expandAllOutliner = false;
        _collapseAllOutliner = false;
        ImGui.End();
    }

    private void DrawOutlinerRow(SceneObject obj, int depth = 0)
    {
        ImGui.TableNextRow();

        // Col 0: Eye visibility toggle
        ImGui.TableSetColumnIndex(0);
        ImGui.PushID(unchecked((int) obj.RuntimeId));
        if (Ue52Widgets.EyeToggle("##vis", obj.IsVisible))
        {
            _commandHistory.Execute(new VisibilityCommand(
                _scene,
                [(obj, obj.IsVisible, !obj.IsVisible)],
                obj.IsVisible ? $"Hide {obj.Name}" : $"Show {obj.Name}"));
        }
        ImGui.PopID();

        // Col 1: Class icon + Tree label (or inline F2 rename)
        ImGui.TableSetColumnIndex(1);
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanFullWidth;
        if (ReferenceEquals(obj, _scene.SelectedObject)) flags |= ImGuiTreeNodeFlags.Selected;
        if (obj.Children.Count == 0 || depth >= 64) flags |= ImGuiTreeNodeFlags.Leaf;

        if (_expandAllOutliner) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        else if (_collapseAllOutliner) ImGui.SetNextItemOpen(false, ImGuiCond.Always);

        if (ReferenceEquals(_renamingObject, obj))
        {
            if (_focusRenameNextFrame)
            {
                ImGui.SetKeyboardFocusHere();
                _focusRenameNextFrame = false;
            }
            ImGui.SetNextItemWidth(-4f);
            var enter = ImGui.InputText($"##OutRename_{obj.RuntimeId}", ref _renameBuffer, 256, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
            if (enter || (!ImGui.IsItemActive() && ImGui.IsItemDeactivated()))
            {
                var trimmed = _renameBuffer.Trim();
                if (!string.IsNullOrEmpty(trimmed) && !string.Equals(trimmed, obj.Name, StringComparison.Ordinal))
                    _commandHistory.Execute(new RenameObjectCommand(_scene, obj, obj.Name, trimmed));
                _renamingObject = null;
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                _renamingObject = null;
            }
            ImGui.TableSetColumnIndex(2);
            Ue52Widgets.EllipsizedText(obj.ClassName, disabled: true);
            return;
        }

        var iconPtr = Ue52StyleAndIcons.Get(Ue52Widgets.ActorIconKind(obj));
        var open = ImGui.TreeNodeEx($"##Out_{obj.RuntimeId}", flags);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            _scene.Select(obj);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _scene.Select(obj);
            _outlinerCtxTarget = obj;
            _openOutlinerItemCtx = true;
        }
        ImGui.SameLine(0f, 3f);
        if (iconPtr != IntPtr.Zero)
        {
            ImGui.Image(iconPtr, new Vector2(14f, 14f));
            ImGui.SameLine(0f, 4f);
        }
        Ue52Widgets.EllipsizedText(obj.Name, disabled: !obj.IsVisible);

        // Col 2: UE5 Type column
        ImGui.TableSetColumnIndex(2);
        Ue52Widgets.EllipsizedText(obj.ClassName, disabled: true);

        if (open)
        {
            if (depth < 64)
            {
                for (var c = 0; c < obj.Children.Count; c++)
                    DrawOutlinerRow(obj.Children[c], depth + 1);
            }
            ImGui.TreePop();
        }
    }

    private void DrawWorldSettings()
    {
        if (!ImGui.Begin("World Settings", ref _showWorldSettings, ImGuiWindowFlags.NoCollapse)) { ImGui.End(); return; }

        Ue52Widgets.SearchBox("##WSSearch", "Search World Settings", ref _wsSearch, -1f);
        ImGui.Spacing();

        bool Matches(string label) => string.IsNullOrWhiteSpace(_wsSearch) || label.Contains(_wsSearch, StringComparison.OrdinalIgnoreCase);

        var ws = _scene.WorldSettings;
        if (Ue52StyleAndIcons.DetailsCategory("Environment / Sky & TODM"))
        {
            if (Ue52Widgets.BeginPropertyTable("##WorldSettingsProps"))
            {
                if (Matches("Enable Sky / TODM"))
                {
                    var masterOn = ws.SkyEnabled && ws.TodmEnabled;
                    if (Ue52Widgets.CheckboxRow("Enable Sky / TODM", ref masterOn, "Toggle procedural sky, skylight, atmosphere sun, and TODM on/off"))
                    {
                        var before = ws.Clone();
                        ws.SkyEnabled = masterOn;
                        ws.TodmEnabled = masterOn;
                        _commandHistory.RecordExecuted(new WorldSettingsCommand(_scene, before, ws, masterOn ? "Enable Sky / TODM" : "Disable Sky / TODM"));
                        _scene.MarkDirty();
                    }
                }

                if (Matches("Sky Enabled"))
                {
                    var sky = ws.SkyEnabled;
                    if (Ue52Widgets.CheckboxRow("Sky Enabled", ref sky))
                    {
                        var before = ws.Clone();
                        ws.SkyEnabled = sky;
                        _commandHistory.RecordExecuted(new WorldSettingsCommand(_scene, before, ws, "Toggle Sky"));
                        _scene.MarkDirty();
                    }
                }

                if (Matches("Time of Day (TODM)"))
                {
                    var todm = ws.TodmEnabled;
                    if (Ue52Widgets.CheckboxRow("Time of Day (TODM)", ref todm))
                    {
                        var before = ws.Clone();
                        ws.TodmEnabled = todm;
                        _commandHistory.RecordExecuted(new WorldSettingsCommand(_scene, before, ws, "Toggle TODM"));
                        _scene.MarkDirty();
                    }
                }

                if (Matches("Height Fog Enabled"))
                {
                    var fog = ws.FogEnabled;
                    if (Ue52Widgets.CheckboxRow("Height Fog Enabled", ref fog))
                    {
                        var before = ws.Clone();
                        ws.FogEnabled = fog;
                        _commandHistory.RecordExecuted(new WorldSettingsCommand(_scene, before, ws, "Toggle Height Fog"));
                        _scene.MarkDirty();
                    }
                }

                if (Matches("Sun Intensity Multiplier"))
                {
                    var sun = ws.SunIntensityMultiplier;
                    var sunChanged = Ue52Widgets.FloatRowWithReset("Sun Intensity", ref sun, 1f, 0.02f, "%.2f", out var act, out var deact, out var reset, min: 0f, max: 10f);
                    if (act && _wsStartSnapshot is null) _wsStartSnapshot = ws.Clone();
                    if (sunChanged)
                    {
                        if (reset)
                        {
                            var before = ws.Clone();
                            ws.SunIntensityMultiplier = sun;
                            _commandHistory.RecordExecuted(new WorldSettingsCommand(_scene, before, ws, "Reset Sun Intensity"));
                            _wsStartSnapshot = null;
                        }
                        else
                        {
                            ws.SunIntensityMultiplier = Math.Clamp(sun, 0f, 10f);
                        }
                        _scene.MarkDirty();
                    }
                    if (deact && _wsStartSnapshot is { } startWs)
                    {
                        _commandHistory.RecordExecuted(new WorldSettingsCommand(_scene, startWs, ws, "Sun Intensity"));
                        _wsStartSnapshot = null;
                    }
                }

                if (Matches("Sky Intensity Multiplier"))
                {
                    var skyInt = ws.SkyIntensityMultiplier;
                    var skyChanged = Ue52Widgets.FloatRowWithReset("Sky Intensity", ref skyInt, 1f, 0.02f, "%.2f", out var act, out var deact, out var reset, min: 0f, max: 10f);
                    if (act && _wsStartSnapshot is null) _wsStartSnapshot = ws.Clone();
                    if (skyChanged)
                    {
                        if (reset)
                        {
                            var before = ws.Clone();
                            ws.SkyIntensityMultiplier = skyInt;
                            _commandHistory.RecordExecuted(new WorldSettingsCommand(_scene, before, ws, "Reset Sky Intensity"));
                            _wsStartSnapshot = null;
                        }
                        else
                        {
                            ws.SkyIntensityMultiplier = Math.Clamp(skyInt, 0f, 10f);
                        }
                        _scene.MarkDirty();
                    }
                    if (deact && _wsStartSnapshot is { } startWs)
                    {
                        _commandHistory.RecordExecuted(new WorldSettingsCommand(_scene, startWs, ws, "Sky Intensity"));
                        _wsStartSnapshot = null;
                    }
                }

                ImGui.EndTable();
            }

            ImGui.Spacing();
            if (ImGui.Button("Reset Environment Defaults", new Vector2(-1f, 0f)))
            {
                var before = ws.Clone();
                ws.Reset();
                _commandHistory.RecordExecuted(new WorldSettingsCommand(_scene, before, ws, "Reset World Settings"));
                _scene.MarkDirty();
            }
        }

        if (Ue52StyleAndIcons.DetailsCategory("World Info", defaultOpen: false))
        {
            if (Ue52Widgets.BeginPropertyTable("##WorldInfoProps"))
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.Indent(8f); Ue52Widgets.EllipsizedText("World Name"); ImGui.Unindent(8f);
                ImGui.TableSetColumnIndex(1); Ue52Widgets.EllipsizedText(_scene.WorldName, disabled: true);

                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0); ImGui.Indent(8f); Ue52Widgets.EllipsizedText("Loaded Levels"); ImGui.Unindent(8f);
                ImGui.TableSetColumnIndex(1); Ue52Widgets.EllipsizedText($"{_scene.Levels.Count} loaded / {_scene.DiscoveredStreamingLevels.Count} streaming", disabled: true);
                ImGui.EndTable();
            }
        }

        ImGui.End();
    }

    private void DrawDetails()
    {
        if (!ImGui.Begin("Details", ref _showDetails, ImGuiWindowFlags.NoCollapse)) { ImGui.End(); return; }

        if (_scene.SelectedObject is not { } sel)
        {
            ImGui.TextDisabled("Select an object in the Outliner or Viewport to view its details.");
            ImGui.End();
            return;
        }

        var editable = IsEditable(sel);
        var key = sel.RuntimeId.ToString();
        if (!string.Equals(_detailsNameEditKey, key, StringComparison.Ordinal))
        {
            _detailsNameEditKey = key;
            _detailsNameEditBuf = sel.Name;
            _detailsUndoKey = null;
        }

        // SActorDetails header: actor name box + class + Mobility pill, then Search Details
        ImGui.SetNextItemWidth(-1f);
        var nameFlags = editable ? ImGuiInputTextFlags.EnterReturnsTrue : ImGuiInputTextFlags.ReadOnly;
        var nameEnter = ImGui.InputText("##ActorName", ref _detailsNameEditBuf, 256, nameFlags);
        if (editable && (nameEnter || ImGui.IsItemDeactivatedAfterEdit()))
        {
            var trimmed = _detailsNameEditBuf.Trim();
            if (!string.IsNullOrEmpty(trimmed) && !string.Equals(trimmed, sel.Name, StringComparison.Ordinal))
                _commandHistory.Execute(new RenameObjectCommand(_scene, sel, sel.Name, trimmed));
            else
                _detailsNameEditBuf = sel.Name;
        }

        ImGui.Spacing();
        Ue52Widgets.EllipsizedText(sel.ClassName, disabled: true);

        // UE5 Mobility indicator strip
        ImGui.PushStyleColor(ImGuiCol.Button, Ue52StyleAndIcons.StyleColors.Header);
        ImGui.SmallButton("Static");
        ImGui.SameLine(0f, 2f);
        ImGui.SmallButton("Stationary");
        ImGui.SameLine(0f, 2f);
        ImGui.PopStyleColor();
        ImGui.PushStyleColor(ImGuiCol.Button, Ue52StyleAndIcons.StyleColors.SelectInactive);
        ImGui.SmallButton("Movable");
        ImGui.PopStyleColor();

        ImGui.Spacing();
        Ue52Widgets.SearchBox("##DetailsSearch", "Search Details", ref _detailsSearch, -1f);
        ImGui.Spacing();

        bool Matches(string label) => string.IsNullOrEmpty(_detailsSearch) || label.Contains(_detailsSearch, StringComparison.OrdinalIgnoreCase);

        if (Ue52StyleAndIcons.DetailsCategory("Transform"))
        {
            if (Ue52Widgets.BeginPropertyTable("##TransformProps"))
            {
                if (Matches("Location"))
                {
                    var loc = sel.LocationCm;
                    var changed = Ue52Widgets.VectorRowWithReset("Location", ref loc, Vector3.Zero, 1f, "%.1f", out var act, out var deact, out var reset);
                    if (act) BeginTransformUndo(sel, "loc");
                    if (changed)
                    {
                        if (reset)
                        {
                            var before = TransformSnapshot.Capture(sel);
                            sel.LocationCm = loc; sel.OverrideLocalMatrix = null;
                            _commandHistory.RecordExecuted(new TransformCommand(_scene, sel, before, TransformSnapshot.Capture(sel), "Reset Location"));
                            _detailsUndoKey = null;
                        }
                        else
                        {
                            sel.LocationCm = loc; sel.OverrideLocalMatrix = null;
                        }
                        _scene.MarkDirty();
                    }
                    FinishTransformUndo(sel, "loc", deact);
                }

                if (Matches("Rotation"))
                {
                    var rot = sel.RotationDeg;
                    var changed = Ue52Widgets.VectorRowWithReset("Rotation", ref rot, Vector3.Zero, 0.5f, "%.1f\u00b0", out var act, out var deact, out var reset, roll: true);
                    if (act) BeginTransformUndo(sel, "rot");
                    if (changed)
                    {
                        if (reset)
                        {
                            var before = TransformSnapshot.Capture(sel);
                            sel.SetRotationDeg(rot); sel.OverrideLocalMatrix = null;
                            _commandHistory.RecordExecuted(new TransformCommand(_scene, sel, before, TransformSnapshot.Capture(sel), "Reset Rotation"));
                            _detailsUndoKey = null;
                        }
                        else
                        {
                            sel.SetRotationDeg(rot); sel.OverrideLocalMatrix = null;
                        }
                        _scene.MarkDirty();
                    }
                    FinishTransformUndo(sel, "rot", deact);
                }

                if (Matches("Scale"))
                {
                    var scl = sel.Scale;
                    var changed = Ue52Widgets.VectorRowWithReset("Scale", ref scl, Vector3.One, 0.02f, "%.4f", out var act, out var deact, out var reset);
                    if (act) BeginTransformUndo(sel, "scl");
                    if (changed)
                    {
                        if (reset)
                        {
                            var before = TransformSnapshot.Capture(sel);
                            sel.Scale = scl; sel.OverrideLocalMatrix = null;
                            _commandHistory.RecordExecuted(new TransformCommand(_scene, sel, before, TransformSnapshot.Capture(sel), "Reset Scale"));
                            _detailsUndoKey = null;
                        }
                        else
                        {
                            sel.Scale = scl; sel.OverrideLocalMatrix = null;
                        }
                        _scene.MarkDirty();
                    }
                    FinishTransformUndo(sel, "scl", deact);
                }
                ImGui.EndTable();
            }
        }

        var targetMesh = sel as SceneMesh ?? sel.Children.OfType<SceneMesh>().FirstOrDefault();
        if (targetMesh is not null && _mountService.Provider is { } prov)
        {
            var canUseSel = !string.IsNullOrEmpty(_contentBrowser.SelectedAssetPath);
            if (Matches("Static Mesh") && Ue52StyleAndIcons.DetailsCategory("Static Mesh"))
            {
                if (Ue52Widgets.BeginPropertyTable("##StaticMeshProps"))
                {
                    var smThumb = _contentBrowser.GetThumbnailOrIcon(targetMesh.Mesh.Owner?.Name, Ue52IconKind.StaticMesh);
                    if (Ue52Widgets.AssetReferenceRow("Static Mesh", targetMesh.Mesh.Name, smThumb, canUseSel, canReset: false,
                            out var assignedSmPath, out var openSm, out _))
                    {
                        var pathToLoad = assignedSmPath ?? _contentBrowser.SelectedAssetPath;
                        if (!string.IsNullOrEmpty(pathToLoad) && LoadPrimaryAssetFromPackage(prov, pathToLoad) is UStaticMesh newSm)
                        {
                            var oldMesh = targetMesh.Mesh;
                            var oldMats = targetMesh.Materials.ToArray();
                            var newMats = WorldLoader.ResolveMaterialSlots(targetMesh.SourceObject ?? newSm, newSm);
                            _commandHistory.Execute(new StaticMeshReplaceCommand(_scene, targetMesh, oldMesh, oldMats, newSm, newMats, $"Set StaticMesh {newSm.Name}"));
                            AddLog($"Assigned StaticMesh '{newSm.Name}' to {targetMesh.Name}.");
                        }
                    }
                    if (openSm)
                        _assetPreview.Open(targetMesh.Mesh, prov);
                    ImGui.EndTable();
                }
            }

            if ((Matches("Materials") || Matches("Element")) && Ue52StyleAndIcons.DetailsCategory("Materials"))
            {
                if (Ue52Widgets.BeginPropertyTable("##MaterialSlotsProps"))
                {
                    var slotCount = Math.Max(1, targetMesh.Materials.Length);
                    for (var s = 0; s < slotCount; s++)
                    {
                        var curMat = s < targetMesh.Materials.Length ? targetMesh.Materials[s] : null;
                        var matName = curMat?.Name ?? "None";
                        var pkgName = curMat?.CookedMaterial?.Owner?.Name;
                        var matIconKind = matName.StartsWith("MI_", StringComparison.OrdinalIgnoreCase)
                            ? Ue52IconKind.MaterialInstance
                            : Ue52IconKind.Material;
                        var matThumb = _contentBrowser.GetThumbnailOrIcon(pkgName, matIconKind);
                        if (Ue52Widgets.AssetReferenceRow($"Element {s}", matName, matThumb, canUseSel, canReset: true,
                                out var assignedMatPath, out var openMat, out var resetMat))
                        {
                            var pathToLoad = assignedMatPath ?? _contentBrowser.SelectedAssetPath;
                            if (!string.IsNullOrEmpty(pathToLoad) && LoadPrimaryAssetFromPackage(prov, pathToLoad) is UUnrealMaterial newUMat)
                            {
                                var oldMats = targetMesh.Materials.ToArray();
                                var newMats = targetMesh.Materials.ToArray();
                                if (s >= newMats.Length)
                                    Array.Resize(ref newMats, s + 1);
                                newMats[s] = new MaterialAsset(newUMat);
                                _commandHistory.Execute(new MaterialSlotCommand(_scene, targetMesh, oldMats, newMats, $"Assign Material {newUMat.Name}"));
                                AddLog($"Assigned Material '{newUMat.Name}' to {targetMesh.Name} [Element {s}].");
                            }
                        }
                        if (openMat && curMat?.CookedMaterial is { } cookedMat)
                            _assetPreview.Open(cookedMat, prov);
                        if (resetMat && s < targetMesh.Materials.Length)
                        {
                            var oldMats = targetMesh.Materials.ToArray();
                            var newMats = targetMesh.Materials.ToArray();
                            var baseMats = targetMesh.Mesh.Materials ?? [];
                            newMats[s] = s < baseMats.Length && baseMats[s] is { IsNull: false } bIdx && bIdx.TryLoad<UUnrealMaterial>(out var defMat)
                                ? new MaterialAsset(defMat)
                                : null;
                            _commandHistory.Execute(new MaterialSlotCommand(_scene, targetMesh, oldMats, newMats, $"Reset Material Slot {s}"));
                        }
                    }
                    ImGui.EndTable();
                }
            }
        }

        if (sel is SceneLight light && Ue52StyleAndIcons.DetailsCategory("Light"))
        {
            if (Ue52Widgets.BeginPropertyTable("##LightProps"))
            {
                if (Matches("Intensity"))
                {
                    var inten = light.Intensity;
                    var changed = Ue52Widgets.FloatRowWithReset("Intensity", ref inten, 1f, 0.1f, "%.3f", out var act, out var deact, out var reset, min: 0f);
                    if (act) BeginLightUndo(light, "lint");
                    if (changed)
                    {
                        if (reset)
                        {
                            var before = LightSnapshot.Capture(light);
                            light.Intensity = inten;
                            _commandHistory.RecordExecuted(new LightPropertyCommand(_scene, light, before, LightSnapshot.Capture(light), "Reset Intensity"));
                            _detailsUndoKey = null;
                        }
                        else light.Intensity = MathF.Max(0f, inten);
                        _scene.MarkDirty();
                    }
                    FinishLightUndo(light, "lint", deact);
                }

                if (Matches("Light Color"))
                {
                    var col = light.Color;
                    var changed = Ue52Widgets.ColorRowWithReset("Light Color", ref col, Vector3.One, out var act, out var deact, out var reset);
                    if (act) BeginLightUndo(light, "lcol");
                    if (changed)
                    {
                        if (reset)
                        {
                            var before = LightSnapshot.Capture(light);
                            light.Color = col;
                            _commandHistory.RecordExecuted(new LightPropertyCommand(_scene, light, before, LightSnapshot.Capture(light), "Reset Light Color"));
                            _detailsUndoKey = null;
                        }
                        else light.Color = col;
                        _scene.MarkDirty();
                    }
                    FinishLightUndo(light, "lcol", deact);
                }

                if (Matches("Cast Shadows"))
                {
                    var cs = light.CastShadows;
                    if (Ue52Widgets.CheckboxRow("Cast Shadows", ref cs))
                    {
                        var before = LightSnapshot.Capture(light);
                        light.CastShadows = cs;
                        _commandHistory.RecordExecuted(new LightPropertyCommand(_scene, light, before, LightSnapshot.Capture(light), "Toggle Cast Shadows"));
                        _scene.MarkDirty();
                    }
                }

                if (light is LocalLightComponent local && Matches("Attenuation Radius"))
                {
                    var rad = local.AttenuationRadius;
                    var changed = Ue52Widgets.FloatRowWithReset("Attenuation Radius", ref rad, 1000f, 5f, "%.1f cm", out var act, out var deact, out var reset, min: 10f);
                    if (act) BeginLightUndo(light, "lrad");
                    if (changed)
                    {
                        if (reset)
                        {
                            var before = LightSnapshot.Capture(light);
                            local.AttenuationRadius = rad;
                            _commandHistory.RecordExecuted(new LightPropertyCommand(_scene, light, before, LightSnapshot.Capture(light), "Reset Attenuation Radius"));
                            _detailsUndoKey = null;
                        }
                        else local.AttenuationRadius = MathF.Max(10f, rad);
                        _scene.MarkDirty();
                    }
                    FinishLightUndo(light, "lrad", deact);
                }

                if (light is SceneSpotLight spot)
                {
                    if (Matches("Inner Cone Angle"))
                    {
                        var inner = spot.InnerConeAngle;
                        var changed = Ue52Widgets.FloatRowWithReset("Inner Cone Angle", ref inner, 22f, 0.25f, "%.1f\u00b0", out var act, out var deact, out var reset, min: 0.1f, max: 89f);
                        if (act) BeginLightUndo(light, "linner");
                        if (changed)
                        {
                            if (reset)
                            {
                                var before = LightSnapshot.Capture(light);
                                spot.InnerConeAngle = inner;
                                _commandHistory.RecordExecuted(new LightPropertyCommand(_scene, light, before, LightSnapshot.Capture(light), "Reset Inner Cone"));
                                _detailsUndoKey = null;
                            }
                            else spot.InnerConeAngle = Math.Clamp(inner, 0.1f, 89f);
                            _scene.MarkDirty();
                        }
                        FinishLightUndo(light, "linner", deact);
                    }
                    if (Matches("Outer Cone Angle"))
                    {
                        var outer = spot.OuterConeAngle;
                        var changed = Ue52Widgets.FloatRowWithReset("Outer Cone Angle", ref outer, 44f, 0.25f, "%.1f\u00b0", out var act, out var deact, out var reset, min: 0.1f, max: 89f);
                        if (act) BeginLightUndo(light, "louter");
                        if (changed)
                        {
                            if (reset)
                            {
                                var before = LightSnapshot.Capture(light);
                                spot.OuterConeAngle = outer;
                                _commandHistory.RecordExecuted(new LightPropertyCommand(_scene, light, before, LightSnapshot.Capture(light), "Reset Outer Cone"));
                                _detailsUndoKey = null;
                            }
                            else spot.OuterConeAngle = Math.Clamp(outer, 0.1f, 89f);
                            _scene.MarkDirty();
                        }
                        FinishLightUndo(light, "louter", deact);
                    }
                }
                ImGui.EndTable();
            }
        }

        if (sel.SourceObject is { } uo && Ue52StyleAndIcons.DetailsCategory($"Properties ({uo.Properties.Count})", defaultOpen: false))
        {
            const ImGuiTableFlags propFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                                              ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.PadOuterX;
            if (ImGui.BeginTable("##DetailsProps", 2, propFlags, new Vector2(0f, Math.Max(160f, ImGui.GetContentRegionAvail().Y))))
            {
                ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthStretch, 0.42f);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.58f);
                foreach (var p in uo.Properties)
                {
                    if (!Matches(p.Name.Text)) continue;
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    Ue52Widgets.EllipsizedText(p.Name.Text);
                    ImGui.TableSetColumnIndex(1);
                    Ue52Widgets.EllipsizedText(p.Tag?.GenericValue?.ToString() ?? string.Empty, disabled: true);
                }
                ImGui.EndTable();
            }
        }

        ImGui.End();
    }

    private void BeginTransformUndo(SceneObject sel, string tag)
    {
        var k = $"{sel.RuntimeId}_{tag}";
        if (_detailsUndoKey != k)
        {
            _detailsUndoKey = k;
            _detailsStartTransform = TransformSnapshot.Capture(sel);
        }
    }

    private void FinishTransformUndo(SceneObject sel, string tag, bool deactivatedAfterEdit)
    {
        var k = $"{sel.RuntimeId}_{tag}";
        if (deactivatedAfterEdit && _detailsUndoKey == k)
        {
            var endSnap = TransformSnapshot.Capture(sel);
            if (endSnap != _detailsStartTransform)
                _commandHistory.RecordExecuted(new TransformCommand(_scene, sel, _detailsStartTransform, endSnap, "Details Transform"));
            _detailsUndoKey = null;
        }
    }

    private void BeginLightUndo(SceneLight light, string tag)
    {
        var k = $"{light.RuntimeId}_{tag}";
        if (_detailsUndoKey != k)
        {
            _detailsUndoKey = k;
            _detailsStartLight = LightSnapshot.Capture(light);
        }
    }

    private void FinishLightUndo(SceneLight light, string tag, bool deactivatedAfterEdit)
    {
        var k = $"{light.RuntimeId}_{tag}";
        if (deactivatedAfterEdit && _detailsUndoKey == k)
        {
            var endSnap = LightSnapshot.Capture(light);
            if (endSnap != _detailsStartLight)
                _commandHistory.RecordExecuted(new LightPropertyCommand(_scene, light, _detailsStartLight, endSnap, "Edit Light"));
            _detailsUndoKey = null;
        }
    }

    private void FocusCameraOnObject(SceneObject obj)
    {
        var posM = obj.WorldMatrix.Translation;
        _camPos = posM + new Vector3(0f, 2f, 6f);
    }

    private void FocusCameraOnScene(WorldScene scene)
    {
        if (scene.Meshes.Count == 0) return;

        var sum = Vector3.Zero;
        var count = 0;
        var minP = new Vector3(float.MaxValue);
        var maxP = new Vector3(float.MinValue);
        foreach (var m in scene.Meshes)
        {
            if (m.IsHlod) continue;
            var p = m.WorldMatrix.Translation;
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) continue;
            sum += p;
            minP = Vector3.Min(minP, p);
            maxP = Vector3.Max(maxP, p);
            count++;
        }

        if (count == 0) return;
        var center = sum / count;
        var extent = Math.Clamp((maxP - minP).Length() * 0.35f, 8f, 180f);
        _camPos = center + new Vector3(0f, extent * 0.45f, extent);
        _camYaw = 0f;
        _camPitch = -0.35f;
    }

    private void DrawContentDrawer(ImGuiViewportPtr vp, float statusH)
    {
        var drawerW = Math.Clamp(vp.Size.X * 0.72f, 680f, Math.Max(680f, vp.Size.X - 24f));
        var drawerH = Math.Clamp(vp.Size.Y * 0.42f, 280f, Math.Max(280f, vp.Size.Y - 120f));
        var drawerPos = new Vector2(vp.Pos.X + 8f, vp.Pos.Y + vp.Size.Y - statusH - drawerH - 2f);

        ImGui.SetNextWindowPos(drawerPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(drawerW, drawerH), ImGuiCond.Always);
        if (_focusDrawerOnOpen)
        {
            ImGui.SetNextWindowFocus();
            _focusDrawerOnOpen = false;
        }

        ImGui.PushStyleColor(ImGuiCol.WindowBg, Ue52StyleAndIcons.StyleColors.Panel);
        ImGui.PushStyleColor(ImGuiCol.Border, Ue52StyleAndIcons.StyleColors.DropdownOutline);
        const ImGuiWindowFlags drawerFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                                             ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking;
        if (ImGui.Begin("##Ue52ContentDrawer", ref _showContentDrawer, drawerFlags))
        {
            var drawerMin = ImGui.GetWindowPos();
            var drawerMax = drawerMin + ImGui.GetWindowSize();

            _contentBrowser.IsDrawerMode = true;
            _contentBrowser.DockInLayoutRequested = false;

            if (_mountService.IsMounting)
            {
                ImGui.TextColored(Ue52StyleAndIcons.StyleColors.AccentBlue, _mountStage ?? _mountService.Status);
            }
            else if (!_mountService.IsMounted || _mountService.Provider is not { } prov)
            {
                ImGui.TextDisabled("No game mounted. Open Settings -> Project Settings to configure Game Directory, AES Keys & Mappings.");
            }
            else
            {
                _contentBrowser.Draw(prov, world =>
                {
                    _showContentDrawer = false;
                    StartLoadWorld(prov, world);
                }, _assetPreview, pauseThumbnails: _loadingWorld);
            }

            if (_contentBrowser.DockInLayoutRequested)
            {
                _contentBrowser.DockInLayoutRequested = false;
                _closeDrawerAfterDrag = false;
                _showContentDrawer = false;
                _showContentBrowser = true;
                if (_layoutBuilt)
                    DockHelper.DockBottomWindow(_dockSpaceId, "Content Browser");
            }
            else
            {
                var mPos = ImGui.GetMousePos();
                var inDrawer = mPos.X >= drawerMin.X && mPos.X <= drawerMax.X && mPos.Y >= drawerMin.Y && mPos.Y <= drawerMax.Y;
                var inToggleBtn = mPos.X >= _drawerToggleBtnMin.X && mPos.X <= _drawerToggleBtnMax.X && mPos.Y >= _drawerToggleBtnMin.Y && mPos.Y <= _drawerToggleBtnMax.Y;

                bool hasDragPayload;
                unsafe { hasDragPayload = ImGui.GetDragDropPayload().NativePtr != null; }
                var isActiveDrag = hasDragPayload || (_contentBrowser.IsDraggingAsset && ImGui.IsMouseDown(ImGuiMouseButton.Left));

                // Keep Content Drawer alive until drag-drop payload delivery completes; close after release outside drawer
                if (isActiveDrag)
                {
                    _closeDrawerAfterDrag = !inDrawer;
                }
                else if (_closeDrawerAfterDrag)
                {
                    _closeDrawerAfterDrag = false;
                    if (!inDrawer)
                        _showContentDrawer = false;
                }
                else if (!_contentBrowser.IsAnyContextMenuOpen && !ImGui.IsPopupOpen(string.Empty, ImGuiPopupFlags.AnyPopupId) &&
                         (ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.IsMouseClicked(ImGuiMouseButton.Right)) &&
                         !inDrawer && !inToggleBtn)
                {
                    _showContentDrawer = false;
                }
            }
        }
        ImGui.End();
        ImGui.PopStyleColor(2);
    }

    private void DrawContentBrowser()
    {
        if (!ImGui.Begin("Content Browser", ref _showContentBrowser, ImGuiWindowFlags.NoCollapse)) { ImGui.End(); return; }

        _contentBrowser.IsDrawerMode = false;

        if (_mountService.IsMounting)
        {
            ImGui.TextColored(Ue52StyleAndIcons.StyleColors.AccentBlue, _mountStage ?? _mountService.Status);
            ImGui.End();
            return;
        }

        if (!_mountService.IsMounted || _mountService.Provider is not { } prov)
        {
            ImGui.TextDisabled("No game mounted. Open Settings -> Project Settings to configure Game Directory, AES Keys & Mappings.");
            ImGui.End();
            return;
        }

        _contentBrowser.Draw(prov, world => StartLoadWorld(prov, world), _assetPreview, pauseThumbnails: _loadingWorld);
        ImGui.End();
    }

    private void StartLoadWorld(IFileProvider prov, UWorld world)
    {
        if (_loadingWorld) return;
        _loadingWorld = true;
        _loadingWorldName = world.Name;
        _focusViewportNextFrame = true;
        Interlocked.Increment(ref _worldGeneration);
        var mountGen = Volatile.Read(ref _mountGeneration);
        _ = Task.Run(() =>
        {
            try
            {
                var staged = new WorldScene();
                WorldLoader.LoadWorld(world, staged, prov, AddLog);
                if (Volatile.Read(ref _mountGeneration) == mountGen && !_disposed)
                    Interlocked.Exchange(ref _pendingScene, staged);
            }
            catch (Exception ex)
            {
                AddLog($"Failed to load world {world.Name}: {ex.Message}");
            }
            finally
            {
                _loadingWorld = false;
            }
        });
    }

    private void DrawOutputLog()
    {
        if (!ImGui.Begin("Output Log", ref _showOutputLog, ImGuiWindowFlags.NoCollapse)) { ImGui.End(); return; }

        if (ImGui.SmallButton("Clear")) lock (_logLock) _logs.Clear();
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy All"))
        {
            string allText;
            lock (_logLock) allText = string.Join(Environment.NewLine, _logs);
            ImGui.SetClipboardText(allText);
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(Click any line to copy)");
        ImGui.Separator();
        ImGui.BeginChild("##LogScroll", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
        lock (_logLock)
        {
            for (var i = 0; i < _logs.Count; i++)
            {
                var line = _logs[i];
                ImGui.PushID(i);
                if (ImGui.Selectable(line, false, ImGuiSelectableFlags.AllowDoubleClick))
                    ImGui.SetClipboardText(line);
                if (ImGui.BeginPopupContextItem("##LogLineCtx"))
                {
                    if (ImGui.MenuItem("Copy Line"))
                        ImGui.SetClipboardText(line);
                    if (ImGui.MenuItem("Copy All Logs"))
                        ImGui.SetClipboardText(string.Join(Environment.NewLine, _logs));
                    ImGui.EndPopup();
                }
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        ImGui.End();
    }

    private void DrawHelpModal()
    {
        if (_showHelpModal)
        {
            _showHelpModal = false;
            ImGui.OpenPopup("##UExHelpModal");
        }
        var open = true;
        ImGui.SetNextWindowSize(new Vector2(460f, 340f), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("##UExHelpModal", ref open, ImGuiWindowFlags.None))
        {
            Ue52StyleAndIcons.PushBoldFont();
            ImGui.TextUnformatted("UExplorer — UE 5.2 Editor Controls");
            Ue52StyleAndIcons.PopFontSafe(Ue52StyleAndIcons.BoldFont);
            ImGui.Separator();
            ImGui.TextWrapped(
                "Viewport Camera:\n" +
                "  RMB Drag + WASD/QE : Orbit & Fly (Shift = 3x speed)\n" +
                "  Mouse Wheel        : Adjust Camera Speed\n" +
                "  LMB Click          : Select Actor (Alt+Click = Component)\n" +
                "  RMB Click (< 4px)  : Open Context Menu\n\n" +
                "Shortcuts:\n" +
                "  Ctrl+Space         : Toggle Content Drawer\n" +
                "  W / E / R          : Translate / Rotate / Scale Gizmo\n" +
                "  F / End / H / G    : Focus / Snap to Floor / Hide / Game View\n" +
                "  Ctrl+Z / Ctrl+Y    : Undo / Redo\n" +
                "  Ctrl+X / C / V / D : Cut / Copy / Paste / Duplicate\n" +
                "  Delete / F2        : Delete / Rename in Outliner");
            ImGui.Spacing();
            if (Ue52StyleAndIcons.PrimaryButton("Close", new Vector2(90f, 0f)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    // -------------------------------------------------------------------------
    // Editing Actions & Helpers
    // -------------------------------------------------------------------------

    private SceneLevel GetActiveSpawnLevel()
    {
        if (_activeSpawnLevel is not null && _scene.Levels.Contains(_activeSpawnLevel) && _activeSpawnLevel.IsLoaded)
            return _activeSpawnLevel;
        _activeSpawnLevel = _scene.GetOrCreatePersistentLevel();
        return _activeSpawnLevel;
    }

    private static bool IsEditable(SceneObject obj) =>
        !string.Equals(obj.ClassName, "LevelStreaming", StringComparison.Ordinal);

    private void BeginOutlinerRename(SceneObject obj)
    {
        if (!IsEditable(obj)) return;
        _renamingObject = obj;
        _renameBuffer = obj.Name;
        _focusRenameNextFrame = true;
    }

    private void CopySelected()
    {
        if (_scene.SelectedObject is not { } sel || !IsEditable(sel)) return;
        _clipboard.Copy([sel]);
        AddLog($"Copied '{sel.Name}'.");
    }

    private void CutSelected()
    {
        if (_scene.SelectedObject is not { } sel || !IsEditable(sel)) return;
        _clipboard.Copy([sel]);
        _commandHistory.Execute(new DeleteObjectsCommand(_scene, [sel], $"Cut {sel.Name}"));
        AddLog($"Cut '{sel.Name}'.");
    }

    private void DeleteSelected()
    {
        if (_scene.SelectedObject is not { } sel || !IsEditable(sel)) return;
        _commandHistory.Execute(new DeleteObjectsCommand(_scene, [sel], $"Delete {sel.Name}"));
        AddLog($"Deleted '{sel.Name}'.");
    }

    private void DuplicateSelected()
    {
        if (_scene.SelectedObject is not { } sel || !IsEditable(sel)) return;
        var level = _scene.FindOwningLevel(sel) ?? GetActiveSpawnLevel();
        var clone = sel.DeepClone();
        clone.Name = MakeUniqueName(level, sel.Name);
        var parentSpace = sel.Parent is not null ? sel.ParentSpaceMatrix : (level.LevelRoot?.WorldMatrix ?? Matrix4x4.Identity);
        ApplyWorldDeltaCm(clone, new Vector3(25f, 25f, 0f), parentSpace);
        _commandHistory.Execute(new SpawnObjectsCommand(_scene, level, [clone], sel.Parent, $"Duplicate {sel.Name}"));
        AddLog($"Duplicated '{sel.Name}' as '{clone.Name}'.");
    }

    private void PasteAt(Vector3? targetWorldPosCm)
    {
        if (!_clipboard.HasItems) return;
        var level = GetActiveSpawnLevel();
        var clones = new List<SceneObject>(_clipboard.Items.Count);
        var offset = targetWorldPosCm is { } dst ? dst - _clipboard.SourceAnchorCm : new Vector3(25f, 25f, 0f);
        var targetParentSpace = level.LevelRoot?.WorldMatrix ?? Matrix4x4.Identity;

        foreach (var item in _clipboard.Items)
        {
            var c = item.DeepClone();
            c.Name = MakeUniqueName(level, c.Name);
            ApplyWorldDeltaCm(c, offset, targetParentSpace);
            clones.Add(c);
        }
        _commandHistory.Execute(new SpawnObjectsCommand(_scene, level, clones, description: "Paste"));
        AddLog($"Pasted {clones.Count} actor(s) into '{level.Name}'.");
    }

    private static void ApplyWorldDeltaCm(SceneObject obj, Vector3 worldDeltaCm, Matrix4x4 parentSpace)
    {
        var worldDeltaMeters = new Vector3(worldDeltaCm.X, worldDeltaCm.Z, worldDeltaCm.Y) * 0.01f;
        var localDeltaMeters = worldDeltaMeters;
        var localDeltaCm = worldDeltaCm;

        if (!obj.AbsLocation && parentSpace != Matrix4x4.Identity && Matrix4x4.Invert(parentSpace, out var invParent))
        {
            localDeltaMeters = Vector3.TransformNormal(worldDeltaMeters, invParent);
            localDeltaCm = new Vector3(localDeltaMeters.X, localDeltaMeters.Z, localDeltaMeters.Y) * 100f;
        }

        if (obj.OverrideLocalMatrix is { } ovr)
        {
            ovr.Translation += localDeltaMeters;
            obj.OverrideLocalMatrix = ovr;
        }
        else
        {
            obj.LocationCm += localDeltaCm;
        }
    }

    private void PlaceLightActor(string lightKind, Vector3? spawnPosCm)
    {
        var level = GetActiveSpawnLevel();
        var fwd = GetCameraForward();
        var camUn = new Vector3(_camPos.X, _camPos.Z, _camPos.Y) * 100f;
        var fwdUn = new Vector3(fwd.X, fwd.Z, fwd.Y);
        var locCm = spawnPosCm ?? (camUn + (fwdUn.LengthSquared() > 1e-6f ? Vector3.Normalize(fwdUn) : Vector3.UnitX) * 350f);

        SceneLight node = lightKind switch
        {
            "SpotLight" => new SceneSpotLight { ClassName = "SpotLightComponent", Intensity = 5000f, AttenuationRadius = 1000f },
            "DirectionalLight" => new SceneDirectionalLight { ClassName = "DirectionalLightComponent", Intensity = 3.14f, RotationDeg = new Vector3(-45f, 35f, 0f) },
            "RectLight" => new SceneRectLight { ClassName = "RectLightComponent", Intensity = 5000f, AttenuationRadius = 1000f },
            "SkyLight" => new SceneSkyLight { ClassName = "SkyLightComponent", Intensity = 1.0f },
            _ => new ScenePointLight { ClassName = "PointLightComponent", Intensity = 5000f, AttenuationRadius = 1000f }
        };
        if (node is SceneDirectionalLight dl)
            dl.SetRotationDeg(dl.RotationDeg);
        node.Name = MakeUniqueName(level, lightKind);
        node.LocationCm = locCm;
        _commandHistory.Execute(new SpawnObjectsCommand(_scene, level, [node], description: $"Place {lightKind}"));
        AddLog($"Placed {lightKind} '{node.Name}'.");
    }

    private void PlacePrimitiveShape(string shapeName, Vector3? spawnPosCm)
    {
        if (_mountService.Provider is not { } prov)
        {
            AddLog("Mount a game first to place BasicShapes.");
            return;
        }
        var pkgPath = $"Engine/Content/BasicShapes/{shapeName}.{shapeName}";
        if (!prov.TryLoadPackageObject<UStaticMesh>(pkgPath, out var sm))
        {
            AddLog($"BasicShape '{pkgPath}' not found in mounted game.");
            return;
        }
        var level = GetActiveSpawnLevel();
        var fwd = GetCameraForward();
        var camUn = new Vector3(_camPos.X, _camPos.Z, _camPos.Y) * 100f;
        var fwdUn = new Vector3(fwd.X, fwd.Z, fwd.Y);
        var locCm = spawnPosCm ?? (camUn + (fwdUn.LengthSquared() > 1e-6f ? Vector3.Normalize(fwdUn) : Vector3.UnitX) * 350f);

        var node = new SceneMesh
        {
            Name = MakeUniqueName(level, shapeName),
            ClassName = "StaticMeshActor",
            Path = sm.GetPathName(),
            SourceObject = sm,
            LocationCm = locCm,
            Mesh = sm,
            Materials = WorldLoader.ResolveMaterialSlots(sm, sm)
        };
        _commandHistory.Execute(new SpawnObjectsCommand(_scene, level, [node], description: $"Place {shapeName}"));
        AddLog($"Placed {shapeName} '{node.Name}'.");
    }

    private void SnapObjectToFloor(SceneObject obj)
    {
        var before = TransformSnapshot.Capture(obj);
        if (SnapObjectToFloorInternal(obj, excludeSelected: true))
        {
            var after = TransformSnapshot.Capture(obj);
            if (after != before)
                _commandHistory.RecordExecuted(new TransformCommand(_scene, obj, before, after, "Snap to Floor"));
            _scene.MarkDirty();
        }
    }

    private int _vfxOcclusionFrame;
    private readonly Dictionary<SceneNiagara, (int Frame, Vector3 CamPos, bool Occluded)> _vfxOcclusionCache = new(ReferenceEqualityComparer.Instance);

    private bool IsVfxOccludedByWalls(SceneNiagara sn, Vector3 camUn, Vector3 vfxPosCm, float distCm)
    {
        if (_vfxOcclusionCache.TryGetValue(sn, out var cached) &&
            _vfxOcclusionFrame - cached.Frame < 6 &&
            Vector3.DistanceSquared(cached.CamPos, camUn) < 2500f)
        {
            return cached.Occluded;
        }

        // Probe two vertical points on the VFX volume (+45cm and +170cm) so floor contact doesn't false-occlude
        var occluded = IsSegmentBlockedByOpaqueWall(camUn, vfxPosCm + new Vector3(0f, 0f, 45f), distCm - 120f) &&
                       IsSegmentBlockedByOpaqueWall(camUn, vfxPosCm + new Vector3(0f, 0f, 170f), distCm - 120f);
        _vfxOcclusionCache[sn] = (_vfxOcclusionFrame, camUn, occluded);
        return occluded;
    }

    private bool IsSegmentBlockedByOpaqueWall(Vector3 originCm, Vector3 targetCm, float maxHitDistCm)
    {
        if (maxHitDistCm <= 150f) return false;
        var delta = targetCm - originCm;
        var len = delta.Length();
        if (len <= 150f) return false;
        var dir = delta / len;

        foreach (var sm in _scene.Meshes)
        {
            if (sm.IsHlod || !sm.IsVisibleRecursive || sm.InstanceLocalMatrices is { Count: > 0 }) continue;
            if (sm.Materials.Length > 0 && sm.Materials[0] is { } mat0 && mat0.BlendMode != CUE4Parse.UE4.Assets.Exports.Material.EBlendMode.BLEND_Opaque)
                continue;
            if ((!_retainedSceneCache.TryGetCachedMesh(sm.Mesh, out var lods) && !_frameMeshCache.TryGetValue(sm.Mesh, out lods)) || lods is null || lods.Lods.Length == 0)
                continue;
            var mesh = lods.Lods[0];
            if (mesh.BoundsRadius < 120f) continue; // ignore small props

            var l2w = UnrealSpace.ToUnrealLocalToWorld(sm.WorldMatrix);
            var scale = MathF.Max(new Vector3(l2w.M11, l2w.M12, l2w.M13).Length(),
                MathF.Max(new Vector3(l2w.M21, l2w.M22, l2w.M23).Length(), new Vector3(l2w.M31, l2w.M32, l2w.M33).Length()));
            var center = Vector3.Transform(mesh.BoundsCenter, l2w);
            if (!RaySphere(originCm, dir, center, mesh.BoundsRadius * scale, out var sphereT) || sphereT > maxHitDistCm)
                continue;
            if (!Matrix4x4.Invert(l2w, out var w2l)) continue;

            var lo = Vector3.Transform(originCm, w2l);
            var ld = Vector3.TransformNormal(dir, w2l);
            var pos = mesh.CpuPositions;
            var idx = mesh.CpuIndices;
            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                if (idx[i] >= pos.Length || idx[i + 1] >= pos.Length || idx[i + 2] >= pos.Length) continue;
                if (!RayTriangle(lo, ld, pos[idx[i]], pos[idx[i + 1]], pos[idx[i + 2]], out var t) || t <= 25f) continue;
                var wh = Vector3.Transform(lo + ld * t, l2w);
                var hitDist = Vector3.Distance(originCm, wh);
                if (hitDist > 30f && hitDist < maxHitDistCm)
                    return true;
            }
        }
        return false;
    }

    private bool SnapObjectToFloorInternal(SceneObject obj, bool excludeSelected)
    {
        var worldPosM = obj.WorldMatrix.Translation;
        var rayOriginCm = new Vector3(worldPosM.X, worldPosM.Z, worldPosM.Y) * 100f + new Vector3(0f, 0f, 50f);
        var rayDirCm = new Vector3(0f, 0f, -1f);
        var bestDist = float.MaxValue;
        var hitZ = 0f;
        var found = false;

        foreach (var sm in _scene.Meshes)
        {
            if (sm.IsHlod || !sm.IsVisibleRecursive) continue;
            if (excludeSelected && (ReferenceEquals(sm, obj) || sm.IsSelectedRecursive)) continue;
            if ((!_retainedSceneCache.TryGetCachedMesh(sm.Mesh, out var lods) && !_frameMeshCache.TryGetValue(sm.Mesh, out lods)) || lods is null || lods.Lods.Length == 0) continue;
            var mesh = lods.Lods[0];
            var l2w = UnrealSpace.ToUnrealLocalToWorld(sm.WorldMatrix);
            if (!Matrix4x4.Invert(l2w, out var w2l)) continue;
            var lo = Vector3.Transform(rayOriginCm, w2l);
            var ld = Vector3.TransformNormal(rayDirCm, w2l);
            var pos = mesh.CpuPositions;
            var idx = mesh.CpuIndices;
            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                if (idx[i] >= pos.Length || idx[i + 1] >= pos.Length || idx[i + 2] >= pos.Length) continue;
                if (!RayTriangle(lo, ld, pos[idx[i]], pos[idx[i + 1]], pos[idx[i + 2]], out var t)) continue;
                var wh = Vector3.Transform(lo + ld * t, l2w);
                var dist = Vector3.Distance(rayOriginCm, wh);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    hitZ = wh.Z;
                    found = true;
                }
            }
        }

        if (!found) return false;
        var curWorld = obj.WorldMatrix;
        curWorld.M42 = hitZ * 0.01f;
        obj.SetWorldMatrix(curWorld);
        return true;
    }

    private static string MakeUniqueName(SceneLevel level, string baseName)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(SceneObject o)
        {
            existing.Add(o.Name);
            foreach (var c in o.Children) Collect(c);
        }
        foreach (var r in level.RootObjects) Collect(r);

        if (!existing.Contains(baseName))
            return baseName;
        for (var n = 1; n < 10000; n++)
        {
            var candidate = $"{baseName}_{n}";
            if (!existing.Contains(candidate))
                return candidate;
        }
        return $"{baseName}_{Guid.NewGuid().ToString("N")[..4]}";
    }

    public void Dispose()
    {
        _disposed = true;
        try { _disposeCts.Cancel(); } catch { }
        _disposeCts.Dispose();
        _settingsTab.Dispose();
        _retainedSceneCache.Dispose();
        _contentBrowser.Dispose();
        _assetPreview.Dispose();
        _sceneRenderer?.Dispose();
    }
}
