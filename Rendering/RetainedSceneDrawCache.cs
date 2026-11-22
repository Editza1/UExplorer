using System.Numerics;
using System.Runtime.InteropServices;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using UExplorer.Scene;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UExplorer.Rendering;

public sealed class RetainedPrimitiveGroup : IDisposable
{
    public required SceneMesh Source { get; init; }
    public required NativeMeshLods Mesh { get; init; }
    public required MaterialAsset?[] MaterialAssetsBySlot { get; init; }
    public required NativeMaterialPipeline?[] MaterialsBySlot { get; init; }
    public required bool IsHlod { get; init; }
    public required bool IsIsm { get; init; }
    public required int BasePrimitiveId { get; init; }
    public required int PrimitiveCount { get; init; }

    // Single-instance data
    public Matrix4x4 LocalToWorld;
    public Matrix4x4 WorldToLocal;
    public Vector4 WorldBounds;
    public Vector3 Scale;
    public bool Mirrored;

    // InstancedStaticMeshComponent (ISM) data
    public Vector4 AggregateBounds;
    public Matrix4x4[] InstanceLocalToWorlds = [];
    public Matrix4x4[] InstanceWorldToLocals = [];
    public Vector4[] InstanceWorldBounds = [];
    public Vector3[] InstanceScales = [];
    public bool[] InstanceMirrored = [];
    public bool HasUniformMirrored { get; set; } = true;
    public bool HasMixedMirror => !HasUniformMirrored;
    public ID3D11Buffer? PersistentInstanceIdVb;

    public void Dispose()
    {
        PersistentInstanceIdVb?.Dispose();
        PersistentInstanceIdVb = null;
    }
}

public sealed class RetainedSceneDrawCache : IDisposable
{
    private const int PrimitiveStride = 36;

    private readonly List<RetainedPrimitiveGroup> _groups = [];
    private readonly List<SceneMesh> _pendingMeshResolves = [];
    private readonly List<(RetainedPrimitiveGroup Group, int Slot, MaterialAsset Mat)> _pendingMaterialResolves = [];
    private readonly Dictionary<UStaticMesh, NativeMeshLods> _meshCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MaterialAsset, NativeMaterialPipeline> _pipeCache = new(ReferenceEqualityComparer.Instance);
    private readonly List<(MaterialAsset Asset, NativeMaterialPipeline Pipeline)> _activeMaterialPairs = [];
    private readonly HashSet<MaterialAsset> _seenActiveMaterials = new(ReferenceEqualityComparer.Instance);
    private readonly List<IDisposable> _ownedSyncResources = [];

    private WorldScene? _builtScene;
    private long _builtRevision = -1;
    private ID3D11Buffer? _retainedPrimitiveBuffer;
    private ID3D11ShaderResourceView? _retainedPrimitiveSrv;
    private int _primitiveCapacity;
    private Vector4[] _primitiveUploadScratch = [];
    private bool _gpuDirty;
    private int _nextPrimitiveId;

    internal WorldScene? BuiltScene => _builtScene;
    public IReadOnlyList<RetainedPrimitiveGroup> Groups => _groups;
    public ID3D11ShaderResourceView? PrimitiveSrv => _retainedPrimitiveSrv;
    public int TotalPrimitives => _nextPrimitiveId;
    public long DataVersion { get; private set; }
    public int DistinctMeshCount { get; private set; }
    public int LoadedMeshCount => _meshCache.Count;
    public int VisibleMeshCount { get; private set; }

    public bool TryGetCachedMesh(UStaticMesh mesh, out NativeMeshLods? lods) => _meshCache.TryGetValue(mesh, out lods);
    public bool TryGetCachedPipeline(MaterialAsset mat, out NativeMaterialPipeline? pipe) => _pipeCache.TryGetValue(mat, out pipe);

    public void Clear()
    {
        for (var i = 0; i < _groups.Count; i++)
            _groups[i].Dispose();
        _groups.Clear();
        _pendingMeshResolves.Clear();
        _pendingMaterialResolves.Clear();
        _meshCache.Clear();
        _pipeCache.Clear();
        _activeMaterialPairs.Clear();
        _seenActiveMaterials.Clear();
        for (var i = 0; i < _ownedSyncResources.Count; i++)
            _ownedSyncResources[i].Dispose();
        _ownedSyncResources.Clear();
        _builtScene = null;
        _builtRevision = -1;
        _nextPrimitiveId = 0;
        _gpuDirty = false;
        DistinctMeshCount = 0;
        VisibleMeshCount = 0;
        DataVersion++;
    }

    public void Update(D3D11NativeShaderDevice device, WorldScene scene, bool syncBuild = false)
    {
        var sceneChanged = !ReferenceEquals(_builtScene, scene) || _builtRevision != scene.SceneRevision;
        if (!sceneChanged && _pendingMeshResolves.Count == 0 && _pendingMaterialResolves.Count == 0)
            return;

        if (sceneChanged)
        {
            RebuildFromScene(device, scene, syncBuild);
        }
        else
        {
            ResolvePending(device, syncBuild);
        }

        if (_gpuDirty)
        {
            UploadPrimitiveBuffer(device);
            _gpuDirty = false;
            DataVersion++;
        }
    }

    public void SyncMaterialParameters(D3D11NativeShaderDevice device, IFileProvider? provider)
    {
        for (var i = 0; i < _activeMaterialPairs.Count; i++)
        {
            var (asset, pipe) = _activeMaterialPairs[i];
            pipe.EnsureParametersSynced(device.Device, device.Context, provider, asset);
        }
    }

    private void RebuildFromScene(D3D11NativeShaderDevice device, WorldScene scene, bool syncBuild)
    {
        for (var i = 0; i < _groups.Count; i++)
            _groups[i].Dispose();
        _groups.Clear();
        _pendingMeshResolves.Clear();
        _pendingMaterialResolves.Clear();
        _activeMaterialPairs.Clear();
        _seenActiveMaterials.Clear();
        _nextPrimitiveId = 0;

        _builtScene = scene;
        _builtRevision = scene.SceneRevision;

        var distinctMeshes = new HashSet<UStaticMesh>(ReferenceEqualityComparer.Instance);
        var visibleCount = 0;

        var meshes = scene.Meshes;
        for (var i = 0; i < meshes.Count; i++)
        {
            var sm = meshes[i];
            distinctMeshes.Add(sm.Mesh);
            if (!sm.IsHlod && sm.IsVisible)
                visibleCount++;

            if (!sm.IsVisible || !sm.IsVisibleRecursive)
                continue;

            if (!TryResolveMesh(device, sm.Mesh, syncBuild, out var nm) || nm is null)
            {
                _pendingMeshResolves.Add(sm);
                continue;
            }

            AddGroupForSceneMesh(device, sm, nm, syncBuild);
        }

        DistinctMeshCount = distinctMeshes.Count;
        VisibleMeshCount = visibleCount;
        _gpuDirty = true;
    }

    private void ResolvePending(D3D11NativeShaderDevice device, bool syncBuild)
    {
        for (var i = _pendingMeshResolves.Count - 1; i >= 0; i--)
        {
            var sm = _pendingMeshResolves[i];
            if (TryResolveMesh(device, sm.Mesh, syncBuild, out var nm) && nm is not null)
            {
                _pendingMeshResolves.RemoveAt(i);
                AddGroupForSceneMesh(device, sm, nm, syncBuild);
                _gpuDirty = true;
            }
        }

        for (var i = _pendingMaterialResolves.Count - 1; i >= 0; i--)
        {
            var (group, slot, mat) = _pendingMaterialResolves[i];
            if (TryResolvePipeline(device, mat, syncBuild, out var pipe) && pipe is not null)
            {
                group.MaterialsBySlot[slot] = pipe;
                RegisterActivePair(mat, pipe);
                _pendingMaterialResolves.RemoveAt(i);
            }
        }
    }

    private bool TryResolveMesh(D3D11NativeShaderDevice device, UStaticMesh mesh, bool syncBuild, out NativeMeshLods? nm)
    {
        if (_meshCache.TryGetValue(mesh, out nm))
            return true;
        nm = NativeAssetCache.GetMesh(device, mesh, allowQueue: !syncBuild);
        if (nm is null && syncBuild)
        {
            try
            {
                nm = NativeMeshLods.FromStaticMesh(device.Device, mesh);
                if (nm is not null)
                    _ownedSyncResources.Add(nm);
            }
            catch { nm = null; }
        }
        if (nm is not null)
        {
            _meshCache[mesh] = nm;
            return true;
        }
        return false;
    }

    private bool TryResolvePipeline(D3D11NativeShaderDevice device, MaterialAsset mat, bool syncBuild, out NativeMaterialPipeline? pipe)
    {
        if (_pipeCache.TryGetValue(mat, out pipe))
            return true;
        pipe = NativeAssetCache.GetPipeline(device, mat, out _, allowQueue: !syncBuild);
        if (pipe is null && syncBuild)
        {
            pipe = NativeAssetCache.BuildPipelineSync(device, mat, out _);
            if (pipe is not null)
                _ownedSyncResources.Add(pipe);
        }
        if (pipe is not null)
        {
            _pipeCache[mat] = pipe;
            return true;
        }
        return false;
    }

    private void RegisterActivePair(MaterialAsset mat, NativeMaterialPipeline pipe)
    {
        if (_seenActiveMaterials.Add(mat))
            _activeMaterialPairs.Add((mat, pipe));
    }

    private void AddGroupForSceneMesh(D3D11NativeShaderDevice device, SceneMesh sm, NativeMeshLods nm, bool syncBuild)
    {
        var slotCount = sm.Materials.Length;
        var matAssets = new MaterialAsset?[slotCount];
        var pipes = new NativeMaterialPipeline?[slotCount];

        var group = new RetainedPrimitiveGroup
        {
            Source = sm,
            Mesh = nm,
            MaterialAssetsBySlot = matAssets,
            MaterialsBySlot = pipes,
            IsHlod = sm.IsHlod,
            IsIsm = sm.InstanceLocalMatrices is { Count: > 0 },
            BasePrimitiveId = _nextPrimitiveId,
            PrimitiveCount = sm.InstanceLocalMatrices is { Count: > 0 } instList ? instList.Count : 1
        };

        for (var s = 0; s < slotCount; s++)
        {
            var ma = sm.Materials[s];
            matAssets[s] = ma;
            if (ma is null) continue;
            if (TryResolvePipeline(device, ma, syncBuild, out var pipe) && pipe is not null)
            {
                pipes[s] = pipe;
                RegisterActivePair(ma, pipe);
            }
            else
            {
                _pendingMaterialResolves.Add((group, s, ma));
            }
        }

        if (sm.InstanceLocalMatrices is { Count: > 0 } insts)
        {
            var n = insts.Count;
            group.InstanceLocalToWorlds = new Matrix4x4[n];
            group.InstanceWorldToLocals = new Matrix4x4[n];
            group.InstanceWorldBounds = new Vector4[n];
            group.InstanceScales = new Vector3[n];
            group.InstanceMirrored = new bool[n];

            var parentWorld = sm.WorldMatrix;
            var minB = new Vector3(float.MaxValue);
            var maxB = new Vector3(float.MinValue);
            var ids = new uint[n];
            var uniformMirrored = true;
            var firstMirrored = false;

            for (var k = 0; k < n; k++)
            {
                var l2w = UnrealSpace.ToUnrealLocalToWorld(insts[k] * parentWorld);
                Matrix4x4.Invert(l2w, out var w2l);
                var sx = new Vector3(l2w.M11, l2w.M12, l2w.M13).Length();
                var sy = new Vector3(l2w.M21, l2w.M22, l2w.M23).Length();
                var sz = new Vector3(l2w.M31, l2w.M32, l2w.M33).Length();
                var maxScale = MathF.Max(sx, MathF.Max(sy, sz));
                var center = Vector3.Transform(nm.BoundsCenter, l2w);
                var radius = nm.BoundsRadius * maxScale;
                var mirrored = l2w.GetDeterminant() < 0;

                group.InstanceLocalToWorlds[k] = l2w;
                group.InstanceWorldToLocals[k] = w2l;
                group.InstanceWorldBounds[k] = new Vector4(center, radius);
                group.InstanceScales[k] = new Vector3(sx, sy, sz);
                group.InstanceMirrored[k] = mirrored;
                if (k == 0)
                    firstMirrored = mirrored;
                else if (mirrored != firstMirrored)
                    uniformMirrored = false;
                ids[k] = (uint) (group.BasePrimitiveId + k);

                var ext = new Vector3(radius);
                minB = Vector3.Min(minB, center - ext);
                maxB = Vector3.Max(maxB, center + ext);
            }

            group.HasUniformMirrored = uniformMirrored;

            var aggCenter = (minB + maxB) * 0.5f;
            var aggRadius = (maxB - minB).Length() * 0.5f;
            group.AggregateBounds = new Vector4(aggCenter, aggRadius);
            group.PersistentInstanceIdVb = device.Device.CreateBuffer(
                ids.AsSpan(),
                new BufferDescription((uint) (n * sizeof(uint)), BindFlags.VertexBuffer, ResourceUsage.Immutable));
        }
        else
        {
            var l2w = UnrealSpace.ToUnrealLocalToWorld(sm.WorldMatrix);
            Matrix4x4.Invert(l2w, out var w2l);
            var sx = new Vector3(l2w.M11, l2w.M12, l2w.M13).Length();
            var sy = new Vector3(l2w.M21, l2w.M22, l2w.M23).Length();
            var sz = new Vector3(l2w.M31, l2w.M32, l2w.M33).Length();
            var maxScale = MathF.Max(sx, MathF.Max(sy, sz));
            var center = Vector3.Transform(nm.BoundsCenter, l2w);
            var radius = nm.BoundsRadius * maxScale;

            group.LocalToWorld = l2w;
            group.WorldToLocal = w2l;
            group.WorldBounds = new Vector4(center, radius);
            group.Scale = new Vector3(sx, sy, sz);
            group.Mirrored = l2w.GetDeterminant() < 0;
        }

        _nextPrimitiveId += group.PrimitiveCount;
        _groups.Add(group);
    }

    private void EnsurePrimitiveCapacity(ID3D11Device device, int count)
    {
        if (count <= _primitiveCapacity && _retainedPrimitiveBuffer is not null)
            return;
        var capacity = Math.Max(count, Math.Max(_primitiveCapacity * 2, 256));
        _retainedPrimitiveSrv?.Dispose();
        _retainedPrimitiveBuffer?.Dispose();
        _retainedPrimitiveBuffer = device.CreateBuffer(new BufferDescription(
            (uint) (capacity * PrimitiveStride * 16),
            BindFlags.ShaderResource,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write,
            ResourceOptionFlags.BufferStructured,
            16));
        _retainedPrimitiveSrv = device.CreateShaderResourceView(
            _retainedPrimitiveBuffer,
            new ShaderResourceViewDescription(_retainedPrimitiveBuffer, Format.Unknown, 0, (uint) (capacity * PrimitiveStride)));
        _primitiveCapacity = capacity;
    }

    private unsafe void UploadPrimitiveBuffer(D3D11NativeShaderDevice device)
    {
        var total = Math.Max(1, _nextPrimitiveId);
        EnsurePrimitiveCapacity(device.Device, total);

        var neededVecs = total * PrimitiveStride;
        if (_primitiveUploadScratch.Length < neededVecs)
            _primitiveUploadScratch = new Vector4[Math.Max(neededVecs, Math.Max(_primitiveUploadScratch.Length * 2, PrimitiveStride * 256))];

        var maskBits = BitConverter.Int32BitsToSingle(0x7FF);
        var buf = _primitiveUploadScratch;

        void WritePrimitive(int primId, Matrix4x4 m, Matrix4x4 w2l, Vector3 scale, Vector4 bounds, Vector3 meshBoundsCenter, bool mirrored)
        {
            var o = primId * PrimitiveStride;
            WriteMatrix(buf, o, m);
            buf[o + 4] = new Vector4(1f / MathF.Max(scale.X, 1e-6f), 1f / MathF.Max(scale.Y, 1e-6f), 1f / MathF.Max(scale.Z, 1e-6f), mirrored ? -1f : 1f);
            buf[o + 5] = bounds;
            WriteMatrix(buf, o + 6, w2l);
            WriteMatrix(buf, o + 10, m);
            WriteMatrix(buf, o + 14, w2l);
            buf[o + 18] = new Vector4(m.Translation, 0f);
            var halfExt = new Vector3(MathF.Max(bounds.W, 10000f));
            buf[o + 19] = new Vector4(halfExt, 1f);
            buf[o + 20] = new Vector4(scale, 0f);
            buf[o + 21] = new Vector4(meshBoundsCenter - halfExt, 0f);
            buf[o + 22] = new Vector4(meshBoundsCenter + halfExt, 0f);
            buf[o + 23] = new Vector4(maskBits, maskBits, maskBits, maskBits);
        }

        for (var i = 0; i < _groups.Count; i++)
        {
            var g = _groups[i];
            if (g.IsIsm)
            {
                for (var k = 0; k < g.PrimitiveCount; k++)
                {
                    WritePrimitive(
                        g.BasePrimitiveId + k,
                        g.InstanceLocalToWorlds[k],
                        g.InstanceWorldToLocals[k],
                        g.InstanceScales[k],
                        g.InstanceWorldBounds[k],
                        g.Mesh.BoundsCenter,
                        g.InstanceMirrored[k]);
                }
            }
            else
            {
                WritePrimitive(
                    g.BasePrimitiveId,
                    g.LocalToWorld,
                    g.WorldToLocal,
                    g.Scale,
                    g.WorldBounds,
                    g.Mesh.BoundsCenter,
                    g.Mirrored);
            }
        }

        var mapped = device.Context.Map(_retainedPrimitiveBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bytes = MemoryMarshal.AsBytes(buf.AsSpan(0, neededVecs));
            bytes.CopyTo(new Span<byte>((void*) mapped.DataPointer, bytes.Length));
        }
        finally
        {
            device.Context.Unmap(_retainedPrimitiveBuffer!, 0);
        }
    }

    private static void WriteMatrix(Vector4[] registers, int index, Matrix4x4 m)
    {
        registers[index] = new Vector4(m.M11, m.M12, m.M13, m.M14);
        registers[index + 1] = new Vector4(m.M21, m.M22, m.M23, m.M24);
        registers[index + 2] = new Vector4(m.M31, m.M32, m.M33, m.M34);
        registers[index + 3] = new Vector4(m.M41, m.M42, m.M43, m.M44);
    }

    public void Dispose()
    {
        Clear();
        _retainedPrimitiveSrv?.Dispose();
        _retainedPrimitiveSrv = null;
        _retainedPrimitiveBuffer?.Dispose();
        _retainedPrimitiveBuffer = null;
    }
}
