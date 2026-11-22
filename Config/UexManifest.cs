using System.Text.Json;
using CUE4Parse.UE4.Versions;

namespace UExplorer.Config;

public sealed class DynamicAesEntry
{
    public string Guid { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
}

/// <summary>
/// Serializable .uexmanif game profile archiving directory, UE version, AES keys, and .usmap mappings.
/// </summary>
public sealed class UexManifest
{
    public string Name { get; set; } = "Default";
    public string GameDirectory { get; set; } = string.Empty;
    public EGame EngineVersion { get; set; } = EGame.GAME_UE5_2;
    public string MainAesKey { get; set; } = string.Empty;
    public List<DynamicAesEntry> DynamicKeys { get; set; } = [];
    public string MappingsPath { get; set; } = string.Empty;
    public bool UseMappings { get; set; } = true;
    public bool ReadShaderMaps { get; set; } = true;
    public string OutputDirectory { get; set; } = string.Empty;
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;

    private static string DefaultConfigPath => Path.Combine(AppContext.BaseDirectory, "UExplorer.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static UexManifest LoadDefault() => LoadFromFile(DefaultConfigPath) ?? new UexManifest();

    public void SaveDefault() => SaveToFile(DefaultConfigPath);

    public static UexManifest? LoadFromFile(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<UexManifest>(File.ReadAllText(path), JsonOpts);
        }
        catch
        {
        }
        return null;
    }

    public bool SaveToFile(string path)
    {
        try
        {
            SavedUtc = DateTime.UtcNow;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryImportFromFModel(out string status)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var settingsPath = Path.Combine(appData, "FModel", "AppSettings.json");
            if (!File.Exists(settingsPath))
            {
                status = "FModel AppSettings.json not found in %AppData%/FModel";
                return false;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("GameDirectory", out var gd) && gd.ValueKind == JsonValueKind.String)
                GameDirectory = gd.GetString() ?? GameDirectory;
            if (root.TryGetProperty("OutputDirectory", out var od) && od.ValueKind == JsonValueKind.String)
                OutputDirectory = od.GetString() ?? OutputDirectory;

            if (root.TryGetProperty("PerDirectory", out var perDir) && perDir.ValueKind == JsonValueKind.Object)
            {
                foreach (var dirEntry in perDir.EnumerateObject())
                {
                    if (dirEntry.Value.TryGetProperty("GameDirectory", out var gProp) && gProp.GetString() is { Length: > 0 } gStr)
                    {
                        if (!string.IsNullOrEmpty(GameDirectory) &&
                            !gStr.Equals(GameDirectory, StringComparison.OrdinalIgnoreCase))
                            continue;
                        GameDirectory = gStr;
                    }

                    if (dirEntry.Value.TryGetProperty("UeVersion", out var verProp) &&
                        verProp.ValueKind == JsonValueKind.Number &&
                        verProp.TryGetInt32(out var verInt))
                    {
                        EngineVersion = (EGame) verInt;
                    }

                    if (dirEntry.Value.TryGetProperty("AesKeys", out var aesProp))
                    {
                        if (aesProp.TryGetProperty("MainKey", out var mk) && mk.GetString() is { Length: > 0 } mks)
                            MainAesKey = mks;
                        if (aesProp.TryGetProperty("DynamicKeys", out var dk) && dk.ValueKind == JsonValueKind.Array)
                        {
                            DynamicKeys.Clear();
                            foreach (var item in dk.EnumerateArray())
                            {
                                var g = item.TryGetProperty("Guid", out var gp) ? gp.GetString() ?? "" : "";
                                var k = item.TryGetProperty("Key", out var kp) ? kp.GetString() ?? "" : "";
                                if (!string.IsNullOrWhiteSpace(g) && !string.IsNullOrWhiteSpace(k))
                                    DynamicKeys.Add(new DynamicAesEntry { Guid = g, Key = k });
                            }
                        }
                    }

                    if (dirEntry.Value.TryGetProperty("MappingEndpoint", out var mapEp) &&
                        mapEp.TryGetProperty("FilePath", out var fp) && fp.GetString() is { Length: > 0 } mapPath &&
                        File.Exists(mapPath))
                    {
                        MappingsPath = mapPath;
                        UseMappings = true;
                    }
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(Name) || Name == "Default")
                Name = Path.GetFileName(GameDirectory.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : "ImportedGame";

            SaveDefault();
            status = $"Imported FModel settings ({GameDirectory})";
            return !string.IsNullOrEmpty(GameDirectory);
        }
        catch (Exception e)
        {
            status = $"Import failed: {e.Message}";
            return false;
        }
    }
}
