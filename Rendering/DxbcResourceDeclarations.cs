using System.Buffers.Binary;
using System.Text;

namespace UExplorer.Rendering;

public enum EDxbcResourceKind { Unknown, Buffer, Texture1D, Texture2D, Texture2DMS, Texture3D, TextureCube, Texture1DArray, Texture2DArray, Texture2DMSArray, TextureCubeArray, Structured, Raw }

public readonly record struct DxbcInputElement(string SemanticName, int SemanticIndex, int Register, byte Mask, int SystemValueType, int ComponentType);

/// <summary>Reads t# declarations (dcl_resource*), ISGN input signatures, and OSGN output signature sizes of a SM4/SM5 program.</summary>
public static class DxbcResourceDeclarations
{
    private static IEnumerable<(uint FourCC, int Offset, int Size)> Chunks(byte[] dxbc)
    {
        if (dxbc.Length < 32) yield break;
        var chunkCount = BinaryPrimitives.ReadInt32LittleEndian(dxbc.AsSpan(28));
        for (var c = 0; c < chunkCount; c++)
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian(dxbc.AsSpan(32 + c * 4));
            if (offset + 8 > dxbc.Length) yield break;
            yield return (BinaryPrimitives.ReadUInt32LittleEndian(dxbc.AsSpan(offset)), offset + 8, BinaryPrimitives.ReadInt32LittleEndian(dxbc.AsSpan(offset + 4)));
        }
    }

    public static IReadOnlyList<DxbcInputElement> ReadInputSignature(byte[] dxbc)
    {
        var result = new List<DxbcInputElement>();
        foreach (var (fourCC, offset, size) in Chunks(dxbc))
        {
            if (fourCC is not (0x4E475349 or 0x31475349)) // ISGN, ISG1
                continue;
            if (size < 8 || offset + size > dxbc.Length)
                continue;

            var isIgs1 = fourCC == 0x31475349;
            var stride = isIgs1 ? 28 : 24;
            var count = BinaryPrimitives.ReadInt32LittleEndian(dxbc.AsSpan(offset));
            var baseElem = offset + 8;
            for (var i = 0; i < count; i++)
            {
                var p = baseElem + i * stride;
                if (p + stride > offset + size)
                    break;
                if (isIgs1)
                    p += 4; // skip Stream uint32

                var nameRel = BinaryPrimitives.ReadInt32LittleEndian(dxbc.AsSpan(p));
                var semIdx = BinaryPrimitives.ReadInt32LittleEndian(dxbc.AsSpan(p + 4));
                var sysVal = BinaryPrimitives.ReadInt32LittleEndian(dxbc.AsSpan(p + 8));
                var compType = BinaryPrimitives.ReadInt32LittleEndian(dxbc.AsSpan(p + 12));
                var reg = BinaryPrimitives.ReadInt32LittleEndian(dxbc.AsSpan(p + 16));
                var mask = dxbc[p + 20];

                var nameAbs = offset + nameRel;
                var semName = string.Empty;
                if (nameAbs >= offset && nameAbs < offset + size)
                {
                    var len = 0;
                    while (nameAbs + len < offset + size && dxbc[nameAbs + len] != 0)
                        len++;
                    semName = Encoding.ASCII.GetString(dxbc, nameAbs, len);
                }

                result.Add(new DxbcInputElement(semName, semIdx, reg, mask, sysVal, compType));
            }
            break;
        }
        return result;
    }

    /// <summary>Number of output signature elements (render targets for a pixel shader).</summary>
    public static int OutputCount(byte[] dxbc)
    {
        foreach (var (fourCC, offset, _) in Chunks(dxbc))
            if (fourCC is 0x4E47534F or 0x35475351 or 0x3147534F) // OSGN, OSG5, OSG1
                return BinaryPrimitives.ReadInt32LittleEndian(dxbc.AsSpan(offset));
        return 0;
    }

    /// <summary>True when a SM4/SM5 shader executes D3D10_SB_OPCODE_DISCARD (13 = 0x0D).</summary>
    public static bool HasDiscard(byte[] dxbc)
    {
        foreach (var (fourCC, offset, size) in Chunks(dxbc))
        {
            if (fourCC is not (0x58454853 or 0x52444853)) continue; // SHEX / SHDR
            var program = dxbc.AsSpan(offset, size);
            var tokens = (int) BinaryPrimitives.ReadUInt32LittleEndian(program[4..]);
            var i = 2;
            while (i < tokens)
            {
                var token = BinaryPrimitives.ReadUInt32LittleEndian(program[(i * 4)..]);
                var opcode = token & 0x7FF;
                var length = (int) ((token >> 24) & 0x7F);
                if (opcode == 0x35 && i + 1 < tokens) length = (int) BinaryPrimitives.ReadUInt32LittleEndian(program[((i + 1) * 4)..]);
                if (length <= 0) break;
                if (opcode == 13) return true; // D3D10_SB_OPCODE_DISCARD
                i += length;
            }
        }
        return false;
    }

    public static Dictionary<int, EDxbcResourceKind> Read(byte[] dxbc) => ReadWithTypes(dxbc, out _);

    public static Dictionary<int, EDxbcResourceKind> ReadWithTypes(byte[] dxbc, out Dictionary<int, bool> isUintResource)
    {
        var result = new Dictionary<int, EDxbcResourceKind>();
        isUintResource = new Dictionary<int, bool>();
        foreach (var (fourCC, offset, size) in Chunks(dxbc))
        {
            if (fourCC is not (0x58454853 or 0x52444853)) continue; // SHEX / SHDR
            var program = dxbc.AsSpan(offset, size);
            var tokens = (int) BinaryPrimitives.ReadUInt32LittleEndian(program[4..]);
            var i = 2;
            while (i < tokens)
            {
                var token = BinaryPrimitives.ReadUInt32LittleEndian(program[(i * 4)..]);
                var opcode = token & 0x7FF;
                var length = (int) ((token >> 24) & 0x7F);
                if (opcode == 0x35) length = (int) BinaryPrimitives.ReadUInt32LittleEndian(program[((i + 1) * 4)..]); // customdata
                if (length <= 0) break;

                if (opcode is 0x58 or 0xA1 or 0xA2 && i + 2 < tokens)
                {
                    var register = (int) BinaryPrimitives.ReadUInt32LittleEndian(program[((i + 2) * 4)..]);
                    if (opcode == 0x58 && i + length - 1 < tokens)
                    {
                        var retToken = BinaryPrimitives.ReadUInt32LittleEndian(program[((i + length - 1) * 4)..]);
                        var comp0 = retToken & 0xF;
                        isUintResource[register] = comp0 is 3 or 4; // SINT(3) or UINT(4)
                    }
                    result[register] = opcode switch
                    {
                        0xA1 => EDxbcResourceKind.Raw,
                        0xA2 => EDxbcResourceKind.Structured,
                        _ => ((token >> 11) & 0x1F) switch
                        {
                            1 => EDxbcResourceKind.Buffer,
                            2 => EDxbcResourceKind.Texture1D,
                            3 => EDxbcResourceKind.Texture2D,
                            4 => EDxbcResourceKind.Texture2DMS,
                            5 => EDxbcResourceKind.Texture3D,
                            6 => EDxbcResourceKind.TextureCube,
                            7 => EDxbcResourceKind.Texture1DArray,
                            8 => EDxbcResourceKind.Texture2DArray,
                            9 => EDxbcResourceKind.Texture2DMSArray,
                            10 => EDxbcResourceKind.TextureCubeArray,
                            _ => EDxbcResourceKind.Unknown
                        }
                    };
                }
                i += length;
            }
        }
        return result;
    }
}
