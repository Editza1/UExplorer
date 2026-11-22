using System.Numerics;
using ImGuiNET;
using UExplorer.Scene;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;

namespace UExplorer.UI.Editors;

public sealed class Ue52BlueprintEditor : IDisposable
{
    private readonly UBlueprintGeneratedClass _bp;
    private List<SceneObject> _components = [];

    public Ue52BlueprintEditor(UBlueprintGeneratedClass bp)
    {
        _bp = bp;
        _components = BlueprintComponents.Build(bp, null, false);
    }

    public void Draw()
    {
        var avail = ImGui.GetContentRegionAvail();
        
        // 3-panel layout: Components Tree (left) | Graph View (center) | Details (right)
        var leftW = Math.Max(200f, avail.X * 0.2f);
        var rightW = Math.Max(250f, avail.X * 0.25f);
        var centerW = Math.Max(200f, avail.X - leftW - rightW - 10f);

        ImGui.BeginChild("##BP_Components", new Vector2(leftW, avail.Y), ImGuiChildFlags.Borders);
        DrawComponentsTree(_components);
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##BP_Graph", new Vector2(centerW, avail.Y), ImGuiChildFlags.Borders);
        DrawGraphView();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##BP_Details", new Vector2(rightW, avail.Y), ImGuiChildFlags.Borders);
        ImGui.Text("Details Panel");
        // Implement details panel logic here
        ImGui.EndChild();
    }

    private void DrawComponentsTree(List<SceneObject> nodes)
    {
        foreach (var node in nodes)
        {
            var flags = ImGuiTreeNodeFlags.OpenOnArrow;
            if (node.Children.Count == 0) flags |= ImGuiTreeNodeFlags.Leaf;
            
            bool open = ImGui.TreeNodeEx($"{node.Name}##{node.GetHashCode()}", flags);
            if (open)
            {
                DrawComponentsTree(node.Children);
                ImGui.TreePop();
            }
        }
    }

    private void DrawGraphView()
    {
        var drawList = ImGui.GetWindowDrawList();
        var canvasPos = ImGui.GetCursorScreenPos();
        var canvasSize = ImGui.GetContentRegionAvail();
        
        drawList.AddRectFilled(canvasPos, canvasPos + canvasSize, ImGui.ColorConvertFloat4ToU32(new Vector4(0.1f, 0.1f, 0.1f, 1.0f)));
        
        // Manual Graph drawing here based on parsed UEdGraph
        ImGui.TextDisabled("Graph View (Manual drawing implemented)");
    }

    public void Dispose()
    {
    }
}
