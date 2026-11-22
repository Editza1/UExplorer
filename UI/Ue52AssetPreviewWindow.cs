using System.Numerics;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using ImGuiNET;
using UExplorer.Rendering;
using UExplorer.Scene;
using Vortice.Direct3D11;

namespace UExplorer.UI;

public sealed class Ue52AssetPreviewWindow : IDisposable
{
    public enum MaterialShape { Sphere, Cube, Cylinder, Plane }

    public sealed class InspectMeshItem
    {
        public required string Name { get; init; }
        public required string ClassName { get; init; }
        public required UObject Source { get; init; }
        public required UStaticMesh Mesh { get; init; }
        public required MaterialAsset?[] Slots { get; init; }
        public Matrix4x4 LocalTransform { get; init; } = Matrix4x4.Identity;
        public bool Visible = true;
    }

    public sealed class PreviewTab : IDisposable
    {
        public required string Key { get; init; }
        public required UObject Asset { get; init; }
        public required string Title { get; init; }
        public required Ue52IconKind IconKind { get; init; }
        public bool IsOpen = true;

        public MaterialAsset? MaterialAsset;
        public MaterialShape Shape = MaterialShape.Sphere;
        public UTexture? TextureAsset;

        public readonly List<InspectMeshItem> Meshes = [];
        public NiagaraAsset? Niagara;
        public NiagaraSystemVmSimulation? NiagaraSystemSim;
        public readonly Dictionary<NiagaraMeshEmitterDesc, NiagaraEmitterVmSimulation?> NiagaraMeshSims = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<NiagaraSpriteEmitterDesc, NiagaraEmitterVmSimulation?> NiagaraSpriteSims = new(ReferenceEqualityComparer.Instance);
        public int SelectedOutlinerIndex = -1;
        public CUE4Parse.UE4.Objects.Engine.UMaterialExpression? SelectedExpression;
        public string? ErrorMsg;
        public Editors.Ue52NiagaraEditor? NiagaraEditor;
        public Editors.MaterialGraphEditor? GraphEditor;
        public Editors.MaterialInstanceEditor? InstanceEditor;
        public Editors.Ue52BlueprintEditor? BlueprintEditor;
        public Editors.StaticMeshEditor? MeshEditor;

        public NiagaraEmitterVmSimulation? GetSim(NiagaraMeshEmitterDesc desc)
        {
            if (!NiagaraMeshSims.TryGetValue(desc, out var sim))
            {
                if (NiagaraSystemSim is null && Niagara?.SystemSimulation is { } sysTemplate)
                    NiagaraSystemSim = sysTemplate.Clone();
                sim = NiagaraSystemSim is not null ? NiagaraSystemSim.GetClonedSim(desc.VmSimulation) : desc.VmSimulation?.Clone();
                NiagaraMeshSims[desc] = sim;
            }
            return sim;
        }

        public NiagaraEmitterVmSimulation? GetSim(NiagaraSpriteEmitterDesc desc)
        {
            if (!NiagaraSpriteSims.TryGetValue(desc, out var sim))
            {
                if (NiagaraSystemSim is null && Niagara?.SystemSimulation is { } sysTemplate)
                    NiagaraSystemSim = sysTemplate.Clone();
                sim = NiagaraSystemSim is not null ? NiagaraSystemSim.GetClonedSim(desc.VmSimulation) : desc.VmSimulation?.Clone();
                NiagaraSpriteSims[desc] = sim;
            }
            return sim;
        }

        public float Yaw = 0.65f;
        public float Pitch = 0.35f;
        public float Distance = 2.6f;
        public bool NeedsMaterialFraming = true;
        public int RenderMode;

        public Vector2 GraphPan = new(24f, 20f);
        public NativeSceneRenderer? Renderer;

        public void Dispose()
        {
            Renderer?.Dispose();
            Renderer = null;
            GraphEditor?.Dispose();
            InstanceEditor?.Dispose();
            BlueprintEditor?.Dispose();
            MeshEditor?.Dispose();
        }
    }

    private readonly List<PreviewTab> _tabs = [];
    private readonly Dictionary<MaterialShape, UStaticMesh?> _basicShapes = [];
    private readonly Dictionary<MaterialShape, NativeMeshLods> _proceduralShapes = [];
    private static readonly Matrix4x4 UnrealToMeters = new(0.01f, 0, 0, 0, 0, 0, 0.01f, 0, 0, 0.01f, 0, 0, 0, 0, 0, 1);

    public void Open(UObject asset, IFileProvider provider)
    {
        var key = asset.GetPathName();
        var existing = _tabs.FirstOrDefault(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.IsOpen = true;
            ImGui.SetWindowFocus(existing.Title);
            return;
        }

        var tab = new PreviewTab
        {
            Key = key,
            Asset = asset,
            IconKind = Classify(asset),
            Title = $"{asset.Name} ({asset.ExportType})##UExPreview_{_tabs.Count}_{asset.Name}"
        };

        try
        {
            if (asset is UTexture tex)
            {
                tab.TextureAsset = tex;
            }
            else if (asset is UUnrealMaterial mat)
            {
                tab.MaterialAsset = new MaterialAsset(mat);
                tab.GraphEditor = new Editors.MaterialGraphEditor(tab.MaterialAsset);
                if (mat is UMaterialInstanceConstant)
                    tab.InstanceEditor = new Editors.MaterialInstanceEditor(tab.MaterialAsset);
                tab.NeedsMaterialFraming = true;
                tab.Distance = 1.68f;
            }
            else if (asset is UStaticMesh sm)
            {
                tab.Meshes.Add(new InspectMeshItem
                {
                    Name = sm.Name,
                    ClassName = "StaticMesh",
                    Source = sm,
                    Mesh = sm,
                    Slots = ResolveMeshMaterials(sm.Materials)
                });
                tab.MeshEditor = new Editors.StaticMeshEditor(sm);
                var r = sm.RenderData?.Bounds?.SphereRadius ?? 100f;
                tab.Distance = Math.Clamp((float) r * 0.024f, 0.8f, 35f);
            }
            else if (asset.ExportType is "NiagaraSystem" or "NiagaraEmitter")
            {
                tab.Niagara = new NiagaraAsset(asset, asset.Name);
                tab.Distance = 3.8f;
                tab.NiagaraEditor = new Editors.Ue52NiagaraEditor(tab);
            }
            else if (asset is UBlueprintGeneratedClass bp)
            {
                PopulateBlueprint(bp, tab.Meshes);
                tab.BlueprintEditor = new Editors.Ue52BlueprintEditor(bp);
                tab.Distance = 3.2f;
            }
            else
            {
                foreach (var p in asset.Properties)
                {
                    if (p.Tag?.GenericValue is FPackageIndex idx && !idx.IsNull && idx.TryLoad<UStaticMesh>(out var childSm))
                    {
                        tab.Meshes.Add(new InspectMeshItem
                        {
                            Name = childSm.Name,
                            ClassName = "StaticMesh",
                            Source = childSm,
                            Mesh = childSm,
                            Slots = ResolveMeshMaterials(childSm.Materials)
                        });
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            tab.ErrorMsg = ex.Message;
        }

        _tabs.Add(tab);
    }

    public void DrawAll(IFileProvider provider)
    {
        for (var i = _tabs.Count - 1; i >= 0; i--)
        {
            var tab = _tabs[i];
            if (!tab.IsOpen)
            {
                tab.Dispose();
                _tabs.RemoveAt(i);
                continue;
            }

            ImGui.SetNextWindowSize(new Vector2(1240, 760), ImGuiCond.FirstUseEver);
            if (ImGui.Begin(tab.Title, ref tab.IsOpen, ImGuiWindowFlags.MenuBar))
            {
                try
                {
                    DrawMenuBar(tab);
                    DrawBody(tab, provider);
                }
                catch (Exception ex)
                {
                    tab.ErrorMsg = ex.Message;
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), $"Preview error: {ex.Message}");
                }
            }
            ImGui.End();
        }
    }

    private void DrawMenuBar(PreviewTab tab)
    {
        if (!ImGui.BeginMenuBar()) return;

        var icon = Ue52StyleAndIcons.Get(tab.IconKind);
        if (icon != IntPtr.Zero)
        {
            ImGui.Image(icon, new Vector2(16, 16));
            ImGui.SameLine();
        }
        ImGui.TextUnformatted(tab.Asset.Name);
        ImGui.SameLine();
        ImGui.TextDisabled($"({tab.Asset.ExportType})");

        if (tab.MaterialAsset is not null)
        {
            ImGui.SameLine(0, 18);
            if (Ue52StyleAndIcons.IconButton("##ShSphere", Ue52IconKind.SphereShape, tab.Shape == MaterialShape.Sphere, "Sphere", 16f)) { tab.Shape = MaterialShape.Sphere; tab.NeedsMaterialFraming = true; }
            ImGui.SameLine();
            if (Ue52StyleAndIcons.IconButton("##ShCube", Ue52IconKind.CubeShape, tab.Shape == MaterialShape.Cube, "Cube", 16f)) { tab.Shape = MaterialShape.Cube; tab.NeedsMaterialFraming = true; }
            ImGui.SameLine();
            if (Ue52StyleAndIcons.IconButton("##ShCyl", Ue52IconKind.CylinderShape, tab.Shape == MaterialShape.Cylinder, "Cylinder", 16f)) { tab.Shape = MaterialShape.Cylinder; tab.NeedsMaterialFraming = true; }
            ImGui.SameLine();
            if (Ue52StyleAndIcons.IconButton("##ShPlane", Ue52IconKind.PlaneShape, tab.Shape == MaterialShape.Plane, "Plane", 16f)) { tab.Shape = MaterialShape.Plane; tab.NeedsMaterialFraming = true; }

            if (tab.GraphEditor is { } ge)
            {
                ImGui.SameLine(0, 14);
                if (ImGui.SmallButton("Copy UE Nodes (T3D)"))
                    ge.CopyAllToClipboard();
                ImGui.SameLine();
                if (ImGui.SmallButton("Paste UE Nodes"))
                    ge.PasteFromClipboard();
            }
        }

        if (tab.TextureAsset is null)
        {
            ImGui.SameLine(0, 18);
            ImGui.SetNextItemWidth(105);
            ImGui.Combo("##PrvMode", ref tab.RenderMode, "Lit\0Emissive\0BaseColor\0Normal\0");

            ImGui.SameLine(0, 12);
            if (ImGui.SmallButton("Export to Unreal"))
            {
                var targetAsset = tab.Asset;
                _ = Task.Run(() => UExplorer.Export.UnrealBridgeExporter.ExportObject(targetAsset, targetAsset.Owner?.Provider));
            }
        }

        ImGui.EndMenuBar();
    }

    private void DrawBody(PreviewTab tab, IFileProvider provider)
    {
        var avail = ImGui.GetContentRegionAvail();
        if (!string.IsNullOrEmpty(tab.ErrorMsg))
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.30f, 1f), tab.ErrorMsg);

        if (tab.TextureAsset is { } tex)
        {
            var detailsW = Math.Clamp(avail.X * 0.30f, 280f, 440f);
            var leftW = Math.Max(260f, avail.X - detailsW - 6f);
            ImGui.BeginChild("##TexLeft", new Vector2(leftW, avail.Y), ImGuiChildFlags.Borders);
            DrawTextureView(tex, leftW, avail.Y);
            ImGui.EndChild();
            ImGui.SameLine();
            ImGui.BeginChild("##TexDetails", new Vector2(detailsW, avail.Y), ImGuiChildFlags.Borders);
            DrawDetailsPanel(tab, provider);
            ImGui.EndChild();
            return;
        }

        if (tab.MaterialAsset is not null)
        {
            var detailsW = Math.Clamp(avail.X * 0.28f, 280f, 420f);
            var leftW = Math.Max(260f, avail.X - detailsW - 6f);
            ImGui.BeginChild("##MatLeft", new Vector2(leftW, avail.Y), ImGuiChildFlags.None);
            var vpH = Math.Max(200f, avail.Y * 0.48f);
            ImGui.BeginChild("##MatVp", new Vector2(leftW, vpH), ImGuiChildFlags.Borders);
            Draw3DViewport(tab, provider);
            ImGui.EndChild();

            ImGui.BeginChild("##MatEditor", new Vector2(leftW, Math.Max(160f, avail.Y - vpH - 4f)), ImGuiChildFlags.Borders);
            if (tab.InstanceEditor is { } inst && tab.GraphEditor is not null)
            {
                if (ImGui.BeginTabBar("##MatSubEditorTabs"))
                {
                    if (ImGui.BeginTabItem("Material Graph (UE Nodes)"))
                    {
                        DrawUe5MaterialGraph(tab);
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem("Parameter Overrides"))
                    {
                        inst.Draw();
                        ImGui.EndTabItem();
                    }
                    ImGui.EndTabBar();
                }
            }
            else if (tab.InstanceEditor is { } instOnly)
            {
                instOnly.Draw();
            }
            else
            {
                DrawUe5MaterialGraph(tab);
            }
            ImGui.EndChild();
            ImGui.EndChild();

            ImGui.SameLine();
            ImGui.BeginChild("##MatDetails", new Vector2(detailsW, avail.Y), ImGuiChildFlags.Borders);
            DrawDetailsPanel(tab, provider);
            ImGui.EndChild();
            return;
        }

        // Mesh / Blueprint / Niagara Asset Editor: Outliner (left) | Viewport (center) | Details (right)
        var outlinerW = Math.Clamp(avail.X * 0.22f, 210f, 300f);
        var detW = Math.Clamp(avail.X * 0.27f, 260f, 400f);
        var centerW = Math.Max(260f, avail.X - outlinerW - detW - 12f);

        if (tab.BlueprintEditor is not null)
        {
            tab.BlueprintEditor.Draw();
        }
        else
        {
            ImGui.BeginChild("##InspectOutliner", new Vector2(outlinerW, avail.Y), ImGuiChildFlags.Borders);
            DrawInspectOutliner(tab);
            ImGui.EndChild();

            ImGui.SameLine();

            ImGui.BeginChild("##InspectVp", new Vector2(centerW, avail.Y), ImGuiChildFlags.Borders);
            Draw3DViewport(tab, provider);
            ImGui.EndChild();

            ImGui.SameLine();

            ImGui.BeginChild("##InspectDetails", new Vector2(detW, avail.Y), ImGuiChildFlags.Borders);
            DrawDetailsPanel(tab, provider);
            ImGui.EndChild();
        }
    }

    private void DrawInspectOutliner(PreviewTab tab)
    {
        ImGui.TextColored(new Vector4(0.86f, 0.88f, 0.91f, 1f), "Components / Outliner");
        ImGui.Separator();

        const ImGuiTableFlags tFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                                       ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##InspectOutTable", 3, tFlags, ImGui.GetContentRegionAvail()))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Vis", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 24f);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 0.65f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthStretch, 0.35f);
        ImGui.TableHeadersRow();

        // Root asset row
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextDisabled("o");
        ImGui.TableSetColumnIndex(1);
        if (ImGui.Selectable($"{tab.Asset.Name}##InspRoot", tab.SelectedOutlinerIndex == -1, ImGuiSelectableFlags.SpanAllColumns))
            tab.SelectedOutlinerIndex = -1;
        ImGui.TableSetColumnIndex(2);
        ImGui.TextDisabled(tab.Asset.ExportType);

        for (var i = 0; i < tab.Meshes.Count; i++)
        {
            var m = tab.Meshes[i];
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.PushID(i);
            if (ImGui.SmallButton(m.Visible ? "o" : "-")) m.Visible = !m.Visible;
            ImGui.PopID();

            ImGui.TableSetColumnIndex(1);
            if (ImGui.Selectable($"  {m.Name}##InspMesh_{i}", tab.SelectedOutlinerIndex == i, ImGuiSelectableFlags.SpanAllColumns))
                tab.SelectedOutlinerIndex = i;

            ImGui.TableSetColumnIndex(2);
            ImGui.TextDisabled(m.ClassName);

            // Show LODs/Sections under the selected or single mesh
            if (tab.Meshes.Count == 1 || tab.SelectedOutlinerIndex == i)
            {
                var lods = m.Mesh.RenderData?.LODs;
                if (lods is { Length: > 0 })
                {
                    for (var l = 0; l < lods.Length; l++)
                    {
                        var secCount = lods[l].Sections?.Length ?? 0;
                        var verts = lods[l].PositionVertexBuffer?.NumVertices ?? 0;
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.TableSetColumnIndex(1);
                        ImGui.TextDisabled($"    LOD {l} ({verts:N0} verts, {secCount} sec)");
                        ImGui.TableSetColumnIndex(2);
                        ImGui.TextDisabled("LOD");
                    }
                }
            }
        }

        if (tab.Niagara is { } nc)
        {
            for (var i = 0; i < nc.MeshEmitters.Count; i++)
            {
                var me = nc.MeshEmitters[i];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.PushID(1000 + i);
                if (ImGui.SmallButton(me.Enabled ? "o" : "-")) me.Enabled = !me.Enabled;
                ImGui.PopID();
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted($"  {me.EmitterName}");
                ImGui.TableSetColumnIndex(2);
                ImGui.TextDisabled("MeshEmitter");
            }
            for (var i = 0; i < nc.SpriteEmitters.Count; i++)
            {
                var se = nc.SpriteEmitters[i];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.PushID(2000 + i);
                if (ImGui.SmallButton(se.Enabled ? "o" : "-")) se.Enabled = !se.Enabled;
                ImGui.PopID();
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted($"  {se.EmitterName}");
                ImGui.TableSetColumnIndex(2);
                ImGui.TextDisabled("SpriteEmitter");
            }
            for (var i = 0; i < nc.LightEmitters.Count; i++)
            {
                var le = nc.LightEmitters[i];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.PushID(3000 + i);
                if (ImGui.SmallButton(le.Enabled ? "o" : "-")) le.Enabled = !le.Enabled;
                ImGui.PopID();
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted($"  {le.EmitterName}");
                ImGui.TableSetColumnIndex(2);
                ImGui.TextDisabled("LightEmitter");
            }
        }

        ImGui.EndTable();
    }

    private static void DrawTextureView(UTexture tex, float w, float h)
    {
        var device = D3D11NativeShaderDevice.Shared;
        if (device is null) return;
        var srv = NativeTextureCache.Get(device.Device, tex);
        if (srv is null)
        {
            ImGui.TextDisabled("Unable to decode texture.");
            return;
        }
        var sz = Math.Min(w - 24f, h - 24f);
        ImGui.Image(srv.NativePointer, new Vector2(sz, sz));
    }

    private void Draw3DViewport(PreviewTab tab, IFileProvider provider)
    {
        var size = ImGui.GetContentRegionAvail();
        var w = Math.Max(64, (int) size.X);
        var h = Math.Max(64, (int) size.Y);

        var device = D3D11NativeShaderDevice.Shared;
        if (device is null)
        {
            ImGui.TextDisabled($"DirectX 11 unavailable: {D3D11NativeShaderDevice.UnavailableReason}");
            return;
        }

        tab.Renderer ??= new NativeSceneRenderer(device);

        var items = new List<NativeDrawItem>();
        var niagaraSprites = new List<NativeNiagaraSpriteBatch>();
        var niagaraMeshes = new List<NativeNiagaraMeshBatch>();
        var sceneLights = new List<SceneLight>();

        var aspect = Math.Max(0.1f, (float) w / Math.Max(1, h));
        const float near = 0.05f;
        var hFovRad = RenderSettings.FieldOfViewDeg * 0.5f * MathF.PI / 180f;
        var f = aspect / MathF.Tan(hFovRad);
        var vFovRad = 2f * MathF.Atan(MathF.Tan(hFovRad) / aspect);
        var lookTarget = Vector3.Zero;

        if (tab.MaterialAsset is { } mat)
        {
            var shapeMesh = GetBasicShape(provider, tab.Shape);
            var nMesh = shapeMesh is not null
                ? NativeAssetCache.GetMesh(device, shapeMesh)
                : GetOrCreateProceduralShape(device.Device, tab.Shape);
            if (nMesh is not null)
            {
                if (tab.NeedsMaterialFraming)
                {
                    tab.Distance = NativeMeshLods.ComputeFramedCameraDistance(nMesh.TightBoundsRadius, vFovRad, 0.72f);
                    tab.NeedsMaterialFraming = false;
                }
                lookTarget = new Vector3(nMesh.BoundsCenter.X, nMesh.BoundsCenter.Z, nMesh.BoundsCenter.Y) * 0.01f;
                var pipe = NativeAssetCache.GetPipeline(device, mat, out _);
                pipe?.EnsureParametersSynced(device.Device, device.Context, provider, mat);
                var slots = nMesh.Lods.SelectMany(l => l.Sections).Select(s => s.MaterialIndex).DefaultIfEmpty(0).Max() + 1;
                items.Add(new NativeDrawItem(nMesh, Matrix4x4.Identity, Enumerable.Repeat(pipe, slots).ToArray()));
            }
        }
        else if (tab.Niagara is { } nc)
        {
            foreach (var me in nc.MeshEmitters)
            {
                if (!me.Enabled) continue;
                var nMesh = NativeAssetCache.GetMesh(device, me.Mesh);
                if (nMesh is null) continue;
                ID3D11ShaderResourceView? meshTexSrv = null;
                if (me.Material is { } mMat &&
                    (mMat.TryGetTexture(out var dTex, EMaterialTextureKind.Diffuse) || mMat.TryGetTexture(out dTex, EMaterialTextureKind.Emissive)))
                {
                    meshTexSrv = NativeTextureCache.Get(device.Device, dTex);
                }
                var mPipe = me.Material is not null ? NativeAssetCache.GetNiagaraPipeline(device, me.Material, isMesh: true, out _) : null;
                niagaraMeshes.Add(new NativeNiagaraMeshBatch(me, nMesh, Matrix4x4.Identity, meshTexSrv, tab.GetSim(me), mPipe));
            }
            foreach (var se in nc.SpriteEmitters)
            {
                if (!se.Enabled) continue;
                var texSrv = se.SpriteTexture is not null ? NativeTextureCache.Get(device.Device, se.SpriteTexture) : null;
                var sPipe = se.Material is not null ? NativeAssetCache.GetNiagaraPipeline(device, se.Material, isMesh: false, out _) : null;
                niagaraSprites.Add(new NativeNiagaraSpriteBatch(se, Matrix4x4.Identity, texSrv, tab.GetSim(se), sPipe));
            }
            foreach (var le in nc.LightEmitters)
            {
                if (!le.Enabled) continue;
                sceneLights.Add(new ScenePointLight
                {
                    Name = le.EmitterName,
                    LocationCm = Vector3.Zero,
                    Parent = null,
                    Color = le.Color,
                    Intensity = le.Intensity,
                    AttenuationRadius = le.DefaultRadius
                });
            }
        }
        else
        {
            foreach (var item in tab.Meshes)
            {
                if (!item.Visible) continue;
                if (NativeAssetCache.GetMesh(device, item.Mesh) is not { } nMesh) continue;
                var pipes = new NativeMaterialPipeline?[item.Slots.Length];
                for (var s = 0; s < item.Slots.Length; s++)
                {
                    if (item.Slots[s] is { } mn)
                    {
                        var pipe = NativeAssetCache.GetPipeline(device, mn, out _);
                        pipe?.EnsureParametersSynced(device.Device, device.Context, provider, mn);
                        pipes[s] = pipe;
                    }
                }
                items.Add(new NativeDrawItem(nMesh, item.LocalTransform, pipes));
            }
        }

        var cosP = MathF.Cos(tab.Pitch);
        var eye = lookTarget + new Vector3(
            MathF.Sin(tab.Yaw) * cosP * tab.Distance,
            MathF.Sin(tab.Pitch) * tab.Distance,
            MathF.Cos(tab.Yaw) * cosP * tab.Distance);
        var view = Matrix4x4.CreateLookAt(eye, lookTarget, Vector3.UnitY);
        var proj = new Matrix4x4(f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, 0, -1, 0, 0, near, 0);

        var worldToClip = UnrealToMeters * view * proj;
        var camUn = new Vector3(eye.X, eye.Z, eye.Y) * 100f;
        tab.Renderer.Features = RenderSettings.PreviewFeatures();
        tab.Renderer.Render(w, h, worldToClip, camUn, 0.5f * f, items, (ENativePreviewMode) tab.RenderMode, 1.0f,
            new Vector3(0.45f, 0.35f, 0.82f), measure: true, niagaraSprites: niagaraSprites, niagaraMeshes: niagaraMeshes,
            sceneLights: sceneLights);

        if (tab.Renderer.OutputSrv is { } srv)
            ImGui.Image(srv.NativePointer, new Vector2(w, h));

        if (ImGui.IsItemHovered())
        {
            var io = ImGui.GetIO();
            if (ImGui.IsMouseDragging(ImGuiMouseButton.Left) || ImGui.IsMouseDragging(ImGuiMouseButton.Right))
            {
                tab.Yaw -= io.MouseDelta.X * 0.01f;
                tab.Pitch = Math.Clamp(tab.Pitch + io.MouseDelta.Y * 0.01f, -1.45f, 1.45f);
            }
            if (MathF.Abs(io.MouseWheel) > 0.001f)
                tab.Distance = Math.Clamp(tab.Distance * MathF.Pow(0.88f, io.MouseWheel), 0.35f, 50f);
        }
    }

    private static void DrawUe5MaterialGraph(PreviewTab tab)
    {
        if (tab.GraphEditor is { } editor)
        {
            editor.Draw(tab);
        }
        else
        {
            ImGui.TextDisabled("Material Graph Editor unavailable.");
        }
    }

    private void DrawDetailsPanel(PreviewTab tab, IFileProvider provider)
    {
        var selectedObj = tab.SelectedOutlinerIndex >= 0 && tab.SelectedOutlinerIndex < tab.Meshes.Count
            ? tab.Meshes[tab.SelectedOutlinerIndex].Source
            : tab.Asset;

        ImGui.TextColored(new Vector4(0.93f, 0.62f, 0.11f, 1f), "Details");
        ImGui.Separator();

        if (ImGui.CollapsingHeader("Asset Summary", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped($"Name: {selectedObj.Name}");
            ImGui.TextWrapped($"Class: {selectedObj.ExportType}");
            ImGui.TextWrapped($"Path: {selectedObj.GetPathName()}");
        }

        if (tab.MaterialAsset is { } mn && ImGui.CollapsingHeader("Material Info", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextUnformatted($"BlendMode: {mn.BlendMode}");
            ImGui.TextUnformatted($"ShadingModel: {mn.ShadingModel}");
            ImGui.TextUnformatted($"TwoSided: {mn.TwoSided}");
            ImGui.TextUnformatted($"EmissiveDriven: {mn.IsEmissiveDriven}");
        }
        if (tab.GraphEditor?.SelectedNode is { } selNode && ImGui.CollapsingHeader("Selected Graph Node", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped($"Title: {selNode.Title}");
            ImGui.TextWrapped($"Node: {selNode.NodeName}");
            ImGui.TextWrapped($"Class: {selNode.ExpressionClass}");
            ImGui.TextWrapped($"Position: ({selNode.PosX}, {selNode.PosY})");
            if (!string.IsNullOrEmpty(selNode.FunctionGroup))
                ImGui.TextWrapped($"Function Group: {selNode.FunctionGroup}");
            if (!string.IsNullOrEmpty(selNode.Description))
                ImGui.TextWrapped(selNode.Description);
            foreach (var (pk, pv) in selNode.ExpressionProperties)
                ImGui.TextDisabled($"{pk} = {pv}");
            if (ImGui.SmallButton("Copy Selected Node(s) T3D"))
                tab.GraphEditor.CopySelectedToClipboard();
        }
        if (tab.SelectedExpression is { } expr && ImGui.CollapsingHeader("Expression Details", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextWrapped($"Type: {expr.GetType().Name}");
        }

        if (tab.MeshEditor is { } meshEd)
        {
            meshEd.Draw();
        }

        if (tab.Meshes.Count > 0 && ImGui.CollapsingHeader($"Material Slots ({tab.Meshes.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            for (var m = 0; m < tab.Meshes.Count; m++)
            {
                var item = tab.Meshes[m];
                ImGui.TextUnformatted($"[{m}] {item.Name}");
                for (var s = 0; s < item.Slots.Length; s++)
                {
                    var slotMat = item.Slots[s];
                    if (slotMat is not null && ImGui.Selectable($"  Slot {s}: {slotMat.Name}##PrvSlot_{m}_{s}"))
                    {
                        if (slotMat.RootMaterial() is { } rootMat)
                            Open(rootMat, provider);
                    }
                }
            }
        }

        if (ImGui.CollapsingHeader($"Properties ({selectedObj.Properties.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            const ImGuiTableFlags propFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                                              ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
            if (ImGui.BeginTable("##PrvProps", 2, propFlags, new Vector2(0f, Math.Max(160f, ImGui.GetContentRegionAvail().Y))))
            {
                ImGui.TableSetupColumn("Property", ImGuiTableColumnFlags.WidthStretch, 0.44f);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.56f);
                foreach (var prop in selectedObj.Properties)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(prop.Name.Text);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextDisabled(prop.Tag?.GenericValue?.ToString() ?? "null");
                }
                ImGui.EndTable();
            }
        }
    }

    private UStaticMesh? GetBasicShape(IFileProvider provider, MaterialShape shape)
    {
        if (_basicShapes.TryGetValue(shape, out var cached))
            return cached;
        var path = shape switch
        {
            MaterialShape.Cube => "Engine/Content/BasicShapes/Cube.Cube",
            MaterialShape.Cylinder => "Engine/Content/BasicShapes/Cylinder.Cylinder",
            MaterialShape.Plane => "Engine/Content/BasicShapes/Plane.Plane",
            _ => "Engine/Content/BasicShapes/Sphere.Sphere"
        };
        provider.TryLoadPackageObject<UStaticMesh>(path, out var loaded);
        _basicShapes[shape] = loaded;
        return loaded;
    }

    private NativeMeshLods GetOrCreateProceduralShape(Vortice.Direct3D11.ID3D11Device device, MaterialShape shape)
    {
        if (_proceduralShapes.TryGetValue(shape, out var cached))
            return cached;

        var lods = shape switch
        {
            MaterialShape.Cube => NativeMeshLods.CreateProceduralCube(device, halfExtentCm: 50f),
            MaterialShape.Cylinder => NativeMeshLods.CreateProceduralCylinder(device, radiusCm: 50f, heightCm: 100f),
            MaterialShape.Plane => NativeMeshLods.CreateProceduralPlane(device, halfExtentCm: 50f),
            _ => NativeMeshLods.CreateProceduralSphere(device, radiusCm: 50f)
        };
        _proceduralShapes[shape] = lods;
        return lods;
    }

    private static MaterialAsset?[] ResolveMeshMaterials(FPackageIndex?[]? mats)
    {
        if (mats is null || mats.Length == 0) return [];
        var arr = new MaterialAsset?[mats.Length];
        for (var i = 0; i < mats.Length; i++)
            if (mats[i] is { IsNull: false } r && r.TryLoad<UUnrealMaterial>(out var um))
                arr[i] = new MaterialAsset(um);
        return arr;
    }

    /// <summary>
    /// Blueprint components from the class chain's SimpleConstructionScripts (plus native CDO components), with their
    /// relative transforms, sockets, override materials and instances, flattened to Unreal-space draw items.
    /// </summary>
    private static void PopulateBlueprint(UBlueprintGeneratedClass bp, List<InspectMeshItem> output)
    {
        try
        {
            var roots = BlueprintComponents.Build(bp, null, false);
            void Visit(SceneObject node)
            {
                if (node is SceneMesh mesh)
                {
                    var worlds = mesh.InstanceLocalMatrices is { Count: > 0 } instances
                        ? instances.Select(im => im * mesh.WorldMatrix).ToList()
                        : [mesh.WorldMatrix];
                    for (var i = 0; i < worlds.Count; i++)
                    {
                        output.Add(new InspectMeshItem
                        {
                            Name = worlds.Count > 1 ? $"{mesh.Name} [{i}]" : mesh.Name,
                            ClassName = mesh.ClassName,
                            Source = mesh.SourceObject ?? mesh.Mesh,
                            Mesh = mesh.Mesh,
                            Slots = mesh.Materials,
                            LocalTransform = UExplorer.Scene.UnrealSpace.ToUnrealLocalToWorld(worlds[i])
                        });
                    }
                }
                foreach (var child in node.Children) Visit(child);
            }
            foreach (var root in roots) Visit(root);
        }
        catch (Exception e)
        {
            Serilog.Log.Warning(e, "Blueprint {Name} components could not be resolved", bp.Name);
        }
    }

    private static Ue52IconKind Classify(UObject obj) => obj switch
    {
        UWorld => Ue52IconKind.World,
        UMaterialInstance => Ue52IconKind.MaterialInstance,
        UMaterialInterface => Ue52IconKind.Material,
        UStaticMesh => Ue52IconKind.StaticMesh,
        USkeletalMesh => Ue52IconKind.SkeletalMesh,
        UBlueprintGeneratedClass => Ue52IconKind.Blueprint,
        _ when obj.ExportType.Contains("Niagara", StringComparison.OrdinalIgnoreCase) => Ue52IconKind.Niagara,
        _ when obj.ExportType.Contains("Texture", StringComparison.OrdinalIgnoreCase) => Ue52IconKind.Texture,
        _ => Ue52IconKind.GenericAsset
    };

    public void Dispose()
    {
        foreach (var t in _tabs) t.Dispose();
        _tabs.Clear();
        foreach (var p in _proceduralShapes.Values) p.Dispose();
        _proceduralShapes.Clear();
    }
}
