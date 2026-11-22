using System.Buffers.Binary;

namespace UExplorer.Rendering;

public enum ENativeShaderStage { Unknown = -1, Pixel = 0, Vertex = 1, Geometry = 2, Hull = 3, Domain = 4, Compute = 5 }

public enum ENativeBytecodeKind { Unknown, Dxbc, Dxil }

/// <summary>
/// Finds the DXBC container inside an Unreal shader code blob. UE prefixes / suffixes the native bytecode with its own
/// data (packed resource counts, uniform buffer names, optional data with a trailing size), so instead of parsing every
/// engine version's layout we scan for the container magic and validate its declared size and chunk table.
/// DXIL (SM6) is wrapped in the same container, so this also detects D3D12-only shaders.
/// </summary>
public readonly record struct DxbcContainer(int Offset, int Length, ENativeBytecodeKind Kind, ENativeShaderStage Stage, int MajorVersion, int MinorVersion)
{
    private const uint Magic = 0x43425844; // 'DXBC'

    public ReadOnlyMemory<byte> Slice(byte[] code) => code.AsMemory(Offset, Length);

    public static bool TryFind(ReadOnlySpan<byte> code, out DxbcContainer container)
    {
        container = default;
        for (var i = 0; i + 32 <= code.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(code[i..]) != Magic) continue;

            var size = BinaryPrimitives.ReadUInt32LittleEndian(code[(i + 24)..]);
            var chunkCount = BinaryPrimitives.ReadUInt32LittleEndian(code[(i + 28)..]);
            if (size < 32 || i + (long) size > code.Length || chunkCount is 0 or > 64) continue;

            var blob = code.Slice(i, (int) size);
            if (!TryReadProgram(blob, chunkCount, out var kind, out var stage, out var major, out var minor)) continue;

            container = new DxbcContainer(i, (int) size, kind, stage, major, minor);
            return true;
        }

        return false;
    }

    private static bool TryReadProgram(ReadOnlySpan<byte> blob, uint chunkCount, out ENativeBytecodeKind kind, out ENativeShaderStage stage, out int major, out int minor)
    {
        kind = ENativeBytecodeKind.Unknown;
        stage = ENativeShaderStage.Unknown;
        major = minor = 0;

        for (var c = 0; c < chunkCount; c++)
        {
            var offsetPos = 32 + c * 4;
            if (offsetPos + 4 > blob.Length) return false;
            var chunkOffset = (int) BinaryPrimitives.ReadUInt32LittleEndian(blob[offsetPos..]);
            if (chunkOffset < 0 || chunkOffset + 12 > blob.Length) return false;

            var fourCC = BinaryPrimitives.ReadUInt32LittleEndian(blob[chunkOffset..]);
            var data = blob[(chunkOffset + 8)..];
            switch (fourCC)
            {
                case 0x52444853: // 'SHDR' (SM4)
                case 0x58454853: // 'SHEX' (SM5)
                    kind = ENativeBytecodeKind.Dxbc;
                    break;
                case 0x4C495844: // 'DXIL' (SM6)
                    kind = ENativeBytecodeKind.Dxil;
                    break;
                default:
                    continue;
            }

            // both start with a version token: minor(4) | major(4) | ... | program type(16)
            var token = BinaryPrimitives.ReadUInt32LittleEndian(data);
            minor = (int) (token & 0xF);
            major = (int) ((token >> 4) & 0xF);
            var type = (int) (token >> 16);
            stage = type is >= 0 and <= 5 ? (ENativeShaderStage) type : ENativeShaderStage.Unknown;
            return true;
        }

        return false;
    }
}
