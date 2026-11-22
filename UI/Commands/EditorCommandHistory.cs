using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using UExplorer.Scene;

namespace UExplorer.UI.Commands;

public interface IEditorCommand
{
    string Description { get; }
    void Execute();
    void Undo();
    bool ReferencesLevel(SceneLevel level) => false;
}

public readonly record struct TransformSnapshot(
    Vector3 LocationCm,
    Vector3 RotationDeg,
    Quaternion RotationQuat,
    Vector3 Scale,
    Matrix4x4? OverrideLocalMatrix)
{
    public static TransformSnapshot Capture(SceneObject obj) =>
        new(obj.LocationCm, obj.RotationDeg, obj.RotationQuat, obj.Scale, obj.OverrideLocalMatrix);

    public void Apply(SceneObject obj)
    {
        obj.LocationCm = LocationCm;
        obj.RotationDeg = RotationDeg;
        obj.RotationQuat = RotationQuat;
        obj.Scale = Scale;
        obj.OverrideLocalMatrix = OverrideLocalMatrix;
    }
}

public readonly record struct LightSnapshot(
    float Intensity,
    Vector3 Color,
    bool CastShadows,
    float AttenuationRadius,
    float InnerConeAngle,
    float OuterConeAngle,
    float SourceWidth,
    float SourceHeight)
{
    public static LightSnapshot Capture(SceneLight light) => new(
        light.Intensity,
        light.Color,
        light.CastShadows,
        light is LocalLightComponent ll ? ll.AttenuationRadius : 0f,
        light is SceneSpotLight sl ? sl.InnerConeAngle : 0f,
        light is SceneSpotLight sl2 ? sl2.OuterConeAngle : 0f,
        light is SceneRectLight rl ? rl.SourceWidth : 0f,
        light is SceneRectLight rl2 ? rl2.SourceHeight : 0f);

    public void Apply(SceneLight light)
    {
        light.Intensity = Intensity;
        light.Color = Color;
        light.CastShadows = CastShadows;
        if (light is LocalLightComponent ll)
            ll.AttenuationRadius = AttenuationRadius;
        if (light is SceneSpotLight sl)
        {
            sl.InnerConeAngle = InnerConeAngle;
            sl.OuterConeAngle = OuterConeAngle;
        }
        if (light is SceneRectLight rl)
        {
            rl.SourceWidth = SourceWidth;
            rl.SourceHeight = SourceHeight;
        }
    }
}

public sealed class EditorCommandHistory
{
    private readonly List<IEditorCommand> _undo = [];
    private readonly List<IEditorCommand> _redo = [];

    public int MaxDepth { get; }

    public EditorCommandHistory(int maxDepth = 64)
    {
        MaxDepth = Math.Max(1, maxDepth);
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? NextUndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;
    public string? NextRedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    public void Execute(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Execute();
        RecordExecuted(command);
    }

    public void RecordExecuted(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _undo.Add(command);
        _redo.Clear();
        while (_undo.Count > MaxDepth)
            _undo.RemoveAt(0);
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
            return false;
        var cmd = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        cmd.Undo();
        _redo.Add(cmd);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
            return false;
        var cmd = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        cmd.Execute();
        _undo.Add(cmd);
        while (_undo.Count > MaxDepth)
            _undo.RemoveAt(0);
        return true;
    }

    public void PruneForLevel(SceneLevel unloadedLevel)
    {
        ArgumentNullException.ThrowIfNull(unloadedLevel);
        for (var i = _undo.Count - 1; i >= 0; i--)
        {
            if (_undo[i].ReferencesLevel(unloadedLevel))
                _undo.RemoveAt(i);
        }
        for (var i = _redo.Count - 1; i >= 0; i--)
        {
            if (_redo[i].ReferencesLevel(unloadedLevel))
                _redo.RemoveAt(i);
        }
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}

public sealed class EditorClipboard
{
    public List<SceneObject> Items { get; } = [];
    public Vector3 SourceAnchorCm { get; private set; }
    public bool HasItems => Items.Count > 0;

    public void Copy(IReadOnlyList<SceneObject> targets)
    {
        Items.Clear();
        var sum = Vector3.Zero;
        foreach (var target in targets)
        {
            if (string.Equals(target.ClassName, "LevelStreaming", StringComparison.Ordinal))
                continue;
            var clone = target.DeepClone();
            sum += target.WorldLocationCm;
            Items.Add(clone);
        }
        SourceAnchorCm = Items.Count > 0 ? sum / Items.Count : Vector3.Zero;
    }

    public void Clear()
    {
        Items.Clear();
        SourceAnchorCm = Vector3.Zero;
    }
}

public sealed class TransformCommand(
    WorldScene scene,
    SceneObject target,
    TransformSnapshot before,
    TransformSnapshot after,
    string description = "Transform") : IEditorCommand
{
    private readonly SceneLevel? _owningLevel = scene.FindOwningLevel(target);

    public string Description => description;

    public void Execute()
    {
        after.Apply(target);
        scene.MarkDirty();
    }

    public void Undo()
    {
        before.Apply(target);
        scene.MarkDirty();
    }

    public bool ReferencesLevel(SceneLevel level) =>
        ReferenceEquals(_owningLevel, level) || ReferenceEquals(scene.FindOwningLevel(target), level);
}

public sealed class DeleteObjectsCommand(
    WorldScene scene,
    IReadOnlyList<SceneObject> targets,
    string description = "Delete Actor") : IEditorCommand
{
    private readonly List<SceneObject> _targets = [.. targets];
    private readonly List<RemovedObjectEntry> _removed = [];

    public string Description => description;

    public void Execute()
    {
        _removed.Clear();
        foreach (var target in _targets)
        {
            if (scene.RemoveObjectFromLevel(target) is { } entry)
                _removed.Add(entry);
        }
    }

    public void Undo()
    {
        SceneObject? firstRestored = null;
        for (var i = _removed.Count - 1; i >= 0; i--)
        {
            var entry = _removed[i];
            scene.InsertObjectIntoLevel(entry);
            firstRestored ??= entry.Obj;
        }
        // RebuildAggregates() inside InsertObjectIntoLevel clears SelectedObject if root was missing;
        // restore selection strictly AFTER RebuildAggregates().
        if (firstRestored is not null)
            scene.Select(firstRestored);
    }

    public bool ReferencesLevel(SceneLevel level)
    {
        foreach (var r in _removed)
            if (ReferenceEquals(r.Level, level)) return true;
        foreach (var t in _targets)
            if (ReferenceEquals(scene.FindOwningLevel(t), level)) return true;
        return false;
    }
}

public sealed class SpawnObjectsCommand(
    WorldScene scene,
    SceneLevel targetLevel,
    IReadOnlyList<SceneObject> objects,
    SceneObject? parent = null,
    string description = "Place Actor") : IEditorCommand
{
    private readonly List<SceneObject> _objects = [.. objects];

    public string Description => description;

    public void Execute()
    {
        SceneObject? last = null;
        foreach (var obj in _objects)
        {
            scene.InsertObjectIntoLevel(obj, targetLevel, parent);
            last = obj;
        }
        if (last is not null)
            scene.Select(last);
    }

    public void Undo()
    {
        for (var i = _objects.Count - 1; i >= 0; i--)
            scene.RemoveObjectFromLevel(_objects[i]);
    }

    public bool ReferencesLevel(SceneLevel level) => ReferenceEquals(targetLevel, level);
}

public sealed class RenameObjectCommand(
    WorldScene scene,
    SceneObject target,
    string oldName,
    string newName) : IEditorCommand
{
    private readonly SceneLevel? _owningLevel = scene.FindOwningLevel(target);

    public string Description => "Rename Actor";

    public void Execute()
    {
        target.Name = newName;
        scene.MarkDirty();
    }

    public void Undo()
    {
        target.Name = oldName;
        scene.MarkDirty();
    }

    public bool ReferencesLevel(SceneLevel level) =>
        ReferenceEquals(_owningLevel, level) || ReferenceEquals(scene.FindOwningLevel(target), level);
}

public sealed class VisibilityCommand(
    WorldScene scene,
    IReadOnlyList<(SceneObject Obj, bool OldVis, bool NewVis)> items,
    string description = "Toggle Visibility") : IEditorCommand
{
    private readonly List<(SceneObject Obj, bool OldVis, bool NewVis)> _items = [.. items];
    private readonly HashSet<SceneLevel> _owningLevels = CaptureOwningLevels(scene, items);

    private static HashSet<SceneLevel> CaptureOwningLevels(
        WorldScene scene,
        IReadOnlyList<(SceneObject Obj, bool OldVis, bool NewVis)> items)
    {
        var set = new HashSet<SceneLevel>(ReferenceEqualityComparer.Instance);
        foreach (var (obj, _, _) in items)
        {
            if (scene.FindOwningLevel(obj) is { } lvl)
                set.Add(lvl);
        }
        return set;
    }

    public string Description => description;

    public void Execute()
    {
        foreach (var (obj, _, newVis) in _items)
            obj.IsVisible = newVis;
        scene.MarkDirty();
    }

    public void Undo()
    {
        foreach (var (obj, oldVis, _) in _items)
            obj.IsVisible = oldVis;
        scene.MarkDirty();
    }

    public bool ReferencesLevel(SceneLevel level)
    {
        if (_owningLevels.Contains(level))
            return true;
        foreach (var (obj, _, _) in _items)
            if (ReferenceEquals(scene.FindOwningLevel(obj), level)) return true;
        return false;
    }
}

public sealed class MaterialSlotCommand(
    WorldScene scene,
    SceneMesh mesh,
    MaterialAsset?[] oldMaterials,
    MaterialAsset?[] newMaterials,
    string description = "Assign Material") : IEditorCommand
{
    private readonly SceneLevel? _owningLevel = scene.FindOwningLevel(mesh);

    public string Description => description;

    public void Execute()
    {
        mesh.Materials = [.. newMaterials];
        scene.MarkDirty();
    }

    public void Undo()
    {
        mesh.Materials = [.. oldMaterials];
        scene.MarkDirty();
    }

    public bool ReferencesLevel(SceneLevel level) =>
        ReferenceEquals(_owningLevel, level) || ReferenceEquals(scene.FindOwningLevel(mesh), level);
}

public sealed class StaticMeshReplaceCommand(
    WorldScene scene,
    SceneMesh mesh,
    UStaticMesh oldMesh,
    MaterialAsset?[] oldMaterials,
    UStaticMesh newMesh,
    MaterialAsset?[] newMaterials,
    string description = "Replace Static Mesh") : IEditorCommand
{
    private readonly SceneLevel? _owningLevel = scene.FindOwningLevel(mesh);

    public string Description => description;

    public void Execute()
    {
        mesh.Mesh = newMesh;
        mesh.Materials = [.. newMaterials];
        scene.MarkDirty();
    }

    public void Undo()
    {
        mesh.Mesh = oldMesh;
        mesh.Materials = [.. oldMaterials];
        scene.MarkDirty();
    }

    public bool ReferencesLevel(SceneLevel level) =>
        ReferenceEquals(_owningLevel, level) || ReferenceEquals(scene.FindOwningLevel(mesh), level);
}

public sealed class LightPropertyCommand(
    WorldScene scene,
    SceneLight light,
    LightSnapshot before,
    LightSnapshot after,
    string description = "Edit Light") : IEditorCommand
{
    private readonly SceneLevel? _owningLevel = scene.FindOwningLevel(light);

    public string Description => description;

    public void Execute()
    {
        after.Apply(light);
        scene.MarkDirty();
    }

    public void Undo()
    {
        before.Apply(light);
        scene.MarkDirty();
    }

    public bool ReferencesLevel(SceneLevel level) =>
        ReferenceEquals(_owningLevel, level) || ReferenceEquals(scene.FindOwningLevel(light), level);
}

public sealed class MoveToLevelCommand(
    WorldScene scene,
    SceneObject target,
    SceneLevel destLevel) : IEditorCommand
{
    private RemovedObjectEntry? _originalEntry;
    private Matrix4x4 _worldMatrix;

    public string Description => $"Move to {destLevel.Name}";

    public void Execute()
    {
        _worldMatrix = target.WorldMatrix;
        _originalEntry = scene.RemoveObjectFromLevel(target);
        if (_originalEntry is null)
            return;

        scene.InsertObjectIntoLevel(target, destLevel);
        target.SetWorldMatrix(_worldMatrix);
        scene.RebuildAggregates();
        scene.Select(target);
    }

    public void Undo()
    {
        if (_originalEntry is not { } orig)
            return;
        scene.RemoveObjectFromLevel(target);
        scene.InsertObjectIntoLevel(orig);
        target.SetWorldMatrix(_worldMatrix);
        scene.RebuildAggregates();
        scene.Select(target);
    }

    public bool ReferencesLevel(SceneLevel level) =>
        ReferenceEquals(destLevel, level) ||
        (_originalEntry is { } orig && ReferenceEquals(orig.Level, level)) ||
        ReferenceEquals(scene.FindOwningLevel(target), level);
}

public sealed class WorldSettingsCommand(
    WorldScene scene,
    WorldSettingsState before,
    WorldSettingsState after,
    string description = "World Settings") : IEditorCommand
{
    private readonly WorldSettingsState _before = before.Clone();
    private readonly WorldSettingsState _after = after.Clone();

    public string Description => description;

    public void Execute()
    {
        scene.WorldSettings.CopyFrom(_after);
        scene.MarkDirty();
    }

    public void Undo()
    {
        scene.WorldSettings.CopyFrom(_before);
        scene.MarkDirty();
    }
}
