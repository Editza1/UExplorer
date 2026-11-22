using System.Collections.Concurrent;
using System.Text;
using Serilog;

namespace UExplorer.Rendering;

/// <summary>
/// Centralized diagnostic logger for Snooper material, shader, Niagara, and D3D11 rendering pipelines.
/// Writes detailed reports and all warnings/errors to structured folders under <c>logs/</c>.
/// </summary>
public static class SnooperDiagnostics
{
    public static readonly string LogsRoot = Path.Combine(AppContext.BaseDirectory, "logs");
    public static readonly string MaterialsDir = Path.Combine(LogsRoot, "materials");
    public static readonly string NiagaraDir = Path.Combine(LogsRoot, "niagara");
    public static readonly string RendererDir = Path.Combine(LogsRoot, "renderer");

    private static readonly string ErrorsAndWarningsPath = Path.Combine(LogsRoot, "errors_and_warnings.log");
    private static readonly string SessionLogPath = Path.Combine(LogsRoot, "session.log");
    private static readonly object _fileLock = new();
    private static readonly ConcurrentDictionary<string, byte> _dedup = new(StringComparer.OrdinalIgnoreCase);

    static SnooperDiagnostics()
    {
        try
        {
            Directory.CreateDirectory(LogsRoot);
            Directory.CreateDirectory(MaterialsDir);
            Directory.CreateDirectory(NiagaraDir);
            Directory.CreateDirectory(RendererDir);
        }
        catch
        {
        }
    }

    public static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }

    public static void LogInfo(string category, string asset, string message)
    {
        AppendLine(SessionLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] [INF] [{category}] {asset}: {message}");
    }

    public static void LogWarning(string category, string asset, string message)
    {
        var dedupKey = $"W|{category}|{asset}|{message}";
        if (!_dedup.TryAdd(dedupKey, 1))
            return;
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [WRN] [{category}] {asset}: {message}";
        AppendLine(ErrorsAndWarningsPath, line);
        AppendLine(SessionLogPath, line);
        Log.Warning("[{Category}] {Asset}: {Message}", category, asset, message);
    }

    public static void LogError(string category, string asset, string message)
    {
        var dedupKey = $"E|{category}|{asset}|{message}";
        if (!_dedup.TryAdd(dedupKey, 1))
            return;
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [ERR] [{category}] {asset}: {message}";
        AppendLine(ErrorsAndWarningsPath, line);
        AppendLine(SessionLogPath, line);
        Log.Error("[{Category}] {Asset}: {Message}", category, asset, message);
    }

    public static void WriteMaterialLog(string materialName, string content)
    {
        try
        {
            Directory.CreateDirectory(MaterialsDir);
            var safe = SanitizeName(materialName);
            File.WriteAllText(Path.Combine(MaterialsDir, $"{safe}.log"), content, Encoding.UTF8);
        }
        catch
        {
        }
    }

    public static void WriteNiagaraLog(string assetName, string content)
    {
        try
        {
            Directory.CreateDirectory(NiagaraDir);
            var safe = SanitizeName(assetName);
            File.WriteAllText(Path.Combine(NiagaraDir, $"{safe}.log"), content, Encoding.UTF8);
        }
        catch
        {
        }
    }

    public static void WriteRendererLog(string fileName, string content)
    {
        try
        {
            Directory.CreateDirectory(RendererDir);
            var safe = SanitizeName(fileName);
            File.WriteAllText(Path.Combine(RendererDir, $"{safe}.log"), content, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static void AppendLine(string path, string line)
    {
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
