using System.Numerics;
using ImGuiNET;
using UExplorer.Scene;
using UExplorer.Rendering;
using UExplorer.UI;

namespace UExplorer.UI.Editors;

public sealed class Ue52NiagaraEditor
{
    private readonly Ue52AssetPreviewWindow.PreviewTab _tab;
    private float _time = 0f;

    public Ue52NiagaraEditor(Ue52AssetPreviewWindow.PreviewTab tab)
    {
        _tab = tab;
    }

    public void DrawPanels(Vector2 size)
    {
        ImGui.TextUnformatted("Overview / Details");
    }

    public void DrawTimeline(Vector2 size)
    {
        ImGui.TextUnformatted("Timeline");
    }
}
