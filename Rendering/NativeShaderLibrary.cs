using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Shaders;
using Serilog;

namespace UExplorer.Rendering;

/// <summary>
/// Indexes every cooked shader library (*.ushaderbytecode) the provider can see, so a material shader map that was
/// cooked with shared shader code (only a ResourceHash stored in the package) can be resolved to its actual bytecode.
/// Supports the pak layout (archive version 2, code stored after the header) and the I/O Store layout (version 1,
/// code stored in separate, group-compressed ShaderCode chunks).
/// </summary>
public sealed class NativeShaderLibrary
{
    private static readonly Lock _instanceLock = new();
    private static NativeShaderLibrary? _instance;
    private static IFileProvider? _instanceProvider;

    public static NativeShaderLibrary For(IFileProvider provider)
    {
        lock (_instanceLock)
        {
            if (_instance is null || !ReferenceEquals(_instanceProvider, provider))
            {
                _instance = new NativeShaderLibrary(provider);
                _instanceProvider = provider;
            }
            return _instance;
        }
    }

    private readonly IFileProvider _provider;
    private readonly Lazy<List<Archive>> _archives;
    private readonly Lazy<Dictionary<FSHAHash, (Archive Archive, int MapIndex)>> _maps;

    public IReadOnlyList<string> LibraryNames => _archives.Value.Select(x => x.Name).ToList();

    private NativeShaderLibrary(IFileProvider provider)
    {
        _provider = provider;
        _archives = new Lazy<List<Archive>>(LoadArchives);
        _maps = new Lazy<Dictionary<FSHAHash, (Archive, int)>>(() =>
        {
            var maps = new Dictionary<FSHAHash, (Archive, int)>();
            foreach (var archive in _archives.Value)
            {
                for (var i = 0; i < archive.ShaderMapHashes.Length; i++)
                    maps.TryAdd(archive.ShaderMapHashes[i], (archive, i));
            }
            return maps;
        });
    }

    /// <summary>Returns the shader entry indices of a shader map, ordered by FShader.ResourceIndex.</summary>
    public bool TryGetShaderMap(FSHAHash resourceHash, out Func<int, byte[]?> loadByResourceIndex, out int shaderCount, out string libraryName)
    {
        if (!_maps.Value.TryGetValue(resourceHash, out var found))
        {
            loadByResourceIndex = _ => null;
            shaderCount = 0;
            libraryName = string.Empty;
            return false;
        }

        var (archive, mapIndex) = found;
        var (offset, count) = archive.GetMapRange(mapIndex);
        shaderCount = (int) count;
        libraryName = archive.Name;
        loadByResourceIndex = resourceIndex => resourceIndex >= 0 && resourceIndex < count
            ? archive.LoadShader((int) archive.ShaderIndices[offset + resourceIndex])
            : null;
        return true;
    }

    private List<Archive> LoadArchives()
    {
        var archives = new List<Archive>();
        foreach (var (path, file) in _provider.Files)
        {
            if (!path.EndsWith(".ushaderbytecode", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var data = _provider.SaveAsset(file);
                var Ar = new FByteArchive(path, data, _provider.Versions);
                var version = Ar.Read<uint>();
                var isIoStore = version == 1 && Ar.Game >= CUE4Parse.UE4.Versions.EGame.GAME_UE5_0;

                FRHIShaderLibrary header = version == 2 || Ar.Game == CUE4Parse.UE4.Versions.EGame.GAME_ArenaBreakoutMobile
                    ? new FSerializedShaderArchive(Ar)
                    : isIoStore ? new FIoStoreShaderCodeArchive(Ar) : throw new NotSupportedException($"shader archive version {version}");

                archives.Add(new Archive(this, path, data, (int) Ar.Position, header));
                Log.Information("Indexed shader library {Path}", path);
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to index shader library {Path}", path);
            }
        }

        if (archives.Count == 0) Log.Warning("No shader libraries (*.ushaderbytecode) were found, materials cooked with shared shader code cannot be resolved");
        return archives;
    }

    private byte[]? ReadIoChunk(FIoChunkId chunkId)
    {
        if (_provider is not IVfsFileProvider vfs) return null;
        foreach (var reader in vfs.MountedVfs)
        {
            if (reader is IoStoreReader io && io.TryResolve(chunkId, out _))
                return io.Read(chunkId);
        }
        return null;
    }

    internal static byte[] Decompress(byte[] compressed, int uncompressedSize)
    {
        if (compressed.Length == uncompressedSize) return compressed;

        // UE5 defaults to Oodle, UE4.2x shipped LZ4, very old titles used Zlib
        foreach (var method in (ReadOnlySpan<CompressionMethod>) [CompressionMethod.Oodle, CompressionMethod.LZ4, CompressionMethod.Zlib])
        {
            try
            {
                return Compression.Decompress(compressed, uncompressedSize, method);
            }
            catch
            {
                // try the next codec
            }
        }
        throw new InvalidDataException("Shader code could not be decompressed with Oodle, LZ4 or Zlib");
    }

    private sealed class Archive(NativeShaderLibrary owner, string name, byte[] data, int codeBase, FRHIShaderLibrary header)
    {
        public string Name { get; } = name;
        public FSHAHash[] ShaderMapHashes { get; } = header switch
        {
            FSerializedShaderArchive pak => pak.ShaderMapHashes,
            FIoStoreShaderCodeArchive io => io.ShaderMapHashes,
            _ => []
        };

        public uint[] ShaderIndices { get; } = header switch
        {
            FSerializedShaderArchive pak => pak.ShaderIndices,
            FIoStoreShaderCodeArchive io => io.ShaderIndices,
            _ => []
        };

        private readonly Dictionary<int, byte[]> _groupCache = [];
        private readonly Lock _lock = new();

        public (uint Offset, uint Count) GetMapRange(int mapIndex) => header switch
        {
            FSerializedShaderArchive pak => (pak.ShaderMapEntries[mapIndex].ShaderIndicesOffset, pak.ShaderMapEntries[mapIndex].NumShaders),
            FIoStoreShaderCodeArchive io => (io.ShaderMapEntries[mapIndex].ShaderIndicesOffset, io.ShaderMapEntries[mapIndex].NumShaders),
            _ => (0, 0)
        };

        public byte[]? LoadShader(int shaderIndex)
        {
            try
            {
                return header switch
                {
                    FSerializedShaderArchive pak => LoadPak(pak, shaderIndex),
                    FIoStoreShaderCodeArchive io => LoadIoStore(io, shaderIndex),
                    _ => null
                };
            }
            catch (Exception e)
            {
                Log.Warning(e, "Failed to load shader {Index} from {Library}", shaderIndex, Name);
                return null;
            }
        }

        private byte[] LoadPak(FSerializedShaderArchive pak, int shaderIndex)
        {
            var entry = pak.ShaderEntries[shaderIndex];
            var compressed = data.AsSpan(codeBase + (int) entry.Offset, (int) entry.Size).ToArray();
            return Decompress(compressed, (int) entry.UncompressedSize);
        }

        private byte[]? LoadIoStore(FIoStoreShaderCodeArchive io, int shaderIndex)
        {
            var entry = io.ShaderEntries[shaderIndex];
            var groupIndex = (int) entry.ShaderGroupIndex;
            var group = io.ShaderGroupEntries[groupIndex];

            byte[]? groupData;
            lock (_lock)
            {
                if (!_groupCache.TryGetValue(groupIndex, out groupData))
                {
                    var raw = owner.ReadIoChunk(io.ShaderGroupIoHashes[groupIndex]);
                    if (raw is null) return null;
                    groupData = Decompress(raw.Length > group.CompressedSize ? raw[..(int) group.CompressedSize] : raw, (int) group.UncompressedSize);
                    if (_groupCache.Count > 256) _groupCache.Clear();
                    _groupCache[groupIndex] = groupData;
                }
            }

            // a shader's size is the distance to the next shader in the same group (or the group end)
            var start = entry.UncompressedOffsetInGroup;
            var end = group.UncompressedSize;
            for (var i = 0u; i < group.NumShaders; i++)
            {
                var other = io.ShaderEntries[io.ShaderIndices[group.ShaderIndicesOffset + i]].UncompressedOffsetInGroup;
                if (other > start && other < end) end = (uint) other;
            }

            return groupData.AsSpan((int) start, (int) (end - start)).ToArray();
        }
    }
}
