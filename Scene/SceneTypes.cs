using System.Numerics;
using CUE4Parse_Conversion.Options;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using UExplorer.Rendering;

namespace UExplorer.Scene;

public static class ExplorerBridge
{
    public static int MaxTextureMipSize { get; set; } = 1024;
    private static ExportOptions? _exportOptions;
    public static ExportOptions ToExportOptions() => _exportOptions ??= new ExportOptions(
        naniteMeshFormat: ENaniteMeshFormat.NoNanite,
        meshQuality: EMeshQuality.All,
        texturePlatform: CUE4Parse.UE4.Assets.Exports.Texture.ETexturePlatform.DesktopMobile,
        exportMaterials: true,
        exportMorphTargets: true,
        socketFormat: ESocketFormat.Bone,
        includeDisabledMeshSections: false
    );
}

public class SceneObject
{
    private static ulong _nextRuntimeId;
    public ulong RuntimeId { get; } = Interlocked.Increment(ref _nextRuntimeId);
    public string Name { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public UObject? SourceObject { get; set; }
    public SceneObject? Parent { get; set; }
    public List<SceneObject> Children { get; } = [];

    public Vector3 LocationCm { get; set; }
    public Vector3 RotationDeg { get; set; } // Pitch, Yaw, Roll
    public Vector3 Scale { get; set; } = Vector3.One;
    public Quaternion RotationQuat { get; set; } = Quaternion.Identity;
    public bool AbsLocation { get; set; }
    public bool AbsRotation { get; set; }
    public bool AbsScale { get; set; }
    public Matrix4x4? OverrideLocalMatrix { get; set; }
    /// <summary>Socket transform (meters space) between this component and its attach parent, when attached to a socket.</summary>
    public Matrix4x4? AttachSocketMatrix { get; set; }
    public string? AttachSocketName { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool IsSelected { get; set; }

    public void SetRotationFromUnreal(FRotator rot)
    {
        RotationDeg = new Vector3(rot.Pitch, rot.Yaw, rot.Roll);
        var q = rot.Quaternion();
        RotationQuat = new Quaternion(q.X, q.Z, q.Y, -q.W);
    }

    public void SetRotationDeg(Vector3 pitchYawRoll) =>
        SetRotationFromUnreal(new FRotator(pitchYawRoll.X, pitchYawRoll.Y, pitchYawRoll.Z));

    // Snooper-compatible meters Y-up local matrix (matches NativeSceneRenderer & StaticMeshDto)
    public Matrix4x4 LocalMatrix =>
        OverrideLocalMatrix ??
        (Matrix4x4.CreateScale(new Vector3(Scale.X, Scale.Z, Scale.Y)) *
         Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(RotationQuat)) *
         Matrix4x4.CreateTranslation(new Vector3(LocationCm.X, LocationCm.Z, LocationCm.Y) * 0.01f));

    public Matrix4x4 WorldMatrix => ComputeWorldMatrix(0);

    private Matrix4x4 ComputeWorldMatrix(int depth)
    {
        if (Parent is null || depth >= 64)
            return LocalMatrix;
        var parentWorld = Parent.ComputeWorldMatrix(depth + 1);
        var pm = AttachSocketMatrix is { } socket ? socket * parentWorld : parentWorld;
        if (!AbsLocation && !AbsRotation && !AbsScale)
            return LocalMatrix * pm;

        var localPos = new Vector3(LocationCm.X, LocationCm.Z, LocationCm.Y) * 0.01f;
        var localRot = Quaternion.Normalize(RotationQuat);
        var localScl = new Vector3(Scale.X, Scale.Z, Scale.Y);

        Matrix4x4.Decompose(pm, out var pScl, out var pRot, out _);
        var wPos = AbsLocation ? localPos : Vector3.Transform(localPos, pm);
        var wRot = AbsRotation ? localRot : pRot * localRot;
        var wScl = AbsScale ? localScl : localScl * pScl;
        return Matrix4x4.CreateScale(wScl) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(wRot)) * Matrix4x4.CreateTranslation(wPos);
    }

    /// <summary>Parent world matrix including the socket, i.e. the space LocalMatrix is relative to.</summary>
    public Matrix4x4 ParentSpaceMatrix => Parent is null ? Matrix4x4.Identity : AttachSocketMatrix is { } socket ? socket * Parent.ComputeWorldMatrix(1) : Parent.ComputeWorldMatrix(1);

    /// <summary>Writes a meters-space world matrix (from the gizmo) back into the Unreal relative transform.</summary>
    public void SetWorldMatrix(Matrix4x4 world)
    {
        var local = world;
        if (Parent is not null && Matrix4x4.Invert(ParentSpaceMatrix, out var inverseParent) && !AbsLocation && !AbsRotation && !AbsScale)
            local = world * inverseParent;
        if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var translation)) return;

        OverrideLocalMatrix = null;
        LocationCm = new Vector3(translation.X, translation.Z, translation.Y) * 100f;
        Scale = new Vector3(scale.X, scale.Z, scale.Y);
        RotationQuat = Quaternion.Normalize(rotation);
        var rotator = UnrealSpace.ToUnrealQuat(RotationQuat).Rotator();
        RotationDeg = new Vector3(rotator.Pitch, rotator.Yaw, rotator.Roll);
    }

    public bool IsSelectedRecursive
    {
        get
        {
            var cur = this;
            for (var d = 0; cur is not null && d < 64; d++, cur = cur.Parent)
                if (cur.IsSelected) return true;
            return false;
        }
    }

    // Unreal cm Z-up world translation for billboards/gizmos
    public Vector3 WorldLocationCm => new(WorldMatrix.Translation.X * 100f, WorldMatrix.Translation.Z * 100f, WorldMatrix.Translation.Y * 100f);

    public SceneObject? Actor => Parent ?? this;

    public bool IsVisibleRecursive
    {
        get
        {
            var cur = this;
            for (var d = 0; cur is not null && d < 64; d++, cur = cur.Parent)
                if (!cur.IsVisible) return false;
            return true;
        }
    }
}

public sealed class SceneMesh : SceneObject
{
    public required UStaticMesh Mesh { get; set; }
    public MaterialAsset?[] Materials { get; set; } = [];
    public List<Matrix4x4>? InstanceLocalMatrices { get; set; }
    public bool IsHlod { get; set; }
}

public abstract class SceneLight : SceneObject
{
    public Vector3 Color { get; set; } = Vector3.One;
    public float Intensity { get; set; } = 1.0f;
    public bool CastShadows { get; set; }
}

public abstract class LocalLightComponent : SceneLight
{
    public float AttenuationRadius { get; set; } = 1000f;
    public float SourceRadius { get; set; }
    public float SourceLength { get; set; }
    public float SoftSourceRadius { get; set; }
    public bool UseInverseSquaredFalloff { get; set; } = true;
    public float LightFalloffExponent { get; set; } = 8f;
    public CUE4Parse.UE4.Objects.Engine.ELightUnits IntensityUnits { get; set; } = CUE4Parse.UE4.Objects.Engine.ELightUnits.Unitless;
    public float MaxDrawDistance { get; set; }
    public float MaxDistanceFadeRange { get; set; }
}

public sealed class ScenePointLight : LocalLightComponent
{
}

public sealed class SceneSpotLight : LocalLightComponent
{
    public float InnerConeAngle { get; set; } = 22f;
    public float OuterConeAngle { get; set; } = 44f;
}

public sealed class SceneRectLight : LocalLightComponent
{
    public float SourceWidth { get; set; } = 64f;
    public float SourceHeight { get; set; } = 64f;
    public float Width => SourceWidth;
    public float Height => SourceHeight;
    public float BarnDoorAngle { get; set; } = 88f;
    public float BarnDoorLength { get; set; } = 20f;
}

public sealed class SceneDirectionalLight : SceneLight
{
    public bool IsAtmosphereSun { get; set; } = true;
    public float Daylight { get; set; } = 1.0f;
    public float SourceAngle { get; set; } = 0.5357f;

    public Vector3 Direction => Vector3.Transform(Vector3.UnitX,
        Matrix4x4.CreateFromYawPitchRoll(
            RotationDeg.Y * (MathF.PI / 180f),
            RotationDeg.X * (MathF.PI / 180f),
            RotationDeg.Z * (MathF.PI / 180f)));

    public Vector3 GetDirection() => Direction;
}

public sealed class SceneSkyLight : SceneLight
{
    public float SkyDistanceThreshold { get; set; } = 150000f;
    public Vector3 LowerHemisphereColor { get; set; } = new(0.10f, 0.11f, 0.13f);
    public CUE4Parse.UE4.Assets.Exports.Texture.UTextureCube? Cubemap { get; set; }
}

public sealed class SceneHeightFog : SceneObject
{
    public float FogDensity { get; set; } = 0.02f;
    public float FogHeightFalloff { get; set; } = 0.2f;
    public Vector3 FogInscatteringColor { get; set; } = new(0.45f, 0.58f, 0.85f);
    public float FogMaxOpacity { get; set; } = 1f;
    public float StartDistance { get; set; }

    public Vector3 InscatteringColor => FogInscatteringColor;
    public float Density => FogDensity;
    public float HeightFalloff => FogHeightFalloff;
    public float Height => LocationCm.Z * 0.01f;
    public float MaxOpacity => FogMaxOpacity;
    public Vector3 DirectionalInscatteringColor { get; set; } = new(0.25f, 0.25f, 0.25f);
    public float DirectionalInscatteringExponent { get; set; } = 4f;
}

public sealed class SceneNiagara : SceneObject
{
    public required NiagaraAsset Asset { get; init; }
    private NiagaraSystemVmSimulation? _systemSim;
    private readonly Dictionary<NiagaraMeshEmitterDesc, NiagaraEmitterVmSimulation?> _meshSims = new();
    private readonly Dictionary<NiagaraSpriteEmitterDesc, NiagaraEmitterVmSimulation?> _spriteSims = new();

    private NiagaraSystemVmSimulation? EnsureSystemSim()
    {
        if (_systemSim is null && Asset.SystemSimulation is { } sysTemplate)
            _systemSim = sysTemplate.Clone();
        return _systemSim;
    }

    public NiagaraEmitterVmSimulation? GetSim(NiagaraMeshEmitterDesc desc)
    {
        if (!_meshSims.TryGetValue(desc, out var sim))
        {
            var sys = EnsureSystemSim();
            sim = sys is not null ? sys.GetClonedSim(desc.VmSimulation) : desc.VmSimulation?.Clone();
            _meshSims[desc] = sim;
        }
        return sim;
    }

    public NiagaraEmitterVmSimulation? GetSim(NiagaraSpriteEmitterDesc desc)
    {
        if (!_spriteSims.TryGetValue(desc, out var sim))
        {
            var sys = EnsureSystemSim();
            sim = sys is not null ? sys.GetClonedSim(desc.VmSimulation) : desc.VmSimulation?.Clone();
            _spriteSims[desc] = sim;
        }
        return sim;
    }
}

public readonly record struct LandscapeLayerMapping(int TextureIndex, int ChannelIndex, Vector4 DebugColor);

public static class LandscapeSystem
{
    public const string VisibilityLayer = "__VISIBILITY__";
}

public sealed class SceneLandscape : SceneObject
{
    public CUE4Parse.UE4.Assets.Exports.Component.Landscape.ULandscapeComponent SourceComponent { get; init; } = null!;
    public Dictionary<string, LandscapeLayerMapping> Layers { get; } = new();
    public Vector3[] Positions { get; init; } = [];
    public Vector3[] Normals { get; init; } = [];
    public Vector2[] UVs { get; init; } = [];
    public uint[] Indices { get; init; } = [];
    public MaterialAsset? Material { get; init; }
    public MaterialAsset?[] Materials => Material is not null ? [Material] : [];
}

public sealed class ScenePostProcess
{
    public float? AutoExposureBias { get; set; }
    public float? AutoExposureMinEV100 { get; set; }
    public float? AutoExposureMaxEV100 { get; set; }
    public float? BloomIntensity { get; set; }
    public float Priority { get; set; } = float.NegativeInfinity;
}

public sealed class WorldSettingsState
{
    public bool SkyEnabled { get; set; } = true;
    public bool TodmEnabled { get; set; } = true;
    public bool FogEnabled { get; set; } = true;
    public float SunIntensityMultiplier { get; set; } = 1f;
    public float SkyIntensityMultiplier { get; set; } = 1f;

    public void Reset()
    {
        SkyEnabled = true;
        TodmEnabled = true;
        FogEnabled = true;
        SunIntensityMultiplier = 1f;
        SkyIntensityMultiplier = 1f;
    }

    public WorldSettingsState Clone() => new()
    {
        SkyEnabled = SkyEnabled,
        TodmEnabled = TodmEnabled,
        FogEnabled = FogEnabled,
        SunIntensityMultiplier = SunIntensityMultiplier,
        SkyIntensityMultiplier = SkyIntensityMultiplier
    };

    public void CopyFrom(WorldSettingsState src)
    {
        ArgumentNullException.ThrowIfNull(src);
        SkyEnabled = src.SkyEnabled;
        TodmEnabled = src.TodmEnabled;
        FogEnabled = src.FogEnabled;
        SunIntensityMultiplier = src.SunIntensityMultiplier;
        SkyIntensityMultiplier = src.SkyIntensityMultiplier;
    }
}

public readonly record struct RemovedObjectEntry(
    SceneObject Obj,
    SceneLevel Level,
    SceneObject? Parent,
    int IndexInParent,
    bool WasHeightFog);

public static class SceneObjectExtensions
{
    public static SceneObject DeepClone(this SceneObject src)
    {
        ArgumentNullException.ThrowIfNull(src);
        SceneObject clone = src switch
        {
            SceneMesh sm => CloneMesh(sm),
            SceneNiagara sn => new SceneNiagara { Asset = sn.Asset },
            ScenePointLight pl => CopyLocalLight(pl, new ScenePointLight()),
            SceneSpotLight sl => CopySpotLight(sl),
            SceneRectLight rl => CopyRectLight(rl),
            SceneDirectionalLight dl => CopyDirectionalLight(dl),
            SceneSkyLight sky => CopySkyLight(sky),
            SceneHeightFog fog => CopyHeightFog(fog),
            SceneLandscape land => CopyLandscape(land),
            _ => new SceneObject()
        };

        clone.Name = src.Name;
        clone.ClassName = src.ClassName;
        clone.Path = src.Path;
        clone.SourceObject = src.SourceObject;
        clone.LocationCm = src.LocationCm;
        clone.RotationDeg = src.RotationDeg;
        clone.Scale = src.Scale;
        clone.RotationQuat = src.RotationQuat;
        clone.AbsLocation = src.AbsLocation;
        clone.AbsRotation = src.AbsRotation;
        clone.AbsScale = src.AbsScale;
        clone.OverrideLocalMatrix = src.OverrideLocalMatrix;
        clone.AttachSocketMatrix = src.AttachSocketMatrix;
        clone.AttachSocketName = src.AttachSocketName;
        clone.IsVisible = src.IsVisible;
        clone.IsSelected = false;

        foreach (var child in src.Children)
        {
            var childClone = child.DeepClone();
            childClone.Parent = clone;
            clone.Children.Add(childClone);
        }

        return clone;
    }

    private static SceneMesh CloneMesh(SceneMesh sm)
    {
        var mats = new MaterialAsset?[sm.Materials.Length];
        for (var i = 0; i < sm.Materials.Length; i++)
        {
            var m = sm.Materials[i];
            if (m is null) continue;
            if (m.CookedMaterial is { } cooked)
            {
                var copy = new MaterialAsset(cooked);
                copy.CopyOverridesFrom(m);
                mats[i] = copy;
            }
            else
            {
                mats[i] = m;
            }
        }
        return new SceneMesh
        {
            Mesh = sm.Mesh,
            Materials = mats,
            InstanceLocalMatrices = sm.InstanceLocalMatrices is { Count: > 0 } inst ? [.. inst] : null,
            IsHlod = sm.IsHlod
        };
    }

    private static T CopyBaseLight<T>(SceneLight src, T dst) where T : SceneLight
    {
        dst.Color = src.Color;
        dst.Intensity = src.Intensity;
        dst.CastShadows = src.CastShadows;
        return dst;
    }

    private static T CopyLocalLight<T>(LocalLightComponent src, T dst) where T : LocalLightComponent
    {
        CopyBaseLight(src, dst);
        dst.AttenuationRadius = src.AttenuationRadius;
        dst.SourceRadius = src.SourceRadius;
        dst.SourceLength = src.SourceLength;
        dst.SoftSourceRadius = src.SoftSourceRadius;
        dst.UseInverseSquaredFalloff = src.UseInverseSquaredFalloff;
        dst.LightFalloffExponent = src.LightFalloffExponent;
        dst.IntensityUnits = src.IntensityUnits;
        dst.MaxDrawDistance = src.MaxDrawDistance;
        dst.MaxDistanceFadeRange = src.MaxDistanceFadeRange;
        return dst;
    }

    private static SceneSpotLight CopySpotLight(SceneSpotLight src)
    {
        var dst = CopyLocalLight(src, new SceneSpotLight());
        dst.InnerConeAngle = src.InnerConeAngle;
        dst.OuterConeAngle = src.OuterConeAngle;
        return dst;
    }

    private static SceneRectLight CopyRectLight(SceneRectLight src)
    {
        var dst = CopyLocalLight(src, new SceneRectLight());
        dst.SourceWidth = src.SourceWidth;
        dst.SourceHeight = src.SourceHeight;
        dst.BarnDoorAngle = src.BarnDoorAngle;
        dst.BarnDoorLength = src.BarnDoorLength;
        return dst;
    }

    private static SceneDirectionalLight CopyDirectionalLight(SceneDirectionalLight src)
    {
        var dst = CopyBaseLight(src, new SceneDirectionalLight());
        dst.IsAtmosphereSun = src.IsAtmosphereSun;
        dst.Daylight = src.Daylight;
        dst.SourceAngle = src.SourceAngle;
        return dst;
    }

    private static SceneSkyLight CopySkyLight(SceneSkyLight src)
    {
        var dst = CopyBaseLight(src, new SceneSkyLight());
        dst.SkyDistanceThreshold = src.SkyDistanceThreshold;
        dst.LowerHemisphereColor = src.LowerHemisphereColor;
        dst.Cubemap = src.Cubemap;
        return dst;
    }

    private static SceneHeightFog CopyHeightFog(SceneHeightFog src) => new()
    {
        FogDensity = src.FogDensity,
        FogHeightFalloff = src.FogHeightFalloff,
        FogInscatteringColor = src.FogInscatteringColor,
        FogMaxOpacity = src.FogMaxOpacity,
        StartDistance = src.StartDistance,
        DirectionalInscatteringColor = src.DirectionalInscatteringColor,
        DirectionalInscatteringExponent = src.DirectionalInscatteringExponent
    };

    private static SceneLandscape CopyLandscape(SceneLandscape src)
    {
        var dst = new SceneLandscape
        {
            SourceComponent = src.SourceComponent,
            Positions = src.Positions,
            Normals = src.Normals,
            UVs = src.UVs,
            Indices = src.Indices,
            Material = src.Material
        };
        foreach (var kv in src.Layers)
            dst.Layers[kv.Key] = kv.Value;
        return dst;
    }
}

public sealed record StreamingLevelDescriptor(
    string Name,
    string PackagePath,
    Matrix4x4 LevelTransform,
    bool IsAlwaysLoaded,
    bool InitiallyLoaded,
    bool InitiallyVisible,
    string StreamingClass,
    bool IsResolvable);

public sealed class WorldPartitionCellDescriptor
{
    public required string Name { get; init; }
    public required string PackagePath { get; init; }
    public Vector3 BoundsMinCm { get; init; }
    public Vector3 BoundsMaxCm { get; init; }
    public Vector3 CenterCm => (BoundsMinCm + BoundsMaxCm) * 0.5f;
    public float ExtentRadiusCm => (BoundsMaxCm - BoundsMinCm).Length() * 0.5f;
    public int HierarchicalLevel { get; init; }
    public string GridName { get; init; } = string.Empty;
    public bool IsSpatiallyLoaded { get; init; }
    public bool IsHlod { get; init; }
    public bool IsLoaded { get; set; }
    public bool IsLoading { get; set; }
    public SceneLevel? LoadedLevel { get; set; }
}

public sealed class SceneLevel
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string PackagePath { get; set; } = string.Empty;
    public bool IsPersistent { get; init; }
    public bool IsLoaded { get; set; } = true;
    public bool IsVisible { get; set; } = true;
    public Matrix4x4 LevelTransform { get; set; } = Matrix4x4.Identity;
    public SceneObject? LevelRoot { get; set; }

    public List<SceneObject> RootObjects { get; } = [];
    public List<SceneMesh> Meshes { get; } = [];
    public List<SceneLight> Lights { get; } = [];
    public List<SceneNiagara> Niagaras { get; } = [];
    public List<SceneLandscape> Landscapes { get; } = [];
    public SceneHeightFog? HeightFog { get; set; }
    public ScenePostProcess? PostProcess { get; set; }

    public void Clear()
    {
        LevelRoot = null;
        RootObjects.Clear();
        Meshes.Clear();
        Lights.Clear();
        Niagaras.Clear();
        Landscapes.Clear();
        HeightFog = null;
        PostProcess = null;
        IsLoaded = false;
        IsVisible = false;
    }
}

public sealed class WorldScene
{
    public string WorldName { get; set; } = "Untitled World";
    public long SceneRevision { get; private set; }
    public void MarkDirty() => SceneRevision++;
    public List<SceneLevel> Levels { get; } = [];
    public List<StreamingLevelDescriptor> DiscoveredStreamingLevels { get; } = [];
    public List<WorldPartitionCellDescriptor> WorldPartitionCells { get; } = [];
    public WorldSettingsState WorldSettings { get; } = new();

    public List<SceneObject> RootObjects { get; } = [];
    public List<SceneMesh> Meshes { get; } = [];
    public List<SceneLight> Lights { get; } = [];
    public List<SceneNiagara> Niagaras { get; } = [];
    public List<SceneLandscape> Landscapes { get; } = [];
    public SceneHeightFog? HeightFog { get; set; }
    public ScenePostProcess? PostProcess { get; set; }
    public SceneObject? SelectedObject { get; set; }

    public SceneLevel GetOrCreatePersistentLevel()
    {
        var existing = Levels.FirstOrDefault(l => l.IsPersistent) ?? Levels.FirstOrDefault();
        if (existing is not null)
            return existing;

        var created = new SceneLevel
        {
            Name = string.IsNullOrWhiteSpace(WorldName) ? "PersistentLevel" : WorldName,
            IsPersistent = true,
            IsLoaded = true,
            IsVisible = true
        };
        Levels.Add(created);
        return created;
    }

    public void AddActorToLevel(SceneObject actor, SceneLevel? targetLevel = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var level = targetLevel ?? GetOrCreatePersistentLevel();
        if (level.LevelRoot is { } lr && !ReferenceEquals(actor, lr))
        {
            actor.Parent = lr;
            if (!lr.Children.Contains(actor))
                lr.Children.Add(actor);
        }
        else if (!level.RootObjects.Contains(actor))
        {
            level.RootObjects.Add(actor);
        }

        RegisterSubtreeInLevel(actor, level, restoreHeightFog: true);
        RebuildAggregates();
    }

    public SceneLevel? FindOwningLevel(SceneObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var root = obj;
        for (var d = 0; root.Parent is not null && d < 64; d++)
            root = root.Parent;

        foreach (var level in Levels)
        {
            if (ReferenceEquals(level.LevelRoot, root) || level.RootObjects.Contains(root))
                return level;
        }
        return null;
    }

    public RemovedObjectEntry? RemoveObjectFromLevel(SceneObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (string.Equals(obj.ClassName, "LevelStreaming", StringComparison.Ordinal))
            return null;

        var level = FindOwningLevel(obj);
        if (level is null)
            return null;

        var parent = obj.Parent;
        var list = parent is not null ? parent.Children : level.RootObjects;
        var idx = list.IndexOf(obj);
        if (idx < 0)
            return null;

        list.RemoveAt(idx);
        obj.Parent = null;

        var wasHeightFog = false;
        var visited = new HashSet<SceneObject>(ReferenceEqualityComparer.Instance);
        void Unregister(SceneObject node)
        {
            if (!visited.Add(node))
                return;
            if (ReferenceEquals(SelectedObject, node))
                Select(null);
            switch (node)
            {
                case SceneMesh sm:
                    level.Meshes.Remove(sm);
                    break;
                case SceneLight sl:
                    level.Lights.Remove(sl);
                    break;
                case SceneNiagara sn:
                    level.Niagaras.Remove(sn);
                    break;
                case SceneLandscape sland:
                    level.Landscapes.Remove(sland);
                    break;
                case SceneHeightFog fog when ReferenceEquals(level.HeightFog, fog):
                    level.HeightFog = null;
                    wasHeightFog = true;
                    break;
            }
            foreach (var child in node.Children)
                Unregister(child);
        }
        Unregister(obj);
        RebuildAggregates();
        return new RemovedObjectEntry(obj, level, parent, idx, wasHeightFog);
    }

    public void InsertObjectIntoLevel(RemovedObjectEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry.Obj);
        ArgumentNullException.ThrowIfNull(entry.Level);
        InsertObjectIntoLevel(entry.Obj, entry.Level, entry.Parent, entry.IndexInParent, entry.WasHeightFog);
    }

    public void InsertObjectIntoLevel(SceneObject obj, SceneLevel level, SceneObject? parent = null, int index = -1, bool restoreHeightFog = true)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(level);

        if (parent is not null)
        {
            var insertAt = index < 0 ? parent.Children.Count : Math.Clamp(index, 0, parent.Children.Count);
            obj.Parent = parent;
            if (!parent.Children.Contains(obj))
                parent.Children.Insert(insertAt, obj);
        }
        else if (level.LevelRoot is { } lr && !ReferenceEquals(obj, lr))
        {
            var insertAt = index < 0 ? lr.Children.Count : Math.Clamp(index, 0, lr.Children.Count);
            obj.Parent = lr;
            if (!lr.Children.Contains(obj))
                lr.Children.Insert(insertAt, obj);
        }
        else
        {
            var insertAt = index < 0 ? level.RootObjects.Count : Math.Clamp(index, 0, level.RootObjects.Count);
            obj.Parent = null;
            if (!level.RootObjects.Contains(obj))
                level.RootObjects.Insert(insertAt, obj);
        }

        RegisterSubtreeInLevel(obj, level, restoreHeightFog);
        RebuildAggregates();
    }

    private static void RegisterSubtreeInLevel(SceneObject actor, SceneLevel level, bool restoreHeightFog)
    {
        var visited = new HashSet<SceneObject>(ReferenceEqualityComparer.Instance);
        void RegisterSubtree(SceneObject obj)
        {
            if (!visited.Add(obj))
                return;
            switch (obj)
            {
                case SceneMesh sm when !level.Meshes.Contains(sm):
                    level.Meshes.Add(sm);
                    break;
                case SceneLight sl when !level.Lights.Contains(sl):
                    level.Lights.Add(sl);
                    break;
                case SceneNiagara sn when !level.Niagaras.Contains(sn):
                    level.Niagaras.Add(sn);
                    break;
                case SceneLandscape sland when !level.Landscapes.Contains(sland):
                    level.Landscapes.Add(sland);
                    break;
                case SceneHeightFog fog:
                    if (restoreHeightFog || level.HeightFog is null)
                        level.HeightFog = fog;
                    break;
            }
            foreach (var child in obj.Children)
                RegisterSubtree(child);
        }
        RegisterSubtree(actor);
    }

    public void RebuildAggregates()
    {
        MarkDirty();
        RootObjects.Clear();
        Meshes.Clear();
        Lights.Clear();
        Niagaras.Clear();
        Landscapes.Clear();
        HeightFog = null;
        PostProcess = null;

        foreach (var level in Levels)
        {
            if (!level.IsLoaded || !level.IsVisible)
                continue;
            RootObjects.AddRange(level.RootObjects);
            Meshes.AddRange(level.Meshes);
            Lights.AddRange(level.Lights);
            Niagaras.AddRange(level.Niagaras);
            Landscapes.AddRange(level.Landscapes);
            HeightFog ??= level.HeightFog;
            if (level.PostProcess is { } lpp)
                MergePostProcess(lpp);
        }

        if (SelectedObject is not null)
        {
            var root = SelectedObject;
            for (var d = 0; root.Parent is not null && d < 64; d++)
                root = root.Parent;
            if (!RootObjects.Contains(root))
                Select(null);
        }
    }

    private void MergePostProcess(ScenePostProcess src)
    {
        PostProcess ??= new ScenePostProcess();
        if (src.Priority < PostProcess.Priority && PostProcess.AutoExposureBias is not null)
            return;
        PostProcess.Priority = Math.Max(PostProcess.Priority, src.Priority);
        if (src.AutoExposureBias is not null) PostProcess.AutoExposureBias = src.AutoExposureBias;
        if (src.AutoExposureMinEV100 is not null) PostProcess.AutoExposureMinEV100 = src.AutoExposureMinEV100;
        if (src.AutoExposureMaxEV100 is not null) PostProcess.AutoExposureMaxEV100 = src.AutoExposureMaxEV100;
        if (src.BloomIntensity is not null) PostProcess.BloomIntensity = src.BloomIntensity;
    }

    public void Select(SceneObject? obj)
    {
        if (SelectedObject is not null)
            SelectedObject.IsSelected = false;
        SelectedObject = obj;
        if (obj is not null)
            obj.IsSelected = true;
    }

    public void Clear()
    {
        Select(null);
        WorldName = "Untitled World";
        foreach (var level in Levels)
            level.Clear();
        Levels.Clear();
        DiscoveredStreamingLevels.Clear();
        WorldPartitionCells.Clear();
        WorldSettings.Reset();
        RootObjects.Clear();
        Meshes.Clear();
        Lights.Clear();
        Niagaras.Clear();
        Landscapes.Clear();
        HeightFog = null;
        PostProcess = null;
        MarkDirty();
    }
}
