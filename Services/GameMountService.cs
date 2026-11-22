using System.Diagnostics;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using UExplorer.Config;

namespace UExplorer.Services;

public sealed record MountProgress(string Stage, float? Progress = null, TimeSpan Elapsed = default);

public sealed record MountResult(
    DefaultFileProvider? Provider,
    int FileCount,
    string GameSummary,
    TimeSpan Elapsed,
    string? Error,
    string? ResolvedMappingsPath = null);

/// <summary>
/// Mounts an Unreal Engine game directory with AES keys and .usmap mappings into a CUE4Parse DefaultFileProvider.
/// </summary>
public sealed class GameMountService : IDisposable
{
    private DefaultFileProvider? _provider;
    private volatile string _status = "No game mounted. Open Settings tab on the top bar to configure.";
    private volatile bool _isMounting;
    private volatile bool _disposed;
    private readonly SemaphoreSlim _mountLock = new(1, 1);
    private readonly object _ctsLock = new();
    private CancellationTokenSource? _activeMountCts;

    public DefaultFileProvider? Provider => Volatile.Read(ref _provider);
    public string Status => _status;
    public bool IsMounting => _isMounting;
    public bool IsMounted => Provider is { Files.Count: > 0 };

    public bool Mount(UexManifest manifest, out string status)
    {
        var result = MountAsync(manifest).GetAwaiter().GetResult();
        status = result.Error ?? result.GameSummary;
        return result.Error is null && result.Provider is not null;
    }

    public async Task<MountResult> MountAsync(
        UexManifest manifest,
        IProgress<MountProgress>? progress = null,
        CancellationToken ct = default,
        bool saveManifest = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (_disposed)
            return new MountResult(null, 0, "Mount service disposed.", TimeSpan.Zero, "Mount service disposed.");

        // Snapshot manifest inputs on caller thread before entering ThreadPool work
        var gameDirectory = manifest.GameDirectory;
        var engineVersion = manifest.EngineVersion;
        var readShaderMaps = manifest.ReadShaderMaps;
        var mainAesKey = manifest.MainAesKey;
        var dynamicKeys = manifest.DynamicKeys.Select(d => (d.Guid, d.Key)).ToList();
        var mappingsPathSpec = manifest.MappingsPath;
        var useMappings = manifest.UseMappings;

        var myCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_ctsLock)
        {
            if (_disposed)
            {
                myCts.Dispose();
                return new MountResult(null, 0, "Mount service disposed.", TimeSpan.Zero, "Mount service disposed.");
            }
            var prev = _activeMountCts;
            _activeMountCts = myCts;
            try { prev?.Cancel(); } catch { }
        }

        return await Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            var lockAcquired = false;
            DefaultFileProvider? provider = null;

            void Report(string stage, float? pct = null)
            {
                _status = stage;
                progress?.Report(new MountProgress(stage, pct, sw.Elapsed));
            }

            try
            {
                await _mountLock.WaitAsync(myCts.Token).ConfigureAwait(false);
                lockAcquired = true;
                myCts.Token.ThrowIfCancellationRequested();

                if (_disposed)
                    return new MountResult(null, 0, "Mount service disposed.", sw.Elapsed, "Mount service disposed.");

                _isMounting = true;
                Report("Validating game directory...", 0.05f);

                if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
                {
                    const string errMsg = "Game directory does not exist.";
                    _status = errMsg;
                    return new MountResult(null, 0, errMsg, sw.Elapsed, errMsg);
                }

                myCts.Token.ThrowIfCancellationRequested();
                Report("Scanning archives (Initialize)...", 0.20f);

                var versions = new VersionContainer(engineVersion);
                provider = new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versions, StringComparer.OrdinalIgnoreCase)
                {
                    ReadShaderMaps = readShaderMaps
                };
                provider.Initialize();

                myCts.Token.ThrowIfCancellationRequested();
                Report("Decrypting archives (SubmitKeys)...", 0.45f);

                var keys = new Dictionary<FGuid, FAesKey>();
                if (!string.IsNullOrWhiteSpace(mainAesKey))
                    keys[new FGuid()] = new FAesKey(mainAesKey.Trim());

                foreach (var (guid, key) in dynamicKeys)
                {
                    if (string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(key))
                        continue;
                    try
                    {
                        keys[new FGuid(guid.Trim().Replace("-", ""))] = new FAesKey(key.Trim());
                    }
                    catch
                    {
                    }
                }

                if (keys.Count > 0)
                    provider.SubmitKeys(keys);

                myCts.Token.ThrowIfCancellationRequested();
                Report("Mounting file index (PostMount)...", 0.70f);
                provider.PostMount();

                myCts.Token.ThrowIfCancellationRequested();
                Report("Loading type mappings (.usmap)...", 0.90f);

                var resolvedMappingsPath = mappingsPathSpec;
                if (useMappings && (string.IsNullOrWhiteSpace(resolvedMappingsPath) || !File.Exists(resolvedMappingsPath)))
                {
                    var localFallback = Path.Combine(AppContext.BaseDirectory, "fortnite_17_50.usmap");
                    if (File.Exists(localFallback))
                        resolvedMappingsPath = localFallback;
                    else if (Directory.Exists(AppContext.BaseDirectory))
                        resolvedMappingsPath = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.usmap").FirstOrDefault() ?? resolvedMappingsPath;
                }

                if (useMappings && !string.IsNullOrWhiteSpace(resolvedMappingsPath) && File.Exists(resolvedMappingsPath))
                {
                    provider.MappingsContainer = new FileUsmapTypeMappingsProvider(resolvedMappingsPath, StringComparer.Ordinal);
                }
                else
                {
                    resolvedMappingsPath = null;
                }

                myCts.Token.ThrowIfCancellationRequested();
                if (_disposed)
                {
                    provider.Dispose();
                    provider = null;
                    return new MountResult(null, 0, "Mount service disposed.", sw.Elapsed, "Mount service disposed.");
                }

                if (saveManifest)
                    manifest.SaveDefault();

                var summary = $"Mounted {provider.MountedVfs.Count} archives | {provider.Files.Count:N0} files | AES={provider.Keys.Count}";
                var oldProvider = Interlocked.Exchange(ref _provider, provider);
                var fileCount = provider.Files.Count;
                var mounted = provider;
                provider = null;
                oldProvider?.Dispose();

                _status = summary;
                Report(summary, 1.0f);
                return new MountResult(mounted, fileCount, summary, sw.Elapsed, null, resolvedMappingsPath);
            }
            catch (OperationCanceledException)
            {
                provider?.Dispose();
                const string cancelMsg = "Mount cancelled.";
                if (!_disposed && Volatile.Read(ref _provider) is null)
                    _status = cancelMsg;
                return new MountResult(null, 0, cancelMsg, sw.Elapsed, cancelMsg);
            }
            catch (ObjectDisposedException)
            {
                provider?.Dispose();
                return new MountResult(null, 0, "Mount service disposed.", sw.Elapsed, "Mount service disposed.");
            }
            catch (Exception e)
            {
                provider?.Dispose();
                var errMsg = $"Mount failed: {e.Message}";
                _status = errMsg;
                return new MountResult(null, 0, errMsg, sw.Elapsed, errMsg);
            }
            finally
            {
                _isMounting = false;
                lock (_ctsLock)
                {
                    if (ReferenceEquals(_activeMountCts, myCts))
                        _activeMountCts = null;
                }
                myCts.Dispose();
                if (lockAcquired)
                {
                    try { _mountLock.Release(); }
                    catch (ObjectDisposedException) { }
                }
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_ctsLock)
        {
            try { _activeMountCts?.Cancel(); } catch { }
        }
        Interlocked.Exchange(ref _provider, null)?.Dispose();
        _mountLock.Dispose();
    }
}
