using System.Numerics;
using System.Runtime.CompilerServices;
using CUE4Parse.FileProvider;
using ImGuiNET;
using UExplorer.Platform;
using UExplorer.Scene;
using UExplorer.UI.Commands;

namespace UExplorer.UI.Panels;

public sealed class LevelsPanel
{
    private static readonly ConditionalWeakTable<IFileProvider, List<(string Name, string Path)>> UmapCache = new();

    private string _umapSearch = string.Empty;
    private bool _openPickerModal;
    private volatile bool _streamingBusy;
    private string? _streamingStatus;

    public bool IsBusy => _streamingBusy;

    public void Draw(
        ref bool showLevels,
        WorldScene scene,
        IFileProvider? provider,
        Dx11AppHost host,
        EditorCommandHistory commandHistory,
        ref SceneLevel? activeSpawnLevel,
        Func<int> getMountGeneration,
        Func<int> getWorldGeneration,
        Func<bool> isDisposed,
        Action onLevelMutated,
        Action<string> log)
    {
        if (!showLevels)
            return;

        ImGui.SetNextWindowSize(new Vector2(460f, 360f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Levels", ref showLevels))
        {
            ImGui.End();
            return;
        }

        if (activeSpawnLevel is not null && !scene.Levels.Contains(activeSpawnLevel))
            activeSpawnLevel = null;
        var currentLevel = activeSpawnLevel ?? scene.Levels.FirstOrDefault(l => l.IsPersistent) ?? scene.Levels.FirstOrDefault();

        // Top toolbar
        ImGui.BeginDisabled(provider is null || _streamingBusy);
        if (Ue52StyleAndIcons.ToolbarIconTextButton("##LevelsAddBtn", Ue52IconKind.AddContent, "Add Level (.umap)...", false, "Add another .umap level into the current scene"))
            _openPickerModal = true;
        ImGui.EndDisabled();

        ImGui.SameLine(0f, 10f);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled($"Current: {(currentLevel?.Name ?? "None")}");

        if (_streamingBusy && !string.IsNullOrEmpty(_streamingStatus))
        {
            ImGui.SameLine(0f, 10f);
            ImGui.TextColored(Ue52StyleAndIcons.StyleColors.AccentBlue, _streamingStatus);
        }

        ImGui.Separator();

        const ImGuiTableFlags tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                                           ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV |
                                           ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("##LevelsTable", 4, tableFlags, ImGui.GetContentRegionAvail()))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Vis", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 26f);
            ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthStretch, 0.48f);
            ImGui.TableSetupColumn("Objects", ImGuiTableColumnFlags.WidthStretch, 0.24f);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthStretch, 0.28f);
            ImGui.TableHeadersRow();

            // 1. Loaded SceneLevels
            SceneLevel? levelToUnload = null;
            for (var i = 0; i < scene.Levels.Count; i++)
            {
                var level = scene.Levels[i];
                var isCurrent = ReferenceEquals(level, currentLevel);
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.PushID($"lvl_vis_{level.Id}");
                if (Ue52Widgets.EyeToggle("##lvlEye", level.IsVisible))
                {
                    WorldLoader.SetLevelVisible(scene, level, !level.IsVisible);
                    onLevelMutated();
                }
                ImGui.PopID();

                ImGui.TableSetColumnIndex(1);
                var badge = level.IsPersistent ? " (Persistent)" : (isCurrent ? " (Current)" : string.Empty);
                var label = $"{level.Name}{badge}";
                if (isCurrent) Ue52StyleAndIcons.PushBoldFont();
                if (ImGui.Selectable($"{label}##lvlRow_{level.Id}", isCurrent, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        activeSpawnLevel = level;
                }
                if (isCurrent) Ue52StyleAndIcons.PopFontSafe(Ue52StyleAndIcons.BoldFont);
                if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(level.PackagePath))
                    ImGui.SetTooltip(level.PackagePath);

                ImGui.TableSetColumnIndex(2);
                ImGui.TextDisabled($"{level.Meshes.Count}m / {level.Lights.Count}l / {level.Niagaras.Count}fx");

                ImGui.TableSetColumnIndex(3);
                ImGui.PushID($"lvl_act_{level.Id}");
                if (!isCurrent)
                {
                    if (ImGui.SmallButton("Current"))
                        activeSpawnLevel = level;
                    ImGui.SameLine(0f, 4f);
                }
                ImGui.BeginDisabled(level.IsPersistent || _streamingBusy);
                if (ImGui.SmallButton("Unload"))
                    levelToUnload = level;
                ImGui.EndDisabled();
                ImGui.PopID();
            }

            if (levelToUnload is { IsPersistent: false })
            {
                if (ReferenceEquals(activeSpawnLevel, levelToUnload))
                    activeSpawnLevel = null;
                commandHistory.PruneForLevel(levelToUnload);
                WorldLoader.UnloadLevel(scene, levelToUnload);
                onLevelMutated();
                log($"Unloaded sub-level '{levelToUnload.Name}'.");
            }

            // 2. Unloaded discovered streaming levels
            for (var i = 0; i < scene.DiscoveredStreamingLevels.Count; i++)
            {
                var desc = scene.DiscoveredStreamingLevels[i];
                if (IsAlreadyLoaded(scene, desc.PackagePath))
                    continue;

                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled("-");

                ImGui.TableSetColumnIndex(1);
                var suffix = desc.IsResolvable ? " (Unloaded)" : " (Missing)";
                Ue52Widgets.EllipsizedText($"{desc.Name}{suffix}", disabled: true);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{desc.PackagePath}\nClass: {desc.StreamingClass}\nAlwaysLoaded: {desc.IsAlwaysLoaded}");

                ImGui.TableSetColumnIndex(2);
                ImGui.TextDisabled(desc.StreamingClass);

                ImGui.TableSetColumnIndex(3);
                ImGui.BeginDisabled(provider is null || !desc.IsResolvable || _streamingBusy);
                ImGui.PushID($"lvl_load_{i}");
                if (ImGui.SmallButton("Load"))
                {
                    LoadStreamingSubLevelAsync(
                        scene,
                        provider!,
                        desc.PackagePath,
                        desc.LevelTransform,
                        desc.Name,
                        host,
                        getMountGeneration,
                        getWorldGeneration,
                        isDisposed,
                        onLevelMutated,
                        log);
                }
                ImGui.PopID();
                ImGui.EndDisabled();
            }

            ImGui.EndTable();
        }

        DrawAddLevelPickerModal(scene, provider, host, getMountGeneration, getWorldGeneration, isDisposed, onLevelMutated, log);
        ImGui.End();
    }

    public void LoadStreamingSubLevelAsync(
        WorldScene scene,
        IFileProvider provider,
        string packagePath,
        Matrix4x4? transform,
        string? nameOverride,
        Dx11AppHost host,
        Func<int> getMountGeneration,
        Func<int> getWorldGeneration,
        Func<bool> isDisposed,
        Action onLevelMutated,
        Action<string> log)
    {
        if (_streamingBusy || isDisposed())
            return;

        if (IsAlreadyLoaded(scene, packagePath))
        {
            log($"Level '{packagePath}' is already loaded.");
            return;
        }

        _streamingBusy = true;
        _streamingStatus = $"Loading {Path.GetFileNameWithoutExtension(packagePath)}...";
        var mountGen = getMountGeneration();
        var worldGen = getWorldGeneration();

        _ = Task.Run(() =>
        {
            var queuedUiFinish = false;
            try
            {
                var staged = new WorldScene();
                var lvl = WorldLoader.LoadLevelAdditive(staged, provider, packagePath, transform, isPersistent: false, nameOverride);

                queuedUiFinish = host.TryInvokeOnUiThread(() =>
                {
                    try
                    {
                        if (isDisposed() || getMountGeneration() != mountGen || getWorldGeneration() != worldGen)
                        {
                            lvl.Clear();
                            return;
                        }
                        if (!IsAlreadyLoaded(scene, lvl.PackagePath))
                        {
                            scene.Levels.Add(lvl);
                            scene.RebuildAggregates();
                            onLevelMutated();
                            log($"Loaded streaming sub-level '{lvl.Name}' ({lvl.Meshes.Count} meshes, {lvl.Lights.Count} lights).");
                        }
                        else
                        {
                            lvl.Clear();
                        }
                    }
                    finally
                    {
                        _streamingBusy = false;
                        _streamingStatus = null;
                    }
                });
                if (!queuedUiFinish)
                    lvl.Clear();
            }
            catch (Exception ex)
            {
                queuedUiFinish = host.TryInvokeOnUiThread(() =>
                {
                    try
                    {
                        if (!isDisposed())
                            log($"Failed to load sub-level '{packagePath}': {ex.Message}");
                    }
                    finally
                    {
                        _streamingBusy = false;
                        _streamingStatus = null;
                    }
                });
            }
            finally
            {
                if (!queuedUiFinish)
                {
                    _streamingBusy = false;
                    _streamingStatus = null;
                }
            }
        });
    }

    private void DrawAddLevelPickerModal(
        WorldScene scene,
        IFileProvider? provider,
        Dx11AppHost host,
        Func<int> getMountGeneration,
        Func<int> getWorldGeneration,
        Func<bool> isDisposed,
        Action onLevelMutated,
        Action<string> log)
    {
        if (_openPickerModal)
        {
            _openPickerModal = false;
            ImGui.OpenPopup("##AddLevelPickerModal");
        }

        var open = true;
        ImGui.SetNextWindowSize(new Vector2(540f, 440f), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("##AddLevelPickerModal", ref open, ImGuiWindowFlags.None))
            return;

        ImGui.TextUnformatted("Select a .umap Level to Add as Streaming Sub-Level");
        ImGui.Separator();
        Ue52Widgets.SearchBox("##UmapPickerSearch", "Filter .umap levels...", ref _umapSearch, -1f);
        ImGui.Separator();

        if (provider is null)
        {
            ImGui.TextDisabled("No game mounted.");
        }
        else
        {
            var umaps = GetCachedUmaps(provider);
            ImGui.BeginChild("##UmapList", new Vector2(-1f, -34f), ImGuiChildFlags.Borders);
            var shown = 0;
            for (var i = 0; i < umaps.Count; i++)
            {
                var (name, path) = umaps[i];
                if (!string.IsNullOrWhiteSpace(_umapSearch) &&
                    !name.Contains(_umapSearch, StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains(_umapSearch, StringComparison.OrdinalIgnoreCase))
                    continue;

                var loaded = IsAlreadyLoaded(scene, path);
                ImGui.BeginDisabled(loaded || _streamingBusy);
                if (ImGui.Selectable($"{name}  -  {path}##umap_{i}"))
                {
                    LoadStreamingSubLevelAsync(scene, provider, path, null, name, host, getMountGeneration, getWorldGeneration, isDisposed, onLevelMutated, log);
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndDisabled();
                if (++shown >= 400)
                {
                    ImGui.TextDisabled("(Showing first 400 matches; narrow your search)");
                    break;
                }
            }
            ImGui.EndChild();
        }

        if (ImGui.Button("Close", new Vector2(90f, 0f)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private static List<(string Name, string Path)> GetCachedUmaps(IFileProvider provider) =>
        UmapCache.GetValue(provider, static p =>
        {
            var list = new List<(string Name, string Path)>();
            foreach (var (key, file) in p.Files)
            {
                if (!key.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                    continue;
                list.Add((Path.GetFileNameWithoutExtension(file.Path), file.Path));
            }
            list.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        });

    private static bool IsAlreadyLoaded(WorldScene scene, string packagePath)
    {
        var norm = NormalizePackagePath(packagePath);
        foreach (var l in scene.Levels)
        {
            if (l.IsLoaded && string.Equals(NormalizePackagePath(l.PackagePath), norm, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string NormalizePackagePath(string path)
    {
        var clean = path.Replace('\\', '/').TrimStart('/');
        var dot = clean.LastIndexOf('.');
        var slash = clean.LastIndexOf('/');
        return dot > slash ? clean[..dot] : clean;
    }
}
