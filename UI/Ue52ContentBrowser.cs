using System.Collections.Concurrent;
using System.Numerics;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine;
using ImGuiNET;
using UExplorer.Rendering;
using UExplorer.Scene;
using UExplorer.UI.Menus;
using Vortice.Direct3D11;

namespace UExplorer.UI;

/// <summary>
/// UE 5.2 Content Browser browsing mounted game assets from CUE4Parse with left-sidebar filters,
/// FModel-style search, and async non-blocking DX11 Material/Texture thumbnails.
/// </summary>
public sealed class Ue52ContentBrowser : IDisposable
{
    private const int MaxThumbnailCacheEntries = 256;
    private const int ThumbnailWorkerCount = 2;

    private sealed class FolderNode(string name, string fullPath)
    {
        public string Name { get; } = name;
        public string FullPath { get; } = fullPath;
        public readonly Dictionary<string, FolderNode> SubFolders = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<AssetEntry> Assets = [];
        public FolderNode[] SortedSubFolders = [];
        public AssetEntry[] SortedAssets = [];

        public void SortRecursive()
        {
            if (SubFolders.Count > 0)
            {
                SortedSubFolders = [.. SubFolders.Values];
                Array.Sort(SortedSubFolders, static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                for (var i = 0; i < SortedSubFolders.Length; i++)
                    SortedSubFolders[i].SortRecursive();
            }
            else
            {
                SortedSubFolders = [];
            }

            if (Assets.Count > 0)
            {
                SortedAssets = [.. Assets];
                Array.Sort(SortedAssets, static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                SortedAssets = [];
            }
        }
    }

    private sealed record AssetEntry(string Name, string PackagePath, string Extension, Ue52IconKind GuessKind);

    private enum ThumbState : byte { Queued, Ready, Failed }

    private readonly record struct ThumbnailRequest(string PackagePath, string Name, Ue52IconKind GuessKind, int Gen);

    private sealed class ReadyThumbnail
    {
        public required string PackagePath { get; init; }
        public required int Gen { get; init; }
        public PreparedTextureData? TextureCpu { get; init; }
        public MaterialAsset? Material { get; init; }
        public NativeMaterialPipeline? Pipeline { get; init; }
    }

    private sealed class ThumbCacheEntry(ID3D11ShaderResourceView srv, LinkedListNode<string> lruNode)
    {
        public ID3D11ShaderResourceView Srv { get; } = srv;
        public LinkedListNode<string> LruNode { get; } = lruNode;
    }

    private FolderNode? _root;
    private FolderNode? _selectedFolder;
    private volatile bool _building;
    private int _treeGen;
    private string _search = string.Empty;
    private bool _showFiltersSidebar = true;
    private readonly HashSet<Ue52IconKind> _activeFilters = [];
    private int _filterVersion;
    private string? _statusMsg;
    public string? SelectedAssetPath { get; private set; }
    public bool IsDrawerMode { get; set; }
    public bool DockInLayoutRequested { get; set; }
    public bool IsAnyContextMenuOpen { get; private set; }
    public bool IsDraggingAsset { get; private set; }
    public EditorMenuHost? MenuHost { get; set; }
    private volatile bool _disposed;
    private bool _expandAllRequested;
    private bool _collapseAllRequested;
    private string? _pendingRevealAssetPath;

    // Cached grid list to avoid per-frame LINQ allocations
    private FolderNode? _cachedGridFolder;
    private string _cachedGridSearch = string.Empty;
    private int _cachedGridFilterVersion = -1;
    private readonly List<(bool IsFolder, FolderNode? Folder, AssetEntry? Asset)> _cachedGridItems = [];

    // Async DX11 Thumbnail pipeline & LRU cache
    private readonly Dictionary<string, ThumbCacheEntry> _thumbCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _thumbLru = new();
    private readonly Queue<(ID3D11ShaderResourceView Srv, long RetireFrame)> _retiredThumbResources = new();
    private readonly ConcurrentDictionary<string, ThumbState> _thumbStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<ThumbnailRequest> _thumbQueue = new();
    private readonly ConcurrentQueue<ReadyThumbnail> _readyThumbs = new();
    private readonly SemaphoreSlim _thumbSemaphore = new(0);
    private CancellationTokenSource _thumbCts = new();
    private IFileProvider? _workerProvider;
    private Task[]? _workers;
    private int _thumbGen;
    private long _frame;

    private NativeThumbnailRenderer? _thumbnailRenderer;
    private NativeMeshLods? _thumbSphere;

    private static readonly (string Label, Ue52IconKind Kind)[] FilterItems =
    [
        ("Static Mesh", Ue52IconKind.StaticMesh),
        ("Skeletal Mesh", Ue52IconKind.SkeletalMesh),
        ("Material", Ue52IconKind.Material),
        ("Material Inst", Ue52IconKind.MaterialInstance),
        ("Texture", Ue52IconKind.Texture),
        ("Niagara", Ue52IconKind.Niagara),
        ("Blueprint", Ue52IconKind.Blueprint),
        ("Level (.umap)", Ue52IconKind.World),
    ];

    public IntPtr GetThumbnailOrIcon(string? packagePath, Ue52IconKind fallbackKind)
    {
        if (!string.IsNullOrEmpty(packagePath) && _thumbCache.TryGetValue(packagePath, out var entry))
        {
            TouchLru(entry);
            return entry.Srv.NativePointer;
        }
        return Ue52StyleAndIcons.Get(fallbackKind);
    }

    public void Reset()
    {
        if (_disposed) return;
        Interlocked.Increment(ref _treeGen);
        _root = null;
        _selectedFolder = null;
        _building = false;
        _cachedGridFolder = null;
        _cachedGridItems.Clear();
        ClearThumbnails(recreateCts: true);
    }

    public void SelectAndRevealAsset(string packagePath)
    {
        if (_disposed || string.IsNullOrWhiteSpace(packagePath))
            return;
        _pendingRevealAssetPath = packagePath;
        if (_root is not null)
            TryRevealPendingAsset();
    }

    private void TryRevealPendingAsset()
    {
        if (string.IsNullOrWhiteSpace(_pendingRevealAssetPath) || _root is null)
            return;
        var target = _pendingRevealAssetPath.Replace('\\', '/').TrimStart('/');
        var dotIdx = target.IndexOf('.');
        var targetNoSub = dotIdx >= 0 ? target[..dotIdx] : target;

        static (FolderNode? Folder, AssetEntry? Asset) FindRecursive(FolderNode node, string fullTarget, string noSub)
        {
            foreach (var a in node.SortedAssets)
            {
                var p = a.PackagePath.Replace('\\', '/').TrimStart('/');
                var pDot = p.IndexOf('.');
                var pNoExt = pDot >= 0 ? p[..pDot] : p;
                if (string.Equals(p, fullTarget, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pNoExt, noSub, StringComparison.OrdinalIgnoreCase) ||
                    pNoExt.EndsWith(noSub, StringComparison.OrdinalIgnoreCase))
                    return (node, a);
            }
            foreach (var sub in node.SortedSubFolders)
            {
                var found = FindRecursive(sub, fullTarget, noSub);
                if (found.Asset is not null)
                    return found;
            }
            return (null, null);
        }

        var (folder, asset) = FindRecursive(_root, target, targetNoSub);
        if (folder is not null && asset is not null)
        {
            _selectedFolder = folder;
            _search = string.Empty;
            _activeFilters.Clear();
            _filterVersion++;
            SelectedAssetPath = asset.PackagePath;
            _cachedGridFolder = null;
        }
        _pendingRevealAssetPath = null;
    }

    private void ClearThumbnails(bool recreateCts = true)
    {
        Interlocked.Increment(ref _thumbGen);
        StopWorkers(recreateCts);

        while (_thumbQueue.TryDequeue(out _)) { }
        while (_readyThumbs.TryDequeue(out var ready))
            ready.Pipeline?.Dispose();

        _thumbStates.Clear();
        foreach (var entry in _thumbCache.Values)
            entry.Srv.Dispose();
        _thumbCache.Clear();
        _thumbLru.Clear();

        while (_retiredThumbResources.Count > 0)
            _retiredThumbResources.Dequeue().Srv.Dispose();
    }

    private void StopWorkers(bool recreateCts)
    {
        try { _thumbCts.Cancel(); } catch { }
        if (_workers is { Length: > 0 } workers)
        {
            try { _thumbSemaphore.Release(workers.Length * 2); } catch { }
            try { Task.WaitAll(workers, TimeSpan.FromMilliseconds(250)); } catch { }
        }
        _workers = null;
        _workerProvider = null;
        try { _thumbCts.Dispose(); } catch { }
        if (recreateCts && !_disposed)
            _thumbCts = new CancellationTokenSource();
    }

    private void EnsureWorkersStarted(IFileProvider provider)
    {
        if (_disposed)
            return;
        if (ReferenceEquals(_workerProvider, provider) && _workers is { Length: > 0 })
            return;

        StopWorkers(recreateCts: true);
        if (_disposed)
            return;
        _workerProvider = provider;
        var ct = _thumbCts.Token;
        _workers = new Task[ThumbnailWorkerCount];
        for (var i = 0; i < ThumbnailWorkerCount; i++)
            _workers[i] = Task.Run(() => WorkerLoop(provider, ct), ct);
    }

    private void WorkerLoop(IFileProvider provider, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                _thumbSemaphore.Wait(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }

            if (ct.IsCancellationRequested || _disposed || !_thumbQueue.TryDequeue(out var req))
                continue;
            if (req.Gen != Volatile.Read(ref _thumbGen))
                continue;

            try
            {
                var device = D3D11NativeShaderDevice.Shared;
                if (!provider.TryLoadPackage(req.PackagePath, out var pkg))
                {
                    if (req.Gen == Volatile.Read(ref _thumbGen))
                        _thumbStates[req.PackagePath] = ThumbState.Failed;
                    continue;
                }

                var mainObj = pkg.GetExports().FirstOrDefault(e => e.Name.Equals(req.Name, StringComparison.OrdinalIgnoreCase))
                              ?? pkg.GetExports().FirstOrDefault();
                if (mainObj is null)
                {
                    if (req.Gen == Volatile.Read(ref _thumbGen))
                        _thumbStates[req.PackagePath] = ThumbState.Failed;
                    continue;
                }

                if (mainObj is UTexture tex)
                {
                    var cpu = NativeTextureCache.PrepareCpu(tex, asCube: false, forceLinearUncompressed: false, maxSizeOverride: 128);
                    if (ct.IsCancellationRequested || _disposed || req.Gen != Volatile.Read(ref _thumbGen))
                        continue;
                    if (cpu is null)
                    {
                        if (req.Gen == Volatile.Read(ref _thumbGen))
                            _thumbStates[req.PackagePath] = ThumbState.Failed;
                        continue;
                    }
                    _readyThumbs.Enqueue(new ReadyThumbnail
                    {
                        PackagePath = req.PackagePath,
                        Gen = req.Gen,
                        TextureCpu = cpu
                    });
                }
                else if (mainObj is UUnrealMaterial mat && device is not null)
                {
                    var matAsset = new MaterialAsset(mat);
                    var pipe = NativeAssetCache.BuildPipelineSync(device, matAsset, out _);
                    if (ct.IsCancellationRequested || _disposed || req.Gen != Volatile.Read(ref _thumbGen))
                    {
                        pipe?.Dispose();
                        continue;
                    }
                    if (pipe is null)
                    {
                        if (req.Gen == Volatile.Read(ref _thumbGen))
                            _thumbStates[req.PackagePath] = ThumbState.Failed;
                        continue;
                    }
                    _readyThumbs.Enqueue(new ReadyThumbnail
                    {
                        PackagePath = req.PackagePath,
                        Gen = req.Gen,
                        Material = matAsset,
                        Pipeline = pipe
                    });
                }
                else
                {
                    if (req.Gen == Volatile.Read(ref _thumbGen))
                        _thumbStates[req.PackagePath] = ThumbState.Failed;
                }
            }
            catch
            {
                if (req.Gen == Volatile.Read(ref _thumbGen))
                    _thumbStates[req.PackagePath] = ThumbState.Failed;
            }
        }
    }

    public void Draw(IFileProvider provider, Action<UWorld> onOpenWorld, Ue52AssetPreviewWindow previewWindow, bool pauseThumbnails = false)
    {
        if (_disposed)
            return;

        IsAnyContextMenuOpen = false;
        IsDraggingAsset = false;

        if (_root is null && !_building)
            StartBuildTree(provider);

        if (_building || _root is null)
        {
            ImGui.TextDisabled("Indexing game packages...");
            return;
        }

        TryRevealPendingAsset();
        EnsureWorkersStarted(provider);

        // Process at most 2 prepared thumbnails on the UI thread per frame
        if (!pauseThumbnails)
            ProcessThumbnailQueue(provider, maxGpuUploadsPerFrame: 2);

        var avail = ImGui.GetContentRegionAvail();
        var treeW = Math.Clamp(avail.X * 0.19f, 160f, 270f);
        var filterW = _showFiltersSidebar ? Math.Clamp(avail.X * 0.14f, 130f, 185f) : 0f;

        // 1) Folder Tree
        ImGui.BeginChild("##UExCBTree", new Vector2(treeW, avail.Y), ImGuiChildFlags.Borders);
        DrawFolderTree(_root, provider);
        _expandAllRequested = false;
        _collapseAllRequested = false;
        ImGui.EndChild();

        ImGui.SameLine();

        // 2) Left Sidebar Filters
        if (_showFiltersSidebar)
        {
            ImGui.BeginChild("##UExCBFilters", new Vector2(filterW, avail.Y), ImGuiChildFlags.Borders);
            DrawFiltersSidebar();
            ImGui.EndChild();
            ImGui.SameLine();
        }

        // 3) Right Asset Toolbar + Grid
        var gridW = Math.Max(200f, avail.X - treeW - filterW - (_showFiltersSidebar ? 12f : 6f));
        ImGui.BeginChild("##UExCBGrid", new Vector2(gridW, avail.Y), ImGuiChildFlags.None);
        DrawToolbar(provider);
        ImGui.Separator();
        DrawAssetGrid(provider, onOpenWorld, previewWindow);
        ImGui.EndChild();
    }

    private void DrawFiltersSidebar()
    {
        ImGui.TextColored(new Vector4(0.85f, 0.86f, 0.88f, 1f), "Filters");
        if (_activeFilters.Count > 0)
        {
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 42f);
            if (ImGui.SmallButton("Clear"))
            {
                _activeFilters.Clear();
                _filterVersion++;
            }
        }
        ImGui.Separator();

        for (var i = 0; i < FilterItems.Length; i++)
        {
            var (label, kind) = FilterItems[i];
            var active = _activeFilters.Contains(kind);
            var accent = Ue52StyleAndIcons.GetAssetAccentColor(kind);

            var rowPos = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(rowPos + new Vector2(1f, 3f), rowPos + new Vector2(4f, 18f), ImGui.GetColorU32(accent), 1f);

            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 7f);
            if (ImGui.Checkbox($"{label}##CBF_{i}", ref active))
            {
                if (active) _activeFilters.Add(kind);
                else _activeFilters.Remove(kind);
                _filterVersion++;
            }
        }
    }

    private void StartBuildTree(IFileProvider provider)
    {
        if (_disposed) return;
        var gen = Interlocked.Increment(ref _treeGen);
        _building = true;
        _ = Task.Run(() =>
        {
            try
            {
                var root = new FolderNode("All", "");
                foreach (var (path, file) in provider.Files)
                {
                    if (_disposed || Volatile.Read(ref _treeGen) != gen)
                        return;
                    if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                        !path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var norm = path.Replace('\\', '/').TrimStart('/');
                    var parts = norm.Split('/');
                    var cur = root;
                    for (var i = 0; i < parts.Length - 1; i++)
                    {
                        var seg = parts[i];
                        if (!cur.SubFolders.TryGetValue(seg, out var next))
                        {
                            var fp = string.IsNullOrEmpty(cur.FullPath) ? seg : $"{cur.FullPath}/{seg}";
                            next = new FolderNode(seg, fp);
                            cur.SubFolders[seg] = next;
                        }
                        cur = next;
                    }

                    var fileName = parts[^1];
                    var ext = Path.GetExtension(fileName);
                    var assetName = Path.GetFileNameWithoutExtension(fileName);
                    cur.Assets.Add(new AssetEntry(assetName, file.Path, ext, GuessAssetKind(assetName, ext)));
                }

                root.SortRecursive();

                if (Volatile.Read(ref _treeGen) == gen)
                {
                    _root = root;
                    _selectedFolder = root.SortedSubFolders.Length > 0 ? root.SortedSubFolders[0] : root;
                    _cachedGridFolder = null;
                }
            }
            catch (Exception e)
            {
                if (Volatile.Read(ref _treeGen) == gen)
                    _statusMsg = e.Message;
            }
            finally
            {
                if (Volatile.Read(ref _treeGen) == gen)
                    _building = false;
            }
        });
    }

    private void DrawFolderTree(FolderNode node, IFileProvider provider)
    {
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
        if (node == _selectedFolder) flags |= ImGuiTreeNodeFlags.Selected;
        if (node.SortedSubFolders.Length == 0) flags |= ImGuiTreeNodeFlags.Leaf;
        if (string.IsNullOrEmpty(node.FullPath)) flags |= ImGuiTreeNodeFlags.DefaultOpen;

        if (_expandAllRequested) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        else if (_collapseAllRequested && !string.IsNullOrEmpty(node.FullPath)) ImGui.SetNextItemOpen(false, ImGuiCond.Always);

        var open = ImGui.TreeNodeEx($"##Dir_{node.FullPath}", flags);
        if (ImGui.IsItemClicked()) _selectedFolder = node;

        if (ImGui.BeginPopupContextItem($"##CBFolderCtx_{node.FullPath}"))
        {
            IsAnyContextMenuOpen = true;
            EditorContextMenus.DrawContentBrowserFolderContextMenu(
                node.Name,
                node.FullPath,
                () => _selectedFolder = node,
                () => StartBuildTree(provider));
            ImGui.Separator();
            if (ImGui.MenuItem("Expand All")) _expandAllRequested = true;
            if (ImGui.MenuItem("Collapse All")) _collapseAllRequested = true;
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        var folderTex = Ue52StyleAndIcons.Get(Ue52IconKind.Folder);
        if (folderTex != IntPtr.Zero) { ImGui.Image(folderTex, new Vector2(14, 14)); ImGui.SameLine(); }
        ImGui.TextUnformatted(node.Name);

        if (open)
        {
            var subs = node.SortedSubFolders;
            for (var i = 0; i < subs.Length; i++)
                DrawFolderTree(subs[i], provider);
            ImGui.TreePop();
        }
    }

    private void DrawToolbar(IFileProvider provider)
    {
        if (Ue52StyleAndIcons.ToolbarIconTextButton("##CBFiltersBtn", Ue52IconKind.Filter, "Filters", _showFiltersSidebar, "Toggle Asset Filters"))
            _showFiltersSidebar = !_showFiltersSidebar;
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh")) StartBuildTree(provider);
        ImGui.SameLine();
        Ue52Widgets.SearchBox("##CBSearch", "Search Content (letters / tokens)...", ref _search, 240f);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(_selectedFolder?.FullPath is { Length: > 0 } p ? p : "All");
        if (!string.IsNullOrEmpty(_statusMsg))
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.95f, 0.55f, 0.20f, 1f), _statusMsg);
        }

        if (IsDrawerMode)
        {
            const string dockLabel = "Dock in Layout";
            var dockW = ImGui.CalcTextSize(dockLabel).X + 34f;
            var rightX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - dockW;
            if (rightX > ImGui.GetCursorPosX())
                ImGui.SameLine(rightX);
            else
                ImGui.SameLine();
            if (Ue52StyleAndIcons.ToolbarIconTextButton("##CBDockBtn", Ue52IconKind.DockLayout, dockLabel, false, "Dock Content Browser into Level Editor Layout"))
                DockInLayoutRequested = true;
        }
    }

    private void DrawAssetGrid(IFileProvider provider, Action<UWorld> onOpenWorld, Ue52AssetPreviewWindow previewWindow)
    {
        var folder = _selectedFolder ?? _root!;
        ImGui.BeginChild("##CBScroll", ImGui.GetContentRegionAvail(), ImGuiChildFlags.None);

        if (!ReferenceEquals(_cachedGridFolder, folder) ||
            !string.Equals(_cachedGridSearch, _search, StringComparison.Ordinal) ||
            _cachedGridFilterVersion != _filterVersion)
        {
            _cachedGridFolder = folder;
            _cachedGridSearch = _search;
            _cachedGridFilterVersion = _filterVersion;
            _cachedGridItems.Clear();

            if (string.IsNullOrWhiteSpace(_search) && _activeFilters.Count == 0)
            {
                var subs = folder.SortedSubFolders;
                for (var i = 0; i < subs.Length; i++)
                    _cachedGridItems.Add((true, subs[i], null));
                var assets = folder.SortedAssets;
                for (var i = 0; i < assets.Length; i++)
                    _cachedGridItems.Add((false, null, assets[i]));
            }
            else
            {
                var matches = new List<(AssetEntry Entry, int Score)>();
                CollectFiltered(folder, matches, 800);
                matches.Sort(static (a, b) =>
                {
                    var c = b.Score.CompareTo(a.Score);
                    return c != 0 ? c : string.Compare(a.Entry.Name, b.Entry.Name, StringComparison.OrdinalIgnoreCase);
                });
                for (var i = 0; i < matches.Count; i++)
                    _cachedGridItems.Add((false, null, matches[i].Entry));
            }
        }

        var items = _cachedGridItems;
        const float tileW = 94f;
        const float tileH = 110f;
        const float thumbSize = 62f;
        var cols = Math.Max(1, (int) (ImGui.GetContentRegionAvail().X / (tileW + 8f)));
        var dl = ImGui.GetWindowDrawList();
        var gen = Volatile.Read(ref _thumbGen);

        for (var i = 0; i < items.Count; i++)
        {
            if (i % cols != 0) ImGui.SameLine();

            var entry = items[i];
            var kind = entry.IsFolder ? Ue52IconKind.Folder : entry.Asset!.GuessKind;
            var label = entry.IsFolder ? entry.Folder!.Name : entry.Asset!.Name;
            var accent = Ue52StyleAndIcons.GetAssetAccentColor(kind);

            var pos = ImGui.GetCursorScreenPos();
            ImGui.PushID(i);
            if (ImGui.InvisibleButton("##Tile", new Vector2(tileW, tileH)))
            {
                if (entry.IsFolder)
                {
                    _selectedFolder = entry.Folder;
                }
                else
                {
                    SelectedAssetPath = entry.Asset!.PackagePath;
                    OpenAsset(entry.Asset!, provider, onOpenWorld, previewWindow);
                }
            }

            if (!entry.IsFolder && entry.Asset is { } dragAsset && ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
            {
                IsDraggingAsset = true;
                SelectedAssetPath = dragAsset.PackagePath;
                unsafe
                {
                    var utf8 = System.Text.Encoding.UTF8.GetBytes(dragAsset.PackagePath);
                    fixed (byte* pBytes = utf8)
                        ImGui.SetDragDropPayload(Ue52Widgets.AssetDragPayloadType, (IntPtr) pBytes, (uint) utf8.Length);
                }
                ImGui.TextUnformatted($"{dragAsset.Name} ({kind})");
                ImGui.EndDragDropSource();
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right) && !entry.IsFolder && entry.Asset is not null)
                SelectedAssetPath = entry.Asset.PackagePath;

            if (ImGui.BeginPopupContextItem($"##CBTileCtx_{i}"))
            {
                IsAnyContextMenuOpen = true;
                if (entry.IsFolder && entry.Folder is { } ctxFolder)
                {
                    EditorContextMenus.DrawContentBrowserFolderContextMenu(
                        ctxFolder.Name,
                        ctxFolder.FullPath,
                        () => _selectedFolder = ctxFolder,
                        () => StartBuildTree(provider));
                }
                else if (!entry.IsFolder && entry.Asset is { } ctxAsset && MenuHost is not null)
                {
                    SelectedAssetPath = ctxAsset.PackagePath;
                    EditorContextMenus.DrawContentBrowserAssetContextMenu(
                        MenuHost,
                        ctxAsset.Name,
                        ctxAsset.PackagePath,
                        ctxAsset.GuessKind,
                        () => SelectAndRevealAsset(ctxAsset.PackagePath));
                }
                ImGui.EndPopup();
            }

            var hovered = ImGui.IsItemHovered();
            var isSelectedTile = !entry.IsFolder && entry.Asset is not null && string.Equals(SelectedAssetPath, entry.Asset.PackagePath, StringComparison.OrdinalIgnoreCase);
            dl.AddRectFilled(pos, pos + new Vector2(tileW, tileH), ImGui.GetColorU32(hovered || isSelectedTile ? new Vector4(0.20f, 0.22f, 0.25f, 1f) : new Vector4(0.125f, 0.125f, 0.135f, 1f)), 3f);
            dl.AddRect(pos, pos + new Vector2(tileW, tileH), ImGui.GetColorU32(isSelectedTile ? new Vector4(0.05f, 0.52f, 0.98f, 1f) : (hovered ? new Vector4(0.00f, 0.44f, 0.88f, 1f) : new Vector4(0.20f, 0.20f, 0.22f, 1f))), 3f);

            var thumbPtr = IntPtr.Zero;
            if (!entry.IsFolder && entry.Asset is { } asset &&
                (kind is Ue52IconKind.Material or Ue52IconKind.MaterialInstance or Ue52IconKind.Texture))
            {
                if (_thumbCache.TryGetValue(asset.PackagePath, out var cachedEntry))
                {
                    TouchLru(cachedEntry);
                    thumbPtr = cachedEntry.Srv.NativePointer;
                }
                else if (!_disposed && ImGui.IsItemVisible() && _thumbStates.TryAdd(asset.PackagePath, ThumbState.Queued))
                {
                    _thumbQueue.Enqueue(new ThumbnailRequest(asset.PackagePath, asset.Name, asset.GuessKind, gen));
                    if (!_disposed)
                    {
                        try { _thumbSemaphore.Release(); }
                        catch (ObjectDisposedException) { }
                    }
                }
            }

            if (thumbPtr != IntPtr.Zero)
            {
                var thumbPos = pos + new Vector2((tileW - thumbSize) * 0.5f, 4f);
                dl.AddImage(thumbPtr, thumbPos, thumbPos + new Vector2(thumbSize, thumbSize));
            }
            else
            {
                var iconTex = Ue52StyleAndIcons.Get(kind);
                if (iconTex != IntPtr.Zero)
                    dl.AddImage(iconTex, pos + new Vector2((tileW - 44f) * 0.5f, 12f), pos + new Vector2((tileW + 44f) * 0.5f, 56f));
            }

            dl.AddRectFilled(pos + new Vector2(0, 68f), pos + new Vector2(tileW, 71f), ImGui.GetColorU32(accent));

            var hasTitleFont = Ue52StyleAndIcons.HasValidFont(Ue52StyleAndIcons.AssetTitleFont);
            if (hasTitleFont) ImGui.PushFont(Ue52StyleAndIcons.AssetTitleFont);
            var shortName = EllipsizeTextToWidth(label, tileW - 8f);
            if (hasTitleFont) ImGui.PopFont();
            var classText = entry.IsFolder ? "Folder" : kind.ToString();
            if (hasTitleFont)
                dl.AddText(Ue52StyleAndIcons.AssetTitleFont, Ue52StyleAndIcons.AssetTitleFont.FontSize, pos + new Vector2(6f, 75f), ImGui.GetColorU32(new Vector4(0.90f, 0.91f, 0.93f, 1f)), shortName);
            else
                dl.AddText(pos + new Vector2(6f, 75f), ImGui.GetColorU32(new Vector4(0.90f, 0.91f, 0.93f, 1f)), shortName);

            if (Ue52StyleAndIcons.HasValidFont(Ue52StyleAndIcons.AssetClassFont))
                dl.AddText(Ue52StyleAndIcons.AssetClassFont, Ue52StyleAndIcons.AssetClassFont.FontSize, pos + new Vector2(6f, 92f), ImGui.GetColorU32(new Vector4(0.54f, 0.56f, 0.60f, 1f)), classText);
            else
                dl.AddText(pos + new Vector2(6f, 92f), ImGui.GetColorU32(new Vector4(0.54f, 0.56f, 0.60f, 1f)), classText);

            if (hovered) ImGui.SetTooltip(entry.IsFolder ? entry.Folder!.FullPath : entry.Asset!.PackagePath);
            ImGui.PopID();
        }

        ImGui.EndChild();
    }

    private static string EllipsizeTextToWidth(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        const string ellipsis = "..";
        var targetW = MathF.Max(8f, maxWidth - ImGui.CalcTextSize(ellipsis).X);
        var lo = 1;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid]).X <= targetW)
                lo = mid;
            else
                hi = mid - 1;
        }
        return string.Concat(text.AsSpan(0, Math.Clamp(lo, 1, text.Length)), ellipsis);
    }

    private void ProcessThumbnailQueue(IFileProvider provider, int maxGpuUploadsPerFrame)
    {
        if (_disposed)
            return;
        _frame++;

        while (_retiredThumbResources.Count > 0 && _retiredThumbResources.Peek().RetireFrame <= _frame)
            _retiredThumbResources.Dequeue().Srv.Dispose();

        if (_readyThumbs.IsEmpty || D3D11NativeShaderDevice.Shared is not { } device)
            return;

        var gen = Volatile.Read(ref _thumbGen);
        var uploaded = 0;

        while (uploaded < maxGpuUploadsPerFrame && _readyThumbs.TryDequeue(out var item))
        {
            if (item.Gen != gen)
            {
                item.Pipeline?.Dispose();
                continue;
            }

            ID3D11ShaderResourceView? srv = null;
            try
            {
                if (item.TextureCpu is { } cpu)
                {
                    srv = NativeTextureCache.UploadPrepared(device.Device, cpu);
                }
                else if (item.Material is { } mat && item.Pipeline is { } pipe)
                {
                    _thumbnailRenderer ??= new NativeThumbnailRenderer(device, NativeThumbnailRenderer.DefaultSize);
                    _thumbSphere ??= NativeThumbnailRenderer.CreateSphereMesh(device.Device, radiusCm: 50f);
                    srv = _thumbnailRenderer.RenderMaterialToThumbnail(mat, pipe, _thumbSphere, provider);
                }
            }
            catch
            {
                srv = null;
            }
            finally
            {
                item.Pipeline?.Dispose();
            }

            if (srv is not null)
            {
                InsertLru(item.PackagePath, srv);
                _thumbStates[item.PackagePath] = ThumbState.Ready;
                uploaded++;
            }
            else
            {
                _thumbStates[item.PackagePath] = ThumbState.Failed;
            }
        }
    }

    private void TouchLru(ThumbCacheEntry entry)
    {
        if (entry.LruNode.List == _thumbLru && !ReferenceEquals(_thumbLru.First, entry.LruNode))
        {
            _thumbLru.Remove(entry.LruNode);
            _thumbLru.AddFirst(entry.LruNode);
        }
    }

    private void InsertLru(string packagePath, ID3D11ShaderResourceView srv)
    {
        if (_thumbCache.TryGetValue(packagePath, out var existing))
        {
            _thumbLru.Remove(existing.LruNode);
            _thumbCache.Remove(packagePath);
            _retiredThumbResources.Enqueue((existing.Srv, _frame + 2));
        }

        while (_thumbCache.Count >= MaxThumbnailCacheEntries && _thumbLru.Last is { } tail)
        {
            var tailKey = tail.Value;
            _thumbLru.RemoveLast();
            if (_thumbCache.Remove(tailKey, out var evicted))
                _retiredThumbResources.Enqueue((evicted.Srv, _frame + 2));
            _thumbStates.TryRemove(tailKey, out _);
        }

        var node = _thumbLru.AddFirst(packagePath);
        _thumbCache[packagePath] = new ThumbCacheEntry(srv, node);
    }

    private static int MatchAssetScore(AssetEntry a, string[] tokens)
    {
        if (tokens.Length == 0) return 1;

        var allSub = true;
        foreach (var tok in tokens)
        {
            if (!a.Name.Contains(tok, StringComparison.OrdinalIgnoreCase) &&
                !a.PackagePath.Contains(tok, StringComparison.OrdinalIgnoreCase))
            {
                allSub = false;
                break;
            }
        }
        if (allSub)
        {
            if (tokens.Length == 1 && a.Name.StartsWith(tokens[0], StringComparison.OrdinalIgnoreCase))
                return 100;
            if (tokens.Length == 1 && a.Name.Contains(tokens[0], StringComparison.OrdinalIgnoreCase))
                return 80;
            return 60;
        }

        if (tokens.Length == 1 && IsSubsequence(tokens[0], a.Name))
            return 30;

        return 0;
    }

    private static bool IsSubsequence(string query, string target)
    {
        var qi = 0;
        for (var ti = 0; ti < target.Length && qi < query.Length; ti++)
        {
            if (char.ToLowerInvariant(query[qi]) == char.ToLowerInvariant(target[ti]))
                qi++;
        }
        return qi == query.Length;
    }

    private void CollectFiltered(FolderNode node, List<(AssetEntry Entry, int Score)> output, int limit)
    {
        var tokens = string.IsNullOrWhiteSpace(_search)
            ? []
            : _search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        CollectFilteredRecursive(node, tokens, output, limit);
    }

    private void CollectFilteredRecursive(FolderNode node, string[] tokens, List<(AssetEntry Entry, int Score)> output, int limit)
    {
        var assets = node.SortedAssets;
        for (var i = 0; i < assets.Length; i++)
        {
            if (output.Count >= limit) return;
            var a = assets[i];
            if (_activeFilters.Count > 0 && !_activeFilters.Contains(a.GuessKind)) continue;
            var score = MatchAssetScore(a, tokens);
            if (score <= 0) continue;
            output.Add((a, score));
        }
        var subs = node.SortedSubFolders;
        for (var i = 0; i < subs.Length; i++)
        {
            if (output.Count >= limit) return;
            CollectFilteredRecursive(subs[i], tokens, output, limit);
        }
    }

    private void OpenAsset(AssetEntry entry, IFileProvider provider, Action<UWorld> onOpenWorld, Ue52AssetPreviewWindow previewWindow)
    {
        try
        {
            _statusMsg = null;
            if (!provider.TryLoadPackage(entry.PackagePath, out var pkg))
            {
                _statusMsg = $"Failed to load {entry.Name}";
                return;
            }
            var mainObj = pkg.GetExports().FirstOrDefault(e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
                          ?? pkg.GetExports().FirstOrDefault(e => e is UWorld)
                          ?? pkg.GetExports().FirstOrDefault();
            if (mainObj is null) { _statusMsg = $"No exports in {entry.Name}"; return; }

            if (mainObj is UWorld world) onOpenWorld(world);
            else previewWindow.Open(mainObj, provider);
        }
        catch (Exception e) { _statusMsg = e.Message; }
    }

    private static Ue52IconKind GuessAssetKind(string name, string ext)
    {
        if (ext.Equals(".umap", StringComparison.OrdinalIgnoreCase)) return Ue52IconKind.World;
        if (name.StartsWith("MI_", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_Inst", StringComparison.OrdinalIgnoreCase)) return Ue52IconKind.MaterialInstance;
        if (name.StartsWith("M_", StringComparison.OrdinalIgnoreCase)) return Ue52IconKind.Material;
        if (name.StartsWith("T_", StringComparison.OrdinalIgnoreCase)) return Ue52IconKind.Texture;
        if (name.StartsWith("NS_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("NE_", StringComparison.OrdinalIgnoreCase)) return Ue52IconKind.Niagara;
        if (name.StartsWith("BP_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("B_", StringComparison.OrdinalIgnoreCase)) return Ue52IconKind.Blueprint;
        if (name.StartsWith("SK_", StringComparison.OrdinalIgnoreCase)) return Ue52IconKind.SkeletalMesh;
        if (name.StartsWith("SM_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("S_", StringComparison.OrdinalIgnoreCase)) return Ue52IconKind.StaticMesh;
        return Ue52IconKind.GenericAsset;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearThumbnails(recreateCts: false);
        while (_readyThumbs.TryDequeue(out var ready))
            ready.Pipeline?.Dispose();
        _thumbSphere?.Dispose();
        _thumbnailRenderer?.Dispose();
        _thumbSemaphore.Dispose();
    }
}
