using System.Buffers.Binary;
using System.Text;

namespace UExplorer.Rendering;

/// <summary>
/// Unreal's D3D11 shader blob = [FBaseShaderResourceTable][DXBC][optional data][int32 optional data size].
/// The resource table says which uniform buffer resource (texture / SRV / sampler) goes to which register, the
/// optional data ('u') lists the uniform buffer names in constant buffer slot order. Both survive RDEF stripping.
/// </summary>
public sealed class UnrealD3D11ShaderInfo
{
    /// <summary>(UniformBufferIndex, ResourceIndex within that buffer, register)</summary>
    public readonly record struct Entry(int UniformBuffer, int Resource, int Register);

    public IReadOnlyList<Entry> ShaderResourceViews { get; private init; } = [];
    public IReadOnlyList<Entry> Samplers { get; private init; } = [];
    public IReadOnlyList<Entry> UnorderedAccessViews { get; private init; } = [];
    public IReadOnlyList<Entry> Textures { get; private init; } = [];
    public IReadOnlyList<string> UniformBufferNames { get; private init; } = [];

    public static UnrealD3D11ShaderInfo Parse(byte[] code, DxbcContainer container)
    {
        var header = code.AsSpan(0, container.Offset);
        var pos = 0;
        uint U(ReadOnlySpan<byte> span) { var v = BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]); pos += 4; return v; }

        var bits = U(header);
        var bufferCount = bits == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount(bits);

        List<Entry> ReadMap(ReadOnlySpan<byte> span)
        {
            var count = (int) U(span);
            var values = new uint[count];
            for (var i = 0; i < count; i++) values[i] = U(span);
            var entries = new List<Entry>();
            for (var i = Math.Min(bufferCount, count); i < count; i++)
            {
                if (values[i] == 0xFFFFFFFF) continue;
                entries.Add(new Entry((int) (values[i] >> 24), (int) ((values[i] >> 8) & 0xFFFF), (int) (values[i] & 0xFF)));
            }
            return entries;
        }

        var srvs = ReadMap(header);
        var samplers = ReadMap(header);
        var uavs = ReadMap(header);
        var hashCount = (int) U(header); pos += hashCount * 4; // ResourceTableLayoutHashes
        var textures = pos < header.Length ? ReadMap(header) : [];

        // optional data: key byte, uint32 size, payload ... then the int32 total size at the very end
        var names = new List<string>();
        var tail = container.Offset + container.Length;
        var p = tail;
        while (p + 5 <= code.Length - 4)
        {
            var key = code[p];
            var size = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(p + 1));
            if (size < 0 || p + 5 + size > code.Length) break;
            if (key == (byte) 'u')
            {
                var q = p + 5;
                var count = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(q)); q += 4;
                for (var i = 0; i < count && q + 4 <= p + 5 + size; i++)
                {
                    var length = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(q)); q += 4;
                    names.Add(Encoding.ASCII.GetString(code, q, Math.Max(0, length - 1)));
                    q += length;
                }
            }
            p += 5 + size;
        }

        return new UnrealD3D11ShaderInfo { ShaderResourceViews = srvs, Samplers = samplers, UnorderedAccessViews = uavs, Textures = textures, UniformBufferNames = names };
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < UniformBufferNames.Count; i++) sb.AppendLine($"cb{i} = {UniformBufferNames[i]}");
        string N(int ub) => ub < UniformBufferNames.Count ? UniformBufferNames[ub] : $"UB{ub}";
        foreach (var e in Textures) sb.AppendLine($"t{e.Register} = {N(e.UniformBuffer)} texture resource {e.Resource}");
        foreach (var e in ShaderResourceViews) sb.AppendLine($"t{e.Register} = {N(e.UniformBuffer)} srv resource {e.Resource}");
        foreach (var e in Samplers) sb.AppendLine($"s{e.Register} = {N(e.UniformBuffer)} sampler resource {e.Resource}");
        foreach (var e in UnorderedAccessViews) sb.AppendLine($"u{e.Register} = {N(e.UniformBuffer)} uav resource {e.Resource}");
        return sb.ToString();
    }
}
