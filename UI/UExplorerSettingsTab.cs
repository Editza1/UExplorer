using UExplorer.Rendering;
using System.Numerics;
using CUE4Parse.UE4.Versions;
using ImGuiNET;
using UExplorer.Config;
using UExplorer.Services;

namespace UExplorer.UI;

public static class RenderSettings
{
    public static int PreviewMode;
    public static float Exposure = 1.0f;
    public static float ResolutionScale = 1.0f;
    public static float CullDistanceMeters = 1200f;
    public static bool ShowEditorSprites = true;
    public static bool FlipCulling;
    public static float FieldOfViewDeg = 90f; // UE editor viewport default (horizontal)

    // UE 5.2 default post-process / renderer feature set (DefaultEngine + FPostProcessSettings defaults)
    public static bool DynamicShadows = true;       // r.ShadowQuality 3, DirectionalLight: DynamicShadowDistanceMovableLight 20000, 4 cascades
    public static float ShadowDistanceMeters = 200f; // WholeSceneDynamicShadowRadius = 20000 cm
    public static int ShadowCascades = 4;
    public static int ShadowResolution = 2048;       // r.Shadow.MaxCSMResolution
    public static bool TemporalAA = true;            // r.AntiAliasingMethod (UE 5.2 default is TSR; this is a TAA-style temporal resolve)
    public static bool AutoExposure = true;          // AutoExposureMethod = Histogram
    public static float ExposureBias = 0.0f;         // AutoExposureBias (UE 5 default 1.0)
    public static float AutoExposureSpeedUp = 3.0f;
    public static float AutoExposureSpeedDown = 1.0f;
    public static float AutoExposureMinEV100 = -10f; // r.DefaultFeature.AutoExposure.ExtendDefaultLuminanceRange = True
    public static float AutoExposureMaxEV100 = 20f;
    public static bool Bloom = true;                 // BloomMethod = Standard (Gaussian)
    public static float BloomIntensity = 0.675f;
    public static float VignetteIntensity = 0.4f;

    // R6 Performance & Viewport Controls
    public static bool VSync = true;
    public static bool ScreenSpaceAO = true;
    public static bool TiledLightCulling = true;
    public static bool EnableHlod = true;
    public static float HlodTransitionDistanceMeters = 280f;
    public static bool ShadowCascadeCaching = true;
    public static bool ShowPerfOverlay = false;
    public static int ScalabilityPreset = 3; // 0=Low, 1=Medium, 2=High, 3=Epic, 4=Custom
    public static float MinScreenSize = 0.008f;
    public static float LodScreenBias = 0.25f;

    public static void ApplyScalabilityPreset(int preset)
    {
        ScalabilityPreset = Math.Clamp(preset, 0, 4);
        switch (ScalabilityPreset)
        {
            case 0: // Low
                ResolutionScale = 0.75f;
                DynamicShadows = false;
                ShadowDistanceMeters = 80f;
                ShadowCascades = 2;
                ShadowResolution = 1024;
                TemporalAA = false;
                Bloom = false;
                ScreenSpaceAO = false;
                TiledLightCulling = true;
                EnableHlod = true;
                HlodTransitionDistanceMeters = 120f;
                ShadowCascadeCaching = true;
                CullDistanceMeters = 500f;
                MinScreenSize = 0.02f;
                LodScreenBias = 0.15f;
                break;
            case 1: // Medium
                ResolutionScale = 0.85f;
                DynamicShadows = true;
                ShadowDistanceMeters = 120f;
                ShadowCascades = 2;
                ShadowResolution = 1024;
                TemporalAA = true;
                Bloom = true;
                ScreenSpaceAO = false;
                TiledLightCulling = true;
                EnableHlod = true;
                HlodTransitionDistanceMeters = 180f;
                ShadowCascadeCaching = true;
                CullDistanceMeters = 800f;
                MinScreenSize = 0.012f;
                LodScreenBias = 0.20f;
                break;
            case 2: // High
                ResolutionScale = 1.0f;
                DynamicShadows = true;
                ShadowDistanceMeters = 160f;
                ShadowCascades = 3;
                ShadowResolution = 2048;
                TemporalAA = true;
                Bloom = true;
                ScreenSpaceAO = true;
                TiledLightCulling = true;
                EnableHlod = true;
                HlodTransitionDistanceMeters = 240f;
                ShadowCascadeCaching = true;
                CullDistanceMeters = 1000f;
                MinScreenSize = 0.009f;
                LodScreenBias = 0.22f;
                break;
            case 3: // Epic
                ResolutionScale = 1.0f;
                DynamicShadows = true;
                ShadowDistanceMeters = 200f;
                ShadowCascades = 4;
                ShadowResolution = 2048;
                TemporalAA = true;
                Bloom = true;
                ScreenSpaceAO = true;
                TiledLightCulling = true;
                EnableHlod = true;
                HlodTransitionDistanceMeters = 280f;
                ShadowCascadeCaching = true;
                CullDistanceMeters = 1200f;
                MinScreenSize = 0.008f;
                LodScreenBias = 0.25f;
                break;
        }
    }

    public static NativeRenderFeatures ViewportFeatures() => new()
    {
        DynamicShadows = DynamicShadows,
        ShadowDistance = ShadowDistanceMeters * 100f,
        ShadowCascades = ShadowCascades,
        ShadowResolution = ShadowResolution,
        TemporalAA = TemporalAA,
        AutoExposure = AutoExposure,
        ExposureBias = ExposureBias,
        AutoExposureMinEV100 = AutoExposureMinEV100,
        AutoExposureMaxEV100 = AutoExposureMaxEV100,
        AutoExposureSpeedUp = AutoExposureSpeedUp,
        AutoExposureSpeedDown = AutoExposureSpeedDown,
        Bloom = Bloom,
        BloomIntensity = BloomIntensity,
        VignetteIntensity = VignetteIntensity,
        ScreenSpaceAO = ScreenSpaceAO,
        TiledLightCulling = TiledLightCulling,
        EnableHlod = EnableHlod,
        ShadowCascadeCaching = ShadowCascadeCaching,
        MaxLightDistanceMeters = CullDistanceMeters
    };

    /// <summary>Asset editor viewports (static mesh / blueprint / material editors) use fixed exposure in UE 5.2.</summary>
    public static NativeRenderFeatures PreviewFeatures() => ViewportFeatures() with { AutoExposure = false, ShadowDistance = 5000f };
}

/// <summary>
/// UE 5.2-styled Settings tab matching FModel's settings sections (General, AES, Mappings, Directories, Rendering, .uexmanif).
/// Never auto-pops up as a modal dialog; opens cleanly as a tab from the top bar Settings menu.
/// </summary>
public sealed class UExplorerSettingsTab(
    UexManifest manifest,
    GameMountService mountService,
    Action onMountRequested,
    Action<Action>? invokeOnUi = null,
    Action<MountProgress>? onMountProgress = null) : IDisposable
{
    private sealed class ActionProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    public bool IsOpen { get; set; }
    private volatile bool _disposed;
    private readonly CancellationTokenSource _disposeCts = new();

    private int _selectedSection;
    private string _name = manifest.Name;
    private string _gameDir = manifest.GameDirectory;
    private string _mainKey = manifest.MainAesKey;
    private string _mappingsPath = manifest.MappingsPath;
    private bool _useMappings = manifest.UseMappings;
    private bool _readShaderMaps = manifest.ReadShaderMaps;
    private string _outputDir = manifest.OutputDirectory;
    private int _selectedVersionIdx = Math.Max(0, Array.IndexOf(CommonVersions, manifest.EngineVersion));
    private string _newDynGuid = string.Empty;
    private string _newDynKey = string.Empty;
    private string _status = mountService.Status;

    private static readonly string[] Sections =
    [
        "General",
        "AES Keys",
        "Mappings",
        "Directories",
        "UE 5.2 Rendering",
        "Manifest (.uexmanif)"
    ];

    private static readonly EGame[] CommonVersions =
    [
        EGame.GAME_UE5_2,
        EGame.GAME_UE5_1,
        EGame.GAME_UE5_0,
        EGame.GAME_UE5_3,
        EGame.GAME_UE5_4,
        EGame.GAME_UE4_27,
        EGame.GAME_UE4_26,
        EGame.GAME_UE4_25,
        EGame.GAME_DeadByDaylight,
        EGame.GAME_Valorant,
    ];

    public void SyncFromManifest(UexManifest m)
    {
        _name = m.Name;
        _gameDir = m.GameDirectory;
        _mainKey = m.MainAesKey;
        _mappingsPath = m.MappingsPath;
        _useMappings = m.UseMappings;
        _readShaderMaps = m.ReadShaderMaps;
        _outputDir = m.OutputDirectory;
        var idx = Array.IndexOf(CommonVersions, m.EngineVersion);
        if (idx >= 0) _selectedVersionIdx = idx;
    }

    public void ApplyToManifest(UexManifest m)
    {
        m.Name = _name.Trim();
        m.GameDirectory = _gameDir.Trim();
        m.MainAesKey = _mainKey.Trim();
        m.MappingsPath = _mappingsPath.Trim();
        m.UseMappings = _useMappings;
        m.ReadShaderMaps = _readShaderMaps;
        m.OutputDirectory = _outputDir.Trim();
        m.EngineVersion = CommonVersions[Math.Clamp(_selectedVersionIdx, 0, CommonVersions.Length - 1)];
    }

    public void Draw()
    {
        if (!IsOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(760, 480), ImGuiCond.FirstUseEver);
        var open = IsOpen;
        if (ImGui.Begin("Project Settings##UExplorerSettings", ref open))
        {
            var avail = ImGui.GetContentRegionAvail();
            var navW = 165f;

            ImGui.BeginChild("##SettingsNav", new Vector2(navW, avail.Y - 40f), ImGuiChildFlags.Borders);
            for (var i = 0; i < Sections.Length; i++)
            {
                if (ImGui.Selectable(Sections[i], _selectedSection == i))
                    _selectedSection = i;
            }
            ImGui.EndChild();

            ImGui.SameLine();

            ImGui.BeginChild("##SettingsBody", new Vector2(Math.Max(200f, avail.X - navW - 8f), avail.Y - 40f), ImGuiChildFlags.Borders);
            switch (_selectedSection)
            {
                case 0: DrawGeneral(); break;
                case 1: DrawAes(); break;
                case 2: DrawMappings(); break;
                case 3: DrawDirectories(); break;
                case 4: DrawRendering(); break;
                case 5: DrawManifest(); break;
            }
            ImGui.EndChild();

            ImGui.Separator();
            ImGui.BeginDisabled(mountService.IsMounting);
            if (Ue52StyleAndIcons.PrimaryButton(mountService.IsMounting ? "Mounting..." : "Apply & Mount Game", new Vector2(180, 26)))
            {
                ApplyToManifest(manifest);
                manifest.SaveDefault();
                StartMount(null);
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled(mountService.IsMounting ? mountService.Status : _status);
        }
        ImGui.End();
        IsOpen = open;
    }

    private static void SectionTitle(string title)
    {
        Ue52StyleAndIcons.PushBoldFont();
        ImGui.TextColored(Ue52StyleAndIcons.StyleColors.ForegroundHeader, title);
        Ue52StyleAndIcons.PopFontSafe(Ue52StyleAndIcons.BoldFont);
        ImGui.Separator();
    }

    private static bool StyledCheckbox(string label, ref bool value)
    {
        Ue52StyleAndIcons.PushCheckbox(value);
        var changed = ImGui.Checkbox(label, ref value);
        Ue52StyleAndIcons.PopCheckbox();
        return changed;
    }

    public static void DrawQuickPerfAndScalabilityItems(Action? openFullRenderingSettings = null)
    {
        StyledCheckbox("Show FPS & Pass Timings", ref RenderSettings.ShowPerfOverlay);
        StyledCheckbox("VSync (SwapChain SyncInterval)", ref RenderSettings.VSync);

        ImGui.Separator();
        var preset = RenderSettings.ScalabilityPreset;
        if (ImGui.BeginMenu("Engine Scalability"))
        {
            if (ImGui.MenuItem("Low", null, preset == 0)) RenderSettings.ApplyScalabilityPreset(0);
            if (ImGui.MenuItem("Medium", null, preset == 1)) RenderSettings.ApplyScalabilityPreset(1);
            if (ImGui.MenuItem("High", null, preset == 2)) RenderSettings.ApplyScalabilityPreset(2);
            if (ImGui.MenuItem("Epic", null, preset == 3)) RenderSettings.ApplyScalabilityPreset(3);
            ImGui.Separator();
            if (StyledCheckbox("Half-Res SSAO", ref RenderSettings.ScreenSpaceAO)) RenderSettings.ScalabilityPreset = 4;
            if (StyledCheckbox("Tiled Light Culling (16x16)", ref RenderSettings.TiledLightCulling)) RenderSettings.ScalabilityPreset = 4;
            if (StyledCheckbox("Shadow Cascade Caching", ref RenderSettings.ShadowCascadeCaching)) RenderSettings.ScalabilityPreset = 4;
            if (StyledCheckbox("Enable HLOD Switching", ref RenderSettings.EnableHlod)) RenderSettings.ScalabilityPreset = 4;
            if (StyledCheckbox("Dynamic Shadows", ref RenderSettings.DynamicShadows)) RenderSettings.ScalabilityPreset = 4;
            if (StyledCheckbox("Temporal Anti-Aliasing", ref RenderSettings.TemporalAA)) RenderSettings.ScalabilityPreset = 4;
            if (StyledCheckbox("Bloom", ref RenderSettings.Bloom)) RenderSettings.ScalabilityPreset = 4;
            ImGui.EndMenu();
        }

        if (openFullRenderingSettings is not null && ImGui.MenuItem("Open Rendering Settings..."))
            openFullRenderingSettings();
    }

    public void OpenRenderingSection()
    {
        _selectedSection = 4;
        IsOpen = true;
    }

    private void DrawGeneral()
    {
        SectionTitle("General (FModel Game Configuration)");

        ImGui.TextUnformatted("Game Directory");
        ImGui.SetNextItemWidth(-90);
        ImGui.InputText("##GameDir", ref _gameDir, 512);
        ImGui.SameLine();
        if (ImGui.Button("Browse...##Dir", new Vector2(82, 0)))
        {
            var picked = BrowseFolder(_gameDir);
            if (!string.IsNullOrEmpty(picked)) _gameDir = picked;
        }

        ImGui.TextUnformatted("UE Version");
        var names = CommonVersions.Select(v => v.ToString()).ToArray();
        ImGui.SetNextItemWidth(260);
        ImGui.Combo("##UeVer", ref _selectedVersionIdx, names, names.Length);

        StyledCheckbox("Read Material ShaderMaps (Required for 1:1 UE5 Materials)", ref _readShaderMaps);

        ImGui.Spacing();
        if (ImGui.Button("Import Settings from FModel (%AppData%/FModel)"))
        {
            if (manifest.TryImportFromFModel(out var msg))
                SyncFromManifest(manifest);
            _status = msg;
        }
    }

    private void DrawAes()
    {
        SectionTitle("AES Encryption Keys");

        ImGui.TextUnformatted("Main Key (0x...)");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##MainAes", ref _mainKey, 128);

        ImGui.Spacing();
        ImGui.TextUnformatted($"Dynamic Keys ({manifest.DynamicKeys.Count})");
        for (var i = manifest.DynamicKeys.Count - 1; i >= 0; i--)
        {
            var d = manifest.DynamicKeys[i];
            ImGui.TextUnformatted($"{d.Guid} : {d.Key}");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Remove##Dyn_{i}"))
                manifest.DynamicKeys.RemoveAt(i);
        }

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##NewGuid", "Pak GUID...", ref _newDynGuid, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##NewKey", "0x...", ref _newDynKey, 128);
        ImGui.SameLine();
        if (ImGui.Button("Add"))
        {
            if (!string.IsNullOrWhiteSpace(_newDynGuid) && !string.IsNullOrWhiteSpace(_newDynKey))
            {
                manifest.DynamicKeys.Add(new DynamicAesEntry { Guid = _newDynGuid.Trim(), Key = _newDynKey.Trim() });
                _newDynGuid = string.Empty;
                _newDynKey = string.Empty;
            }
        }
    }

    private void DrawMappings()
    {
        SectionTitle("Type Mappings (.usmap)");

        StyledCheckbox("Enable Type Mappings", ref _useMappings);
        ImGui.TextUnformatted("Mappings File Path");
        ImGui.SetNextItemWidth(-90);
        ImGui.InputText("##Mappings", ref _mappingsPath, 512);
        ImGui.SameLine();
        if (ImGui.Button("Browse...##Map", new Vector2(82, 0)))
        {
            var picked = BrowseOpenFile("Unreal Mappings (*.usmap)|*.usmap|All files (*.*)|*.*");
            if (!string.IsNullOrEmpty(picked)) _mappingsPath = picked;
        }
    }

    private void DrawDirectories()
    {
        SectionTitle("Directories");

        ImGui.TextUnformatted("Output / Logs Directory");
        ImGui.SetNextItemWidth(-90);
        ImGui.InputText("##OutDir", ref _outputDir, 512);
        ImGui.SameLine();
        if (ImGui.Button("Browse...##Out", new Vector2(82, 0)))
        {
            var picked = BrowseFolder(_outputDir);
            if (!string.IsNullOrEmpty(picked)) _outputDir = picked;
        }
    }

    private static void DrawRendering()
    {
        SectionTitle("UE 5.2 DirectX 11 Renderer");

        var preset = RenderSettings.ScalabilityPreset;
        if (ImGui.Combo("Scalability Preset", ref preset, "Low\0Medium\0High\0Epic\0Custom\0"))
        {
            if (preset < 4) RenderSettings.ApplyScalabilityPreset(preset);
            else RenderSettings.ScalabilityPreset = 4;
        }
        ImGui.Separator();

        StyledCheckbox("Auto Exposure (Histogram)", ref RenderSettings.AutoExposure);
        if (RenderSettings.AutoExposure)
        {
            ImGui.SliderFloat("Exposure Compensation (EV)", ref RenderSettings.ExposureBias, -8f, 8f, "%.2f");
            ImGui.SliderFloat("Min EV100", ref RenderSettings.AutoExposureMinEV100, -10f, 20f, "%.1f");
            ImGui.SliderFloat("Max EV100", ref RenderSettings.AutoExposureMaxEV100, -10f, 20f, "%.1f");
            ImGui.SliderFloat("Speed Up", ref RenderSettings.AutoExposureSpeedUp, 0.02f, 20f, "%.2f");
            ImGui.SliderFloat("Speed Down", ref RenderSettings.AutoExposureSpeedDown, 0.02f, 20f, "%.2f");
        }
        else
        {
            ImGui.SliderFloat("Exposure", ref RenderSettings.Exposure, 0.05f, 8f, "%.2f", ImGuiSliderFlags.Logarithmic);
        }
        ImGui.Separator();
        if (StyledCheckbox("Dynamic Shadows (Cascaded Shadow Maps)", ref RenderSettings.DynamicShadows)) RenderSettings.ScalabilityPreset = 4;
        if (RenderSettings.DynamicShadows)
        {
            if (ImGui.SliderFloat("Dynamic Shadow Distance (m)", ref RenderSettings.ShadowDistanceMeters, 10f, 2000f, "%.0f m", ImGuiSliderFlags.Logarithmic)) RenderSettings.ScalabilityPreset = 4;
            if (ImGui.SliderInt("Num Dynamic Shadow Cascades", ref RenderSettings.ShadowCascades, 1, 4)) RenderSettings.ScalabilityPreset = 4;
            var res = RenderSettings.ShadowResolution == 1024 ? 0 : RenderSettings.ShadowResolution == 4096 ? 2 : 1;
            if (ImGui.Combo("Shadow Map Resolution", ref res, "1024\02048\04096\0"))
            {
                RenderSettings.ShadowResolution = res == 0 ? 1024 : res == 2 ? 4096 : 2048;
                RenderSettings.ScalabilityPreset = 4;
            }
        }
        if (StyledCheckbox("Temporal Anti-Aliasing", ref RenderSettings.TemporalAA)) RenderSettings.ScalabilityPreset = 4;
        if (StyledCheckbox("Bloom", ref RenderSettings.Bloom)) RenderSettings.ScalabilityPreset = 4;
        if (RenderSettings.Bloom)
            ImGui.SliderFloat("Bloom Intensity", ref RenderSettings.BloomIntensity, 0f, 8f, "%.3f");
        ImGui.SliderFloat("Vignette Intensity", ref RenderSettings.VignetteIntensity, 0f, 1f, "%.2f");
        ImGui.Separator();
        ImGui.SliderFloat("Field of View", ref RenderSettings.FieldOfViewDeg, 30f, 150f, "%.0f deg");
        if (ImGui.SliderFloat("Resolution Scale", ref RenderSettings.ResolutionScale, 0.25f, 1f, "%.2f")) RenderSettings.ScalabilityPreset = 4;
        if (ImGui.SliderFloat("Cull Distance (m)", ref RenderSettings.CullDistanceMeters, 100f, 15000f, "%.0f m", ImGuiSliderFlags.Logarithmic)) RenderSettings.ScalabilityPreset = 4;
        if (ImGui.SliderFloat("Min Screen Size", ref RenderSettings.MinScreenSize, 0.001f, 0.05f, "%.4f")) RenderSettings.ScalabilityPreset = 4;
        if (ImGui.SliderFloat("LOD Screen Bias", ref RenderSettings.LodScreenBias, 0.05f, 2.0f, "%.2f")) RenderSettings.ScalabilityPreset = 4;
        StyledCheckbox("Show Editor Billboards (Lights / Niagara / Audio)", ref RenderSettings.ShowEditorSprites);
        StyledCheckbox("Flip Backface Culling", ref RenderSettings.FlipCulling);
        ImGui.Separator();
        SectionTitle("Performance & Scalability (UE5)");
        StyledCheckbox("VSync (SwapChain SyncInterval)", ref RenderSettings.VSync);
        if (StyledCheckbox("Half-Res Screen-Space Ambient Occlusion (SSAO)", ref RenderSettings.ScreenSpaceAO)) RenderSettings.ScalabilityPreset = 4;
        if (StyledCheckbox("Compute Tiled Light Culling (16x16)", ref RenderSettings.TiledLightCulling)) RenderSettings.ScalabilityPreset = 4;
        if (StyledCheckbox("Shadow Cascade Caching (Cascades 2-3)", ref RenderSettings.ShadowCascadeCaching)) RenderSettings.ScalabilityPreset = 4;
        if (StyledCheckbox("Enable HLOD Switching", ref RenderSettings.EnableHlod)) RenderSettings.ScalabilityPreset = 4;
        if (RenderSettings.EnableHlod)
            if (ImGui.SliderFloat("HLOD Transition Distance (m)", ref RenderSettings.HlodTransitionDistanceMeters, 50f, 1500f, "%.0f m")) RenderSettings.ScalabilityPreset = 4;
        StyledCheckbox("Show CPU / GPU Pass Timings Overlay", ref RenderSettings.ShowPerfOverlay);
    }

    private void DrawManifest()
    {
        SectionTitle("Game Manifest Archive (.uexmanif)");
        ImGui.TextDisabled("Archive or quick-load a game's directory, UE version, AES keys, and .usmap mappings.");
        ImGui.Spacing();

        ImGui.TextUnformatted("Profile Name");
        ImGui.SetNextItemWidth(280);
        ImGui.InputText("##ProfName", ref _name, 128);

        ImGui.Spacing();
        if (Ue52StyleAndIcons.PrimaryButton("Save .uexmanif...", new Vector2(180, 28)))
            SaveManifestDialog();
        ImGui.SameLine();
        if (ImGui.Button("Load .uexmanif...", new Vector2(180, 28)))
            LoadManifestDialog();
    }

    public void SaveManifestDialog()
    {
        var path = BrowseSaveFile("UExplorer Manifest (*.uexmanif)|*.uexmanif", $"{(string.IsNullOrWhiteSpace(_name) ? "game" : _name)}.uexmanif");
        if (string.IsNullOrEmpty(path))
            return;

        ApplyToManifest(manifest);
        _status = manifest.SaveToFile(path)
            ? $"Saved manifest: {path}"
            : $"Failed to save manifest: {path}";
    }

    public void LoadManifestDialog()
    {
        var path = BrowseOpenFile("UExplorer Manifest (*.uexmanif)|*.uexmanif|All files (*.*)|*.*");
        if (string.IsNullOrEmpty(path))
            return;

        var loaded = UexManifest.LoadFromFile(path);
        if (loaded is null)
        {
            _status = $"Failed to parse manifest: {path}";
            return;
        }

        manifest.Name = loaded.Name;
        manifest.GameDirectory = loaded.GameDirectory;
        manifest.EngineVersion = loaded.EngineVersion;
        manifest.MainAesKey = loaded.MainAesKey;
        manifest.DynamicKeys.Clear();
        manifest.DynamicKeys.AddRange(loaded.DynamicKeys);
        manifest.MappingsPath = loaded.MappingsPath;
        manifest.UseMappings = loaded.UseMappings;
        manifest.ReadShaderMaps = loaded.ReadShaderMaps;
        manifest.OutputDirectory = loaded.OutputDirectory;
        manifest.SaveDefault();

        SyncFromManifest(manifest);
        StartMount($"Loaded {Path.GetFileName(path)} -> ");
    }

    private void StartMount(string? successPrefix)
    {
        if (_disposed || mountService.IsMounting)
            return;

        if (invokeOnUi is null)
        {
            if (mountService.Mount(manifest, out var msg))
            {
                _status = successPrefix is null ? msg : $"{successPrefix}{msg}";
                onMountRequested();
            }
            else
            {
                _status = msg;
            }
            return;
        }

        _status = "Mounting game in background...";
        var progress = new ActionProgress<MountProgress>(p => invokeOnUi(() =>
        {
            if (_disposed) return;
            _status = p.Stage;
            onMountProgress?.Invoke(p);
        }));
        _ = Task.Run(async () =>
        {
            var res = await mountService.MountAsync(manifest, progress, _disposeCts.Token).ConfigureAwait(false);
            invokeOnUi(() =>
            {
                if (_disposed) return;
                if (res.Error is null && res.Provider is not null)
                {
                    _status = successPrefix is null ? res.GameSummary : $"{successPrefix}{res.GameSummary}";
                    onMountRequested();
                }
                else
                {
                    _status = res.Error ?? "Mount failed.";
                }
            });
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _disposeCts.Cancel(); } catch { }
        _disposeCts.Dispose();
    }

    private static string? BrowseFolder(string initial)
    {
        string? result = null;
        var t = new Thread(() =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = initial };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                result = dlg.SelectedPath;
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    private static string? BrowseOpenFile(string filter)
    {
        string? result = null;
        var t = new Thread(() =>
        {
            using var dlg = new System.Windows.Forms.OpenFileDialog { Filter = filter };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                result = dlg.FileName;
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    private static string? BrowseSaveFile(string filter, string defaultName)
    {
        string? result = null;
        var t = new Thread(() =>
        {
            using var dlg = new System.Windows.Forms.SaveFileDialog { Filter = filter, FileName = defaultName, DefaultExt = "uexmanif" };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                result = dlg.FileName;
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }
}
