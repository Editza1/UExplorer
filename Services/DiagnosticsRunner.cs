using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Security;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using UExplorer.Config;
using UExplorer.Platform;
using UExplorer.Rendering;
using UExplorer.Scene;
using UExplorer.UI;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UExplorer.Services;

public static class DiagnosticsRunner
{
    private sealed class ActionProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    public static int Run(string[] args)
    {
        if (!TryParseArgs(args, out var explicitOut, out var noWindowProbe, out var dumpShaders, out var argError))
        {
            Console.Error.WriteLine(argError);
            return 1;
        }

        NativeAssetCache.EnableDiskDumps = dumpShaders;

        var defaultLogPath = Path.Combine(AppContext.BaseDirectory, "logs", "diagnose_report.txt");
        string targetPath;
        try
        {
            targetPath = explicitOut is not null ? Path.GetFullPath(explicitOut) : defaultLogPath;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] Invalid --diagnose-out path '{explicitOut}': {ex.Message}");
            return 1;
        }
        if (!TryOpenReportWriter(targetPath, allowFallback: explicitOut is null, out var sw, out var resolvedLogPath, out var openErr))
        {
            Console.Error.WriteLine($"[FAIL] Could not open diagnostics report '{targetPath}': {openErr}");
            return 1;
        }

        var origOut = Console.Out;
        var origErr = Console.Error;
        var exitCode = 0;
        var maxUiGapMs = 0.0;
        var niagaraVerified = "SKIP";
        var streamingVerified = "SKIP";
        var meanFps = 0.0;

        try
        {
            using (sw)
            {
                Console.SetOut(sw);
                Console.SetError(sw);

                Console.WriteLine("=== UExplorer Headless Diagnostics ===");
                Console.WriteLine($"PID={Environment.ProcessId}");
                Console.WriteLine($"ReportPath={resolvedLogPath}");

                var manifest = UexManifest.LoadDefault();
                if (string.IsNullOrWhiteSpace(manifest.GameDirectory))
                    manifest.TryImportFromFModel(out _);

                Console.WriteLine($"GameDir: {manifest.GameDirectory}");
                Console.WriteLine($"MappingsSpec: {manifest.MappingsPath} (exists={File.Exists(manifest.MappingsPath)})");

                using var mount = new GameMountService();
                var dev = D3D11NativeShaderDevice.Shared;
                Console.WriteLine($"[D3D11] Shared device={(dev is not null ? "OK" : "NULL: " + D3D11NativeShaderDevice.UnavailableReason)}");

                var mountResult = RunStartupDiagnostics(mount, manifest, dev, noWindowProbe, out maxUiGapMs, out var windowProbeOk);
                if (!windowProbeOk)
                    exitCode = 1;
                if (mountResult.Provider is null || !mount.IsMounted)
                {
                    Console.WriteLine($"[FAIL] Mount failed: {mountResult.Error}");
                    exitCode = 1;
                    return exitCode;
                }

                var prov = mountResult.Provider;
                Console.WriteLine($"[OK] Mounted {prov.Files.Count:N0} files.");

                var niagaraOk = RunNiagaraDiagnostics(prov, dev, out niagaraVerified);
                if (!niagaraOk)
                    exitCode = 1;
                RunMaterialDiagnostics(prov, dev);
                var thumbOk = RunThumbnailDiagnostics(prov, dev, out var thumbVerified);
                if (!thumbOk)
                    exitCode = 1;
                var liveMiOk = RunLiveMaterialOverrideDiagnostics(prov, dev, out var liveMiVerified);
                if (!liveMiOk)
                    exitCode = 1;

                var streamOk = RunStreamingDiagnostics(prov, out streamingVerified);
                if (!streamOk)
                    exitCode = 1;

                meanFps = RunWorldRenderAndPerfDiagnostics(prov, dev, out var perfOk);
                if (!perfOk)
                    exitCode = 1;

                var tiledOk = RunTiledLightDiagnostics(prov, dev, out var tiledVerified);
                if (!tiledOk)
                    exitCode = 1;

                Console.WriteLine("[Summary]");
                Console.WriteLine($"ReportPath={resolvedLogPath}");
                Console.WriteLine($"ExitCode={exitCode}");
                Console.WriteLine($"MaxUiGapMs={maxUiGapMs:F2}");
                Console.WriteLine($"NiagaraVerified={niagaraVerified}");
                Console.WriteLine($"ThumbnailsVerified={thumbVerified}");
                Console.WriteLine($"LiveMiVerified={liveMiVerified}");
                Console.WriteLine($"StreamingVerified={streamingVerified}");
                Console.WriteLine($"TiledLightVerified={tiledVerified}");
                Console.WriteLine($"MeanFps={meanFps:F1}");
            }
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
            origOut.WriteLine($"ReportPath={resolvedLogPath}");
        }

        return exitCode;
    }

    private static bool TryParseArgs(
        string[] args,
        out string? explicitOut,
        out bool noWindowProbe,
        out bool dumpShaders,
        out string? error)
    {
        explicitOut = null;
        noWindowProbe = false;
        dumpShaders = false;
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--no-window-probe", StringComparison.OrdinalIgnoreCase))
            {
                noWindowProbe = true;
            }
            else if (a.Equals("--dump-shaders", StringComparison.OrdinalIgnoreCase))
            {
                dumpShaders = true;
            }
            else if (a.Equals("--diagnose-out", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    error = "Missing value for --diagnose-out";
                    return false;
                }
                explicitOut = args[++i];
            }
            else if (a.StartsWith("--diagnose-out=", StringComparison.OrdinalIgnoreCase))
            {
                var val = a["--diagnose-out=".Length..].Trim();
                if (string.IsNullOrWhiteSpace(val))
                {
                    error = "Missing value for --diagnose-out=";
                    return false;
                }
                explicitOut = val;
            }
        }

        return true;
    }

    private static bool TryOpenReportWriter(
        string targetPath,
        bool allowFallback,
        out StreamWriter writer,
        out string resolvedPath,
        out string? error)
    {
        writer = null!;
        resolvedPath = targetPath;
        error = null;

        if (string.IsNullOrEmpty(Path.GetFileName(targetPath)) || Directory.Exists(targetPath))
        {
            error = $"Target path '{targetPath}' is a directory or root path, not a file.";
            return false;
        }

        string dir;
        try
        {
            dir = Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            error = ex.Message;
            return false;
        }

        var attempts = allowFallback ? 1 : 3;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                writer = new StreamWriter(fs) { AutoFlush = true };
                resolvedPath = targetPath;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
            {
                error = ex.Message;
                if (!allowFallback && attempt + 1 < attempts)
                    Thread.Sleep(50);
            }
        }

        if (allowFallback)
        {
            string[] fallbacks =
            [
                Path.Combine(dir, $"diagnose_report_{Environment.ProcessId}.txt"),
                Path.Combine(dir, $"diagnose_report_{Environment.ProcessId}_{Guid.NewGuid().ToString("N")[..6]}.txt")
            ];
            foreach (var fb in fallbacks)
            {
                try
                {
                    var fs = new FileStream(fb, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    writer = new StreamWriter(fs) { AutoFlush = true };
                    resolvedPath = fb;
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
                {
                    error = ex.Message;
                }
            }
        }

        return false;
    }

    private static MountResult RunStartupDiagnostics(
        GameMountService mount,
        UexManifest manifest,
        D3D11NativeShaderDevice? dev,
        bool noWindowProbe,
        out double maxUiGapMs,
        out bool windowProbeOk)
    {
        windowProbeOk = true;
        var progressEvents = new ConcurrentQueue<MountProgress>();
        var progress = new ActionProgress<MountProgress>(p => progressEvents.Enqueue(p));

        var swLoop = Stopwatch.StartNew();
        var mountTask = mount.MountAsync(manifest, progress, saveManifest: false);

        var uiTicks = 0;
        var prevMs = swLoop.Elapsed.TotalMilliseconds;
        maxUiGapMs = 0.0;

        while (!mountTask.IsCompleted)
        {
            Thread.Sleep(4);
            var nowMs = swLoop.Elapsed.TotalMilliseconds;
            var gapMs = nowMs - prevMs;
            if (gapMs > maxUiGapMs)
                maxUiGapMs = gapMs;
            prevMs = nowMs;
            uiTicks++;
        }

        var result = mountTask.GetAwaiter().GetResult();
        Console.WriteLine(
            $"[Startup] MountAsync: elapsedMs={result.Elapsed.TotalMilliseconds:F1}, files={result.FileCount:N0}, " +
            $"uiTicks={uiTicks}, maxUiGapMs={maxUiGapMs:F2}, progressEvents={progressEvents.Count}, " +
            $"mappings={result.ResolvedMappingsPath ?? "none"}, status={result.GameSummary}");

        if (!noWindowProbe && Environment.UserInteractive && dev is not null)
        {
            try
            {
                var swProbe = Stopwatch.StartNew();
                using var probeHost = new Dx11AppHost("UExplorer Diagnostics Probe", 320, 180)
                {
                    SyncInterval = 0
                };
                var windowReadyMs = swProbe.Elapsed.TotalMilliseconds;

                var invoked = false;
                probeHost.InvokeOnUiThread(() => invoked = true);
                var stepOk = true;
                for (var f = 0; f < 3; f++)
                    stepOk &= probeHost.StepProbeFrame();

                var firstFrameMs = swProbe.Elapsed.TotalMilliseconds;
                var (regularOk, boldOk) = CheckFonts(probeHost);
                windowProbeOk = stepOk && invoked && regularOk && boldOk;
                Console.WriteLine(
                    $"[Startup] WindowProbe: {(windowProbeOk ? "PASS" : "FAIL")} windowReadyMs={windowReadyMs:F1}, firstFrameMs={firstFrameMs:F1}, " +
                    $"stepOk={stepOk}, invoked={invoked}, regularFont={regularOk}, boldFont={boldOk}");
            }
            catch (Exception ex)
            {
                windowProbeOk = false;
                Console.WriteLine($"[Startup] WindowProbe: FAIL ({ex.Message})");
            }
        }
        else
        {
            Console.WriteLine($"[Startup] WindowProbe: SKIP (userInteractive={Environment.UserInteractive}, d3d11={dev is not null}, noWindowProbe={noWindowProbe})");
        }

        return result;
    }

    private static unsafe (bool Regular, bool Bold) CheckFonts(Dx11AppHost host)
        => (host.RegularFont.NativePtr != null, host.BoldFont.NativePtr != null);

    private static bool RunNiagaraDiagnostics(IFileProvider prov, D3D11NativeShaderDevice? dev, out string statusLabel)
    {
        string[] niagaraTargets =
        [
            "MoleGame/Items/ElimDevice/FX/NS_Mole_ElimDevice_Idle",
            "MoleEnvironments/FX/NS_Mole_Bubbles",
            "MoleEnvironments/FX/NS_Mole_Bridge_Bubbles",
            "MoleGame/Player/Effects/NS_Mole_Elim_Beam"
        ];

        var allOk = true;
        var verifiedCount = 0;
        NiagaraAsset? bridgeBubblesAsset = null;
        NiagaraAsset? elimDeviceAsset = null;

        foreach (var np in niagaraTargets)
        {
            try
            {
                var fileKey = prov.Files.Keys.FirstOrDefault(k =>
                    k.Contains(Path.GetFileName(np), StringComparison.OrdinalIgnoreCase) &&
                    k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase));
                if (fileKey is null) { Console.WriteLine($"[Niagara] SKIP (not found): {np}"); continue; }
                if (!prov.TryLoadPackage(fileKey, out var pkg)) { Console.WriteLine($"[Niagara] FAIL (LoadPackage): {fileKey}"); allOk = false; continue; }
                var sys = pkg.GetExports().FirstOrDefault(e => e.ExportType.Contains("NiagaraSystem", StringComparison.OrdinalIgnoreCase));
                if (sys is null) { Console.WriteLine($"[Niagara] SKIP (no NiagaraSystem export): {fileKey}"); continue; }
                var na = new NiagaraAsset(sys, sys.Name);
                Console.WriteLine($"[Niagara] {sys.Name}: Handles={na.DiscoveredEmitters.Count}, MeshEmitters={na.MeshEmitters.Count}, SpriteEmitters={na.SpriteEmitters.Count}, LightEmitters={na.LightEmitters.Count}");

                var sysClone = na.SystemSimulation?.Clone();
                NiagaraEmitterVmSimulation? GetSim(NiagaraEmitterVmSimulation? tmpl) =>
                    sysClone is not null ? sysClone.GetClonedSim(tmpl) : tmpl?.Clone();

                var emitterSamples = new Dictionary<string, NiagaraSimulatedParticle[]>(StringComparer.OrdinalIgnoreCase);

                foreach (var me in na.MeshEmitters)
                {
                    NiagaraSimulatedParticle[] vmSample = [];
                    var sim = GetSim(me.VmSimulation);
                    var vmOk = false;
                    if (sim is not null)
                    {
                        for (var s = 0; s < 90; s++)
                            vmOk = sim.TrySimulate((s + 1) * (1f / 60f), out vmSample);
                    }
                    emitterSamples[me.EmitterName] = vmSample;
                    var zSpan = vmSample.Length > 0 ? vmSample.Max(p => p.Position.Z) - vmSample.Min(p => p.Position.Z) : 0f;
                    var dynRange = vmSample.Length > 0
                        ? MathF.Max(
                            vmSample.Max(p => p.DynamicMaterialParameter.X) - vmSample.Min(p => p.DynamicMaterialParameter.X),
                            MathF.Max(
                                vmSample.Max(p => p.DynamicMaterialParameter.Y) - vmSample.Min(p => p.DynamicMaterialParameter.Y),
                                vmSample.Max(p => p.DynamicMaterialParameter.Z) - vmSample.Min(p => p.DynamicMaterialParameter.Z)))
                        : 0f;
                    string? pipeStatus = null;
                    using var meshPipe = (dev is not null && me.Material is not null)
                        ? NativeAssetCache.BuildNiagaraPipelineSync(dev, me.Material, isMesh: true, out pipeStatus)
                        : null;
                    var pipeOk = dev is null || me.Material is null || meshPipe is not null;
                    if (!pipeOk) allOk = false;
                    Console.WriteLine($"   MeshEmitter '{me.EmitterName}': Mesh={me.Mesh.Name}, Mat={me.Material?.Name ?? "null"}, PipeOK={pipeOk}({pipeStatus ?? "ok"}), HasVM={me.VmSimulation?.HasVmPrograms}, SimOk={vmOk} (cnt={vmSample.Length}, zSpan={zSpan:F1}cm, dynVar={dynRange:F3}, dynAttr={sim?.HasDynamicMaterialParameterAttr})");
                }

                foreach (var se in na.SpriteEmitters)
                {
                    NiagaraSimulatedParticle[] vmSample = [];
                    NiagaraSimulatedParticle[] vmSampleLate = [];
                    var sim = GetSim(se.VmSimulation);
                    var vmOk = false;
                    if (sim is not null)
                    {
                        for (var s = 0; s < 30; s++)
                            vmOk = sim.TrySimulate((s + 1) * (1f / 60f), out vmSample);
                        for (var s = 30; s < 60; s++)
                            vmOk = sim.TrySimulate((s + 1) * (1f / 60f), out vmSampleLate);
                    }
                    emitterSamples[se.EmitterName] = vmSampleLate.Length > 0 ? vmSampleLate : vmSample;
                    var avgDelta = 0f;
                    var pairCount = Math.Min(vmSample.Length, vmSampleLate.Length);
                    if (pairCount > 0)
                    {
                        for (var i = 0; i < pairCount; i++)
                            avgDelta += Vector3.Distance(vmSample[i].Position, vmSampleLate[i].Position);
                        avgDelta /= pairCount;
                    }
                    string? pipeStatus = null;
                    var isEngineDefault = se.Material?.Name.Equals("DefaultSpriteMaterial", StringComparison.OrdinalIgnoreCase) ?? false;
                    using var spritePipe = (dev is not null && se.Material is not null && !se.IsRibbon && !isEngineDefault)
                        ? NativeAssetCache.BuildNiagaraPipelineSync(dev, se.Material, isMesh: false, out pipeStatus)
                        : null;
                    var pipeOk = dev is null || se.Material is null || se.IsRibbon || isEngineDefault || spritePipe is not null;
                    if (!pipeOk) allOk = false;
                    Console.WriteLine($"   SpriteEmitter '{se.EmitterName}': Mat={se.Material?.Name ?? "null"}, PipeOK={pipeOk}({pipeStatus ?? (isEngineDefault ? "engine-default" : "ok")}), Ribbon={se.IsRibbon}, GPU={se.IsGpuCompute}, HasVM={se.VmSimulation?.HasVmPrograms}, SimOk={vmOk} (cnt={emitterSamples[se.EmitterName].Length}, avgPosDelta={avgDelta:F2}cm)");
                }

                foreach (var le in na.LightEmitters)
                    Console.WriteLine($"   LightEmitter '{le.EmitterName}': Col={le.Color}, Rad={le.DefaultRadius}");

                if (sys.Name.Equals("NS_Mole_ElimDevice_Idle", StringComparison.OrdinalIgnoreCase))
                {
                    elimDeviceAsset = na;
                    verifiedCount++;
                    var ringsOk = emitterSamples.TryGetValue("Rings", out var rP) && rP.Length > 2;
                    var ringsInnerOk = emitterSamples.TryGetValue("RingsInner", out var riP) && riP.Length > 2;
                    var allRings = (rP ?? []).Concat(riP ?? []).ToArray();
                    var zSpan = allRings.Length > 0 ? allRings.Max(p => p.Position.Z) - allRings.Min(p => p.Position.Z) : 0f;
                    var dynNonZero = allRings.Any(p => p.DynamicMaterialParameter.LengthSquared() > 1e-4f);
                    var dynVaries = allRings.Length > 1 &&
                                    (allRings.Max(p => p.DynamicMaterialParameter.X) - allRings.Min(p => p.DynamicMaterialParameter.X) > 1e-3f ||
                                     allRings.Max(p => p.DynamicMaterialParameter.Y) - allRings.Min(p => p.DynamicMaterialParameter.Y) > 1e-3f ||
                                     allRings.Max(p => p.DynamicMaterialParameter.Z) - allRings.Min(p => p.DynamicMaterialParameter.Z) > 1e-3f);
                    var glowOk = emitterSamples.TryGetValue("GlowLight", out var gP) && gP.Length > 0 && na.LightEmitters.Count > 0;
                    var simPass = ringsOk && ringsInnerOk && zSpan > 10f && dynNonZero && dynVaries && glowOk;
                    Console.WriteLine($"[Niagara] NS_Mole_ElimDevice_Idle SimCheck: {(simPass ? "PASS" : "FAIL")} (Rings={rP?.Length ?? 0}, RingsInner={riP?.Length ?? 0}, zSpan={zSpan:F1}cm, dynNonZero={dynNonZero}, dynVaries={dynVaries}, GlowLight={gP?.Length ?? 0})");
                    if (!simPass) allOk = false;

                    if (dev is not null)
                    {
                        var renderPass = VerifyElimDeviceRender(dev, na);
                        if (!renderPass) allOk = false;
                    }
                }
                else if (sys.Name.Equals("NS_Mole_Bridge_Bubbles", StringComparison.OrdinalIgnoreCase))
                {
                    bridgeBubblesAsset = na;
                    verifiedCount++;
                    var bSim = GetSim(na.SpriteEmitters.FirstOrDefault(e => e.EmitterName.Equals("Bubbles", StringComparison.OrdinalIgnoreCase))?.VmSimulation);
                    var bgSim = GetSim(na.SpriteEmitters.FirstOrDefault(e => e.EmitterName.Equals("BubblesGlass", StringComparison.OrdinalIgnoreCase))?.VmSimulation);
                    NiagaraSimulatedParticle[] b0 = [], b1 = [], bg1 = [];
                    if (bSim is not null)
                    {
                        for (var s = 0; s < 30; s++) bSim.TrySimulate((s + 1) * (1f / 60f), out b0);
                        for (var s = 30; s < 60; s++) bSim.TrySimulate((s + 1) * (1f / 60f), out b1);
                    }
                    if (bgSim is not null)
                    {
                        for (var s = 0; s < 60; s++) bgSim.TrySimulate((s + 1) * (1f / 60f), out bg1);
                    }
                    var finiteOk = b1.All(p => float.IsFinite(p.Position.X + p.Position.Y + p.Position.Z + p.Color.W + p.SpriteSize.X));
                    var motionDelta = 0f;
                    var n = Math.Min(b0.Length, b1.Length);
                    if (n > 0)
                    {
                        for (var i = 0; i < n; i++)
                            motionDelta += Vector3.Distance(b0[i].Position, b1[i].Position);
                        motionDelta /= n;
                    }
                    var simPass = b1.Length >= 8 && bg1.Length >= 8 && finiteOk && motionDelta > 1.0f;
                    Console.WriteLine($"[Niagara] NS_Mole_Bridge_Bubbles SimCheck: {(simPass ? "PASS" : "FAIL")} (Bubbles={b1.Length}, BubblesGlass={bg1.Length}, finite={finiteOk}, avgDelta={motionDelta:F2}cm)");
                    if (!simPass) allOk = false;

                    if (dev is not null)
                    {
                        var renderPass = VerifyBridgeBubblesRender(dev, na);
                        if (!renderPass) allOk = false;
                    }
                }
                else
                {
                    verifiedCount++;
                    var anyParticles = emitterSamples.Values.Any(arr => arr.Length > 0);
                    var renderActive = 0;
                    if (dev is not null)
                    {
                        var pngPath = Path.Combine(AppContext.BaseDirectory, "dump", "diagnose_niagara", sys.Name + ".png");
                        var cam = na.MeshEmitters.Count > 0 ? new Vector3(3.5f, 1.8f, 3.5f) : new Vector3(2.5f, 1.5f, 2.5f);
                        var rgba = RenderNiagaraCapture(dev, na, cam, new Vector3(0f, 0.8f, 0f), 640, 360, 45, 1f / 60f, pngPath);
                        int bgMax = Math.Max(rgba[0], Math.Max(rgba[1], rgba[2]));
                        for (var p = 0; p < 640 * 360; p++)
                        {
                            int mx = Math.Max(rgba[p * 4], Math.Max(rgba[p * 4 + 1], rgba[p * 4 + 2]));
                            if (mx > bgMax + 15) renderActive++;
                        }
                    }
                    Console.WriteLine($"[Niagara] {sys.Name} GeneralCheck: {(anyParticles ? "PASS" : "FAIL")} (particles={anyParticles}, renderActivePx={renderActive})");
                    if (!anyParticles) allOk = false;
                }
            }
            catch (Exception ex)
            {
                allOk = false;
                Console.WriteLine($"[Niagara EX] {np}: {ex}");
            }
        }

        if (dev is not null)
        {
            if (bridgeBubblesAsset is not null && elimDeviceAsset is not null)
            {
                if (!VerifyWorldTransformPlacement(dev, bridgeBubblesAsset, elimDeviceAsset))
                    allOk = false;
            }
            var spriteMat = elimDeviceAsset?.SpriteEmitters.FirstOrDefault()?.Material
                            ?? bridgeBubblesAsset?.SpriteEmitters.FirstOrDefault()?.Material;
            if (!VerifyNonSquareSpriteRotation(dev, spriteMat))
                allOk = false;
        }

        statusLabel = verifiedCount == 0 ? "SKIP" : (allOk ? "PASS" : "FAIL");
        return verifiedCount == 0 || allOk;
    }

    private static byte[] RenderNiagaraCapture(
        D3D11NativeShaderDevice dev,
        NiagaraAsset na,
        Vector3 camMeters,
        Vector3 targetMeters,
        int width,
        int height,
        int steps,
        float dt,
        string? dumpPngPath,
        Matrix4x4? worldMatrixOverride = null,
        bool forceWorldSpace = false)
    {
        using var renderer = new NativeSceneRenderer(dev)
        {
            ReadbackEnabled = true,
            Features = NativeRenderFeatures.None with { Bloom = true }
        };

        var worldMat = worldMatrixOverride ?? Matrix4x4.Identity;
        var sysClone = na.SystemSimulation?.Clone();
        var niagaraMeshes = new List<NativeNiagaraMeshBatch>();
        var niagaraSprites = new List<NativeNiagaraSpriteBatch>();
        var sceneLights = new List<SceneLight>();
        var builtMeshes = new Dictionary<UStaticMesh, NativeMeshLods>(ReferenceEqualityComparer.Instance);

        var savedMeshLocal = new List<(NiagaraMeshEmitterDesc Desc, bool Prev)>();
        var savedSpriteLocal = new List<(NiagaraSpriteEmitterDesc Desc, bool Prev)>();
        try
        {
            foreach (var me in na.MeshEmitters)
            {
                if (!me.Enabled) continue;
                if (!builtMeshes.TryGetValue(me.Mesh, out var nm))
                {
                    nm = NativeMeshLods.FromStaticMesh(dev.Device, me.Mesh);
                    builtMeshes[me.Mesh] = nm;
                }
                var mPipe = me.Material is not null ? NativeAssetCache.BuildNiagaraPipelineSync(dev, me.Material, isMesh: true, out _) : null;
                var sim = sysClone is not null ? sysClone.GetClonedSim(me.VmSimulation) : me.VmSimulation?.Clone();
                if (forceWorldSpace)
                {
                    savedMeshLocal.Add((me, me.LocalSpace));
                    me.LocalSpace = false;
                    if (sim is not null) sim.RuntimeLocalSpace = false;
                }
                niagaraMeshes.Add(new NativeNiagaraMeshBatch(me, nm, worldMat, null, sim, mPipe));
            }
            foreach (var se in na.SpriteEmitters)
            {
                if (!se.Enabled) continue;
                var sPipe = se.Material is not null ? NativeAssetCache.BuildNiagaraPipelineSync(dev, se.Material, isMesh: false, out _) : null;
                var srv = se.SpriteTexture is not null ? NativeTextureCache.Get(dev.Device, se.SpriteTexture) : null;
                var sim = sysClone is not null ? sysClone.GetClonedSim(se.VmSimulation) : se.VmSimulation?.Clone();
                if (forceWorldSpace)
                {
                    savedSpriteLocal.Add((se, se.LocalSpace));
                    se.LocalSpace = false;
                    if (sim is not null) sim.RuntimeLocalSpace = false;
                }
                niagaraSprites.Add(new NativeNiagaraSpriteBatch(se, worldMat, srv, sim, sPipe));
            }
            foreach (var le in na.LightEmitters)
            {
                if (!le.Enabled) continue;
                sceneLights.Add(new ScenePointLight
                {
                    Name = le.EmitterName,
                    LocationCm = worldMat.Translation,
                    Parent = null,
                    Color = le.Color,
                    Intensity = le.Intensity,
                    AttenuationRadius = le.DefaultRadius
                });
            }

            var unrealToMeters = new Matrix4x4(0.01f, 0, 0, 0, 0, 0, 0.01f, 0, 0, 0.01f, 0, 0, 0, 0, 0, 1);
            var view = Matrix4x4.CreateLookAt(camMeters, targetMeters, Vector3.UnitY);
            var aspect = (float) width / height;
            const float near = 0.05f;
            var f = aspect / MathF.Tan(60f * 0.5f * MathF.PI / 180f);
            var proj = new Matrix4x4(f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, 0, -1, 0, 0, near, 0);
            var worldToClip = unrealToMeters * view * proj;
            var camUn = new Vector3(camMeters.X, camMeters.Z, camMeters.Y) * 100f;

            var items = Array.Empty<NativeDrawItem>();
            for (var s = 0; s < steps; s++)
            {
                renderer.Render(width, height, worldToClip, camUn, 0.5f * f, items, ENativePreviewMode.Lit, 1.0f,
                    new Vector3(0.45f, 0.35f, 0.82f), measure: false, niagaraSprites: niagaraSprites,
                    niagaraMeshes: niagaraMeshes, sceneLights: sceneLights, timeOverride: (s + 1) * dt);
            }

            byte[] rgba = [];
            var finalTime = steps * dt;
            for (var flush = 0; flush < 3; flush++)
            {
                rgba = renderer.Render(width, height, worldToClip, camUn, 0.5f * f, items, ENativePreviewMode.Lit, 1.0f,
                    new Vector3(0.45f, 0.35f, 0.82f), measure: false, niagaraSprites: niagaraSprites,
                    niagaraMeshes: niagaraMeshes, sceneLights: sceneLights, timeOverride: finalTime);
            }

            if (!string.IsNullOrEmpty(dumpPngPath))
            {
                var dir = Path.GetDirectoryName(dumpPngPath)!;
                Directory.CreateDirectory(dir);
                using var img = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(rgba, width, height);
                img.SaveAsPng(dumpPngPath);
            }

            return rgba;
        }
        finally
        {
            foreach (var (d, prev) in savedMeshLocal) d.LocalSpace = prev;
            foreach (var (d, prev) in savedSpriteLocal) d.LocalSpace = prev;
            foreach (var nm in builtMeshes.Values)
                nm.Dispose();
            foreach (var m in niagaraMeshes)
                m.MaterialPipeline?.Dispose();
            foreach (var s in niagaraSprites)
                s.MaterialPipeline?.Dispose();
        }
    }

    private static int CountActivePixels(byte[] rgba, int w, int h, int deltaThreshold = 25)
    {
        if (rgba.Length < w * h * 4) return 0;
        int bgMax = Math.Max(rgba[0], Math.Max(rgba[1], rgba[2]));
        var active = 0;
        for (var p = 0; p < w * h; p++)
        {
            int mx = Math.Max(rgba[p * 4], Math.Max(rgba[p * 4 + 1], rgba[p * 4 + 2]));
            if (mx > bgMax + deltaThreshold)
                active++;
        }
        return active;
    }

    private static bool VerifyWorldTransformPlacement(D3D11NativeShaderDevice dev, NiagaraAsset bridgeBubbles, NiagaraAsset elimDevice)
    {
        const int w = 640, h = 360;
        var worldTranslation = Matrix4x4.CreateTranslation(2500f, -1500f, 800f);

        // (a1) Natural !isLocalSpace sprite system (NS_Mole_Bridge_Bubbles) placed at (2500, -1500, 800) cm
        var spriteIsLocal = bridgeBubbles.SpriteEmitters.Any(e => (e.VmSimulation?.RuntimeLocalSpace ?? e.LocalSpace));
        var spOriginRgba = RenderNiagaraCapture(dev, bridgeBubbles, new Vector3(0f, 2.5f, 11.5f), new Vector3(0f, 2.5f, 0f), w, h, 45, 1f / 60f, null, worldTranslation);
        var spTransRgba = RenderNiagaraCapture(dev, bridgeBubbles, new Vector3(25f, 10.5f, -3.5f), new Vector3(25f, 10.5f, -15f), w, h, 45, 1f / 60f, null, worldTranslation);
        var spOriginPx = CountActivePixels(spOriginRgba, w, h);
        var spTransPx = CountActivePixels(spTransRgba, w, h);

        // (a2) Forced !isLocalSpace mesh system (NS_Mole_ElimDevice_Idle) placed at (2500, -1500, 800) cm
        var elimOriginRgba = RenderNiagaraCapture(dev, elimDevice, new Vector3(0.45f, 0.16f, 0.45f), Vector3.Zero, w, h, 45, 1f / 60f, null, worldTranslation, forceWorldSpace: true);
        var elimTransRgba = RenderNiagaraCapture(dev, elimDevice, new Vector3(25.45f, 8.16f, -14.55f), new Vector3(25f, 8f, -15f), w, h, 45, 1f / 60f, null, worldTranslation, forceWorldSpace: true);
        var elimOriginPx = CountActivePixels(elimOriginRgba, w, h);
        var elimTransPx = CountActivePixels(elimTransRgba, w, h);

        // Also check RuntimeLocalSpace preservation on authored bLocalSpace=true emitters after stepping
        var elimClone = elimDevice.SystemSimulation?.Clone();
        var elimSim0 = elimClone is not null ? elimClone.GetClonedSim(elimDevice.MeshEmitters[0].VmSimulation) : elimDevice.MeshEmitters[0].VmSimulation?.Clone();
        if (elimSim0 is not null)
        {
            for (var s = 0; s < 10; s++)
                elimSim0.TrySimulate((s + 1) * (1f / 60f), out _);
        }
        var localSpacePreserved = elimDevice.MeshEmitters[0].LocalSpace && (elimSim0?.RuntimeLocalSpace ?? false);

        var pass = !spriteIsLocal && spOriginPx == 0 && spTransPx >= 200 &&
                   elimOriginPx == 0 && elimTransPx >= 200 && localSpacePreserved;
        Console.WriteLine($"[NiagaraRender] WorldTransformPlacement: {(pass ? "PASS" : "FAIL")} | Sprite(!local: origPx={spOriginPx}, transPx={spTransPx}), Mesh(!local: origPx={elimOriginPx}, transPx={elimTransPx}), LocalSpacePreserved={localSpacePreserved}");
        return pass;
    }

    private static bool VerifyNonSquareSpriteRotation(D3D11NativeShaderDevice dev, MaterialAsset? spriteMat)
    {
        const int w = 640, h = 360;

        static NiagaraEmitterVmSimulation MakeRectSpriteSim(float rotDeg)
        {
            var lits = new byte[20];
            BitConverter.TryWriteBytes(lits.AsSpan(0, 4), 1.0f);   // c[608]
            BitConverter.TryWriteBytes(lits.AsSpan(4, 4), 0.0f);   // c[612]
            BitConverter.TryWriteBytes(lits.AsSpan(8, 4), 180.0f); // c[616]
            BitConverter.TryWriteBytes(lits.AsSpan(12, 4), 30.0f); // c[620]
            BitConverter.TryWriteBytes(lits.AsSpan(16, 4), rotDeg);// c[624]

            var bc = new List<byte>();
            void U16(ushort v) { bc.Add((byte) (v & 0xFF)); bc.Add((byte) (v >> 8)); }
            // acquireindex ds=0 valid=c[608] -> r0
            bc.Add(86); bc.Add(1); U16(0); U16(608); U16(0);
            void OutFloat(ushort srcOff, ushort col)
            {
                bc.Add(83); bc.Add(1); U16(0); U16(0); U16(srcOff); U16(col);
            }
            OutFloat(612, 0); OutFloat(612, 1); OutFloat(612, 2); // Position
            OutFloat(608, 3); OutFloat(608, 4); OutFloat(608, 5); OutFloat(608, 6); // Color
            OutFloat(616, 7); OutFloat(620, 8); // SpriteSize = (180, 30)
            OutFloat(624, 9); // SpriteRotation = rotDeg
            bc.Add(0); // done

            var prog = new NiagaraVmProgram
            {
                ScriptName = $"DiagRectSprite_{rotDeg:0}",
                ByteCode = [.. bc],
                NumTempRegisters = 4,
                ScriptLiterals = lits,
                Attributes = ["Position", "Color", "SpriteSize", "SpriteRotation"],
            };
            return new NiagaraEmitterVmSimulation(prog, null, null, targetParticles: 1, lifetime: 5f, burstCount: 1);
        }

        (int W, int H, int Px) RenderRectBounds(float rotDeg, MaterialAsset? mat)
        {
            using var renderer = new NativeSceneRenderer(dev)
            {
                ReadbackEnabled = true,
                Features = NativeRenderFeatures.None with { Bloom = false }
            };
            using var pipe = mat is not null ? NativeAssetCache.BuildNiagaraPipelineSync(dev, mat, isMesh: false, out _) : null;
            var sim = MakeRectSpriteSim(rotDeg);
            var desc = new NiagaraSpriteEmitterDesc
            {
                EmitterName = "DiagRect",
                Material = mat,
                LocalSpace = true,
                SpriteSize = new Vector2(180f, 30f),
                MaxParticles = 1,
                Lifetime = 5f,
                IsAdditive = true,
                VmSimulation = sim,
                Enabled = true
            };
            var batches = new[] { new NativeNiagaraSpriteBatch(desc, Matrix4x4.Identity, null, sim, pipe) };
            var camMeters = new Vector3(0f, 0f, 5f);
            var unrealToMeters = new Matrix4x4(0.01f, 0, 0, 0, 0, 0, 0.01f, 0, 0, 0.01f, 0, 0, 0, 0, 0, 1);
            var view = Matrix4x4.CreateLookAt(camMeters, Vector3.Zero, Vector3.UnitY);
            var aspect = (float) w / h;
            const float near = 0.05f;
            var f = aspect / MathF.Tan(60f * 0.5f * MathF.PI / 180f);
            var proj = new Matrix4x4(f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, 0, -1, 0, 0, near, 0);
            var worldToClip = unrealToMeters * view * proj;
            var camUn = new Vector3(camMeters.X, camMeters.Z, camMeters.Y) * 100f;

            byte[] rgba = [];
            for (var s = 0; s < 3; s++)
            {
                rgba = renderer.Render(w, h, worldToClip, camUn, 0.5f * f, Array.Empty<NativeDrawItem>(), ENativePreviewMode.Lit, 1.0f,
                    new Vector3(0.45f, 0.35f, 0.82f), measure: false, niagaraSprites: batches, timeOverride: (s + 1) * (1f / 60f));
            }

            int bgMax = Math.Max(rgba[0], Math.Max(rgba[1], rgba[2]));
            int minX = w, maxX = -1, minY = h, maxY = -1, active = 0;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var p = (y * w + x) * 4;
                    int mx = Math.Max(rgba[p], Math.Max(rgba[p + 1], rgba[p + 2]));
                    if (mx > bgMax + 15)
                    {
                        active++;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            return maxX >= minX ? (maxX - minX + 1, maxY - minY + 1, active) : (0, 0, 0);
        }

        var fb0 = RenderRectBounds(0f, null);
        var fb90 = RenderRectBounds(90f, null);
        var fbPass = fb0.Px > 50 && fb90.Px > 50 && fb0.W > fb0.H * 2 && fb90.H > fb90.W * 2;

        var cookedPass = true;
        var cookedDetail = "skipped";
        if (spriteMat is not null)
        {
            var ck0 = RenderRectBounds(0f, spriteMat);
            var ck90 = RenderRectBounds(90f, spriteMat);
            cookedPass = ck0.Px > 50 && ck90.Px > 50 && ck0.W > ck0.H * 2 && ck90.H > ck90.W * 2;
            cookedDetail = $"0deg={ck0.W}x{ck0.H}(px={ck0.Px}), 90deg={ck90.W}x{ck90.H}(px={ck90.Px})";
        }

        var pass = fbPass && cookedPass;
        Console.WriteLine($"[NiagaraRender] NonSquareSpriteRotation: {(pass ? "PASS" : "FAIL")} | Fallback(0deg={fb0.W}x{fb0.H}(px={fb0.Px}), 90deg={fb90.W}x{fb90.H}(px={fb90.Px})), CookedAdapter({cookedDetail})");
        return pass;
    }

    private static bool VerifyBridgeBubblesRender(D3D11NativeShaderDevice dev, NiagaraAsset na)
    {
        const int w = 640, h = 360;
        var pngPath = Path.Combine(AppContext.BaseDirectory, "dump", "diagnose_niagara", "NS_Mole_Bridge_Bubbles.png");
        var rgba = RenderNiagaraCapture(dev, na, new Vector3(0f, 2.5f, 11.5f), new Vector3(0f, 2.5f, 0f), w, h, 45, 1f / 60f, pngPath);

        int bgR = rgba[0], bgG = rgba[1], bgB = rgba[2];
        var bgMax = Math.Max(bgR, Math.Max(bgG, bgB));
        var activeLums = new List<double>();
        int softCount = 0, brightRimCount = 0;
        int redBucket = 0, greenBucket = 0, blueBucket = 0, magentaBucket = 0, cyanBucket = 0;

        for (var p = 0; p < w * h; p++)
        {
            int r = rgba[p * 4 + 0], g = rgba[p * 4 + 1], b = rgba[p * 4 + 2];
            var maxC = Math.Max(r, Math.Max(g, b));
            if (maxC <= bgMax + 25)
                continue;

            var lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            var bgLum = 0.2126 * bgR + 0.7152 * bgG + 0.0722 * bgB;
            activeLums.Add(lum);
            if (lum > bgLum + 80) brightRimCount++;
            else softCount++;

            if (r > g + 15 && b > g + 15) magentaBucket++;
            else if (b > r + 15 && g > r + 10) cyanBucket++;
            else if (r > g + 15 && r > b + 15) redBucket++;
            else if (g > r + 15 && g > b + 15) greenBucket++;
            else if (b > r + 15 && b > g + 15) blueBucket++;
        }

        var active = activeLums.Count;
        var mean = active > 0 ? activeLums.Average() : 0.0;
        var stdDev = active > 1 ? Math.Sqrt(activeLums.Sum(l => (l - mean) * (l - mean)) / active) : 0.0;
        var minThreshold = Math.Max(5, (int) (active * 0.02));
        var hueBuckets = 0;
        if (redBucket >= minThreshold) hueBuckets++;
        if (greenBucket >= minThreshold) hueBuckets++;
        if (blueBucket >= minThreshold) hueBuckets++;
        if (magentaBucket >= minThreshold) hueBuckets++;
        if (cyanBucket >= minThreshold) hueBuckets++;

        var pass = active >= 200 && stdDev > 12.0 && brightRimCount > 0 && softCount > 0 && hueBuckets >= 2;
        Console.WriteLine($"[NiagaraRender] NS_Mole_Bridge_Bubbles: {(pass ? "PASS" : "FAIL")} | activePx={active}, brightRim={brightRimCount}, soft={softCount}, stdDevLum={stdDev:F1}, hueBuckets={hueBuckets} (R={redBucket},G={greenBucket},B={blueBucket},M={magentaBucket},C={cyanBucket}), DumpPng={pngPath}");
        return pass;
    }

    private static bool VerifyElimDeviceRender(D3D11NativeShaderDevice dev, NiagaraAsset na)
    {
        const int w = 640, h = 360;
        var pngPath = Path.Combine(AppContext.BaseDirectory, "dump", "diagnose_niagara", "NS_Mole_ElimDevice_Idle.png");
        var rgba = RenderNiagaraCapture(dev, na, new Vector3(0.45f, 0.16f, 0.45f), Vector3.Zero, w, h, 45, 1f / 60f, pngPath);

        int bgR = rgba[0], bgG = rgba[1], bgB = rgba[2];
        var bgMax = Math.Max(bgR, Math.Max(bgG, bgB));
        int active = 0, cyanCount = 0, magentaCount = 0, coreBrightCount = 0;
        int minY = h, maxY = -1;
        var activeRows = new HashSet<int>();

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var p = y * w + x;
                int r = rgba[p * 4 + 0], g = rgba[p * 4 + 1], b = rgba[p * 4 + 2];
                var maxC = Math.Max(r, Math.Max(g, b));
                if (maxC <= bgMax + 25)
                    continue;

                active++;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                activeRows.Add(y);

                if (b > r + 20 && g > r + 10) cyanCount++;
                if (r > g + 15 && b > g) magentaCount++;
                if (maxC >= 200) coreBrightCount++;
            }
        }

        var vSpan = maxY >= minY ? maxY - minY : 0;
        var pass = active >= 300 && cyanCount >= 30 && magentaCount >= 30 && coreBrightCount >= 15 &&
                   vSpan >= (int) (h * 0.12) && activeRows.Count >= 25;
        Console.WriteLine($"[NiagaraRender] NS_Mole_ElimDevice_Idle: {(pass ? "PASS" : "FAIL")} | activePx={active}, cyan={cyanCount}, magenta={magentaCount}, coreBright={coreBrightCount}, vSpan={vSpan}px, activeRows={activeRows.Count}, DumpPng={pngPath}");
        return pass;
    }

    private static void RunMaterialDiagnostics(IFileProvider prov, D3D11NativeShaderDevice? dev)
    {
        if (dev is null)
        {
            Console.WriteLine("[Materials] SKIP (no D3D11 device)");
            return;
        }

        string[] matTargets =
        [
            "MI_Agency_Midas_DeskLamp_01",
            "MI_Lab_Tunnel_Clamp",
            "M_Mole_Rock_PDO_AB",
            "M_NewMole_AquariumGlass",
            "M_NexPillarBlend_2",
            "M_DarkenArea_Inst",
            "M_Table_Holo_FX"
        ];

        foreach (var mt in matTargets)
        {
            var mk = prov.Files.Keys.FirstOrDefault(k => k.EndsWith("/" + mt + ".uasset", StringComparison.OrdinalIgnoreCase));
            if (mk is null || !prov.TryLoadPackage(mk, out var mpkg)) { Console.WriteLine($"[Materials] Not found: {mt}"); continue; }
            var uMat = mpkg.GetExports().OfType<UUnrealMaterial>().FirstOrDefault();
            if (uMat is null) continue;
            var ma = new MaterialAsset(uMat);
            var pipe = NativeAssetCache.BuildPipelineSync(dev, ma, out var perr);
            Console.WriteLine($"[Materials] {mt}: Built={(pipe is not null ? "OK" : "FAIL: " + perr)} | Blend={pipe?.BlendMode} | Forward={pipe?.IsForward} | EarlyZMask={pipe?.NeedsEarlyZMaskPass} | DepthDiscard={pipe?.HasDepthOnlyDiscard}");
        }
    }

    private static bool RunThumbnailDiagnostics(IFileProvider prov, D3D11NativeShaderDevice? dev, out string statusLabel)
    {
        if (dev is null)
        {
            statusLabel = "SKIP";
            Console.WriteLine("[Thumbnails] SKIP (no D3D11 device)");
            return true;
        }

        string[] targets =
        [
            "MI_Lab_Tunnel_Clamp",
            "MI_Agency_Midas_DeskLamp_01",
            "M_NewMole_AquariumGlass",
            "M_Table_Holo_FX",
            "MI_Agency_Security_Camera_01",
            "M_Mole_Rock_PDO_AB"
        ];

        var dumpDir = Path.Combine(AppContext.BaseDirectory, "dump", "diagnose_thumbnails");
        Directory.CreateDirectory(dumpDir);

        try
        {
            using var thumbRenderer = new NativeThumbnailRenderer(dev, NativeThumbnailRenderer.DefaultSize) { ReadbackEnabled = true };
            using var procSphere = NativeThumbnailRenderer.CreateSphereMesh(dev.Device, radiusCm: 50f);

            NativeMeshLods? basicSphere = null;
            if (prov.TryLoadPackageObject<UStaticMesh>("Engine/Content/BasicShapes/Sphere.Sphere", out var basicSm))
            {
                try { basicSphere = NativeMeshLods.FromStaticMesh(dev.Device, basicSm); } catch { basicSphere = null; }
            }

            // 1. Bounded async background prep (TryLoadPackage + MaterialAsset + NativeTextureCache.PrepareCpu + BuildPipelineSync)
            var prepTasks = new List<Task<(string Name, MaterialAsset? Mat, NativeMaterialPipeline? Pipe)>>();
            using var sem = new SemaphoreSlim(2);
            foreach (var name in targets)
            {
                var n = name;
                prepTasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var mk = prov.Files.Keys.FirstOrDefault(k => k.EndsWith("/" + n + ".uasset", StringComparison.OrdinalIgnoreCase));
                        if (mk is null || !prov.TryLoadPackage(mk, out var pkg))
                            return (n, (MaterialAsset?) null, (NativeMaterialPipeline?) null);
                        var uMat = pkg.GetExports().OfType<UUnrealMaterial>().FirstOrDefault();
                        if (uMat is null)
                            return (n, null, null);
                        var ma = new MaterialAsset(uMat);
                        var pipe = NativeAssetCache.BuildPipelineSync(dev, ma, out _);
                        return (n, ma, pipe);
                    }
                    finally
                    {
                        sem.Release();
                    }
                }));
            }

            // Also verify NativeTextureCache.PrepareCpu + UploadPrepared on a Fortnite texture
            var texKey = prov.Files.Keys.FirstOrDefault(k =>
                k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                k.Contains("/T_", StringComparison.OrdinalIgnoreCase) &&
                k.Contains("Mole", StringComparison.OrdinalIgnoreCase));
            if (texKey is not null && prov.TryLoadPackage(texKey, out var texPkg))
            {
                var tex = texPkg.GetExports().OfType<UTexture2D>().FirstOrDefault();
                if (tex is not null)
                {
                    var cpu = NativeTextureCache.PrepareCpu(tex, maxSizeOverride: 128);
                    using var texSrv = cpu is not null ? NativeTextureCache.UploadPrepared(dev.Device, cpu) : null;
                    Console.WriteLine($"[Thumbnails] TextureSplit '{tex.Name}': PrepareCpu={(cpu is not null)}, UploadPrepared={(texSrv is not null)}");
                }
            }

            var prepared = Task.WhenAll(prepTasks).GetAwaiter().GetResult();

            // 2. Measure budgeted UI-thread GPU render pacing (batches of 2) & verify pixel bounds
            var allOk = true;
            var renderedCount = 0;
            var maxBatchMs = 0.0;
            var sw = new Stopwatch();
            var verifiedOpaqueSphere = false;
            MaterialAsset? firstOpaqueMat = null;
            NativeMaterialPipeline? firstOpaquePipe = null;

            for (var i = 0; i < prepared.Length; i += 2)
            {
                sw.Restart();
                var batchStart = i;
                for (var b = 0; b < 2 && i + b < prepared.Length; b++)
                {
                    var (name, mat, pipe) = prepared[i + b];
                    if (mat is null || pipe is null) continue;

                    using var srv = thumbRenderer.RenderMaterialToThumbnail(mat, pipe, procSphere, prov);
                    if (srv is null)
                    {
                        Console.WriteLine($"[Thumbnails] FAIL render returned null for {name}");
                        allOk = false;
                        continue;
                    }
                    renderedCount++;
                }
                sw.Stop();
                if (sw.Elapsed.TotalMilliseconds > maxBatchMs)
                    maxBatchMs = sw.Elapsed.TotalMilliseconds;

                for (var b = 0; b < 2 && batchStart + b < prepared.Length; b++)
                {
                    var (name, mat, pipe) = prepared[batchStart + b];
                    if (mat is null || pipe is null) continue;

                    var rgba = thumbRenderer.RenderMaterialCapture(mat, pipe, procSphere, prov);
                    var pngPath = Path.Combine(dumpDir, $"{name}_proc.png");
                    SaveRgbaPng(pngPath, thumbRenderer.Size, thumbRenderer.Size, rgba);

                    if (!pipe.IsForward)
                    {
                        firstOpaqueMat ??= mat;
                        firstOpaquePipe ??= pipe;
                        var frameOk = VerifyThumbnailSpherePixels(name + "_proc", rgba, thumbRenderer.Size, out var summary);
                        Console.WriteLine($"[Thumbnails] {name} (proc r=50cm): {(frameOk ? "PASS" : "FAIL")} | {summary} | {pngPath}");
                        if (!frameOk) allOk = false;
                        else verifiedOpaqueSphere = true;
                    }
                    else
                    {
                        Console.WriteLine($"[Thumbnails] {name} (forward {pipe.BlendMode}): OK | {pngPath}");
                    }
                }
            }

            if (basicSphere is not null && firstOpaqueMat is not null && firstOpaquePipe is not null)
            {
                var rgbaBasic = thumbRenderer.RenderMaterialCapture(firstOpaqueMat, firstOpaquePipe, basicSphere, prov);
                var basicPng = Path.Combine(dumpDir, $"{firstOpaqueMat.Name}_BasicSphere.png");
                SaveRgbaPng(basicPng, thumbRenderer.Size, thumbRenderer.Size, rgbaBasic);
                var basicOk = VerifyThumbnailSpherePixels(firstOpaqueMat.Name + "_BasicSphere", rgbaBasic, thumbRenderer.Size, out var basicSummary);
                Console.WriteLine($"[Thumbnails] BasicShapes/Sphere: {(basicOk ? "PASS" : "FAIL")} | {basicSummary} | {basicPng}");
                if (!basicOk) allOk = false;
                basicSphere.Dispose();
            }

            foreach (var (_, _, pipe) in prepared)
                pipe?.Dispose();

            var pacingOk = maxBatchMs < 50.0; // generous headless warmup guard; log actual ms
            Console.WriteLine($"[Thumbnails] Pacing: rendered={renderedCount}, maxBatch2Ms={maxBatchMs:F2}ms (target < 15ms steady), verifiedOpaqueSphere={verifiedOpaqueSphere}");
            var pass = allOk && renderedCount >= 3 && verifiedOpaqueSphere && pacingOk;
            statusLabel = pass ? "PASS" : "FAIL";
            Console.WriteLine($"[Thumbnails] Overall: {statusLabel}");
            return pass;
        }
        catch (Exception ex)
        {
            statusLabel = "FAIL";
            Console.WriteLine($"[Thumbnails] FAIL: {ex}");
            return false;
        }
    }

    private static bool VerifyThumbnailSpherePixels(string label, byte[] rgba, int size, out string summary)
    {
        int active = 0, minX = size, maxX = -1, minY = size, maxY = -1;
        double sumLum = 0, sumLumSq = 0;

        for (var y = 0; y < size; y++)
        {
            // Compare against left/right edge pixels on the same scanline to tolerate the studio vertical gradient backdrop.
            var rowLeft = y * size * 4;
            var rowRight = (y * size + (size - 1)) * 4;
            int bgR = (rgba[rowLeft] + rgba[rowRight]) >> 1;
            int bgG = (rgba[rowLeft + 1] + rgba[rowRight + 1]) >> 1;
            int bgB = (rgba[rowLeft + 2] + rgba[rowRight + 2]) >> 1;

            for (var x = 0; x < size; x++)
            {
                var p = (y * size + x) * 4;
                int r = rgba[p], g = rgba[p + 1], b = rgba[p + 2];
                var diff = Math.Max(Math.Abs(r - bgR), Math.Max(Math.Abs(g - bgG), Math.Abs(b - bgB)));
                if (diff <= 15) continue;

                active++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                var lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                sumLum += lum;
                sumLumSq += lum * lum;
            }
        }

        var coverage = active / (double) (size * size);
        var bboxW = maxX >= minX ? maxX - minX + 1 : 0;
        var bboxH = maxY >= minY ? maxY - minY + 1 : 0;
        var cx = maxX >= minX ? (minX + maxX) * 0.5 : 0.0;
        var cy = maxY >= minY ? (minY + maxY) * 0.5 : 0.0;
        var aspect = bboxH > 0 ? bboxW / (double) bboxH : 0.0;
        var meanLum = active > 0 ? sumLum / active : 0.0;
        var variance = active > 0 ? Math.Max(0.0, sumLumSq / active - meanLum * meanLum) : 0.0;
        var stdDev = Math.Sqrt(variance);

        var ok = coverage is >= 0.15 and <= 0.70 &&
                 minX >= 4 && maxX <= size - 5 &&
                 minY >= 4 && maxY <= size - 5 &&
                 Math.Abs(cx - size * 0.5) <= size * 0.10 &&
                 Math.Abs(cy - size * 0.5) <= size * 0.10 &&
                 aspect is >= 0.85 and <= 1.18 &&
                 stdDev > 6.0;

        summary = $"cov={coverage:P1}, bbox=[{minX}..{maxX},{minY}..{maxY}], aspect={aspect:F2}, center=({cx:F1},{cy:F1}), lumStdDev={stdDev:F1}";
        return ok;
    }

    private static void SaveRgbaPng(string path, int width, int height, byte[] rgba)
    {
        using var img = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(rgba, width, height);
        img.SaveAsPng(path);
    }

    private static bool RunLiveMaterialOverrideDiagnostics(IFileProvider prov, D3D11NativeShaderDevice? dev, out string statusLabel)
    {
        if (dev is null)
        {
            statusLabel = "SKIP";
            Console.WriteLine("[LiveMI] SKIP (no D3D11 device)");
            return true;
        }

        try
        {
            string[] candidates = ["MI_Lab_Tunnel_Clamp", "MI_Agency_Midas_DeskLamp_01", "MI_Agency_Security_Camera_01"];
            MaterialAsset? targetMat = null;
            NativeMaterialShaders? targetSet = null;
            string? scalarName = null;
            string? vectorName = null;
            string? texName = null;

            foreach (var c in candidates)
            {
                var mk = prov.Files.Keys.FirstOrDefault(k => k.EndsWith("/" + c + ".uasset", StringComparison.OrdinalIgnoreCase));
                if (mk is null || !prov.TryLoadPackage(mk, out var pkg)) continue;
                var uMat = pkg.GetExports().OfType<UUnrealMaterial>().FirstOrDefault();
                if (uMat is null) continue;
                var ma = new MaterialAsset(uMat);
                var set = ma.GetNativeShaders().FirstOrDefault(s => s.UniformSet is not null);
                if (set?.UniformSet is not { } u) continue;

                var sName = u.UniformScalarParameters.Select(p => p.ParameterInfo?.Name.Text ?? p.ParameterName)
                    .FirstOrDefault(n => !string.IsNullOrEmpty(n) && n != "None");
                var vName = u.UniformVectorParameters.Select(p => p.ParameterInfo?.Name.Text ?? p.ParameterName)
                    .FirstOrDefault(n => !string.IsNullOrEmpty(n) && n != "None");
                var tName = u.UniformTextureParameters.SelectMany(a => a ?? [])
                    .Select(p => p.ParameterInfo?.Name.Text ?? p.ParameterName)
                    .FirstOrDefault(n => !string.IsNullOrEmpty(n) && n != "None");
                if (sName is not null || vName is not null || tName is not null)
                {
                    targetMat = ma;
                    targetSet = set;
                    scalarName = sName;
                    vectorName = vName;
                    texName = tName;
                    break;
                }
            }

            if (targetMat is null || targetSet is null)
            {
                statusLabel = "SKIP";
                Console.WriteLine("[LiveMI] SKIP (no suitable material with parameters found)");
                return true;
            }

            using var pipe = NativeAssetCache.BuildPipelineSync(dev, targetMat, out var err);
            if (pipe is null)
            {
                statusLabel = "FAIL";
                Console.WriteLine($"[LiveMI] FAIL building pipeline for {targetMat.Name}: {err}");
                return false;
            }

            var vsBefore = pipe.VertexShader;
            var psBefore = pipe.PixelShader;

            using var thumbRenderer = new NativeThumbnailRenderer(dev, 128) { ReadbackEnabled = true };
            using var sphere = NativeThumbnailRenderer.CreateSphereMesh(dev.Device, 50f);

            var beforeBytes = thumbRenderer.RenderMaterialCapture(targetMat, pipe, sphere, prov);

            // Mutate scalar/vector/texture overrides live without rebuilding shaders
            if (scalarName is not null)
                targetMat.NativeScalarOverrides[scalarName] = 12.5f;
            if (vectorName is not null)
                targetMat.NativeVectorOverrides[vectorName] = new Vector4(0.95f, 0.15f, 0.85f, 1.0f);
            if (texName is not null)
                targetMat.NativeTextureOverrides[texName] = null; // explicit null override short-circuits TextureIndex fallback

            targetMat.NotifyParametersChanged();
            var synced = pipe.EnsureParametersSynced(dev.Device, dev.Context, prov, targetMat);
            var afterNullTexBytes = thumbRenderer.RenderMaterialCapture(targetMat, pipe, sphere, prov);

            // Also test overriding texture to another mounted UTexture2D if available
            var altTexPath = prov.Files.Keys.FirstOrDefault(k =>
                k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                k.Contains("/T_", StringComparison.OrdinalIgnoreCase) &&
                k.Contains("Mole", StringComparison.OrdinalIgnoreCase));
            if (texName is not null && altTexPath is not null)
            {
                targetMat.NativeTextureOverrides[texName] = altTexPath;
                targetMat.NotifyParametersChanged();
                pipe.EnsureParametersSynced(dev.Device, dev.Context, prov, targetMat);
            }
            var afterAltTexBytes = thumbRenderer.RenderMaterialCapture(targetMat, pipe, sphere, prov);

            var diffPixels1 = CountDiffPixels(beforeBytes, afterNullTexBytes);
            var diffPixels2 = CountDiffPixels(beforeBytes, afterAltTexBytes);
            var sameShaders = ReferenceEquals(vsBefore, pipe.VertexShader) && ReferenceEquals(psBefore, pipe.PixelShader);
            var versionMatch = pipe.SyncedParameterVersion == targetMat.ParameterVersion;

            var pass = synced && sameShaders && versionMatch && (diffPixels1 > 10 || diffPixels2 > 10);
            statusLabel = pass ? "PASS" : "FAIL";
            Console.WriteLine(
                $"[LiveMI] {targetMat.Name}: {statusLabel} | synced={synced}, sameShaders={sameShaders}, version={pipe.SyncedParameterVersion}=={targetMat.ParameterVersion}, " +
                $"scalar={scalarName ?? "none"}, vector={vectorName ?? "none"}, tex={texName ?? "none"}, diffPx1={diffPixels1}, diffPx2={diffPixels2}");
            return pass;
        }
        catch (Exception ex)
        {
            statusLabel = "FAIL";
            Console.WriteLine($"[LiveMI] FAIL: {ex}");
            return false;
        }
    }

    private static int CountDiffPixels(byte[] a, byte[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        var diff = 0;
        for (var i = 0; i + 3 < len; i += 4)
        {
            var d = Math.Max(Math.Abs(a[i] - b[i]), Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
            if (d > 4) diff++;
        }
        return diff;
    }

    private static bool RunStreamingDiagnostics(IFileProvider prov, out string statusLabel)
    {
        var allMoleMaps = prov.Files.Keys
            .Where(k => k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) &&
                        k.Contains("Mole", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x)
            .ToList();

        Console.WriteLine($"[Streaming] Total Mole .umap files ({allMoleMaps.Count}):");
        foreach (var mm in allMoleMaps.Take(50))
            Console.WriteLine($"   map: {mm}");

        if (allMoleMaps.Count == 0)
        {
            statusLabel = "SKIP";
            Console.WriteLine("[Streaming] SKIP (no Mole .umap maps found in mounted game)");
            return true;
        }

        try
        {
            // Part A: Scan prioritized Mole .umap headers for DiscoverStreamingLevels
            var candidates = allMoleMaps
                .OrderByDescending(k =>
                    (k.Contains("Layout", StringComparison.OrdinalIgnoreCase) ? 4 : 0) +
                    (k.Contains("Persistent", StringComparison.OrdinalIgnoreCase) ? 3 : 0) +
                    (k.Contains("Main", StringComparison.OrdinalIgnoreCase) ? 2 : 0) +
                    (k.Contains("Master", StringComparison.OrdinalIgnoreCase) ? 2 : 0) +
                    (k.Contains("MoleMapUnderBase", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
                .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Take(25)
                .ToList();

            string? discoveredParentMap = null;
            IReadOnlyList<StreamingLevelDescriptor> discoveredDescriptors = [];

            foreach (var cand in candidates)
            {
                if (!prov.TryLoadPackage(cand, out var pkg))
                    continue;
                var world = pkg.GetExports().OfType<UWorld>().FirstOrDefault();
                if (world is null)
                    continue;
                var descs = WorldLoader.DiscoverStreamingLevels(world, prov);
                if (descs.Count > 0)
                {
                    discoveredParentMap = cand;
                    discoveredDescriptors = descs;
                    break;
                }
            }

            if (discoveredParentMap is not null)
            {
                Console.WriteLine($"[Streaming] DiscoverStreamingLevels found {discoveredDescriptors.Count} entries in '{Path.GetFileName(discoveredParentMap)}':");
                foreach (var d in discoveredDescriptors.Take(12))
                    Console.WriteLine($"   descriptor: Name={d.Name}, Class={d.StreamingClass}, Resolvable={d.IsResolvable}, InitLoaded={d.InitiallyLoaded}, InitVisible={d.InitiallyVisible}, Path={d.PackagePath}");

                var resolvable = discoveredDescriptors.FirstOrDefault(d => d.IsResolvable);
                if (resolvable is not null)
                {
                    var discScene = new WorldScene();
                    var pLvl = WorldLoader.LoadLevelAdditive(discScene, prov, discoveredParentMap, isPersistent: true);
                    var sLvl = WorldLoader.LoadLevelAdditive(discScene, prov, resolvable.PackagePath, resolvable.LevelTransform, isPersistent: false, nameOverride: resolvable.Name);
                    if (discScene.Levels.Count != 2 || !discScene.Levels.Contains(pLvl) || !discScene.Levels.Contains(sLvl))
                        throw new InvalidOperationException($"Discovered streaming LoadLevelAdditive mismatch on {resolvable.Name}.");
                    Console.WriteLine($"[Streaming] Discovery additive check PASS: {pLvl.Name} + {sLvl.Name}");
                }
            }
            else
            {
                Console.WriteLine($"[Streaming] DiscoverStreamingLevels scanned {candidates.Count} Mole headers (0 StreamingLevels descriptors; breakouts are standalone .umap maps).");
            }

            // Part B: Deterministic multi-.umap additive load / visibility / unload / WorldSettings checks
            var moleGlobal = allMoleMaps.FirstOrDefault(k => k.Contains("Mole_UB_Lighting_Global", StringComparison.OrdinalIgnoreCase));
            var moleSleep = allMoleMaps.FirstOrDefault(k => k.Contains("Mole_UB_SleepQuarters", StringComparison.OrdinalIgnoreCase));
            var moleFx = allMoleMaps.FirstOrDefault(k => k.Contains("Mole_UB_FX", StringComparison.OrdinalIgnoreCase));

            if (moleGlobal is null || moleSleep is null)
            {
                statusLabel = "SKIP";
                Console.WriteLine("[Streaming] SKIP (Mole_UB_Lighting_Global or Mole_UB_SleepQuarters missing)");
                return true;
            }

            var scene = new WorldScene();
            if (!scene.WorldSettings.SkyEnabled || !scene.WorldSettings.TodmEnabled || !scene.WorldSettings.FogEnabled)
                throw new InvalidOperationException("WorldSettings defaults were not initialized to true.");

            scene.WorldSettings.SkyEnabled = false;
            scene.WorldSettings.TodmEnabled = false;
            if (scene.WorldSettings.SkyEnabled || scene.WorldSettings.TodmEnabled)
                throw new InvalidOperationException("WorldSettings toggle did not persist.");
            scene.WorldSettings.Reset();
            if (!scene.WorldSettings.SkyEnabled || !scene.WorldSettings.TodmEnabled)
                throw new InvalidOperationException("WorldSettings.Reset() failed.");

            var baseLvl = WorldLoader.LoadLevelAdditive(scene, prov, moleGlobal, isPersistent: true);
            var baseRoots = scene.RootObjects.Count;
            var baseMeshes = scene.Meshes.Count;
            var baseLights = scene.Lights.Count;
            var baseNiagaras = scene.Niagaras.Count;
            if (baseRoots <= 0 || baseMeshes <= 0)
                throw new InvalidOperationException($"Expected non-empty base level '{moleGlobal}', got roots={baseRoots}, meshes={baseMeshes}.");

            var sleepLvl = WorldLoader.LoadLevelAdditive(scene, prov, moleSleep, isPersistent: false);
            var sleepMeshes = sleepLvl.Meshes.Count;
            var sleepLights = sleepLvl.Lights.Count;
            if (scene.Levels.Count != 2 ||
                scene.Meshes.Count != baseMeshes + sleepMeshes ||
                scene.RootObjects.Count != baseRoots + sleepLvl.RootObjects.Count)
            {
                throw new InvalidOperationException("Additive load counts mismatch after adding Mole_UB_SleepQuarters.");
            }

            if (sleepLvl.RootObjects.Count > 0)
                scene.Select(sleepLvl.RootObjects[0]);

            WorldLoader.SetLevelVisible(scene, sleepLvl, false);
            if (sleepLvl.IsVisible || !sleepLvl.IsLoaded ||
                scene.Meshes.Count != baseMeshes ||
                scene.RootObjects.Count != baseRoots ||
                scene.SelectedObject is not null)
            {
                throw new InvalidOperationException("SetLevelVisible(false) failed to hide sub-level or clear dangling SelectedObject.");
            }

            WorldLoader.SetLevelVisible(scene, sleepLvl, true);
            if (!sleepLvl.IsVisible ||
                scene.Meshes.Count != baseMeshes + sleepLvl.Meshes.Count ||
                scene.RootObjects.Count != baseRoots + sleepLvl.RootObjects.Count)
            {
                throw new InvalidOperationException("SetLevelVisible(true) failed to restore sub-level counts.");
            }

            if (moleFx is not null)
            {
                var fxLvl = WorldLoader.LoadLevelAdditive(scene, prov, moleFx, isPersistent: false);
                if (fxLvl.Niagaras.Count <= 0 || scene.Niagaras.Count != baseNiagaras + sleepLvl.Niagaras.Count + fxLvl.Niagaras.Count)
                    throw new InvalidOperationException("Additive Mole_UB_FX Niagara count mismatch.");
                WorldLoader.UnloadLevel(scene, fxLvl);
                if (fxLvl.IsLoaded || scene.Levels.Contains(fxLvl) || scene.Niagaras.Count != baseNiagaras + sleepLvl.Niagaras.Count)
                    throw new InvalidOperationException("UnloadLevel(Mole_UB_FX) failed.");
            }

            if (sleepLvl.RootObjects.Count > 0)
                scene.Select(sleepLvl.RootObjects[0]);

            WorldLoader.UnloadLevel(scene, sleepLvl);
            if (sleepLvl.IsLoaded ||
                scene.Levels.Count != 1 ||
                scene.Levels.Contains(sleepLvl) ||
                scene.Meshes.Count != baseMeshes ||
                scene.RootObjects.Count != baseRoots ||
                scene.Lights.Count != baseLights ||
                scene.SelectedObject is not null)
            {
                throw new InvalidOperationException("UnloadLevel(Mole_UB_SleepQuarters) failed to restore baseline state.");
            }

            // Verify M1 note #2: WorldScene.Clear() resets WorldName to "Untitled World"
            var clearTestScene = new WorldScene();
            WorldLoader.LoadLevelAdditive(clearTestScene, prov, moleGlobal, isPersistent: true);
            clearTestScene.Clear();
            if (!string.Equals(clearTestScene.WorldName, "Untitled World", StringComparison.Ordinal))
                throw new InvalidOperationException($"WorldScene.Clear() left WorldName='{clearTestScene.WorldName}' instead of 'Untitled World'.");

            statusLabel = "PASS";
            Console.WriteLine(
                $"[Streaming] PASS: base=({baseLvl.Name}: roots={baseRoots}, meshes={baseMeshes}, lights={baseLights}), " +
                $"additive=({sleepLvl.Name}: +{sleepMeshes} meshes, +{sleepLights} lights), hide/show/unload/WorldSettings verified.");
            return true;
        }
        catch (Exception ex)
        {
            statusLabel = "FAIL";
            Console.WriteLine($"[Streaming] FAIL: {ex.Message}");
            return false;
        }
    }

    private static double RunWorldRenderAndPerfDiagnostics(IFileProvider prov, D3D11NativeShaderDevice? dev, out bool allOk)
    {
        allOk = true;
        var allMoleMaps = prov.Files.Keys
            .Where(k => k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) &&
                        k.Contains("Mole", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x)
            .ToList();

        var umapKeys = new List<string>();
        void AddFirstMatch(string token)
        {
            var match = allMoleMaps.FirstOrDefault(k => k.Contains(token, StringComparison.OrdinalIgnoreCase) && !umapKeys.Contains(k));
            if (match is not null)
                umapKeys.Add(match);
        }

        AddFirstMatch("Mole_UB_Lighting_Global");
        AddFirstMatch("Mole_UB_SleepQuarters");
        AddFirstMatch("Mole_UB_FX");
        AddFirstMatch("MoleTask_UB_GP_Sleeping");

        if (umapKeys.Count < 4)
        {
            foreach (var extra in allMoleMaps.Where(k =>
                !umapKeys.Contains(k) &&
                (k.Contains("FX", StringComparison.OrdinalIgnoreCase) ||
                 k.Contains("Props", StringComparison.OrdinalIgnoreCase) ||
                 k.Contains("Geo", StringComparison.OrdinalIgnoreCase) ||
                 k.Contains("Art", StringComparison.OrdinalIgnoreCase))).Take(4 - umapKeys.Count))
            {
                umapKeys.Add(extra);
            }
        }

        Console.WriteLine($"[Perf] Selected {umapKeys.Count} test .umap files: {string.Join(", ", umapKeys.Select(Path.GetFileName))}");
        if (dev is null)
        {
            Console.WriteLine("[Perf] SKIP (no D3D11 device)");
            return 0.0;
        }

        using var renderer = new NativeSceneRenderer(dev);
        var fpsSamples = new List<double>();
        var builtMeshes = new Dictionary<UStaticMesh, NativeMeshLods>(ReferenceEqualityComparer.Instance);
        var builtNiagaraMeshPipes = new Dictionary<MaterialAsset, NativeNiagaraMaterialPipeline?>(ReferenceEqualityComparer.Instance);
        var builtNiagaraSpritePipes = new Dictionary<MaterialAsset, NativeNiagaraMaterialPipeline?>(ReferenceEqualityComparer.Instance);

        foreach (var uk in umapKeys)
        {
            try
            {
                Console.WriteLine($"--- Loading UMAP: {uk} ---");
                if (!prov.TryLoadPackage(uk, out var pkg)) { Console.WriteLine("  TryLoadPackage failed"); continue; }
                var world = pkg.GetExports().OfType<UWorld>().FirstOrDefault();
                if (world is null) { Console.WriteLine("  No UWorld export"); continue; }

                var scene = new WorldScene();
                WorldLoader.LoadWorld(world, scene, prov, msg => Console.WriteLine("  " + msg));
                Console.WriteLine($"  RootObjects={scene.RootObjects.Count}, Meshes={scene.Meshes.Count}, Lights={scene.Lights.Count}, Niagaras={scene.Niagaras.Count}");

                foreach (var m in scene.Meshes) _ = m.WorldMatrix;
                foreach (var l in scene.Lights) _ = l.WorldMatrix;
                foreach (var n in scene.Niagaras) _ = n.WorldMatrix;

                if (scene.Meshes.Count == 0 && scene.Niagaras.Count == 0)
                    continue;

                using var retained = new RetainedSceneDrawCache();
                retained.Update(dev, scene, syncBuild: true);

                var (niagaraSprites, niagaraMeshes, sceneLights) = BuildPerfNiagaraAndLights(dev, scene, builtMeshes, builtNiagaraMeshPipes, builtNiagaraSpritePipes);

                var firstPos = scene.Meshes.Count > 0
                    ? scene.Meshes[0].WorldMatrix.Translation
                    : (scene.Niagaras.Count > 0 ? scene.Niagaras[0].WorldLocationCm * 0.01f : Vector3.Zero);
                var camMeters = firstPos + new Vector3(0, 3, 10);
                var view = Matrix4x4.CreateLookAt(camMeters, firstPos, Vector3.UnitY);
                var proj = new Matrix4x4(1f, 0, 0, 0, 0, 1.5f, 0, 0, 0, 0, 0, -1, 0, 0, 0.05f, 0);
                var unrealToMeters = new Matrix4x4(0.01f, 0, 0, 0, 0, 0, 0.01f, 0, 0, 0.01f, 0, 0, 0, 0, 0, 1);
                var camUn = new Vector3(camMeters.X, camMeters.Z, camMeters.Y) * 100f;
                renderer.Features = RenderSettings.ViewportFeatures();

                var sw = new Stopwatch();
                sw.Restart();
                renderer.Render(640, 360, unrealToMeters * view * proj, camUn, 0.75f, Array.Empty<NativeDrawItem>(),
                    ENativePreviewMode.Lit, 1.0f, new Vector3(0.4f, 0.3f, 0.85f), measure: false,
                    niagaraSprites: niagaraSprites, niagaraMeshes: niagaraMeshes,
                    sceneLights: sceneLights, heightFog: scene.HeightFog, postProcess: scene.PostProcess,
                    retainedCache: retained, worldSettings: scene.WorldSettings);
                sw.Stop();
                var warmupMs = sw.Elapsed.TotalMilliseconds;

                const int steadyFrames = 9;
                var frameTimes = new double[steadyFrames];
                for (var f = 0; f < steadyFrames; f++)
                {
                    sw.Restart();
                    renderer.Render(640, 360, unrealToMeters * view * proj, camUn, 0.75f, Array.Empty<NativeDrawItem>(),
                        ENativePreviewMode.Lit, 1.0f, new Vector3(0.4f, 0.3f, 0.85f), measure: false,
                        niagaraSprites: niagaraSprites, niagaraMeshes: niagaraMeshes,
                        sceneLights: sceneLights, heightFog: scene.HeightFog, postProcess: scene.PostProcess,
                        retainedCache: retained, worldSettings: scene.WorldSettings);
                    sw.Stop();
                    frameTimes[f] = sw.Elapsed.TotalMilliseconds;
                }

                var avgMs = frameTimes.Average();
                var minMs = frameTimes.Min();
                var maxMs = frameTimes.Max();
                var fps = avgMs > 0.001 ? 1000.0 / avgMs : 0.0;
                fpsSamples.Add(fps);
                var t = renderer.LastTimings;

                Console.WriteLine(
                    $"[Perf] {world.Name}: warmupMs={warmupMs:F2}, avgMs={avgMs:F2}, minMs={minMs:F2}, maxMs={maxMs:F2}, fps={fps:F1}, " +
                    $"meshes={scene.Meshes.Count}, prims={retained.TotalPrimitives}, niagaraSprites={niagaraSprites.Count}, niagaraMeshes={niagaraMeshes.Count}, " +
                    $"DrawCalls={renderer.DrawCalls}, Visible={renderer.VisibleCount}, " +
                    $"Cpu[Cull={t.CpuCullSortMs:F2},GBuf={t.CpuGBufferMs:F2},Shad={t.CpuShadowsMs:F2},SSAO={t.CpuSsaoMs:F2},Lit={t.CpuLightCullAndLightingMs:F2},Trans={t.CpuTranslucencyMs:F2},Post={t.CpuPostProcessMs:F2}], " +
                    $"GpuValid={t.GpuValid}, GpuTotal={t.GpuTotalMs:F2}");

                // Verify WorldSettingsState Sky/TODM toggle on Mole_UB_Lighting_Global
                if (world.Name.Contains("Mole_UB_Lighting_Global", StringComparison.OrdinalIgnoreCase))
                {
                    renderer.ReadbackEnabled = true;
                    scene.WorldSettings.SkyEnabled = true;
                    scene.WorldSettings.TodmEnabled = true;
                    scene.WorldSettings.FogEnabled = true;
                    byte[] litBytes = [];
                    for (var f = 0; f < 4; f++)
                    {
                        litBytes = renderer.Render(640, 360, unrealToMeters * view * proj, camUn, 0.75f, Array.Empty<NativeDrawItem>(),
                            ENativePreviewMode.Lit, 1.0f, new Vector3(0.4f, 0.3f, 0.85f), measure: false,
                            niagaraSprites: niagaraSprites, niagaraMeshes: niagaraMeshes,
                            sceneLights: sceneLights, heightFog: scene.HeightFog, postProcess: scene.PostProcess,
                            retainedCache: retained, worldSettings: scene.WorldSettings);
                    }

                    scene.WorldSettings.SkyEnabled = false;
                    scene.WorldSettings.TodmEnabled = false;
                    scene.WorldSettings.FogEnabled = false;
                    byte[] noSkyBytes = [];
                    for (var f = 0; f < 4; f++)
                    {
                        noSkyBytes = renderer.Render(640, 360, unrealToMeters * view * proj, camUn, 0.75f, Array.Empty<NativeDrawItem>(),
                            ENativePreviewMode.Lit, 1.0f, new Vector3(0.4f, 0.3f, 0.85f), measure: false,
                            niagaraSprites: niagaraSprites, niagaraMeshes: niagaraMeshes,
                            sceneLights: sceneLights, heightFog: scene.HeightFog, postProcess: scene.PostProcess,
                            retainedCache: retained, worldSettings: scene.WorldSettings);
                    }
                    renderer.ReadbackEnabled = false;
                    scene.WorldSettings.Reset();

                    var diffSky = CountDiffPixels(litBytes, noSkyBytes);
                    var skyToggleOk = litBytes.Length > 0 && noSkyBytes.Length > 0 && diffSky > 50;
                    if (!skyToggleOk) allOk = false;
                    Console.WriteLine($"[WorldSettings] Sky/TODM toggle on {world.Name}: {(skyToggleOk ? "PASS" : "FAIL")} | diffPixels={diffSky}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Perf EX] {uk}: {ex}");
            }
        }

        // Dense Combined UnderBase Benchmark (1280x720) using RetainedSceneDrawCache + 16 frames
        string[] denseTokens =
        [
            "Mole_UB_Lighting_Global",
            "Mole_UB_SleepQuarters",
            "Mole_UB_Factory",
            "Mole_UB_ResearchLab",
            "Mole_UB_TheBridge",
            "Mole_UB_Cafeteria",
            "Mole_UB_FX"
        ];
        var denseMaps = new List<string>();
        foreach (var tok in denseTokens)
        {
            var match = allMoleMaps.FirstOrDefault(k => k.Contains(tok, StringComparison.OrdinalIgnoreCase) && !denseMaps.Contains(k));
            if (match is not null) denseMaps.Add(match);
        }

        if (denseMaps.Count >= 2)
        {
            try
            {
                var combined = new WorldScene();
                for (var i = 0; i < denseMaps.Count; i++)
                    WorldLoader.LoadLevelAdditive(combined, prov, denseMaps[i], isPersistent: i == 0);

                using var denseRetained = new RetainedSceneDrawCache();
                denseRetained.Update(dev, combined, syncBuild: true);
                var (dSprites, dMeshes, dLights) = BuildPerfNiagaraAndLights(dev, combined, builtMeshes, builtNiagaraMeshPipes, builtNiagaraSpritePipes);

                var firstPos = combined.Meshes.Count > 0 ? combined.Meshes[0].WorldMatrix.Translation : Vector3.Zero;
                var camMeters = firstPos + new Vector3(0, 4, 14);
                var view = Matrix4x4.CreateLookAt(camMeters, firstPos, Vector3.UnitY);
                var aspect = 1280f / 720f;
                var f = aspect / MathF.Tan(90f * 0.5f * MathF.PI / 180f);
                var proj = new Matrix4x4(f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, 0, -1, 0, 0, 0.05f, 0);
                var unrealToMeters = new Matrix4x4(0.01f, 0, 0, 0, 0, 0, 0.01f, 0, 0, 0.01f, 0, 0, 0, 0, 0, 1);
                var camUn = new Vector3(camMeters.X, camMeters.Z, camMeters.Y) * 100f;
                renderer.Features = RenderSettings.ViewportFeatures();

                // Warmup
                renderer.Render(1280, 720, unrealToMeters * view * proj, camUn, 0.5f * f, Array.Empty<NativeDrawItem>(),
                    ENativePreviewMode.Lit, 1.0f, new Vector3(0.4f, 0.3f, 0.85f), measure: false,
                    niagaraSprites: dSprites, niagaraMeshes: dMeshes,
                    sceneLights: dLights, heightFog: combined.HeightFog, postProcess: combined.PostProcess,
                    retainedCache: denseRetained, worldSettings: combined.WorldSettings);

                const int denseFrames = 16;
                var denseTimes = new double[denseFrames];
                var sw = new Stopwatch();
                for (var fr = 0; fr < denseFrames; fr++)
                {
                    sw.Restart();
                    renderer.Render(1280, 720, unrealToMeters * view * proj, camUn, 0.5f * f, Array.Empty<NativeDrawItem>(),
                        ENativePreviewMode.Lit, 1.0f, new Vector3(0.4f, 0.3f, 0.85f), measure: false,
                        niagaraSprites: dSprites, niagaraMeshes: dMeshes,
                        sceneLights: dLights, heightFog: combined.HeightFog, postProcess: combined.PostProcess,
                        retainedCache: denseRetained, worldSettings: combined.WorldSettings);
                    sw.Stop();
                    denseTimes[fr] = sw.Elapsed.TotalMilliseconds;
                }

                // Flush GPU once at the end of the 16-frame loop only if query ring hasn't resolved yet
                dev.Context.Flush();
                if (!renderer.LastTimings.GpuValid)
                {
                    Thread.Sleep(20);
                    renderer.Render(1280, 720, unrealToMeters * view * proj, camUn, 0.5f * f, Array.Empty<NativeDrawItem>(),
                        ENativePreviewMode.Lit, 1.0f, new Vector3(0.4f, 0.3f, 0.85f), measure: false,
                        niagaraSprites: dSprites, niagaraMeshes: dMeshes,
                        sceneLights: dLights, heightFog: combined.HeightFog, postProcess: combined.PostProcess,
                        retainedCache: denseRetained, worldSettings: combined.WorldSettings);
                }

                var dAvgMs = denseTimes.Average();
                var dFps = dAvgMs > 0.001 ? 1000.0 / dAvgMs : 0.0;
                fpsSamples.Add(dFps);
                var dt = renderer.LastTimings;
                if (!dt.GpuValid)
                {
                    allOk = false;
                    Console.WriteLine($"[Perf WARN] DenseUnderBase GpuValid=False (LastGpuQueryStatus={renderer.LastGpuQueryStatus})");
                }

                Console.WriteLine(
                    $"[Perf] DenseUnderBase(1280x720, {denseMaps.Count} levels): avgMs={dAvgMs:F2}, fps={dFps:F1}, " +
                    $"meshes={combined.Meshes.Count}, prims={denseRetained.TotalPrimitives}, lights={combined.Lights.Count}, " +
                    $"DrawCalls={renderer.DrawCalls}, Visible={renderer.VisibleCount}, " +
                    $"CPU(Cull={dt.CpuCullSortMs:F2},GBuf={dt.CpuGBufferMs:F2},Shad={dt.CpuShadowsMs:F2},SSAO={dt.CpuSsaoMs:F2},Lit={dt.CpuLightCullAndLightingMs:F2},Trans={dt.CpuTranslucencyMs:F2},Post={dt.CpuPostProcessMs:F2}), " +
                    $"GPU(GpuValid={dt.GpuValid},Total={dt.GpuTotalMs:F2},GBuf={dt.GpuGBufferMs:F2},Shad={dt.GpuShadowsMs:F2},SSAO={dt.GpuSsaoMs:F2},Lit={dt.GpuLightCullAndLightingMs:F2},Trans={dt.GpuTranslucencyMs:F2},Post={dt.GpuPostProcessMs:F2})");
            }
            catch (Exception ex)
            {
                allOk = false;
                Console.WriteLine($"[Perf EX] DenseUnderBase: {ex}");
            }
        }

        return fpsSamples.Count > 0 ? fpsSamples.Average() : 0.0;
    }

    private static (List<NativeNiagaraSpriteBatch> Sprites, List<NativeNiagaraMeshBatch> Meshes, List<SceneLight> Lights) BuildPerfNiagaraAndLights(
        D3D11NativeShaderDevice dev,
        WorldScene scene,
        Dictionary<UStaticMesh, NativeMeshLods> builtMeshes,
        Dictionary<MaterialAsset, NativeNiagaraMaterialPipeline?> builtNiagaraMeshPipes,
        Dictionary<MaterialAsset, NativeNiagaraMaterialPipeline?> builtNiagaraSpritePipes)
    {
        var niagaraSprites = new List<NativeNiagaraSpriteBatch>();
        var niagaraMeshes = new List<NativeNiagaraMeshBatch>();
        var sceneLights = new List<SceneLight>(scene.Lights.Count + 8);
        sceneLights.AddRange(scene.Lights);

        foreach (var sn in scene.Niagaras)
        {
            var snWorld = UnrealSpace.ToUnrealLocalToWorld(sn.WorldMatrix);
            foreach (var me in sn.Asset.MeshEmitters)
            {
                if (!me.Enabled) continue;
                if (!builtMeshes.TryGetValue(me.Mesh, out var nm))
                {
                    try
                    {
                        nm = NativeMeshLods.FromStaticMesh(dev.Device, me.Mesh);
                        builtMeshes[me.Mesh] = nm;
                    }
                    catch { continue; }
                }
                ID3D11ShaderResourceView? meshTexSrv = null;
                if (me.Material is { } mMat &&
                    (mMat.TryGetTexture(out var dTex, EMaterialTextureKind.Diffuse) || mMat.TryGetTexture(out dTex, EMaterialTextureKind.Emissive)))
                {
                    meshTexSrv = NativeTextureCache.Get(dev.Device, dTex);
                }
                NativeNiagaraMaterialPipeline? mPipe = null;
                if (me.Material is { } matMesh && !builtNiagaraMeshPipes.TryGetValue(matMesh, out mPipe))
                {
                    mPipe = NativeAssetCache.BuildNiagaraPipelineSync(dev, matMesh, isMesh: true, out _);
                    builtNiagaraMeshPipes[matMesh] = mPipe;
                }
                niagaraMeshes.Add(new NativeNiagaraMeshBatch(me, nm, snWorld, meshTexSrv, sn.GetSim(me), mPipe));
            }
            foreach (var se in sn.Asset.SpriteEmitters)
            {
                if (!se.Enabled) continue;
                var srv = se.SpriteTexture is not null ? NativeTextureCache.Get(dev.Device, se.SpriteTexture) : null;
                NativeNiagaraMaterialPipeline? sPipe = null;
                if (se.Material is { } matSprite && !builtNiagaraSpritePipes.TryGetValue(matSprite, out sPipe))
                {
                    sPipe = NativeAssetCache.BuildNiagaraPipelineSync(dev, matSprite, isMesh: false, out _);
                    builtNiagaraSpritePipes[matSprite] = sPipe;
                }
                niagaraSprites.Add(new NativeNiagaraSpriteBatch(se, snWorld, srv, sn.GetSim(se), sPipe));
            }
            foreach (var le in sn.Asset.LightEmitters)
            {
                if (!le.Enabled) continue;
                sceneLights.Add(new ScenePointLight
                {
                    Name = le.EmitterName,
                    LocationCm = sn.WorldLocationCm,
                    Parent = null,
                    Color = le.Color,
                    Intensity = le.Intensity,
                    AttenuationRadius = le.DefaultRadius
                });
            }
        }

        return (niagaraSprites, niagaraMeshes, sceneLights);
    }

    private static (double MeanAbsDiff, int MaxDiff, int DiffOver1) ComputeRgbDiffStats(byte[] a, byte[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        if (len < 4) return (255.0, 255, 0);
        long sumAbs = 0;
        var maxDiff = 0;
        var diffOver1 = 0;
        var pxCount = len / 4;
        for (var i = 0; i + 3 < len; i += 4)
        {
            var dr = Math.Abs(a[i] - b[i]);
            var dg = Math.Abs(a[i + 1] - b[i + 1]);
            var db = Math.Abs(a[i + 2] - b[i + 2]);
            sumAbs += dr + dg + db;
            var m = Math.Max(dr, Math.Max(dg, db));
            if (m > maxDiff) maxDiff = m;
            if (m > 1) diffOver1++;
        }
        return (sumAbs / (double) (pxCount * 3), maxDiff, diffOver1);
    }

    private static bool RunTiledLightDiagnostics(IFileProvider prov, D3D11NativeShaderDevice? dev, out string statusLabel)
    {
        if (dev is null)
        {
            statusLabel = "SKIP";
            Console.WriteLine("[TiledLight] SKIP (no D3D11 device)");
            return true;
        }

        var moleGlobal = prov.Files.Keys.FirstOrDefault(k =>
            k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) &&
            k.Contains("Mole_UB_Lighting_Global", StringComparison.OrdinalIgnoreCase));

        if (moleGlobal is null || !prov.TryLoadPackage(moleGlobal, out var pkg))
        {
            statusLabel = "SKIP";
            Console.WriteLine("[TiledLight] SKIP (Mole_UB_Lighting_Global not found)");
            return true;
        }

        var world = pkg.GetExports().OfType<UWorld>().FirstOrDefault();
        if (world is null)
        {
            statusLabel = "SKIP";
            Console.WriteLine("[TiledLight] SKIP (no UWorld export)");
            return true;
        }

        try
        {
            var scene = new WorldScene();
            WorldLoader.LoadWorld(world, scene, prov);
            foreach (var m in scene.Meshes) _ = m.WorldMatrix;
            foreach (var l in scene.Lights) _ = l.WorldMatrix;

            using var retained = new RetainedSceneDrawCache();
            retained.Update(dev, scene, syncBuild: true);

            const int w = 1280, h = 720;
            var aspect = (float) w / h;
            var f = aspect / MathF.Tan(90f * 0.5f * MathF.PI / 180f);
            var proj = new Matrix4x4(f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, 0, -1, 0, 0, 0.05f, 0);
            var unrealToMeters = new Matrix4x4(0.01f, 0, 0, 0, 0, 0, 0.01f, 0, 0, 0.01f, 0, 0, 0, 0, 0, 1);

            var firstPos = scene.Meshes.Count > 0 ? scene.Meshes[0].WorldMatrix.Translation : Vector3.Zero;
            var centroid = Vector3.Zero;
            if (scene.Meshes.Count > 0)
            {
                foreach (var m in scene.Meshes) centroid += m.WorldMatrix.Translation;
                centroid /= scene.Meshes.Count;
            }

            (Matrix4x4 WorldToClip, Vector3 CamUn, Vector3 CamFwdUn) MakeCam(Vector3 targetMeters)
            {
                var camMeters = targetMeters + new Vector3(0f, 3f, 10f);
                var view = Matrix4x4.CreateLookAt(camMeters, targetMeters, Vector3.UnitY);
                var w2c = unrealToMeters * view * proj;
                var camUn = new Vector3(camMeters.X, camMeters.Z, camMeters.Y) * 100f;
                var tgtUn = new Vector3(targetMeters.X, targetMeters.Z, targetMeters.Y) * 100f;
                return (w2c, camUn, Vector3.Normalize(tgtUn - camUn));
            }

            var camFirst = MakeCam(firstPos);
            var camCentroid = MakeCam(centroid);

            var baseFeatures = RenderSettings.ViewportFeatures() with
            {
                ScreenSpaceAO = false,
                Bloom = false,
                AutoExposure = false,
                TemporalAA = false
            };

            byte[] RenderStaticCapture(NativeRenderFeatures features, (Matrix4x4 WorldToClip, Vector3 CamUn, Vector3 CamFwdUn) cam, IReadOnlyList<SceneLight> lights, int frames, out int activeLights)
            {
                using var r = new NativeSceneRenderer(dev) { ReadbackEnabled = true, Features = features };
                byte[] rgba = [];
                for (var i = 0; i < frames; i++)
                {
                    rgba = r.Render(w, h, cam.WorldToClip, cam.CamUn, 0.5f * f, Array.Empty<NativeDrawItem>(),
                        ENativePreviewMode.Lit, 1.0f, new Vector3(0.4f, 0.3f, 0.85f), measure: false,
                        sceneLights: lights, heightFog: scene.HeightFog, postProcess: scene.PostProcess,
                        timeOverride: 0.5f, retainedCache: retained, worldSettings: scene.WorldSettings);
                }
                activeLights = r.ActiveLocalLights;
                return rgba;
            }

            // 1. TiledLightCulling=true vs false on FirstMeshCam & CentroidCam
            var fTiled = baseFeatures with { TiledLightCulling = true, ShadowCascadeCaching = false };
            var fBrute = baseFeatures with { TiledLightCulling = false, ShadowCascadeCaching = false };

            var firstTiled = RenderStaticCapture(fTiled, camFirst, scene.Lights, 4, out var firstActive);
            var firstBrute = RenderStaticCapture(fBrute, camFirst, scene.Lights, 4, out _);
            var firstDiff = ComputeRgbDiffStats(firstTiled, firstBrute);

            var centTiled = RenderStaticCapture(fTiled, camCentroid, scene.Lights, 4, out var centActive);
            var centBrute = RenderStaticCapture(fBrute, camCentroid, scene.Lights, 4, out _);
            var centDiff = ComputeRgbDiffStats(centTiled, centBrute);

            // 2. ShadowCascadeCaching=true vs false (with TiledLightCulling=true) on static cached frames 2 & 3
            // ReadbackEnabled has a 2-frame staging ring: call index 4 returns frame 2, call index 5 returns frame 3.
            byte[] CaptureFrameIndex(NativeRenderFeatures features, int targetFrameIdx)
            {
                using var r = new NativeSceneRenderer(dev) { ReadbackEnabled = true, Features = features };
                byte[] outBytes = [];
                for (var i = 0; i <= targetFrameIdx + 2; i++)
                {
                    outBytes = r.Render(w, h, camCentroid.WorldToClip, camCentroid.CamUn, 0.5f * f, Array.Empty<NativeDrawItem>(),
                        ENativePreviewMode.Lit, 1.0f, new Vector3(0.4f, 0.3f, 0.85f), measure: false,
                        sceneLights: scene.Lights, heightFog: scene.HeightFog, postProcess: scene.PostProcess,
                        timeOverride: 0.5f, retainedCache: retained, worldSettings: scene.WorldSettings);
                }
                return outBytes;
            }

            var fTiledCached = baseFeatures with { TiledLightCulling = true, ShadowCascadeCaching = true };
            var fTiledUncached = baseFeatures with { TiledLightCulling = true, ShadowCascadeCaching = false };
            var uncachedF2 = CaptureFrameIndex(fTiledUncached, 2);
            var cachedF2 = CaptureFrameIndex(fTiledCached, 2);
            var diffF2 = ComputeRgbDiffStats(cachedF2, uncachedF2);

            var uncachedF3 = CaptureFrameIndex(fTiledUncached, 3);
            var cachedF3 = CaptureFrameIndex(fTiledCached, 3);
            var diffF3 = ComputeRgbDiffStats(cachedF3, uncachedF3);

            // 3. Overflow safeguard (>127 overlapping lights in one tile): 180 overlapping point lights
            var overflowLights = new List<SceneLight>(180);
            var clusterPos = camFirst.CamUn + camFirst.CamFwdUn * 500f;
            for (var li = 0; li < 180; li++)
            {
                overflowLights.Add(new ScenePointLight
                {
                    Name = $"OverflowPt_{li}",
                    LocationCm = clusterPos + new Vector3((li % 9 - 4) * 5f, (li / 9 % 5 - 2) * 5f, 0f),
                    Color = new Vector3(0.3f + (li % 3) * 0.3f, 0.5f, 0.9f - (li % 5) * 0.1f),
                    Intensity = 0.5f,
                    AttenuationRadius = 2500f
                });
            }
            var overTiled = RenderStaticCapture(fTiled, camFirst, overflowLights, 4, out var overActive);
            var overBrute = RenderStaticCapture(fBrute, camFirst, overflowLights, 4, out _);
            var overDiff = ComputeRgbDiffStats(overTiled, overBrute);

            // 4. Cross-lifecycle 4-renderer determinism & fallback VRAM zero-init check
            static uint ReadTexel2DArray(D3D11NativeShaderDevice device, ID3D11ShaderResourceView srv)
            {
                using var res = srv.Resource;
                using var staging = device.Device.CreateTexture2D(new Texture2DDescription(
                    Format.R8G8B8A8_UNorm, 1, 1, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                device.Context.CopyResource(staging, res);
                var mapped = device.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try { return (uint) Marshal.ReadInt32(mapped.DataPointer); }
                finally { device.Context.Unmap(staging, 0); }
            }

            static uint ReadTexel3D(D3D11NativeShaderDevice device, ID3D11ShaderResourceView srv)
            {
                using var res = srv.Resource;
                using var staging = device.Device.CreateTexture3D(new Texture3DDescription(
                    Format.R8G8B8A8_UNorm, 1, 1, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                device.Context.CopyResource(staging, res);
                var mapped = device.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try { return (uint) Marshal.ReadInt32(mapped.DataPointer); }
                finally { device.Context.Unmap(staging, 0); }
            }

            var ambientVals = new uint[4];
            var blackArrVals = new uint[4];
            var runCaptures = new byte[4][];
            var vramZeroOk = true;
            for (var run = 0; run < 4; run++)
            {
                using var r = new NativeSceneRenderer(dev) { ReadbackEnabled = true, Features = fTiled };
                ambientVals[run] = ReadTexel3D(dev, r.SharedResources.AmbientVolume3D);
                blackArrVals[run] = ReadTexel2DArray(dev, r.SharedResources.BlackDefaults[EDxbcResourceKind.Texture2DArray]);
                if (ambientVals[run] != 0u || blackArrVals[run] != 0u)
                    vramZeroOk = false;

                byte[] rgba = [];
                for (var fi = 0; fi < 4; fi++)
                {
                    rgba = r.Render(w, h, camCentroid.WorldToClip, camCentroid.CamUn, 0.5f * f, Array.Empty<NativeDrawItem>(),
                        ENativePreviewMode.Lit, 1.0f, new Vector3(0.4f, 0.3f, 0.85f), measure: false,
                        sceneLights: scene.Lights, heightFog: scene.HeightFog, postProcess: scene.PostProcess,
                        timeOverride: 0.5f, retainedCache: retained, worldSettings: scene.WorldSettings);
                }
                runCaptures[run] = rgba;
            }

            var maxCrossRunDiff = 0;
            var maxTiledVsBruteDiff = 0;
            for (var run = 0; run < 4; run++)
            {
                if (run > 0)
                    maxCrossRunDiff = Math.Max(maxCrossRunDiff, ComputeRgbDiffStats(runCaptures[0], runCaptures[run]).MaxDiff);
                maxTiledVsBruteDiff = Math.Max(maxTiledVsBruteDiff, ComputeRgbDiffStats(runCaptures[run], centBrute).MaxDiff);
            }
            var crossDetOk = vramZeroOk && maxCrossRunDiff == 0 && maxTiledVsBruteDiff == 0;
            Console.WriteLine(
                $"[CrossRendererDeterminism] Mole_UB_Lighting_Global(CentroidCam, 4-renderer): {(crossDetOk ? "PASS" : "FAIL")} | " +
                $"AmbientVol=[{string.Join(",", ambientVals.Select(v => $"0x{v:X8}"))}] | " +
                $"BlackArr2D=[{string.Join(",", blackArrVals.Select(v => $"0x{v:X8}"))}] | " +
                $"MaxDiffAcrossRuns={maxCrossRunDiff} | TiledVsBruteMaxDiff={maxTiledVsBruteDiff}");

            var firstOk = firstDiff.MaxDiff <= 1 && firstDiff.MeanAbsDiff < 0.01;
            var centOk = centDiff.MaxDiff <= 1 && centDiff.MeanAbsDiff < 0.01;
            var shadOk = diffF2.MaxDiff == 0 && diffF3.MaxDiff == 0;
            var overOk = overActive == 180 && overDiff.MaxDiff <= 1 && overDiff.MeanAbsDiff < 0.01;

            var pass = firstOk && centOk && shadOk && overOk && crossDetOk;
            statusLabel = pass ? "PASS" : "FAIL";
            Console.WriteLine(
                $"[TiledLight] Mole_UB_Lighting_Global(1280x720): {statusLabel} | " +
                $"FirstMesh(active={firstActive},MaxDiff={firstDiff.MaxDiff},MeanAbsDiff={firstDiff.MeanAbsDiff:F6},Diff>1={firstDiff.DiffOver1}) | " +
                $"Centroid(active={centActive},MaxDiff={centDiff.MaxDiff},MeanAbsDiff={centDiff.MeanAbsDiff:F6},Diff>1={centDiff.DiffOver1}) | " +
                $"ShadowCacheF2(MaxDiff={diffF2.MaxDiff}), ShadowCacheF3(MaxDiff={diffF3.MaxDiff}) | " +
                $"Overflow180(active={overActive},MaxDiff={overDiff.MaxDiff},MeanAbsDiff={overDiff.MeanAbsDiff:F6})");
            return pass;
        }
        catch (Exception ex)
        {
            statusLabel = "FAIL";
            Console.WriteLine($"[TiledLight] FAIL: {ex}");
            return false;
        }
    }
}
