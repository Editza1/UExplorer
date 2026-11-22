using UExplorer.Config;
using UExplorer.Platform;
using UExplorer.Services;
using UExplorer.UI;

namespace UExplorer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--diagnose", StringComparer.OrdinalIgnoreCase))
            return DiagnosticsRunner.Run(args);

        var manifest = UexManifest.LoadDefault();
        if (string.IsNullOrWhiteSpace(manifest.GameDirectory))
            manifest.TryImportFromFModel(out _);

        using var mountService = new GameMountService();
        using var host = new Dx11AppHost($"UExplorer - Unreal Engine 5.2 ({manifest.Name})");
        using var editor = new Ue52MainEditor(host, manifest, mountService);
        editor.StartInitialMount();
        host.Run(dt => editor.DrawFrame(dt));
        return 0;
    }
}
