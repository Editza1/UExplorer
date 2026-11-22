using ImGuiNET;
using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Engine;
using UExplorer.UI;

namespace UExplorer.UI.Editors;

public sealed class StaticMeshEditor : IDisposable
{
    private readonly UStaticMesh _mesh;
    private int _selectedLod = 0;

    public StaticMeshEditor(UStaticMesh mesh)
    {
        _mesh = mesh;
    }

    public void Draw()
    {
        // LODs
        if (Ue52StyleAndIcons.DetailsCategory("LODs"))
        {
            var lods = _mesh.RenderData?.LODs;
            if (lods != null && lods.Length > 0)
            {
                if (ImGui.BeginCombo("LOD", $"LOD {_selectedLod}"))
                {
                    for (int i = 0; i < lods.Length; i++)
                    {
                        if (ImGui.Selectable($"LOD {i}", _selectedLod == i))
                        {
                            _selectedLod = i;
                        }
                    }
                    ImGui.EndCombo();
                }
                var lod = lods[_selectedLod];
                ImGui.Text($"Vertices: {lod.PositionVertexBuffer?.NumVertices ?? 0:N0}");
                ImGui.Text($"Sections: {lod.Sections?.Length ?? 0}");
            }
            else
            {
                ImGui.TextDisabled("No LODs found.");
            }
        }

        // Bounds
        if (Ue52StyleAndIcons.DetailsCategory("Bounds"))
        {
            var bounds = _mesh.RenderData?.Bounds;
            if (bounds != null)
            {
                ImGui.Text($"Origin: {bounds.Origin.X:F2}, {bounds.Origin.Y:F2}, {bounds.Origin.Z:F2}");
                ImGui.Text($"Box Extent: {bounds.BoxExtent.X:F2}, {bounds.BoxExtent.Y:F2}, {bounds.BoxExtent.Z:F2}");
                ImGui.Text($"Sphere Radius: {bounds.SphereRadius:F2}");
            }
            else
            {
                ImGui.TextDisabled("No bounds information.");
            }
        }

        // Sockets
        if (Ue52StyleAndIcons.DetailsCategory("Sockets"))
        {
            var sockets = _mesh.Sockets;
            if (sockets != null && sockets.Length > 0)
            {
                foreach (var socketIdx in sockets)
                {
                    if (socketIdx.TryLoad<UStaticMeshSocket>(out var socket))
                    {
                        if (ImGui.TreeNode(socket.SocketName.Text))
                        {
                            ImGui.Text($"Relative Location: {socket.RelativeLocation.X:F2}, {socket.RelativeLocation.Y:F2}, {socket.RelativeLocation.Z:F2}");
                            ImGui.Text($"Relative Rotation: {socket.RelativeRotation.Pitch:F2}, {socket.RelativeRotation.Yaw:F2}, {socket.RelativeRotation.Roll:F2}");
                            ImGui.TreePop();
                        }
                    }
                }
            }
            else
            {
                ImGui.TextDisabled("No sockets found.");
            }
        }
    }

    public void Dispose()
    {
    }
}
