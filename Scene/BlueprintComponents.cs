using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;

namespace UExplorer.Scene;

/// <summary>
/// Component hierarchy of a blueprint class: native components of the CDO, then every SimpleConstructionScript of the
/// class chain (base first), with InheritableComponentHandler overrides from derived classes, parented through
/// ChildNodes / ParentComponentOrVariableName and AttachToName sockets.
/// </summary>
public static class BlueprintComponents
{
    /// <param name="skipNames">components already provided by the caller (level instance exports)</param>
    /// <returns>top level nodes (Parent == null) plus their descendants via Children</returns>
    public static List<SceneObject> Build(UBlueprintGeneratedClass bp, WorldScene? scene, bool isHlod, ISet<string>? skipNames = null)
        => BuildForLevel(bp, scene?.GetOrCreatePersistentLevel(), isHlod, skipNames);

    internal static List<SceneObject> BuildForLevel(UBlueprintGeneratedClass bp, SceneLevel? level, bool isHlod, ISet<string>? skipNames = null)
    {
        var roots = new List<SceneObject>();
        var byName = new Dictionary<string, SceneObject>(StringComparer.OrdinalIgnoreCase);

        // class chain, base first
        var chain = new List<UStruct>();
        UStruct? current = bp;
        for (var depth = 0; current is not null && depth < 16; depth++)
        {
            chain.Insert(0, current);
            current = UnrealSpace.Load(current.SuperStruct) as UStruct;
        }

        // overrides of inherited SCS templates, most derived class wins
        var overrides = new Dictionary<string, UObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in chain)
        {
            if (UnrealSpace.Load(cls.GetOrDefault<FPackageIndex?>("InheritableComponentHandler")) is not { } handler) continue;
            foreach (var record in handler.GetOrDefault("Records", Array.Empty<FStructFallback>()))
            {
                var key = record.GetOrDefault<FStructFallback?>("ComponentKey");
                var variable = key?.GetOrDefault<FName>("SCSVariableName");
                if (variable is { IsNone: false } v && UnrealSpace.Load(record.GetOrDefault<FPackageIndex?>("ComponentTemplate")) is { } template)
                    overrides[v.Text] = template;
            }
        }

        void Add(SceneObject node, SceneObject? parent, string? socket)
        {
            if (parent is null) roots.Add(node);
            else
            {
                node.Parent = parent;
                parent.Children.Add(node);
            }
            if (socket is not null) node.AttachSocketName = socket;
        }

        // native components on the class default object (e.g. StaticMeshComponent of a StaticMeshActor based blueprint)
        if (UnrealSpace.Load(bp.ClassDefaultObject) is { } cdo &&
            UnrealSpace.TryGetInherited(cdo, "RootComponent", out FPackageIndex rootIdx) &&
            UnrealSpace.Load(rootIdx) is { } nativeRoot &&
            !(skipNames?.Contains(nativeRoot.Name) ?? false) &&
            WorldLoader.CreateComponentNodeForLevel(nativeRoot, level, isHlod) is { } nativeNode)
        {
            byName[nativeRoot.Name] = nativeNode;
            Add(nativeNode, null, null);
        }

        foreach (var cls in chain)
        {
            if (UnrealSpace.Load(cls.GetOrDefault<FPackageIndex?>("SimpleConstructionScript")) is not { } scs) continue;
            var rootNodes = scs.GetOrDefault("RootNodes", Array.Empty<FPackageIndex>());
            foreach (var rootNode in rootNodes)
                Visit(UnrealSpace.Load(rootNode), null);
        }

        void Visit(UObject? scsNode, SceneObject? parent)
        {
            if (scsNode is null) return;
            var variable = scsNode.GetOrDefault<FName>("InternalVariableName");
            var name = variable.IsNone ? scsNode.Name : variable.Text;

            var template = overrides.GetValueOrDefault(name) ?? UnrealSpace.Load(scsNode.GetOrDefault<FPackageIndex?>("ComponentTemplate"));
            SceneObject? node = null;
            if (template is not null && !(skipNames?.Contains(name) ?? false) && !(skipNames?.Contains(template.Name) ?? false))
            {
                node = WorldLoader.CreateComponentNodeForLevel(template, level, isHlod);
                if (node is not null)
                {
                    node.Name = name;
                    var attachParent = parent;
                    if (attachParent is null)
                    {
                        var parentName = scsNode.GetOrDefault<FName>("ParentComponentOrVariableName");
                        if (!parentName.IsNone) byName.TryGetValue(parentName.Text, out attachParent);
                    }
                    var socket = scsNode.GetOrDefault<FName>("AttachToName");
                    Add(node, attachParent ?? (roots.Count > 0 && !ReferenceEquals(roots[0], node) ? roots[0] : null), socket.IsNone ? null : socket.Text);
                    byName[name] = node;
                }
            }

            foreach (var child in scsNode.GetOrDefault("ChildNodes", Array.Empty<FPackageIndex>()))
                Visit(UnrealSpace.Load(child), node ?? parent);
        }

        return roots;
    }
}
