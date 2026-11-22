using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace UExplorer.Scene;

/// <summary>
/// Conversions between Unreal space (centimetres, Z up, left handed) and the editor's "meters" space
/// (metres, Y up, right handed): meters = (ue.X, ue.Z, ue.Y) * 0.01. Every scene matrix is stored in meters space;
/// the native renderer (cooked UE shaders, GPU Scene) needs Unreal space, see <see cref="ToUnrealLocalToWorld"/>.
/// </summary>
public static class UnrealSpace
{
    public static readonly Matrix4x4 UnrealToMeters = new(0.01f, 0, 0, 0, 0, 0, 0.01f, 0, 0, 0.01f, 0, 0, 0, 0, 0, 1);
    public static readonly Matrix4x4 MetersToUnreal = new(100f, 0, 0, 0, 0, 0, 100f, 0, 0, 100f, 0, 0, 0, 0, 0, 1);

    public static Vector3 Position(FVector v) => new Vector3(v.X, v.Z, v.Y) * 0.01f;
    public static Vector3 Position(Vector3 ueCm) => new Vector3(ueCm.X, ueCm.Z, ueCm.Y) * 0.01f;
    public static Vector3 ScaleOf(FVector v) => new(v.X, v.Z, v.Y);
    public static Quaternion Rotation(FQuat q) => Quaternion.Normalize(new Quaternion(q.X, q.Z, q.Y, -q.W));

    /// <summary>Inverse of <see cref="Rotation(FQuat)"/>.</summary>
    public static FQuat ToUnrealQuat(Quaternion q) => new(q.X, q.Z, q.Y, -q.W);

    public static Matrix4x4 Matrix(FVector location, FQuat rotation, FVector scale) =>
        Matrix4x4.CreateScale(ScaleOf(scale)) * Matrix4x4.CreateFromQuaternion(Rotation(rotation)) * Matrix4x4.CreateTranslation(Position(location));

    public static Matrix4x4 Matrix(FTransform t) => Matrix(t.Translation, t.Rotation, t.Scale3D);

    /// <summary>Meters-space world matrix -> Unreal-space LocalToWorld for mesh vertices in Unreal units.</summary>
    public static Matrix4x4 ToUnrealLocalToWorld(Matrix4x4 meters) => UnrealToMeters * meters * MetersToUnreal;

    public static Vector3 ToUnrealPosition(Vector3 meters) => new Vector3(meters.X, meters.Z, meters.Y) * 100f;

    /// <summary>
    /// Property lookup that follows the archetype chain. Cooked level / blueprint instance exports only store the
    /// properties that differ from their template, everything else lives on the archetype (SCS template, CDO subobject).
    /// </summary>
    public static bool TryGetInherited<T>(UObject? obj, string name, out T value)
    {
        for (var depth = 0; obj is not null && depth < 16; depth++)
        {
            if (obj.TryGetValue(out value, name)) return true;
            obj = Template(obj);
        }
        value = default!;
        return false;
    }

    public static T GetInherited<T>(UObject? obj, string name, T fallback) => TryGetInherited(obj, name, out T value) ? value : fallback;

    public static UObject? Template(UObject obj)
    {
        try
        {
            var template = obj.Template?.Load();
            return ReferenceEquals(template, obj) ? null : template;
        }
        catch
        {
            return null;
        }
    }

    public static UObject? Load(FPackageIndex? index)
    {
        if (index is null || index.IsNull) return null;
        try { return index.Load(); }
        catch { return null; }
    }
}
