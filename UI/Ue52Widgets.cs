using System.Numerics;
using ImGuiNET;
using UExplorer.Scene;

namespace UExplorer.UI;

/// <summary>
/// Small Slate look-alikes used by the editor panels (UE 5.2 PropertyEditor / SceneOutliner):
/// SVectorInputBox with the colored X/Y/Z label strips, two column property rows (name | value, "DetailsView.NameAreaWidth" 0.4),
/// and the Outliner visibility "eye" (SceneOutliner.VisibleIcon / Level.VisibleIcon16x).
/// </summary>
public static class Ue52Widgets
{
    // SNumericEntryBox::RedLabelBackgroundColor / GreenLabelBackgroundColor / BlueLabelBackgroundColor (SNumericEntryBox.h, UE 5.2)
    public static readonly Vector4 AxisRed = Ue52StyleAndIcons.StyleColors.Hex(0xC8371D);   // FLinearColor(0.594f, 0.0197f, 0.0f) in sRGB
    public static readonly Vector4 AxisGreen = Ue52StyleAndIcons.StyleColors.Hex(0x40A11C); // FLinearColor(0.1349f, 0.3959f, 0.0f)
    public static readonly Vector4 AxisBlue = Ue52StyleAndIcons.StyleColors.Hex(0x2A7CCB);  // FLinearColor(0.0251f, 0.207f, 0.85f)

    public const string AssetDragPayloadType = "UEX_ASSET_PATH";

    public static bool BeginPropertyTable(string id)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX;
        var availW = MathF.Max(80f, ImGui.GetContentRegionAvail().X - 2f);
        if (!ImGui.BeginTable(id, 2, flags, new Vector2(availW, 0f)))
            return false;
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.38f);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.62f);
        return true;
    }

    public static void EllipsizedText(string text, bool disabled = false)
    {
        var maxW = MathF.Max(12f, ImGui.GetContentRegionAvail().X - 4f);
        var size = ImGui.CalcTextSize(text);
        string shown = text;
        var truncated = false;
        if (size.X > maxW && text.Length > 3)
        {
            truncated = true;
            var approxChars = Math.Clamp((int) (text.Length * (maxW / MathF.Max(size.X, 1f))) - 2, 1, text.Length - 1);
            shown = text[..approxChars] + "..";
        }
        if (disabled) ImGui.TextDisabled(shown);
        else ImGui.TextUnformatted(shown);
        if (truncated && ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    private static void NameCell(string label)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, 26f);
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.Indent(8f);
        EllipsizedText(label);
        ImGui.Unindent(8f);
        ImGui.TableSetColumnIndex(1);
    }

    /// <summary>SVectorInputBox row: three numeric boxes, each with a 4 px colored strip on the left.</summary>
    public static bool VectorRow(string label, ref Vector3 value, float speed, string format, bool roll = false)
    {
        NameCell(label);
        var changed = false;
        var avail = MathF.Max(60f, ImGui.GetContentRegionAvail().X - 4f);
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var w = MathF.Max(18f, MathF.Floor((avail - spacing * 2f - 4f) / 3f));
        ImGui.PushID(label);
        changed |= AxisBox("##x", ref value.X, AxisRed, w, speed, format);
        ImGui.SameLine(0f, spacing);
        changed |= AxisBox("##y", ref value.Y, AxisGreen, w, speed, format);
        ImGui.SameLine(0f, spacing);
        changed |= AxisBox("##z", ref value.Z, AxisBlue, w, speed, format);
        ImGui.PopID();
        _ = roll;
        return changed;
    }

    /// <summary>
    /// UE5 Details asset reference row (thumbnail/icon + ellipsized name + "Use Selected" arrow + optional Reset + drag-drop target).
    /// </summary>
    public static unsafe bool AssetReferenceRow(
        string label,
        string assetName,
        IntPtr thumbOrIcon,
        bool canUseSelected,
        bool canReset,
        out string? assignedAssetPath,
        out bool clickedOpen,
        out bool clickedReset)
    {
        assignedAssetPath = null;
        clickedOpen = false;
        clickedReset = false;

        NameCell(label);
        ImGui.PushID(label);

        var availW = MathF.Max(60f, ImGui.GetContentRegionAvail().X - 4f);
        var buttonsW = (canReset ? 46f : 24f);
        var boxW = MathF.Max(36f, availW - buttonsW - 4f);

        if (thumbOrIcon != IntPtr.Zero)
        {
            ImGui.Image(thumbOrIcon, new Vector2(20f, 20f));
            ImGui.SameLine(0f, 4f);
            boxW = MathF.Max(24f, boxW - 24f);
        }

        if (ImGui.Button($"{EllipsizeForWidth(assetName, boxW - 10f)}##AssetBtn", new Vector2(boxW, 22f)))
            clickedOpen = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{assetName}\n(Click to inspect, or drop an asset from Content Browser)");

        if (ImGui.BeginDragDropTarget())
        {
            var payload = ImGui.AcceptDragDropPayload(AssetDragPayloadType);
            if (payload.NativePtr != null && payload.Data != IntPtr.Zero && payload.DataSize > 0)
            {
                assignedAssetPath = System.Text.Encoding.UTF8.GetString((byte*) payload.Data, payload.DataSize);
            }
            ImGui.EndDragDropTarget();
        }

        ImGui.SameLine(0f, 3f);
        if (!canUseSelected) ImGui.BeginDisabled();
        var useSel = ImGui.SmallButton("<-##UseSel");
        if (!canUseSelected) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Use Selected Asset from Content Browser");

        if (canReset)
        {
            ImGui.SameLine(0f, 3f);
            if (ImGui.SmallButton("R##ResetSlot"))
                clickedReset = true;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Reset to Default");
        }

        ImGui.PopID();
        return assignedAssetPath is not null || useSel;
    }

    private static string EllipsizeForWidth(string text, float maxW)
    {
        if (string.IsNullOrEmpty(text)) return "None";
        var sz = ImGui.CalcTextSize(text).X;
        if (sz <= maxW || text.Length <= 4) return text;
        var chars = Math.Clamp((int) (text.Length * (MathF.Max(16f, maxW) / MathF.Max(sz, 1f))) - 2, 2, text.Length - 1);
        return text[..chars] + "..";
    }

    private static bool AxisBox(string id, ref float v, Vector4 color, float width, float speed, string format)
    {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.SetNextItemWidth(width);
        var changed = ImGui.DragFloat(id, ref v, speed, 0f, 0f, format);
        var h = ImGui.GetItemRectSize().Y;
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(4f, h), ImGui.ColorConvertFloat4ToU32(color), 4f, ImDrawFlags.RoundCornersLeft);
        return changed;
    }

    public static bool FloatRow(string label, ref float value, float speed, string format)
    {
        NameCell(label);
        ImGui.SetNextItemWidth(-1f);
        return ImGui.DragFloat("##" + label, ref value, speed, 0f, 0f, format);
    }

    public static bool ColorRow(string label, ref Vector3 value)
    {
        NameCell(label);
        ImGui.SetNextItemWidth(-1f);
        return ImGui.ColorEdit3("##" + label, ref value, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel);
    }

    /// <summary>Outliner visibility eye. Returns true when clicked.</summary>
    public static bool EyeToggle(string id, bool visible)
    {
        var size = new Vector2(16f, ImGui.GetFrameHeight() - 2f);
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        if (!ImGui.IsItemVisible()) return clicked; // rows scrolled out of the table must not draw (eyes leaked into other panels)
        var dl = ImGui.GetWindowDrawList();
        var col = visible
            ? (hovered ? Ue52StyleAndIcons.StyleColors.ForegroundHover : Ue52StyleAndIcons.StyleColors.Foreground)
            : Ue52StyleAndIcons.StyleColors.Hover;
        var c = ImGui.ColorConvertFloat4ToU32(col);
        var center = pos + size * 0.5f;
        // almond outline
        const int segments = 12;
        var pts = new Vector2[segments * 2];
        for (var i = 0; i < segments; i++)
        {
            var t = i / (float) (segments - 1);
            var x = (t - 0.5f) * 13f;
            var y = MathF.Sin(t * MathF.PI) * 4.2f;
            pts[i] = center + new Vector2(x, -y);
            pts[segments * 2 - 1 - i] = center + new Vector2(x, y);
        }
        dl.AddPolyline(ref pts[0], pts.Length, c, ImDrawFlags.Closed, 1.2f);
        if (visible)
            dl.AddCircleFilled(center, 2.3f, c);
        else
            dl.AddLine(center + new Vector2(-6f, 5f), center + new Vector2(6f, -5f), c, 1.2f);
        return clicked;
    }

    public static bool SearchBox(string id, string hint, ref string value, float width = -1f)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        var targetW = width < 0f ? MathF.Max(60f, avail + width + 1f) : width;
        if (width == -1f) targetW = MathF.Max(60f, avail);

        var hasText = !string.IsNullOrEmpty(value);
        var clearW = hasText ? 20f : 0f;
        var inputW = MathF.Max(40f, targetW - clearW);

        ImGui.PushID(id);
        ImGui.SetNextItemWidth(inputW);
        var changed = ImGui.InputTextWithHint("##SearchInput", hint, ref value, 128);
        if (hasText)
        {
            ImGui.SameLine(0f, 2f);
            if (ImGui.SmallButton("x##ClearSearch"))
            {
                value = string.Empty;
                changed = true;
            }
        }
        ImGui.PopID();
        return changed;
    }

    public static Ue52IconKind ActorIconKind(SceneObject obj) => obj switch
    {
        ScenePointLight => Ue52IconKind.PointLight,
        SceneSpotLight => Ue52IconKind.SpotLight,
        SceneRectLight => Ue52IconKind.RectLight,
        SceneDirectionalLight => Ue52IconKind.DirectionalLight,
        SceneSkyLight => Ue52IconKind.SkyLight,
        SceneHeightFog => Ue52IconKind.HeightFog,
        SceneNiagara => Ue52IconKind.Niagara,
        SceneMesh => Ue52IconKind.StaticMesh,
        SceneLandscape => Ue52IconKind.World,
        _ when string.Equals(obj.ClassName, "LevelStreaming", StringComparison.Ordinal) => Ue52IconKind.Levels,
        _ => Ue52IconKind.GenericAsset
    };

    public static bool VectorRowWithReset(
        string label,
        ref Vector3 value,
        Vector3 defaultValue,
        float speed,
        string format,
        out bool activated,
        out bool deactivatedAfterEdit,
        out bool resetClicked,
        bool roll = false)
    {
        NameCell(label);
        activated = false;
        deactivatedAfterEdit = false;
        resetClicked = false;
        var changed = false;

        var differs = Vector3.DistanceSquared(value, defaultValue) > 1e-6f;
        var resetW = differs ? 22f : 0f;
        var avail = MathF.Max(60f, ImGui.GetContentRegionAvail().X - 4f - resetW);
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var w = MathF.Max(18f, MathF.Floor((avail - spacing * 2f - 4f) / 3f));

        ImGui.PushID(label);
        changed |= AxisBoxWithState("##x", ref value.X, AxisRed, w, speed, format, ref activated, ref deactivatedAfterEdit);
        ImGui.SameLine(0f, spacing);
        changed |= AxisBoxWithState("##y", ref value.Y, AxisGreen, w, speed, format, ref activated, ref deactivatedAfterEdit);
        ImGui.SameLine(0f, spacing);
        changed |= AxisBoxWithState("##z", ref value.Z, AxisBlue, w, speed, format, ref activated, ref deactivatedAfterEdit);

        if (differs)
        {
            ImGui.SameLine(0f, 3f);
            if (ImGui.SmallButton("R##ResetVec"))
            {
                value = defaultValue;
                resetClicked = true;
                changed = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reset to Default");
        }
        ImGui.PopID();
        _ = roll;
        return changed;
    }

    private static bool AxisBoxWithState(string id, ref float v, Vector4 color, float width, float speed, string format, ref bool activated, ref bool deactivatedAfterEdit)
    {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.SetNextItemWidth(width);
        var changed = ImGui.DragFloat(id, ref v, speed, 0f, 0f, format);
        activated |= ImGui.IsItemActivated();
        deactivatedAfterEdit |= ImGui.IsItemDeactivatedAfterEdit();
        var h = ImGui.GetItemRectSize().Y;
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(4f, h), ImGui.ColorConvertFloat4ToU32(color), 4f, ImDrawFlags.RoundCornersLeft);
        return changed;
    }

    public static bool FloatRowWithReset(
        string label,
        ref float value,
        float defaultValue,
        float speed,
        string format,
        out bool activated,
        out bool deactivatedAfterEdit,
        out bool resetClicked,
        float min = 0f,
        float max = 0f)
    {
        NameCell(label);
        ImGui.PushID(label);
        var differs = MathF.Abs(value - defaultValue) > 1e-4f;
        var resetW = differs ? 22f : 0f;
        ImGui.SetNextItemWidth(MathF.Max(40f, ImGui.GetContentRegionAvail().X - resetW - 2f));
        var changed = ImGui.DragFloat("##val", ref value, speed, min, max, format);
        activated = ImGui.IsItemActivated();
        deactivatedAfterEdit = ImGui.IsItemDeactivatedAfterEdit();
        resetClicked = false;
        if (differs)
        {
            ImGui.SameLine(0f, 3f);
            if (ImGui.SmallButton("R##ResetFloat"))
            {
                value = defaultValue;
                resetClicked = true;
                changed = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reset to Default");
        }
        ImGui.PopID();
        return changed;
    }

    public static bool ColorRowWithReset(
        string label,
        ref Vector3 value,
        Vector3 defaultValue,
        out bool activated,
        out bool deactivatedAfterEdit,
        out bool resetClicked)
    {
        NameCell(label);
        ImGui.PushID(label);
        var differs = Vector3.DistanceSquared(value, defaultValue) > 1e-6f;
        var resetW = differs ? 22f : 0f;
        ImGui.SetNextItemWidth(MathF.Max(40f, ImGui.GetContentRegionAvail().X - resetW - 2f));
        var changed = ImGui.ColorEdit3("##col", ref value, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel);
        activated = ImGui.IsItemActivated();
        deactivatedAfterEdit = ImGui.IsItemDeactivatedAfterEdit();
        resetClicked = false;
        if (differs)
        {
            ImGui.SameLine(0f, 3f);
            if (ImGui.SmallButton("R##ResetCol"))
            {
                value = defaultValue;
                resetClicked = true;
                changed = true;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reset to Default");
        }
        ImGui.PopID();
        return changed;
    }

    public static bool CheckboxRow(string label, ref bool value, string? tooltip = null)
    {
        NameCell(label);
        if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        Ue52StyleAndIcons.PushCheckbox(value);
        var changed = ImGui.Checkbox("##chk_" + label, ref value);
        Ue52StyleAndIcons.PopCheckbox();
        if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        return changed;
    }
}
