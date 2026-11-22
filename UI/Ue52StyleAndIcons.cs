using System.Numerics;
using ImGuiNET;
using SkiaSharp;
using UExplorer.Platform;
using Vortice.Direct3D11;

namespace UExplorer.UI;

public enum Ue52IconKind
{
    Translate,
    Rotate,
    Scale,
    WorldSpace,
    LocalSpace,
    SnapGrid,
    SnapAngle,
    SnapScale,
    Folder,
    StaticMesh,
    SkeletalMesh,
    Material,
    MaterialInstance,
    Texture,
    Niagara,
    Blueprint,
    World,
    Animation,
    Audio,
    PointLight,
    SpotLight,
    DirectionalLight,
    GenericAsset,
    SphereShape,
    CubeShape,
    CylinderShape,
    PlaneShape,
    SelectCursor,
    CameraSpeed,
    OptionsMenu,
    Maximize,
    Restore,
    Save,
    AddContent,
    Settings,
    ContentDrawer,
    WorldSettings,
    Levels,
    SkyLight,
    HeightFog,
    SkyAtmosphere,
    Play,
    Simulate,
    Search,
    Filter,
    DockLayout,
    OutputLog,
    Undo,
    Redo,
    Cut,
    Copy,
    Paste,
    Delete,
    RectLight
}

public static class Ue52StyleAndIcons
{
    private static readonly Dictionary<Ue52IconKind, ID3D11ShaderResourceView> _srvs = [];
    private static bool _initialized;

    public static ImFontPtr RegularFont { get; private set; }
    public static ImFontPtr BoldFont { get; private set; }
    public static ImFontPtr SmallFont { get; private set; }
    public static ImFontPtr SmallBoldFont { get; private set; }
    public static ImFontPtr AssetTitleFont { get; private set; }
    public static ImFontPtr AssetClassFont { get; private set; }
    public static ImFontPtr HeadingFont { get; private set; }

    public static void InitFonts(Dx11AppHost host)
    {
        RegularFont = host.RegularFont;
        BoldFont = host.BoldFont;
        SmallFont = host.SmallFont;
        SmallBoldFont = host.SmallBoldFont;
        AssetTitleFont = host.AssetTitleFont;
        AssetClassFont = host.AssetClassFont;
        HeadingFont = host.HeadingFont;
    }

    public static unsafe bool HasValidFont(ImFontPtr font) => font.NativePtr != null;

    public static void PushSmallBoldFont()
    {
        if (HasValidFont(SmallBoldFont)) ImGui.PushFont(SmallBoldFont);
    }

    public static void PushBoldFont()
    {
        if (HasValidFont(BoldFont)) ImGui.PushFont(BoldFont);
    }

    public static void PushSmallFont()
    {
        if (HasValidFont(SmallFont)) ImGui.PushFont(SmallFont);
    }

    public static void PushHeadingFont()
    {
        if (HasValidFont(HeadingFont)) ImGui.PushFont(HeadingFont);
    }

    public static void PopFontSafe(ImFontPtr font)
    {
        if (HasValidFont(font)) ImGui.PopFont();
    }

    // FStyleColors defaults, UE 5.2 SlateCore/Private/Styling/StyleColors.cpp (USlateThemeManager::InitalizeDefaults).
    // Slate converts these sRGB hex values to linear and renders to an sRGB target; ImGui renders gamma-space colors directly,
    // so the hex values are used as-is to get the same on-screen colors.
    public static class StyleColors
    {
        public static readonly Vector4 Black = Hex(0x000000);
        public static readonly Vector4 Title = Hex(0x151515);
        public static readonly Vector4 Background = Hex(0x151515);
        public static readonly Vector4 WindowBorder = Hex(0x0F0F0F);
        public static readonly Vector4 Foldout = Hex(0x0F0F0F);
        public static readonly Vector4 Input = Hex(0x0F0F0F);
        public static readonly Vector4 InputOutline = Hex(0x383838);
        public static readonly Vector4 Recessed = Hex(0x1A1A1A);
        public static readonly Vector4 Panel = Hex(0x242424);
        public static readonly Vector4 Header = Hex(0x2F2F2F);
        public static readonly Vector4 Dropdown = Hex(0x383838);
        public static readonly Vector4 DropdownOutline = Hex(0x4C4C4C);
        public static readonly Vector4 Hover = Hex(0x575757);
        public static readonly Vector4 Hover2 = Hex(0x808080);
        public static readonly Vector4 White = Hex(0xFFFFFF);
        public static readonly Vector4 White25 = new(1f, 1f, 1f, 0x40 / 255f);
        public static readonly Vector4 Highlight = Hex(0x0070E0);
        public static readonly Vector4 Primary = Hex(0x0070E0);
        public static readonly Vector4 PrimaryHover = Hex(0x0E86FF);
        public static readonly Vector4 PrimaryPress = Hex(0x0050A0);
        public static readonly Vector4 Secondary = Hex(0x383838);
        public static readonly Vector4 Foreground = Hex(0xC0C0C0);
        public static readonly Vector4 ForegroundHover = Hex(0xFFFFFF);
        public static readonly Vector4 ForegroundHeader = Hex(0xC8C8C8);
        public static readonly Vector4 Select = Primary;
        public static readonly Vector4 SelectInactive = Hex(0x40576F);
        public static readonly Vector4 SelectParent = Hex(0x2C323A);
        public static readonly Vector4 SelectHover = Panel;
        public static readonly Vector4 Notifications = Hex(0x464B50);
        public static readonly Vector4 AccentBlue = Hex(0x26BBFF);
        public static readonly Vector4 AccentPurple = Hex(0xA139BF);
        public static readonly Vector4 AccentPink = Hex(0xFF729C);
        public static readonly Vector4 AccentRed = Hex(0xFF4040);
        public static readonly Vector4 AccentOrange = Hex(0xFE9B07);
        public static readonly Vector4 AccentYellow = Hex(0xFFDC1A);
        public static readonly Vector4 AccentGreen = Hex(0x8BC24A);
        public static readonly Vector4 AccentBrown = Hex(0x804D39);
        public static readonly Vector4 AccentGray = Hex(0x808080);
        public static readonly Vector4 AccentFolder = Hex(0xB68F55);
        public static readonly Vector4 Warning = Hex(0xFFB800);
        public static readonly Vector4 Error = Hex(0xEF3535);
        public static readonly Vector4 Success = Hex(0x1FE44B);
        /// <summary>Editor selection outline, UEditorStyleSettings::SelectionColor default (0.828, 0.364, 0.003) linear = #EC9D1D-ish in sRGB.</summary>
        public static readonly Vector4 EditorSelection = Hex(0xF5A623);

        public static Vector4 Hex(uint rgb, float a = 1f) =>
            new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);
    }

    public static void ApplyStarshipTheme()
    {
        var style = ImGui.GetStyle();
        // FStarshipCoreStyle metrics: InputFocusRadius 4, InputFocusThickness 1, ButtonMargins (12, 1.5), tab padding (4,3,10,4),
        // TableView rows 20 px, scrollbar thickness 8 with 4 px rounded thumb.
        style.WindowPadding = new Vector2(4f, 4f);
        style.FramePadding = new Vector2(6f, 3f);
        style.CellPadding = new Vector2(4f, 2f);
        style.ItemSpacing = new Vector2(4f, 4f);
        style.ItemInnerSpacing = new Vector2(4f, 4f);
        style.IndentSpacing = 12f;      // STableRow indentation in the Outliner / Details tree
        style.ScrollbarSize = 8f;
        style.GrabMinSize = 8f;
        style.WindowBorderSize = 1f;
        style.ChildBorderSize = 0f;
        style.PopupBorderSize = 1f;
        style.FrameBorderSize = 1f;     // inputs and buttons have a 1 px outline (InputOutline / Input)
        style.TabBorderSize = 0f;
        style.TabBarBorderSize = 1f;
        style.TabBarOverlineSize = 2f;  // Docking.Tab.ActiveTabIndicatorColor strip
        style.WindowRounding = 0f;
        style.ChildRounding = 0f;
        style.FrameRounding = 4f;
        style.PopupRounding = 0f;
        style.ScrollbarRounding = 4f;
        style.GrabRounding = 4f;
        style.TabRounding = 4f;
        style.WindowTitleAlign = new Vector2(0.0f, 0.5f);
        style.SeparatorTextBorderSize = 1f;
        style.DockingSeparatorSize = 2f;
        style.SelectableTextAlign = new Vector2(0f, 0.5f);

        var c = style.Colors;
        var sc = typeof(StyleColors);
        c[(int) ImGuiCol.Text]                  = StyleColors.Foreground;
        c[(int) ImGuiCol.TextDisabled]          = StyleColors.Foreground with { W = 0.45f };
        c[(int) ImGuiCol.WindowBg]              = StyleColors.Panel;            // Docking.Tab ContentAreaBrush
        c[(int) ImGuiCol.ChildBg]               = StyleColors.Recessed;         // list / tree views
        c[(int) ImGuiCol.PopupBg]               = StyleColors.Panel;            // menus
        c[(int) ImGuiCol.Border]                = StyleColors.InputOutline;     // 1 px input / dropdown outline
        c[(int) ImGuiCol.BorderShadow]          = new Vector4(0f, 0f, 0f, 0f);
        c[(int) ImGuiCol.FrameBg]               = StyleColors.Input;            // EditableTextBox background
        c[(int) ImGuiCol.FrameBgHovered]        = StyleColors.Input;            // hover only changes the outline in Slate
        c[(int) ImGuiCol.FrameBgActive]         = StyleColors.Input;
        c[(int) ImGuiCol.TitleBg]               = StyleColors.Title;
        c[(int) ImGuiCol.TitleBgActive]         = StyleColors.Title;
        c[(int) ImGuiCol.TitleBgCollapsed]      = StyleColors.Title;
        c[(int) ImGuiCol.MenuBarBg]             = StyleColors.Title;
        c[(int) ImGuiCol.ScrollbarBg]           = new Vector4(0f, 0f, 0f, 0f);
        c[(int) ImGuiCol.ScrollbarGrab]         = StyleColors.Hover;
        c[(int) ImGuiCol.ScrollbarGrabHovered]  = StyleColors.Hover2;
        c[(int) ImGuiCol.ScrollbarGrabActive]   = StyleColors.Hover2;
        c[(int) ImGuiCol.CheckMark]             = StyleColors.ForegroundHover; // white tick on the Primary filled box
        c[(int) ImGuiCol.SliderGrab]            = StyleColors.Hover2;
        c[(int) ImGuiCol.SliderGrabActive]      = StyleColors.ForegroundHover;
        c[(int) ImGuiCol.Button]                = StyleColors.Secondary;        // FButtonStyle Button: Normal Secondary
        c[(int) ImGuiCol.ButtonHovered]         = StyleColors.Hover;            //                      Hovered Hover
        c[(int) ImGuiCol.ButtonActive]          = StyleColors.Header;           //                      Pressed Header
        c[(int) ImGuiCol.Header]                = StyleColors.Select;           // TableView.Row ActiveBrush
        c[(int) ImGuiCol.HeaderHovered]         = StyleColors.Hover with { W = 0.55f };
        c[(int) ImGuiCol.HeaderActive]          = StyleColors.PrimaryPress;
        c[(int) ImGuiCol.Separator]             = StyleColors.WindowBorder;
        c[(int) ImGuiCol.SeparatorHovered]      = StyleColors.Primary;
        c[(int) ImGuiCol.SeparatorActive]       = StyleColors.PrimaryHover;
        c[(int) ImGuiCol.ResizeGrip]            = new Vector4(0f, 0f, 0f, 0f);
        c[(int) ImGuiCol.ResizeGripHovered]     = StyleColors.Hover;
        c[(int) ImGuiCol.ResizeGripActive]      = StyleColors.Primary;
        c[(int) ImGuiCol.Tab]                   = StyleColors.Background;       // TabWellBrush / NormalBrush (none)
        c[(int) ImGuiCol.TabHovered]            = StyleColors.Panel with { W = 0.8f };
        c[(int) ImGuiCol.TabSelected]           = StyleColors.Panel;            // ForegroundBrush
        c[(int) ImGuiCol.TabSelectedOverline]   = StyleColors.Primary;          // ActiveTabIndicatorColor
        c[(int) ImGuiCol.TabDimmed]             = StyleColors.Background;
        c[(int) ImGuiCol.TabDimmedSelected]     = StyleColors.Panel;
        c[(int) ImGuiCol.TabDimmedSelectedOverline] = new Vector4(0f, 0f, 0f, 0f);
        c[(int) ImGuiCol.DockingPreview]        = StyleColors.Primary with { W = 0.5f };
        c[(int) ImGuiCol.DockingEmptyBg]        = StyleColors.Background;
        c[(int) ImGuiCol.PlotLines]             = StyleColors.Foreground;
        c[(int) ImGuiCol.PlotHistogram]         = StyleColors.Primary;
        c[(int) ImGuiCol.TableHeaderBg]         = StyleColors.Header;           // SHeaderRow
        c[(int) ImGuiCol.TableBorderStrong]     = StyleColors.WindowBorder;
        c[(int) ImGuiCol.TableBorderLight]      = StyleColors.WindowBorder;
        c[(int) ImGuiCol.TableRowBg]            = StyleColors.Recessed;         // even / odd rows are both Recessed in Starship
        c[(int) ImGuiCol.TableRowBgAlt]         = StyleColors.Recessed;
        c[(int) ImGuiCol.TextSelectedBg]        = StyleColors.Primary with { W = 0.6f };
        c[(int) ImGuiCol.DragDropTarget]        = StyleColors.Primary;
        c[(int) ImGuiCol.NavCursor]             = StyleColors.Primary;
        c[(int) ImGuiCol.ModalWindowDimBg]      = new Vector4(0f, 0f, 0f, 0.5f);
        _ = sc;
    }

    /// <summary>
    /// UE 5.2 checkbox (FCheckBoxStyle "Checkbox"): Input background with InputOutline border, Primary fill with a white tick when checked.
    /// Use around ImGui.Checkbox: PushCheckbox(value) ... PopCheckbox().
    /// </summary>
    public static void PushCheckbox(bool isChecked)
    {
        ImGui.PushStyleColor(ImGuiCol.FrameBg, isChecked ? StyleColors.Primary : StyleColors.Input);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, isChecked ? StyleColors.PrimaryHover : StyleColors.Input);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, StyleColors.PrimaryPress);
    }

    public static void PopCheckbox() => ImGui.PopStyleColor(3);

    /// <summary>Primary (blue) button, FAppStyle "PrimaryButton".</summary>
    public static bool PrimaryButton(string label, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, StyleColors.Primary);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, StyleColors.PrimaryHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, StyleColors.PrimaryPress);
        ImGui.PushStyleColor(ImGuiCol.Text, StyleColors.ForegroundHover);
        var pressed = ImGui.Button(label, size);
        ImGui.PopStyleColor(4);
        return pressed;
    }

    /// <summary>Details panel category header (SDetailCategoryTableRow): Header color bar, SmallBold ForegroundHeader text.</summary>
    public static bool DetailsCategory(string label, bool defaultOpen = true)
    {
        ImGui.PushStyleColor(ImGuiCol.Header, StyleColors.Header);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, StyleColors.Hover with { W = 0.6f });
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, StyleColors.Header);
        ImGui.PushStyleColor(ImGuiCol.Text, StyleColors.ForegroundHeader);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6f, 4f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
        var pushedFont = HasValidFont(SmallBoldFont);
        if (pushedFont) ImGui.PushFont(SmallBoldFont);
        var flags = ImGuiTreeNodeFlags.Framed | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        if (defaultOpen) flags |= ImGuiTreeNodeFlags.DefaultOpen;
        var open = ImGui.CollapsingHeader(label, flags);
        if (pushedFont) ImGui.PopFont();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(4);
        return open;
    }

    public static void InitIcons(Dx11AppHost host)
    {
        InitFonts(host);
        if (_initialized) return;
        _initialized = true;

        TryLoadPng(host, Ue52IconKind.PointLight, "S_LightPoint.png");
        TryLoadPng(host, Ue52IconKind.SpotLight, "S_LightSpot.png");
        TryLoadPng(host, Ue52IconKind.DirectionalLight, "S_LightDirectional.png");
        TryLoadPng(host, Ue52IconKind.Audio, "S_AudioComponent.png");
        TryLoadPng(host, Ue52IconKind.SkyLight, "SkyLight.png");
        TryLoadPng(host, Ue52IconKind.HeightFog, "S_ExpoHeightFog.png");
        TryLoadPng(host, Ue52IconKind.SkyAtmosphere, "S_SkyAtmosphere.png");
        TryLoadPng(host, Ue52IconKind.RectLight, "S_LightRect.png");

        foreach (Ue52IconKind kind in Enum.GetValues<Ue52IconKind>())
        {
            if (_srvs.ContainsKey(kind)) continue;
            _srvs[kind] = RenderProcedural(host, kind);
        }
    }

    public static bool ToolbarIconTextButton(string id, Ue52IconKind kind, string label, bool active = false, string? tooltip = null, float iconSize = 16f, bool hasDropdown = false)
    {
        var tex = Get(kind);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6f, 3f));
        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, StyleColors.Primary);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, StyleColors.PrimaryHover);
            ImGui.PushStyleColor(ImGuiCol.Text, StyleColors.ForegroundHover);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, StyleColors.Hover with { W = 0.55f });
            ImGui.PushStyleColor(ImGuiCol.Text, StyleColors.Foreground);
        }

        var hasLabel = !string.IsNullOrEmpty(label);
        var textSz = hasLabel ? ImGui.CalcTextSize(label) : Vector2.Zero;
        var arrowW = hasDropdown ? 11f : 0f;
        var hasIcon = tex != IntPtr.Zero && iconSize > 0f;
        var w = 10f + (hasIcon ? iconSize : 0f) + (hasIcon && hasLabel ? 5f : 0f) + textSz.X + arrowW;
        var h = MathF.Max(iconSize + 4f, ImGui.GetFrameHeight());
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.Button(id, new Vector2(w, h));
        var dl = ImGui.GetWindowDrawList();
        var x = pos.X + 5f;
        if (hasIcon)
        {
            var iy = pos.Y + (h - iconSize) * 0.5f;
            dl.AddImage(tex, new Vector2(x, iy), new Vector2(x + iconSize, iy + iconSize));
            x += iconSize + 5f;
        }
        if (hasLabel)
        {
            var ty = pos.Y + (h - textSz.Y) * 0.5f;
            dl.AddText(new Vector2(x, ty), ImGui.GetColorU32(active ? StyleColors.ForegroundHover : StyleColors.Foreground), label);
            x += textSz.X + 3f;
        }
        if (hasDropdown)
        {
            var cy = pos.Y + h * 0.5f - 1f;
            var col = ImGui.GetColorU32(StyleColors.Foreground);
            dl.AddTriangleFilled(new Vector2(x, cy), new Vector2(x + 6f, cy), new Vector2(x + 3f, cy + 4f), col);
        }

        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !string.IsNullOrEmpty(tooltip))
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    public static void BeginViewportPillStyle()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6f, 3f));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(2f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, StyleColors.Dropdown with { W = 0.80f });
        ImGui.PushStyleColor(ImGuiCol.Border, StyleColors.DropdownOutline with { W = 0.85f });
    }

    public static void EndViewportPillStyle()
    {
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(5);
    }

    public static IntPtr Get(Ue52IconKind kind) =>
        _srvs.TryGetValue(kind, out var srv) ? srv.NativePointer : IntPtr.Zero;

    public static bool IconButton(string id, Ue52IconKind kind, bool active, string tooltip, float size = 16f, bool enabled = true)
    {
        var tex = Get(kind);
        ImGui.BeginDisabled(!enabled);
        if (active)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.00f, 0.44f, 0.88f, 1.00f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.08f, 0.52f, 0.96f, 1.00f));
        }

        var clicked = tex != IntPtr.Zero
            ? ImGui.ImageButton(id, tex, new Vector2(size, size))
            : ImGui.Button(id, new Vector2(size + 6, size + 4));

        if (active) ImGui.PopStyleColor(2);
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !string.IsNullOrEmpty(tooltip))
            ImGui.SetTooltip(tooltip);

        return clicked;
    }

    public static Vector4 GetAssetAccentColor(Ue52IconKind kind) => kind switch
    {
        Ue52IconKind.StaticMesh => new Vector4(0.28f, 0.71f, 1.00f, 1.0f),
        Ue52IconKind.SkeletalMesh => new Vector4(0.88f, 0.34f, 0.61f, 1.0f),
        Ue52IconKind.Material => new Vector4(0.31f, 0.75f, 0.39f, 1.0f),
        Ue52IconKind.MaterialInstance => new Vector4(0.45f, 0.84f, 0.33f, 1.0f),
        Ue52IconKind.Texture => new Vector4(0.85f, 0.27f, 0.27f, 1.0f),
        Ue52IconKind.Niagara => new Vector4(0.24f, 0.72f, 1.00f, 1.0f),
        Ue52IconKind.Blueprint => new Vector4(0.18f, 0.50f, 0.93f, 1.0f),
        Ue52IconKind.World => new Vector4(0.93f, 0.62f, 0.11f, 1.0f),
        Ue52IconKind.Animation => new Vector4(0.61f, 0.42f, 1.00f, 1.0f),
        Ue52IconKind.Audio => new Vector4(0.58f, 0.40f, 0.85f, 1.0f),
        Ue52IconKind.Folder => new Vector4(0.84f, 0.65f, 0.29f, 1.0f),
        _ => new Vector4(0.55f, 0.58f, 0.63f, 1.0f)
    };

    private static void TryLoadPng(Dx11AppHost host, Ue52IconKind kind, string fileName)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Resources", "UE5", fileName);
            if (!File.Exists(path)) return;
            using var stream = File.OpenRead(path);
            using var bmp = SKBitmap.Decode(stream);
            if (bmp is null) return;
            using var rgba = bmp.ColorType == SKColorType.Rgba8888 ? bmp : bmp.Copy(SKColorType.Rgba8888);
            _srvs[kind] = host.CreateTextureSrv(rgba.Width, rgba.Height, rgba.GetPixelSpan());
        }
        catch { }
    }

    private static ID3D11ShaderResourceView RenderProcedural(Dx11AppHost host, Ue52IconKind kind)
    {
        const int sz = 32;
        using var surface = SKSurface.Create(new SKImageInfo(sz, sz, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        c.Scale(sz / 48f, sz / 48f);

        using var white = new SKPaint { IsAntialias = true, Color = new SKColor(232, 236, 242), StrokeWidth = 2.4f, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        using var fillWhite = new SKPaint { IsAntialias = true, Color = new SKColor(232, 236, 242), Style = SKPaintStyle.Fill };

        switch (kind)
        {
            case Ue52IconKind.Translate:
                c.DrawLine(24, 7, 24, 41, white); c.DrawLine(7, 24, 41, 24, white);
                c.DrawLine(24, 7, 19, 13, white); c.DrawLine(24, 7, 29, 13, white);
                c.DrawLine(24, 41, 19, 35, white); c.DrawLine(24, 41, 29, 35, white);
                c.DrawLine(7, 24, 13, 19, white); c.DrawLine(7, 24, 13, 29, white);
                c.DrawLine(41, 24, 35, 19, white); c.DrawLine(41, 24, 35, 29, white);
                break;
            case Ue52IconKind.Rotate:
                using (var arc = new SKPath()) { arc.AddArc(new SKRect(10, 10, 38, 38), 35, 285); c.DrawPath(arc, white); }
                c.DrawLine(36, 20, 41, 12, white); c.DrawLine(36, 20, 29, 16, white);
                break;
            case Ue52IconKind.Scale:
                c.DrawRect(9, 21, 18, 18, white); c.DrawRect(23, 9, 16, 16, white); c.DrawLine(21, 27, 31, 17, white);
                break;
            case Ue52IconKind.WorldSpace:
                c.DrawCircle(24, 24, 14, white); c.DrawOval(24, 24, 7, 14, white); c.DrawLine(10, 24, 38, 24, white);
                break;
            case Ue52IconKind.LocalSpace:
                c.DrawLine(15, 33, 15, 11, white); c.DrawLine(15, 33, 37, 33, white); c.DrawLine(15, 33, 27, 21, white);
                break;
            case Ue52IconKind.SnapGrid:
                for (var i = 12; i <= 36; i += 8) { c.DrawLine(i, 10, i, 38, white); c.DrawLine(10, i, 38, i, white); }
                break;
            case Ue52IconKind.SnapAngle:
                c.DrawLine(12, 34, 36, 34, white); c.DrawLine(12, 34, 30, 12, white);
                using (var p = new SKPath()) { p.AddArc(new SKRect(6, 20, 32, 46), -50, 50); c.DrawPath(p, white); }
                break;
            case Ue52IconKind.SnapScale:
                c.DrawRect(11, 11, 26, 26, white); c.DrawRect(18, 18, 12, 12, fillWhite);
                break;
            case Ue52IconKind.Folder:
                using (var fp = new SKPaint { IsAntialias = true, Color = new SKColor(215, 167, 74), Style = SKPaintStyle.Fill })
                using (var path = new SKPath())
                {
                    path.MoveTo(6, 13); path.LineTo(18, 13); path.LineTo(22, 17); path.LineTo(42, 17); path.LineTo(42, 37); path.LineTo(6, 37); path.Close();
                    c.DrawPath(path, fp);
                }
                break;
            case Ue52IconKind.Material:
            case Ue52IconKind.MaterialInstance:
            case Ue52IconKind.SphereShape:
            {
                var colA = kind == Ue52IconKind.MaterialInstance ? new SKColor(150, 235, 110)
                    : kind == Ue52IconKind.Material ? new SKColor(90, 215, 115) : new SKColor(170, 185, 205);
                var colB = kind == Ue52IconKind.MaterialInstance ? new SKColor(40, 110, 45)
                    : kind == Ue52IconKind.Material ? new SKColor(28, 88, 42) : new SKColor(55, 62, 72);
                using var mp = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(18, 17), 22, [SKColors.White, colA, colB], [0f, 0.35f, 1f], SKShaderTileMode.Clamp) };
                c.DrawCircle(24, 24, 16, mp);
                break;
            }
            case Ue52IconKind.StaticMesh:
            case Ue52IconKind.CubeShape:
            {
                var sc = kind == Ue52IconKind.CubeShape ? new SKColor(210, 220, 232) : new SKColor(73, 182, 255);
                using var mp = new SKPaint { IsAntialias = true, Color = sc, Style = SKPaintStyle.Stroke, StrokeWidth = 2.6f };
                using var fp = new SKPaint { IsAntialias = true, Color = sc.WithAlpha(65), Style = SKPaintStyle.Fill };
                using var hex = new SKPath();
                hex.MoveTo(24, 7); hex.LineTo(39, 15); hex.LineTo(39, 33); hex.LineTo(24, 41); hex.LineTo(9, 33); hex.LineTo(9, 15); hex.Close();
                c.DrawPath(hex, fp); c.DrawPath(hex, mp);
                c.DrawLine(9, 15, 24, 24, mp); c.DrawLine(39, 15, 24, 24, mp); c.DrawLine(24, 24, 24, 41, mp);
                break;
            }
            case Ue52IconKind.CylinderShape:
                c.DrawOval(24, 13, 12, 5, white); c.DrawLine(12, 13, 12, 35, white); c.DrawLine(36, 13, 36, 35, white);
                using (var bot = new SKPath()) { bot.AddArc(new SKRect(12, 30, 36, 40), 0, 180); c.DrawPath(bot, white); }
                break;
            case Ue52IconKind.PlaneShape:
                using (var p = new SKPath()) { p.MoveTo(8, 30); p.LineTo(20, 16); p.LineTo(40, 16); p.LineTo(28, 30); p.Close(); c.DrawPath(p, white); }
                break;
            case Ue52IconKind.Niagara:
                using (var np = new SKPaint { IsAntialias = true, Color = new SKColor(62, 184, 255), Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f })
                {
                    c.Save(); c.RotateDegrees(-28, 24, 24); c.DrawOval(24, 24, 16, 7, np); c.RotateDegrees(56, 24, 24); c.DrawOval(24, 24, 16, 7, np); c.Restore();
                }
                using (var core = new SKPaint { IsAntialias = true, Color = new SKColor(220, 245, 255) }) c.DrawCircle(24, 24, 4.5f, core);
                break;
            case Ue52IconKind.Blueprint:
                using (var bp = new SKPaint { IsAntialias = true, Color = new SKColor(32, 95, 195), Style = SKPaintStyle.Fill }) c.DrawRoundRect(8, 10, 32, 28, 4, 4, bp);
                c.DrawCircle(16, 20, 3, fillWhite); c.DrawCircle(32, 20, 3, fillWhite); c.DrawCircle(24, 30, 3, fillWhite);
                c.DrawLine(16, 20, 32, 20, white); c.DrawLine(16, 20, 24, 30, white);
                break;
            case Ue52IconKind.World:
                using (var wp = new SKPaint { IsAntialias = true, Color = new SKColor(236, 157, 29), Style = SKPaintStyle.Stroke, StrokeWidth = 2.6f })
                { c.DrawCircle(24, 24, 15, wp); c.DrawOval(24, 24, 7, 15, wp); c.DrawLine(9, 24, 39, 24, wp); }
                break;
            case Ue52IconKind.Texture:
                using (var tf = new SKPaint { IsAntialias = true, Color = new SKColor(195, 58, 58), Style = SKPaintStyle.Fill }) c.DrawRoundRect(8, 10, 32, 28, 3, 3, tf);
                using (var mtn = new SKPath()) { mtn.MoveTo(11, 34); mtn.LineTo(20, 20); mtn.LineTo(27, 28); mtn.LineTo(32, 23); mtn.LineTo(37, 34); mtn.Close(); c.DrawPath(mtn, fillWhite); }
                break;
            case Ue52IconKind.SkeletalMesh:
                using (var sk = new SKPaint { IsAntialias = true, Color = new SKColor(224, 86, 156), StrokeWidth = 2.6f, Style = SKPaintStyle.Stroke })
                { c.DrawCircle(24, 12, 4, sk); c.DrawLine(24, 16, 24, 30, sk); c.DrawLine(14, 21, 34, 21, sk); c.DrawLine(24, 30, 17, 40, sk); c.DrawLine(24, 30, 31, 40, sk); }
                break;
            case Ue52IconKind.SelectCursor:
                using (var p = new SKPath())
                {
                    p.MoveTo(13, 8); p.LineTo(35, 25); p.LineTo(25, 27); p.LineTo(30, 38); p.LineTo(25, 40); p.LineTo(20, 29); p.LineTo(13, 35); p.Close();
                    c.DrawPath(p, fillWhite);
                }
                break;
            case Ue52IconKind.CameraSpeed:
                c.DrawRoundRect(8, 15, 22, 18, 3, 3, white);
                using (var p = new SKPath()) { p.MoveTo(30, 20); p.LineTo(40, 14); p.LineTo(40, 34); p.LineTo(30, 28); p.Close(); c.DrawPath(p, white); }
                break;
            case Ue52IconKind.OptionsMenu:
                using (var bold = new SKPaint { IsAntialias = true, Color = new SKColor(232, 236, 242), StrokeWidth = 3.8f, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round })
                {
                    c.DrawLine(10, 15, 38, 15, bold); c.DrawLine(10, 24, 38, 24, bold); c.DrawLine(10, 33, 38, 33, bold);
                }
                break;
            case Ue52IconKind.Maximize:
                c.DrawRect(10, 10, 28, 28, white);
                break;
            case Ue52IconKind.Restore:
                c.DrawRect(14, 10, 24, 24, white);
                c.DrawRect(10, 16, 24, 22, white);
                break;
            case Ue52IconKind.Save:
                c.DrawRoundRect(9, 9, 30, 30, 3, 3, white);
                c.DrawRect(15, 9, 14, 10, fillWhite);
                c.DrawRect(15, 25, 18, 14, white);
                break;
            case Ue52IconKind.AddContent:
                using (var ac = new SKPaint { IsAntialias = true, Color = new SKColor(139, 194, 74), StrokeWidth = 3.2f, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round })
                { c.DrawLine(24, 10, 24, 38, ac); c.DrawLine(10, 24, 38, 24, ac); }
                break;
            case Ue52IconKind.Settings:
                c.DrawCircle(24, 24, 7, white);
                for (var g = 0; g < 8; g++)
                {
                    var ang = g * MathF.PI * 0.25f;
                    c.DrawLine(24 + MathF.Cos(ang) * 11f, 24 + MathF.Sin(ang) * 11f, 24 + MathF.Cos(ang) * 16f, 24 + MathF.Sin(ang) * 16f, white);
                }
                break;
            case Ue52IconKind.ContentDrawer:
                c.DrawRoundRect(8, 12, 32, 24, 3, 3, white);
                c.DrawLine(8, 22, 40, 22, white);
                c.DrawLine(19, 29, 29, 29, white);
                break;
            case Ue52IconKind.WorldSettings:
                c.DrawCircle(24, 24, 14, white);
                c.DrawOval(24, 24, 7, 14, white);
                c.DrawLine(10, 24, 38, 24, white);
                using (var dot = new SKPaint { IsAntialias = true, Color = new SKColor(245, 166, 35), Style = SKPaintStyle.Fill })
                    c.DrawCircle(35, 35, 6, dot);
                break;
            case Ue52IconKind.Levels:
                using (var p = new SKPath())
                {
                    p.MoveTo(24, 9); p.LineTo(40, 17); p.LineTo(24, 25); p.LineTo(8, 17); p.Close();
                    c.DrawPath(p, white);
                }
                c.DrawLine(8, 24, 24, 32, white); c.DrawLine(24, 32, 40, 24, white);
                c.DrawLine(8, 31, 24, 39, white); c.DrawLine(24, 39, 40, 31, white);
                break;
            case Ue52IconKind.SkyLight:
            case Ue52IconKind.SkyAtmosphere:
                c.DrawCircle(24, 24, 9, white);
                for (var r = 0; r < 8; r++)
                {
                    var a = r * MathF.PI * 0.25f;
                    c.DrawLine(24 + MathF.Cos(a) * 12f, 24 + MathF.Sin(a) * 12f, 24 + MathF.Cos(a) * 17f, 24 + MathF.Sin(a) * 17f, white);
                }
                break;
            case Ue52IconKind.HeightFog:
                c.DrawLine(8, 17, 40, 17, white); c.DrawLine(12, 24, 36, 24, white); c.DrawLine(8, 31, 40, 31, white);
                break;
            case Ue52IconKind.Play:
                using (var gp = new SKPaint { IsAntialias = true, Color = new SKColor(139, 194, 74), Style = SKPaintStyle.Fill })
                using (var p = new SKPath())
                { p.MoveTo(14, 10); p.LineTo(38, 24); p.LineTo(14, 38); p.Close(); c.DrawPath(p, gp); }
                break;
            case Ue52IconKind.Simulate:
                using (var p = new SKPath())
                { p.MoveTo(12, 12); p.LineTo(32, 24); p.LineTo(12, 36); p.Close(); c.DrawPath(p, white); }
                c.DrawCircle(35, 35, 5, fillWhite);
                break;
            case Ue52IconKind.Search:
                c.DrawCircle(21, 21, 10, white); c.DrawLine(28, 28, 38, 38, white);
                break;
            case Ue52IconKind.Filter:
                using (var p = new SKPath())
                { p.MoveTo(9, 12); p.LineTo(39, 12); p.LineTo(27, 25); p.LineTo(27, 37); p.LineTo(21, 34); p.LineTo(21, 25); p.Close(); c.DrawPath(p, white); }
                break;
            case Ue52IconKind.DockLayout:
                c.DrawRect(9, 10, 30, 28, white);
                c.DrawRect(11, 26, 26, 10, fillWhite);
                break;
            case Ue52IconKind.OutputLog:
                c.DrawRoundRect(9, 10, 30, 28, 3, 3, white);
                c.DrawLine(14, 19, 20, 24, white); c.DrawLine(20, 24, 14, 29, white);
                c.DrawLine(23, 29, 33, 29, white);
                break;
            case Ue52IconKind.Undo:
                using (var arc = new SKPath()) { arc.AddArc(new SKRect(12, 14, 38, 38), 190, 170); c.DrawPath(arc, white); }
                c.DrawLine(12, 22, 10, 13, white); c.DrawLine(12, 22, 20, 19, white);
                break;
            case Ue52IconKind.Redo:
                using (var arc = new SKPath()) { arc.AddArc(new SKRect(10, 14, 36, 38), -10, -170); c.DrawPath(arc, white); }
                c.DrawLine(36, 22, 38, 13, white); c.DrawLine(36, 22, 28, 19, white);
                break;
            case Ue52IconKind.Cut:
                c.DrawCircle(15, 33, 5, white); c.DrawCircle(33, 33, 5, white);
                c.DrawLine(18, 29, 32, 11, white); c.DrawLine(30, 29, 16, 11, white);
                break;
            case Ue52IconKind.Copy:
                c.DrawRect(15, 9, 21, 24, white); c.DrawRect(10, 15, 21, 24, white);
                break;
            case Ue52IconKind.Paste:
                c.DrawRoundRect(11, 13, 26, 27, 2, 2, white); c.DrawRect(18, 9, 12, 6, fillWhite);
                break;
            case Ue52IconKind.Delete:
                c.DrawLine(11, 14, 37, 14, white); c.DrawRect(18, 9, 12, 5, white);
                c.DrawRect(14, 14, 20, 24, white);
                c.DrawLine(20, 20, 20, 32, white); c.DrawLine(28, 20, 28, 32, white);
                break;
            case Ue52IconKind.RectLight:
                c.DrawRect(11, 14, 26, 20, white);
                break;
            default:
                c.DrawRoundRect(11, 8, 26, 32, 3, 3, white); c.DrawLine(17, 18, 31, 18, white); c.DrawLine(17, 24, 31, 24, white);
                break;
        }

        using var pix = surface.PeekPixels();
        return host.CreateTextureSrv(sz, sz, pix.GetPixelSpan());
    }
}
