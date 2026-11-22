using System.Numerics;
using CUE4Parse_Conversion.Dto;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;

using Vortice.Direct3D11;
using Vortice.DXGI;

using UExplorer.Scene;
namespace UExplorer.Rendering;

public sealed class NativeMesh : IDisposable
{
    public readonly record struct Section(int MaterialIndex, int FirstIndex, int IndexCount);

    private static int _nextMeshId;
    public int MeshId { get; } = Interlocked.Increment(ref _nextMeshId);

    private readonly List<IDisposable> _owned = [];
    public ID3D11Buffer Positions { get; }
    public ID3D11Buffer Indices { get; }
    public ID3D11Buffer TexCoordBuffer { get; }
    public ID3D11Buffer TangentBuffer { get; }
    public ID3D11Buffer ColorBuffer { get; }
    public ID3D11ShaderResourceView PositionsSrv { get; }
    public ID3D11ShaderResourceView TexCoords { get; }
    public ID3D11ShaderResourceView Tangents { get; }
    public ID3D11ShaderResourceView Colors { get; }
    public ID3D11Buffer LocalVF { get; }
    public int UvChannelCount { get; }
    public IReadOnlyList<Section> Sections { get; }
    public Vector3 BoundsCenter { get; }
    public float BoundsRadius { get; }
    public float TightBoundsRadius { get; }
    public string Name { get; }
    /// <summary>CPU copy (Unreal space) for viewport picking.</summary>
    public Vector3[] CpuPositions { get; }
    public uint[] CpuIndices { get; }

    private readonly ID3D11Buffer? _uniformColor;
    private uint _uniformColorValue;

    private T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }

    public NativeMesh(ID3D11Device device, string name, Vector3[] positions, Vector3[] tangentX, Vector4[] tangentZ, Vector2[][] uvChannels, uint[]? colors, uint[] indices, IReadOnlyList<Section> sections)
    {
        CpuPositions = positions;
        CpuIndices = indices;
        Name = name;
        Sections = sections;
        var count = positions.Length;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions)
        { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        BoundsCenter = count > 0 ? (min + max) / 2 : Vector3.Zero;
        BoundsRadius = count > 0 ? (max - min).Length() / 2 : 0;
        var maxDistSq = 0f;
        for (var i = 0; i < count; i++)
            maxDistSq = MathF.Max(maxDistSq, Vector3.DistanceSquared(positions[i], BoundsCenter));
        TightBoundsRadius = count > 0 ? MathF.Sqrt(maxDistSq) : 0f;

        var safePositions = count > 0 ? positions : [Vector3.Zero];
        var safeIndices = indices.Length > 0 ? indices : [0u];
        Positions = Own(device.CreateBuffer(safePositions.AsSpan(), BindFlags.VertexBuffer | BindFlags.ShaderResource));
        PositionsSrv = Own(device.CreateShaderResourceView(Positions, new ShaderResourceViewDescription(Positions, Format.R32_Float, 0, (uint) Math.Max(3, safePositions.Length * 3))));
        Indices = Own(device.CreateBuffer(safeIndices.AsSpan(), BindFlags.IndexBuffer));

        // Preserve exact mesh UV channels (UV0, UV1, UV2...) from the static mesh LOD!
        // Note: In CUE4Parse_Conversion (StaticMeshDto), Vertex.Uv is UV0, and ExtraUvs[0] is UV1 (which holds the layer index on Fortnite meshes!).
        var channels = Math.Max(1, uvChannels.Length);
        UvChannelCount = channels;
        var uvs = new Vector2[Math.Max(1, count * channels)];
        for (var v = 0; v < count; v++)
            for (var c = 0; c < channels; c++)
                uvs[v * channels + c] = uvChannels[c][v];

        var uvBuffer = Own(device.CreateBuffer(uvs.AsSpan(), BindFlags.VertexBuffer | BindFlags.ShaderResource));
        TexCoordBuffer = uvBuffer;
        TexCoords = Own(device.CreateShaderResourceView(uvBuffer, new ShaderResourceViewDescription(uvBuffer, Format.R32G32_Float, 0, (uint) uvs.Length)));

        var tangents = new uint[Math.Max(2, count * 2)];
        for (var v = 0; v < count; v++)
        {
            tangents[v * 2] = PackSNorm(tangentX[v], 0);
            tangents[v * 2 + 1] = PackSNorm(new Vector3(tangentZ[v].X, tangentZ[v].Y, tangentZ[v].Z), tangentZ[v].W >= 0 ? 1 : -1);
        }
        var tangentBuffer = Own(device.CreateBuffer(tangents.AsSpan(), BindFlags.VertexBuffer | BindFlags.ShaderResource));
        TangentBuffer = tangentBuffer;
        Tangents = Own(device.CreateShaderResourceView(tangentBuffer, new ShaderResourceViewDescription(tangentBuffer, Format.R8G8B8A8_SNorm, 0, (uint) tangents.Length)));

        var perVertex = colors is { Length: > 0 } && colors.Length == count;
        if (perVertex)
        {
            var colorBuffer = Own(device.CreateBuffer(colors!.AsSpan(), BindFlags.VertexBuffer | BindFlags.ShaderResource));
            ColorBuffer = colorBuffer;
            Colors = Own(device.CreateShaderResourceView(colorBuffer, new ShaderResourceViewDescription(colorBuffer, Format.R8G8B8A8_UNorm, 0, (uint) count)));
        }
        else
        {
            _uniformColorValue = 0xFFFFFFFF;
            _uniformColor = Own(device.CreateBuffer(new[] { _uniformColorValue }.AsSpan(), BindFlags.VertexBuffer | BindFlags.ShaderResource));
            ColorBuffer = _uniformColor;
            Colors = Own(device.CreateShaderResourceView(_uniformColor, new ShaderResourceViewDescription(_uniformColor, Format.R8G8B8A8_UNorm, 0, 1)));
        }

        // VertexFetch_Parameters: ColorIndexMask, NumTexCoords, LightMapCoordinateIndex, vertex offset
        LocalVF = Own(device.CreateBuffer(new[] { perVertex ? -1 : 0, channels, Math.Min(1, channels - 1), 0 }.AsSpan(), BindFlags.ConstantBuffer));
    }

    public bool HasVertexColors => _uniformColor is null;

    public void SetUniformColor(ID3D11DeviceContext context, uint bgra)
    {
        if (_uniformColor is null || bgra == _uniformColorValue)
            return;
        _uniformColorValue = bgra;
        context.UpdateSubresource(new[] { bgra }, _uniformColor, 0, 0, 0, null);
    }

    public static NativeMesh FromLod(ID3D11Device device, string name, MeshLodDto<MeshVertex> lod, CUE4Parse.UE4.Assets.Exports.StaticMesh.FStaticMeshLODResources? rawLod = null)
    {
        var vertices = lod.Vertices;
        var count = vertices.Length;

        var positions = new Vector3[count];
        var tangentX = new Vector3[count];
        var tangentZ = new Vector4[count];
        var uv0 = new Vector2[count];
        for (var i = 0; i < count; i++)
        {
            var v = vertices[i];
            positions[i] = new Vector3(v.Position.X, v.Position.Y, v.Position.Z);
            tangentX[i] = new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z);
            tangentZ[i] = new Vector4(v.Normal.X, v.Normal.Y, v.Normal.Z, v.Normal.W);
            uv0[i] = new Vector2(v.Uv.U, v.Uv.V);
        }

        var channels = new List<Vector2[]> { uv0 };

        // Prefer reading raw UV channels directly from CUE4Parse's FStaticMeshUVItem if StaticMeshDto stripped or modified ExtraUvs
        if (rawLod?.VertexBuffer?.UV is { Length: > 0 } rawUvs && rawUvs.Length == count && rawUvs[0].UV is { Length: > 1 } firstUvSet)
        {
            for (var c = 1; c < firstUvSet.Length; c++)
            {
                var ch = new Vector2[count];
                for (var v = 0; v < count; v++)
                {
                    var uvArr = rawUvs[v].UV;
                    if (c < uvArr.Length)
                        ch[v] = new Vector2(uvArr[c].U, uvArr[c].V);
                }
                channels.Add(ch);
            }
        }
        else
        {
            foreach (var extra in lod.ExtraUvs ?? [])
            {
                if (extra is null || extra.Length != count)
                    continue;
                channels.Add(extra.Select(x => new Vector2(x.U, x.V)).ToArray());
            }
        }

        uint[]? colors = null;
        if (lod.VertexColors is { Length: > 0 } sets && sets[0].Colors is { } source && source.Length == count)
            colors = source.Select(c => (uint) c.B | (uint) c.G << 8 | (uint) c.R << 16 | (uint) c.A << 24).ToArray();

        var sections = lod.Sections.Where(s => s.IsValid && !s.bDisabled)
            .Select(s => new Section(s.MaterialIndex, s.FirstIndex, s.NumFaces * 3)).ToList();
        return new NativeMesh(device, name, positions, tangentX, tangentZ, channels.ToArray(), colors, lod.Indices, sections);
    }

    private static uint PackSNorm(Vector3 v, float w)
    {
        static uint C(float f) => (byte) (sbyte) Math.Clamp(MathF.Round(f * 127f), -127, 127);
        return C(v.X) | C(v.Y) << 8 | C(v.Z) << 16 | C(w) << 24;
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
            _owned[i].Dispose();
    }
}

public sealed class NativeMeshLods : IDisposable
{
    public NativeMesh[] Lods { get; }
    public float[] ScreenSizes { get; }
    public int MeshId => Lods[0].MeshId;
    public Vector3 BoundsCenter => Lods[0].BoundsCenter;
    public float BoundsRadius => Lods[0].BoundsRadius;
    public float TightBoundsRadius => Lods[0].TightBoundsRadius;
    public string Name => Lods[0].Name;

    public NativeMeshLods(NativeMesh[] lods, float[] screenSizes)
    {
        Lods = lods;
        ScreenSizes = screenSizes;
    }

    public NativeMesh Select(float screenSize)
    {
        var lod = 0;
        for (var i = 1; i < Lods.Length; i++)
            if (screenSize < ScreenSizes[i])
                lod = i;
        return Lods[lod];
    }

    public void SetUniformColor(ID3D11DeviceContext context, uint bgra)
    {
        foreach (var lod in Lods)
            lod.SetUniformColor(context, bgra);
    }

    public static NativeMeshLods FromStaticMesh(ID3D11Device device, UStaticMesh mesh)
    {
        using var dto = new StaticMeshDto(mesh, ExplorerBridge.ToExportOptions());
        var lods = dto.LODs.Where(x => !x.IsNanite && x.Vertices is { Length: > 0 } && x.Indices is { Length: > 0 }).ToList();
        if (lods.Count == 0)
            lods = dto.LODs.Where(x => x.Vertices is { Length: > 0 } && x.Indices is { Length: > 0 }).ToList();
        if (lods.Count == 0)
            throw new InvalidOperationException("mesh has no non-empty LODs");

        var rawLods = mesh.RenderData?.LODs;
        var built = new NativeMesh[lods.Count];
        for (var i = 0; i < lods.Count; i++)
        {
            var rawLod = rawLods != null && i < rawLods.Length ? rawLods[i] : null;
            built[i] = NativeMesh.FromLod(device, mesh.Name, lods[i], rawLod);
        }

        return new NativeMeshLods(built, lods.Select(l => l.ScreenSize).ToArray());
    }

    public static float ComputeFramedCameraDistance(float tightBoundsRadiusCm, float vFovRad, float fillFraction = 0.72f)
    {
        var radiusMeters = MathF.Max(tightBoundsRadiusCm * 0.01f, 0.05f);
        var halfTan = MathF.Max(MathF.Tan(vFovRad * 0.5f), 1e-4f);
        return (radiusMeters / Math.Clamp(fillFraction, 0.1f, 0.95f)) / halfTan;
    }

    public static NativeMeshLods CreateProceduralSphere(ID3D11Device device, float radiusCm = 50f, int stacks = 24, int slices = 36)
    {
        var positions = new Vector3[(stacks + 1) * (slices + 1)];
        var tangentX = new Vector3[positions.Length];
        var tangentZ = new Vector4[positions.Length];
        var uvs = new Vector2[positions.Length];
        var indices = new uint[stacks * slices * 6];

        var idx = 0;
        for (var i = 0; i <= stacks; i++)
        {
            var v = (float) i / stacks;
            var phi = v * MathF.PI;
            var sinPhi = MathF.Sin(phi);
            var cosPhi = MathF.Cos(phi);
            for (var j = 0; j <= slices; j++)
            {
                var u = (float) j / slices;
                var theta = u * MathF.Tau;
                var cosTheta = MathF.Cos(theta);
                var sinTheta = MathF.Sin(theta);

                var nx = sinPhi * cosTheta;
                var ny = sinPhi * sinTheta;
                var nz = cosPhi;
                positions[idx] = new Vector3(nx, ny, nz) * radiusCm;
                tangentX[idx] = new Vector3(-sinTheta, cosTheta, 0f);
                tangentZ[idx] = new Vector4(nx, ny, nz, -1f);
                uvs[idx] = new Vector2(u, v);
                idx++;
            }
        }

        var ii = 0;
        for (var i = 0; i < stacks; i++)
        {
            for (var j = 0; j < slices; j++)
            {
                var a = (uint) (i * (slices + 1) + j);
                var b = a + (uint) (slices + 1);
                indices[ii++] = a;
                indices[ii++] = a + 1;
                indices[ii++] = b;
                indices[ii++] = a + 1;
                indices[ii++] = b + 1;
                indices[ii++] = b;
            }
        }

        var mesh = new NativeMesh(device, "Proc_Sphere", positions, tangentX, tangentZ, [uvs], null, indices, [new NativeMesh.Section(0, 0, indices.Length)]);
        return new NativeMeshLods([mesh], [1f]);
    }

    public static NativeMeshLods CreateProceduralCube(ID3D11Device device, float halfExtentCm = 50f)
    {
        var pos = new Vector3[24];
        var tx = new Vector3[24];
        var tz = new Vector4[24];
        var uv = new Vector2[24];
        var idx = new uint[36];

        ReadOnlySpan<(Vector3 N, Vector3 U, Vector3 V)> faces =
        [
            (new Vector3( 0,  0,  1), new Vector3( 1,  0,  0), new Vector3( 0, -1,  0)), // +Z
            (new Vector3( 0,  0, -1), new Vector3( 1,  0,  0), new Vector3( 0,  1,  0)), // -Z
            (new Vector3( 1,  0,  0), new Vector3( 0,  1,  0), new Vector3( 0,  0, -1)), // +X
            (new Vector3(-1,  0,  0), new Vector3( 0, -1,  0), new Vector3( 0,  0, -1)), // -X
            (new Vector3( 0,  1,  0), new Vector3(-1,  0,  0), new Vector3( 0,  0, -1)), // +Y
            (new Vector3( 0, -1,  0), new Vector3( 1,  0,  0), new Vector3( 0,  0, -1)), // -Y
        ];

        for (var f = 0; f < 6; f++)
        {
            var (n, u, v) = faces[f];
            var baseV = f * 4;
            pos[baseV + 0] = (n - u - v) * halfExtentCm; uv[baseV + 0] = new Vector2(0, 0);
            pos[baseV + 1] = (n + u - v) * halfExtentCm; uv[baseV + 1] = new Vector2(1, 0);
            pos[baseV + 2] = (n + u + v) * halfExtentCm; uv[baseV + 2] = new Vector2(1, 1);
            pos[baseV + 3] = (n - u + v) * halfExtentCm; uv[baseV + 3] = new Vector2(0, 1);
            for (var k = 0; k < 4; k++)
            {
                tx[baseV + k] = u;
                tz[baseV + k] = new Vector4(n, -1f);
            }
            var baseI = f * 6;
            idx[baseI + 0] = (uint) (baseV + 0);
            idx[baseI + 1] = (uint) (baseV + 1);
            idx[baseI + 2] = (uint) (baseV + 2);
            idx[baseI + 3] = (uint) (baseV + 0);
            idx[baseI + 4] = (uint) (baseV + 2);
            idx[baseI + 5] = (uint) (baseV + 3);
        }

        var mesh = new NativeMesh(device, "Proc_Cube", pos, tx, tz, [uv], null, idx, [new NativeMesh.Section(0, 0, idx.Length)]);
        return new NativeMeshLods([mesh], [1f]);
    }

    public static NativeMeshLods CreateProceduralCylinder(ID3D11Device device, float radiusCm = 50f, float heightCm = 100f, int slices = 32)
    {
        var halfH = heightCm * 0.5f;
        var pos = new List<Vector3>();
        var tx = new List<Vector3>();
        var tz = new List<Vector4>();
        var uv = new List<Vector2>();
        var idx = new List<uint>();

        // Side wall (top ring i=0, bottom ring i=1 -> matches sphere winding)
        for (var i = 0; i <= 1; i++)
        {
            var z = i == 0 ? halfH : -halfH;
            var v = (float) i;
            for (var j = 0; j <= slices; j++)
            {
                var u = (float) j / slices;
                var theta = u * MathF.Tau;
                var c = MathF.Cos(theta);
                var s = MathF.Sin(theta);
                pos.Add(new Vector3(c * radiusCm, s * radiusCm, z));
                tx.Add(new Vector3(-s, c, 0f));
                tz.Add(new Vector4(c, s, 0f, -1f));
                uv.Add(new Vector2(u, v));
            }
        }
        for (var j = 0; j < slices; j++)
        {
            var a = (uint) j;
            var b = a + (uint) (slices + 1);
            idx.Add(a); idx.Add(a + 1); idx.Add(b);
            idx.Add(a + 1); idx.Add(b + 1); idx.Add(b);
        }

        // Top & bottom caps
        void AddCap(float z, float nz)
        {
            var centerIdx = (uint) pos.Count;
            pos.Add(new Vector3(0, 0, z));
            tx.Add(Vector3.UnitX);
            tz.Add(new Vector4(0, 0, nz, -1f));
            uv.Add(new Vector2(0.5f, 0.5f));
            var ringStart = (uint) pos.Count;
            for (var j = 0; j <= slices; j++)
            {
                var theta = (float) j / slices * MathF.Tau;
                var c = MathF.Cos(theta);
                var s = MathF.Sin(theta);
                pos.Add(new Vector3(c * radiusCm, s * radiusCm, z));
                tx.Add(Vector3.UnitX);
                tz.Add(new Vector4(0, 0, nz, -1f));
                uv.Add(new Vector2(c * 0.5f + 0.5f, s * 0.5f + 0.5f));
            }
            for (var j = 0; j < slices; j++)
            {
                var cur = ringStart + (uint) j;
                var next = cur + 1;
                if (nz > 0)
                { idx.Add(centerIdx); idx.Add(next); idx.Add(cur); }
                else
                { idx.Add(centerIdx); idx.Add(cur); idx.Add(next); }
            }
        }
        AddCap(halfH, 1f);
        AddCap(-halfH, -1f);

        var mesh = new NativeMesh(device, "Proc_Cylinder", pos.ToArray(), tx.ToArray(), tz.ToArray(), [uv.ToArray()], null, idx.ToArray(),
            [new NativeMesh.Section(0, 0, idx.Count)]);
        return new NativeMeshLods([mesh], [1f]);
    }

    public static NativeMeshLods CreateProceduralPlane(ID3D11Device device, float halfExtentCm = 50f)
    {
        var n = Vector3.UnitZ;
        var u = Vector3.UnitX;
        var v = -Vector3.UnitY;
        Vector3[] pos =
        [
            (-u - v) * halfExtentCm,
            ( u - v) * halfExtentCm,
            ( u + v) * halfExtentCm,
            (-u + v) * halfExtentCm
        ];
        Vector3[] tx = [u, u, u, u];
        Vector4[] tz = [new(n, -1f), new(n, -1f), new(n, -1f), new(n, -1f)];
        Vector2[] uv = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        uint[] idx = [0, 1, 2, 0, 2, 3];
        var mesh = new NativeMesh(device, "Proc_Plane", pos, tx, tz, [uv], null, idx, [new NativeMesh.Section(0, 0, idx.Length)]);
        return new NativeMeshLods([mesh], [1f]);
    }

    public void Dispose()
    {
        foreach (var lod in Lods)
            lod.Dispose();
    }
}
