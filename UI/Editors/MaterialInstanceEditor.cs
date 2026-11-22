using ImGuiNET;
using System.Numerics;
using UExplorer.Scene;

namespace UExplorer.UI.Editors;

public sealed class MaterialInstanceEditor : IDisposable
{
    private readonly MaterialAsset _material;

    public MaterialInstanceEditor(MaterialAsset material)
    {
        _material = material;
    }

    public void Draw()
    {
        if (ImGui.CollapsingHeader("Scalar Parameters", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // Scalar parameters
            foreach (var (name, value) in _material.NativeScalarOverrides)
            {
                float val = value;
                if (ImGui.DragFloat(name, ref val))
                {
                    _material.NativeScalarOverrides[name] = val;
                    _material.NotifyParametersChanged();
                }
            }
        }

        if (ImGui.CollapsingHeader("Vector Parameters", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // Vector parameters
            foreach (var (name, value) in _material.NativeVectorOverrides)
            {
                Vector4 val = value;
                if (ImGui.ColorEdit4(name, ref val))
                {
                    _material.NativeVectorOverrides[name] = val;
                    _material.NotifyParametersChanged();
                }
            }
        }

        if (ImGui.CollapsingHeader("Texture Parameters", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // Texture parameters
            foreach (var (name, path) in _material.NativeTextureOverrides)
            {
                ImGui.Text($"{name}: {path}");
            }
        }
    }

    public void Dispose()
    {
    }
}
