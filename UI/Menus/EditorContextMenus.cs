using System.Numerics;
using System.Windows.Forms;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using ImGuiNET;
using UExplorer.Scene;
using UExplorer.UI.Commands;

namespace UExplorer.UI.Menus;

public readonly record struct ViewportMenuPick(SceneObject? Hit, Vector3 HitLocationCm);

public sealed class EditorMenuHost
{
    public required WorldScene Scene { get; init; }
    public required EditorCommandHistory History { get; init; }
    public required EditorClipboard Clipboard { get; init; }
    public required Func<SceneLevel> GetActiveSpawnLevel { get; init; }
    public required Action<SceneLevel> SetActiveSpawnLevel { get; init; }
    public required Func<IFileProvider?> GetProvider { get; init; }
    public required Action<SceneObject> FocusObject { get; init; }
    public required Action<Vector3> GoHereCm { get; init; }
    public required Action<SceneObject> SnapCameraToObject { get; init; }
    public required Action<SceneObject> SnapObjectToCamera { get; init; }
    public required Action<SceneObject> SnapObjectToFloor { get; init; }
    public required Action<SceneObject> BeginOutlinerRename { get; init; }
    public required Action<string> FindInContentBrowser { get; init; }
    public required Action<UObject> OpenAssetEditor { get; init; }
    public required Action<string> OpenWorldByPackagePath { get; init; }
    public required Action<string> AddStreamingSubLevel { get; init; }
    public required Action<string> SpawnAssetIntoLevel { get; init; }
    public required Action<SceneLevel> ToggleLevelVisibility { get; init; }
    public required Action<SceneLevel> UnloadLevel { get; init; }
    public required Action ExpandAllOutliner { get; init; }
    public required Action CollapseAllOutliner { get; init; }
    public required Action<string> Log { get; init; }
    public required Action<string, Vector3?> PlacePrimitiveShape { get; init; }
    public required Action<string, Vector3?> PlaceLightActor { get; init; }
    public required Action<Vector3?> PasteAt { get; init; }
    public required Action CutSelected { get; init; }
    public required Action CopySelected { get; init; }
    public required Action DuplicateSelected { get; init; }
    public required Action DeleteSelected { get; init; }
    public Action<string, string>? ExportToUnrealByPackagePath { get; init; }
    public Action<UObject>? ExportToUnrealObject { get; init; }
    public Action? ExportCurrentWorldToUnreal { get; init; }
}

public sealed class EditorContextMenus
{
    private bool _rmbDownInViewport;
    private Vector2 _rmbDownPos;
    private bool _rmbDragged;
    private Vector3 _viewportContextWorldPosCm;

    public void HandleViewportRightClick(bool viewportHovered, bool gizmoActive, Func<Vector2, ViewportMenuPick> pickAtScreenPos)
    {
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && viewportHovered && !gizmoActive)
        {
            _rmbDownInViewport = true;
            _rmbDownPos = ImGui.GetMousePos();
            _rmbDragged = false;
        }

        if (_rmbDownInViewport && ImGui.IsMouseDown(ImGuiMouseButton.Right))
        {
            if (Vector2.Distance(ImGui.GetMousePos(), _rmbDownPos) > 4.0f)
                _rmbDragged = true;
        }

        if (_rmbDownInViewport && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
        {
            if (!_rmbDragged && !gizmoActive && viewportHovered)
            {
                var pick = pickAtScreenPos(_rmbDownPos);
                _viewportContextWorldPosCm = pick.HitLocationCm;
                if (pick.Hit is not null)
                {
                    var target = pick.Hit;
                    if (!ImGui.GetIO().KeyAlt)
                    {
                        while (target.Parent is { } p && !string.Equals(p.ClassName, "LevelStreaming", StringComparison.Ordinal))
                            target = p;
                    }
                    if (!string.Equals(target.ClassName, "LevelStreaming", StringComparison.Ordinal))
                        target.IsSelected = true;
                    // Select on scene
                    _pendingViewportPickTarget = target;
                    ImGui.OpenPopup("##ViewportActorCtx");
                }
                else
                {
                    _pendingViewportPickTarget = null;
                    ImGui.OpenPopup("##ViewportEmptyCtx");
                }
            }
            _rmbDownInViewport = false;
        }
    }

    private SceneObject? _pendingViewportPickTarget;

    public void DrawViewportContextMenus(EditorMenuHost host)
    {
        if (_pendingViewportPickTarget is { } pending)
        {
            host.Scene.Select(pending);
            _pendingViewportPickTarget = null;
        }

        if (ImGui.BeginPopup("##ViewportActorCtx"))
        {
            var sel = host.Scene.SelectedObject;
            if (sel is null)
            {
                DrawEmptyMenuBody(host, _viewportContextWorldPosCm);
            }
            else
            {
                DrawActorMenuBody(host, sel, includeViewportItems: true, _viewportContextWorldPosCm);
            }
            ImGui.EndPopup();
        }

        if (ImGui.BeginPopup("##ViewportEmptyCtx"))
        {
            DrawEmptyMenuBody(host, _viewportContextWorldPosCm);
            ImGui.EndPopup();
        }
    }

    public void DrawOutlinerItemContextMenu(EditorMenuHost host, SceneObject? contextTarget)
    {
        if (!ImGui.BeginPopup("##OutlinerItemCtx"))
            return;

        var sel = contextTarget ?? host.Scene.SelectedObject;
        if (sel is null)
        {
            ImGui.EndPopup();
            return;
        }

        if (string.Equals(sel.ClassName, "LevelStreaming", StringComparison.Ordinal))
        {
            var lvl = host.Scene.Levels.FirstOrDefault(l => ReferenceEquals(l.LevelRoot, sel));
            if (lvl is not null)
            {
                ImGui.TextDisabled($"Level: {lvl.Name}");
                ImGui.Separator();
                if (ImGui.MenuItem("Make Current Level"))
                    host.SetActiveSpawnLevel(lvl);
                if (ImGui.MenuItem(lvl.IsVisible ? "Hide Level" : "Show Level"))
                    host.ToggleLevelVisibility(lvl);
                if (host.ExportCurrentWorldToUnreal is not null && ImGui.MenuItem("Export World to Unreal"))
                    host.ExportCurrentWorldToUnreal();
                ImGui.BeginDisabled(lvl.IsPersistent);
                if (ImGui.MenuItem("Unload Level"))
                    host.UnloadLevel(lvl);
                ImGui.EndDisabled();
            }
        }
        else
        {
            DrawActorMenuBody(host, sel, includeViewportItems: false, null);
        }

        ImGui.EndPopup();
    }

    public void DrawOutlinerEmptyContextMenu(EditorMenuHost host, bool suppressOpen = false)
    {
        if (suppressOpen && !ImGui.IsPopupOpen("##OutlinerEmptyCtx"))
            return;
        if (!ImGui.BeginPopupContextWindow("##OutlinerEmptyCtx", ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
            return;

        ImGui.TextDisabled("Hierarchy");
        ImGui.Separator();
        if (ImGui.MenuItem("Expand All")) host.ExpandAllOutliner();
        if (ImGui.MenuItem("Collapse All")) host.CollapseAllOutliner();
        ImGui.Separator();
        if (ImGui.MenuItem("Show All Actors")) ShowAllActors(host);
        if (ImGui.MenuItem("Paste", "Ctrl+V", false, host.Clipboard.HasItems)) host.PasteAt(null);
        if (ImGui.MenuItem("Select First Actor", null, false, host.Scene.RootObjects.Count > 0))
            host.Scene.Select(host.Scene.RootObjects[0]);
        if (ImGui.MenuItem("Clear Selection", null, false, host.Scene.SelectedObject is not null))
            host.Scene.Select(null);
        ImGui.EndPopup();
    }

    public static void DrawContentBrowserAssetContextMenu(
        EditorMenuHost host,
        string assetName,
        string packagePath,
        Ue52IconKind kind,
        Action showInFolderView)
    {
        var isWorld = kind == Ue52IconKind.World || packagePath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);
        ImGui.TextDisabled($"{assetName} ({kind})");
        ImGui.Separator();

        if (isWorld)
        {
            if (ImGui.MenuItem("Load Level (Open World)"))
                host.OpenWorldByPackagePath(packagePath);
            if (ImGui.MenuItem("Add as Streaming Sub-Level"))
                host.AddStreamingSubLevel(packagePath);
        }
        else
        {
            if (ImGui.MenuItem("Open / Edit..."))
            {
                if (host.GetProvider() is { } prov && TryLoadPrimary(prov, packagePath, assetName) is { } obj)
                    host.OpenAssetEditor(obj);
            }
            var placeable = kind is Ue52IconKind.StaticMesh or Ue52IconKind.Blueprint or Ue52IconKind.Niagara or Ue52IconKind.Material or Ue52IconKind.MaterialInstance;
            if (placeable && ImGui.MenuItem("Spawn into Level"))
                host.SpawnAssetIntoLevel(packagePath);
        }

        var exportableToUnreal = isWorld || kind is Ue52IconKind.StaticMesh or Ue52IconKind.SkeletalMesh or Ue52IconKind.Material or Ue52IconKind.MaterialInstance or Ue52IconKind.Niagara;
        if (exportableToUnreal && host.ExportToUnrealByPackagePath is not null)
        {
            ImGui.Separator();
            if (ImGui.MenuItem("Export to Unreal"))
                host.ExportToUnrealByPackagePath(packagePath, assetName);
        }

        ImGui.Separator();
        if (ImGui.MenuItem("Copy Package Path"))
            SetClipboardTextSafe(packagePath);
        if (ImGui.MenuItem("Copy Asset Name"))
            SetClipboardTextSafe(assetName);
        ImGui.Separator();
        if (ImGui.MenuItem("Show in Folder View"))
            showInFolderView();
    }

    public static void DrawContentBrowserFolderContextMenu(
        string folderName,
        string folderPath,
        Action openFolder,
        Action refreshTree)
    {
        ImGui.TextDisabled($"Folder: {folderName}");
        ImGui.Separator();
        if (ImGui.MenuItem("Open")) openFolder();
        if (ImGui.MenuItem("Copy Folder Path")) SetClipboardTextSafe(string.IsNullOrEmpty(folderPath) ? "All" : folderPath);
        if (ImGui.MenuItem("Refresh")) refreshTree();
    }

    private static void DrawActorMenuBody(EditorMenuHost host, SceneObject sel, bool includeViewportItems, Vector3? contextWorldPosCm)
    {
        var editable = !string.Equals(sel.ClassName, "LevelStreaming", StringComparison.Ordinal);

        var primaryAsset = ResolveInspectableAsset(sel);
        var assetPath = primaryAsset?.GetPathName() ?? sel.Path;
        if (ImGui.BeginMenu("Asset Options"))
        {
            if (ImGui.MenuItem("Find in Content Browser", null, false, !string.IsNullOrEmpty(assetPath)))
                host.FindInContentBrowser(assetPath);
            if (ImGui.MenuItem($"Edit {(primaryAsset is not null ? primaryAsset.Name : "Asset")}...", null, false, primaryAsset is not null))
                host.OpenAssetEditor(primaryAsset!);
            if (primaryAsset is not null && host.ExportToUnrealObject is not null && ImGui.MenuItem("Export to Unreal"))
                host.ExportToUnrealObject(primaryAsset);
            if (ImGui.MenuItem("Copy Asset Path", null, false, !string.IsNullOrEmpty(assetPath)))
                SetClipboardTextSafe(assetPath);
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("View Options"))
        {
            if (ImGui.MenuItem("Focus Selected", "F"))
                host.FocusObject(sel);
            if (includeViewportItems && contextWorldPosCm is { } goPos && ImGui.MenuItem("Go Here"))
                host.GoHereCm(goPos);
            if (ImGui.MenuItem("Snap Camera to Object"))
                host.SnapCameraToObject(sel);
            if (editable && ImGui.MenuItem("Snap Object to Camera"))
                host.SnapObjectToCamera(sel);
            ImGui.EndMenu();
        }

        ImGui.Separator();

        if (sel.Children.Count > 0 && ImGui.MenuItem("Select First Child"))
            host.Scene.Select(sel.Children[0]);

        if (ImGui.BeginMenu("Edit"))
        {
            if (ImGui.MenuItem("Cut", "Ctrl+X", false, editable)) host.CutSelected();
            if (ImGui.MenuItem("Copy", "Ctrl+C", false, editable)) host.CopySelected();
            if (ImGui.MenuItem("Paste", "Ctrl+V", false, host.Clipboard.HasItems)) host.PasteAt(null);
            if (includeViewportItems && ImGui.MenuItem("Paste Here", null, false, host.Clipboard.HasItems))
                host.PasteAt(contextWorldPosCm);
            if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, editable)) host.DuplicateSelected();
            if (ImGui.MenuItem("Delete", "Del", false, editable)) host.DeleteSelected();
            if (ImGui.MenuItem("Rename", "F2", false, editable)) host.BeginOutlinerRename(sel);
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Visibility"))
        {
            if (ImGui.MenuItem(sel.IsVisible ? "Hide Selected" : "Show Selected", "H"))
            {
                host.History.Execute(new VisibilityCommand(
                    host.Scene,
                    [(sel, sel.IsVisible, !sel.IsVisible)],
                    sel.IsVisible ? "Hide Selected" : "Show Selected"));
            }
            if (ImGui.MenuItem("Show Only Selected"))
            {
                var changes = new List<(SceneObject Obj, bool OldVis, bool NewVis)>();
                foreach (var root in host.Scene.RootObjects)
                    CollectVisibilityForShowOnly(root, sel, changes);
                if (changes.Count > 0)
                    host.History.Execute(new VisibilityCommand(host.Scene, changes, "Show Only Selected"));
            }
            if (ImGui.MenuItem("Show All Actors"))
                ShowAllActors(host);
            ImGui.EndMenu();
        }

        if (editable && ImGui.BeginMenu("Transform"))
        {
            if (ImGui.MenuItem("Reset Transform"))
            {
                var before = TransformSnapshot.Capture(sel);
                var after = new TransformSnapshot(Vector3.Zero, Vector3.Zero, Quaternion.Identity, Vector3.One, null);
                host.History.Execute(new TransformCommand(host.Scene, sel, before, after, "Reset Transform"));
            }
            if (ImGui.MenuItem("Snap to Floor", "End"))
                host.SnapObjectToFloor(sel);
            if (ImGui.MenuItem("Mirror X")) ApplyScaleMirror(host, sel, new Vector3(-1f, 1f, 1f), "Mirror X");
            if (ImGui.MenuItem("Mirror Y")) ApplyScaleMirror(host, sel, new Vector3(1f, -1f, 1f), "Mirror Y");
            if (ImGui.MenuItem("Mirror Z")) ApplyScaleMirror(host, sel, new Vector3(1f, 1f, -1f), "Mirror Z");
            ImGui.EndMenu();
        }

        if (editable && host.Scene.Levels.Count > 0 && ImGui.BeginMenu("Level"))
        {
            var owning = host.Scene.FindOwningLevel(sel);
            if (ImGui.BeginMenu("Move Selected to Level"))
            {
                foreach (var lvl in host.Scene.Levels)
                {
                    if (!lvl.IsLoaded) continue;
                    var isCurrentOwner = ReferenceEquals(lvl, owning);
                    if (ImGui.MenuItem(lvl.Name, null, isCurrentOwner, !isCurrentOwner))
                        host.History.Execute(new MoveToLevelCommand(host.Scene, sel, lvl));
                }
                ImGui.EndMenu();
            }
            if (owning is not null && ImGui.MenuItem("Make Owning Level Current"))
                host.SetActiveSpawnLevel(owning);
            ImGui.EndMenu();
        }
    }

    private static void DrawEmptyMenuBody(EditorMenuHost host, Vector3? contextWorldPosCm)
    {
        if (ImGui.MenuItem("Paste Here", "Ctrl+V", false, host.Clipboard.HasItems))
            host.PasteAt(contextWorldPosCm);
        if (ImGui.MenuItem("Select First Actor", null, false, host.Scene.RootObjects.Count > 0))
            host.Scene.Select(host.Scene.RootObjects[0]);
        if (ImGui.MenuItem("Show All Actors"))
            ShowAllActors(host);
        ImGui.Separator();
        DrawPlaceActorSubmenu(host, contextWorldPosCm);
    }

    public static void DrawPlaceActorSubmenu(EditorMenuHost host, Vector3? spawnPosCm)
    {
        if (!ImGui.BeginMenu("Place Actor"))
            return;

        if (ImGui.BeginMenu("Lights"))
        {
            if (ImGui.MenuItem("Point Light")) host.PlaceLightActor("PointLight", spawnPosCm);
            if (ImGui.MenuItem("Spot Light")) host.PlaceLightActor("SpotLight", spawnPosCm);
            if (ImGui.MenuItem("Directional Light")) host.PlaceLightActor("DirectionalLight", spawnPosCm);
            if (ImGui.MenuItem("Rect Light")) host.PlaceLightActor("RectLight", spawnPosCm);
            if (ImGui.MenuItem("Sky Light")) host.PlaceLightActor("SkyLight", spawnPosCm);
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Shapes"))
        {
            if (ImGui.MenuItem("Cube")) host.PlacePrimitiveShape("Cube", spawnPosCm);
            if (ImGui.MenuItem("Sphere")) host.PlacePrimitiveShape("Sphere", spawnPosCm);
            if (ImGui.MenuItem("Plane")) host.PlacePrimitiveShape("Plane", spawnPosCm);
            if (ImGui.MenuItem("Cylinder")) host.PlacePrimitiveShape("Cylinder", spawnPosCm);
            ImGui.EndMenu();
        }

        ImGui.EndMenu();
    }

    private static void ApplyScaleMirror(EditorMenuHost host, SceneObject sel, Vector3 factor, string desc)
    {
        var before = TransformSnapshot.Capture(sel);
        var after = before with { Scale = before.Scale * factor, OverrideLocalMatrix = null };
        host.History.Execute(new TransformCommand(host.Scene, sel, before, after, desc));
    }

    private static void ShowAllActors(EditorMenuHost host)
    {
        var changes = new List<(SceneObject Obj, bool OldVis, bool NewVis)>();
        void Visit(SceneObject o)
        {
            if (!o.IsVisible) changes.Add((o, false, true));
            foreach (var c in o.Children) Visit(c);
        }
        foreach (var r in host.Scene.RootObjects) Visit(r);
        if (changes.Count > 0)
            host.History.Execute(new VisibilityCommand(host.Scene, changes, "Show All Actors"));
    }

    private static void CollectVisibilityForShowOnly(SceneObject node, SceneObject keep, List<(SceneObject Obj, bool OldVis, bool NewVis)> changes)
    {
        var isKeepOrAncestor = ReferenceEquals(node, keep) || IsDescendant(node, keep) || IsDescendant(keep, node);
        if (!string.Equals(node.ClassName, "LevelStreaming", StringComparison.Ordinal))
        {
            var desired = isKeepOrAncestor;
            if (node.IsVisible != desired)
                changes.Add((node, node.IsVisible, desired));
        }
        foreach (var c in node.Children)
            CollectVisibilityForShowOnly(c, keep, changes);
    }

    private static bool IsDescendant(SceneObject parent, SceneObject candidate)
    {
        var cur = candidate.Parent;
        for (var d = 0; cur is not null && d < 64; d++, cur = cur.Parent)
            if (ReferenceEquals(cur, parent)) return true;
        return false;
    }

    private static UObject? ResolveInspectableAsset(SceneObject sel) => sel switch
    {
        SceneMesh sm => sm.Mesh,
        _ => sel.SourceObject
    };

    private static UObject? TryLoadPrimary(IFileProvider prov, string packagePath, string assetName)
    {
        try
        {
            if (!prov.TryLoadPackage(packagePath, out var pkg))
                return null;
            return pkg.GetExports().FirstOrDefault(e => e.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase))
                   ?? pkg.GetExports().FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static void SetClipboardTextSafe(string text)
    {
        try { ImGui.SetClipboardText(text); } catch { }
        try { Clipboard.SetText(text); } catch { }
    }
}
