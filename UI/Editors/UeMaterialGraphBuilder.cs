using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using UExplorer.Rendering;
using UExplorer.Scene;
using Vortice.D3DCompiler;

namespace UExplorer.UI.Editors;

public sealed class UeMatPin
{
    public required string PinId { get; init; }
    public required string PinName { get; set; }
    public required bool IsOutput { get; init; }
    public string PinCategory { get; set; } = "";
    public string PinSubCategory { get; set; } = "";
    public string PinSubCategoryObject { get; set; } = "None";
    public string DefaultValue { get; set; } = "";
    public bool IsNotConnectable { get; set; }
    public bool IsAdvancedView { get; set; }
    public string? FriendlyName { get; set; }
    public UeMatNode? Owner { get; set; }
    public List<(UeMatNode Node, UeMatPin Pin)> LinkedTo { get; } = [];
}

public sealed class UeMatNode
{
    public required string NodeName { get; set; }
    public required string ExpressionClass { get; set; }
    public required string ExpressionName { get; set; }
    public required string Title { get; set; }
    public string Subtitle { get; set; } = "";
    public Vector4 HeaderColor { get; set; } = new(0.22f, 0.25f, 0.30f, 1f);
    public int PosX { get; set; }
    public int PosY { get; set; }
    public Vector2 CachedSize { get; set; } = new(180f, 100f);
    public string NodeGuid { get; set; } = NewUeGuid();
    public string ExpressionGuid { get; set; } = NewUeGuid();
    public string? ParameterGuid { get; set; }
    public bool IsRoot { get; init; }
    public bool CanRename { get; set; }
    public bool HasAdvancedPins { get; set; }
    public string? FunctionGroup { get; set; }
    public string? Description { get; set; }
    public UTexture? PreviewTexture { get; set; }
    public Vector4? PreviewColor { get; set; }
    public UMaterialExpression? SourceExpression { get; set; }
    public List<(string Key, string RawValue)> ExpressionProperties { get; } = [];
    public List<UeMatPin> InputPins { get; } = [];
    public List<UeMatPin> OutputPins { get; } = [];

    public static string NewUeGuid() => Guid.NewGuid().ToString("N").ToUpperInvariant();

    public UeMatPin AddInput(string name, string category = "optional", string subCategory = "", string defaultValue = "", bool notConnectable = false, bool advanced = false, string subCategoryObj = "None")
    {
        var pin = new UeMatPin
        {
            PinId = NewUeGuid(),
            PinName = name,
            IsOutput = false,
            PinCategory = category,
            PinSubCategory = subCategory,
            PinSubCategoryObject = subCategoryObj,
            DefaultValue = defaultValue,
            IsNotConnectable = notConnectable,
            IsAdvancedView = advanced,
            Owner = this
        };
        InputPins.Add(pin);
        return pin;
    }

    public UeMatPin AddOutput(string name, string category = "", string subCategory = "", string? friendlyName = null)
    {
        var pin = new UeMatPin
        {
            PinId = NewUeGuid(),
            PinName = name,
            IsOutput = true,
            PinCategory = category,
            PinSubCategory = subCategory,
            FriendlyName = friendlyName,
            Owner = this
        };
        OutputPins.Add(pin);
        return pin;
    }
}

public sealed class UeMatComment
{
    public required string NodeName { get; set; }
    public required string ExpressionName { get; set; }
    public required string Text { get; set; }
    public int PosX { get; set; }
    public int PosY { get; set; }
    public int SizeX { get; set; } = 420;
    public int SizeY { get; set; } = 260;
    public Vector4 Color { get; set; } = new(0.15f, 0.24f, 0.34f, 1f);
    public string NodeGuid { get; set; } = UeMatNode.NewUeGuid();
    public string ExpressionGuid { get; set; } = UeMatNode.NewUeGuid();
}

public sealed class UeMatGraph
{
    public string MaterialName { get; set; } = "PreviewMaterial_0";
    public UeMatNode RootNode { get; set; } = null!;
    public List<UeMatNode> Nodes { get; } = [];
    public List<UeMatComment> Comments { get; } = [];

    public static void Connect(UeMatPin outputPin, UeMatPin inputPin)
    {
        if (outputPin.Owner is null || inputPin.Owner is null)
            return;
        if (!outputPin.LinkedTo.Any(x => ReferenceEquals(x.Pin, inputPin)))
            outputPin.LinkedTo.Add((inputPin.Owner, inputPin));
        if (!inputPin.LinkedTo.Any(x => ReferenceEquals(x.Pin, outputPin)))
            inputPin.LinkedTo.Add((outputPin.Owner, outputPin));
    }
}

/// <summary>
/// Reconstructs authentic Unreal Engine 5 Material Graph Nodes (<see cref="UeMatGraph"/>) from cooked or uncooked
/// materials (Preshader bytecode + DXBC Pixel/Vertex shader decompilation + CachedExpressionData functions &amp; MPCs),
/// and supports 1:1 UE5 T3D clipboard serialization (<c>Begin Object Class=/Script/UnrealEd.MaterialGraphNode...</c>)
/// and deserialization for direct Copy &amp; Paste into Unreal Engine 5 or BlueprintUE.
/// </summary>
public static class UeMaterialGraphBuilder
{
    private const string EngineTexDiffuse = "/Script/Engine.Texture2D'/Engine/EngineMaterials/DefaultDiffuse.DefaultDiffuse'";
    private const string EngineTexNormal = "/Script/Engine.Texture2D'/Engine/EngineMaterials/DefaultNormal.DefaultNormal'";
    private const string EngineTexMasks = "/Script/Engine.Texture2D'/Engine/EngineMaterials/DefaultWhiteGrid.DefaultWhiteGrid'";

    private sealed record MpcResolvedParam(string CollectionPath, string CollectionName, string ParameterName, string ParameterId, bool IsVector, Vector4 DefaultValue);
    private sealed record FunctionResolvedInfo(string Name, string FullPath, bool IsEngine);

    public static UeMatGraph Build(MaterialAsset material)
    {
        var graph = new UeMatGraph
        {
            MaterialName = string.IsNullOrWhiteSpace(material.Name) ? "PreviewMaterial_0" : material.Name
        };
        graph.RootNode = CreateRootNode();

        var nodeCounter = 0;
        var exprCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        UeMatNode CreateNode(string shortClass, string title, Vector4 color, string? functionGroup = null)
        {
            var fullClass = shortClass.StartsWith("/Script/", StringComparison.Ordinal)
                ? shortClass
                : $"/Script/Engine.{shortClass}";
            var baseName = shortClass.Substring(shortClass.LastIndexOf('.') + 1);
            var exprIdx = exprCounters.GetValueOrDefault(baseName, 0);
            exprCounters[baseName] = exprIdx + 1;

            var n = new UeMatNode
            {
                NodeName = $"MaterialGraphNode_{nodeCounter++}",
                ExpressionClass = fullClass,
                ExpressionName = $"{baseName}_{exprIdx}",
                Title = title,
                HeaderColor = color,
                FunctionGroup = functionGroup
            };
            graph.Nodes.Add(n);
            return n;
        }

        // 1. Gather CachedExpressionData metadata: FunctionInfos, ParameterCollectionInfos, MaterialLayers
        var (functions, layerNames, blendNames, mpcMap) = ExtractCachedMetadata(material);
        var defaultCustomFuncName = functions.FirstOrDefault(f => !f.IsEngine)?.Name;

        string? ResolveGroupName(EMaterialParameterAssociation assoc, int index, string? fallbackFunc = null)
        {
            if (assoc == EMaterialParameterAssociation.LayerParameter)
            {
                if (index >= 0 && index < layerNames.Count && !string.IsNullOrWhiteSpace(layerNames[index]))
                    return $"Material Layer {index}: {layerNames[index]}";
                return $"Material Layer {Math.Max(0, index)}";
            }
            if (assoc == EMaterialParameterAssociation.BlendParameter)
            {
                if (index >= 0 && index < blendNames.Count && !string.IsNullOrWhiteSpace(blendNames[index]))
                    return $"Layer Blend {index}: {blendNames[index]}";
                return $"Layer Blend {Math.Max(0, index)}";
            }
            return fallbackFunc;
        }

        // 2. Find primary shader set (UniformExpressionSet + TBasePassPS/VS)
        var shaderSets = material.GetNativeShaders(validateOnDevice: false);
        var primarySet = shaderSets.FirstOrDefault(s => s.UniformSet is not null && s.Shaders.Count > 0)
                         ?? shaderSets.FirstOrDefault(s => s.UniformSet is not null);

        var scalarParamNodes = new Dictionary<string, UeMatPin>(StringComparer.OrdinalIgnoreCase);
        var vectorParamNodes = new Dictionary<string, UeMatNode>(StringComparer.OrdinalIgnoreCase);
        var textureSlotNodes = new Dictionary<int, UeMatNode>();
        var textureParamNodes = new Dictionary<string, UeMatNode>(StringComparer.OrdinalIgnoreCase);
        var mpcSlotNodes = new Dictionary<(int ColIdx, int RegIdx, int CompIdx), UeMatPin>();
        var constScalarNodes = new Dictionary<int, UeMatPin>();

        UeMatPin GetOrCreateScalarParam(string name, float val, EMaterialParameterAssociation assoc = EMaterialParameterAssociation.GlobalParameter, int assocIdx = -1)
        {
            var key = $"{name}|{(int) assoc}|{assocIdx}";
            if (scalarParamNodes.TryGetValue(key, out var existing))
                return existing;

            var grp = ResolveGroupName(assoc, assocIdx, null);
            var n = CreateNode("MaterialExpressionScalarParameter", $"Scalar '{name}'", new Vector4(0.16f, 0.45f, 0.24f, 1f), grp);
            n.Subtitle = val.ToString("0.####", CultureInfo.InvariantCulture);
            n.CanRename = true;
            n.ParameterGuid = UeMatNode.NewUeGuid();
            n.ExpressionProperties.Add(("DefaultValue", val.ToString("0.000000", CultureInfo.InvariantCulture)));
            n.ExpressionProperties.Add(("ParameterName", $"\"{EscapeT3D(name)}\""));
            n.ExpressionProperties.Add(("ExpressionGUID", n.ParameterGuid));
            n.AddInput("Default Value", "optional", "red", val.ToString("0.0####", CultureInfo.InvariantCulture), notConnectable: true);
            var outPin = n.AddOutput("Output", "", "", friendlyName: " ");
            scalarParamNodes[key] = outPin;
            scalarParamNodes.TryAdd(name, outPin);
            return outPin;
        }

        UeMatNode GetOrCreateVectorParam(string name, Vector4 val, EMaterialParameterAssociation assoc = EMaterialParameterAssociation.GlobalParameter, int assocIdx = -1)
        {
            var key = $"{name}|{(int) assoc}|{assocIdx}";
            if (vectorParamNodes.TryGetValue(key, out var existing))
                return existing;

            var grp = ResolveGroupName(assoc, assocIdx, null);
            var n = CreateNode("MaterialExpressionVectorParameter", $"Vector '{name}'", new Vector4(0.55f, 0.42f, 0.12f, 1f), grp);
            n.Subtitle = $"({val.X:0.##}, {val.Y:0.##}, {val.Z:0.##}, {val.W:0.##})";
            n.CanRename = true;
            n.PreviewColor = val;
            n.ParameterGuid = UeMatNode.NewUeGuid();
            var defStr = $"(R={val.X.ToString("0.000000", CultureInfo.InvariantCulture)},G={val.Y.ToString("0.000000", CultureInfo.InvariantCulture)},B={val.Z.ToString("0.000000", CultureInfo.InvariantCulture)},A={val.W.ToString("0.000000", CultureInfo.InvariantCulture)})";
            n.ExpressionProperties.Add(("DefaultValue", defStr));
            n.ExpressionProperties.Add(("ParameterName", $"\"{EscapeT3D(name)}\""));
            n.ExpressionProperties.Add(("ExpressionGUID", n.ParameterGuid));
            n.AddInput("Default Value", "optional", "rgba", defStr, notConnectable: true);
            n.AddOutput("RGB", "mask", "");
            n.AddOutput("R", "mask", "red");
            n.AddOutput("G", "mask", "green");
            n.AddOutput("B", "mask", "blue");
            n.AddOutput("A", "mask", "alpha");
            n.AddOutput("RGBA", "mask", "rgba");
            vectorParamNodes[key] = n;
            vectorParamNodes.TryAdd(name, n);
            return n;
        }

        UeMatPin CreateConstantScalar(float val, string? grp = null)
        {
            var q = BitConverter.SingleToInt32Bits(MathF.Round(val, 5));
            if (constScalarNodes.TryGetValue(q, out var existing))
                return existing;
            var n = CreateNode("MaterialExpressionConstant", $"Constant ({val:0.###})", new Vector4(0.24f, 0.36f, 0.24f, 1f), grp);
            n.Subtitle = val.ToString("0.####", CultureInfo.InvariantCulture);
            n.ExpressionProperties.Add(("R", val.ToString("0.000000", CultureInfo.InvariantCulture)));
            n.AddInput("Value", "optional", "red", val.ToString("0.0####", CultureInfo.InvariantCulture), notConnectable: true);
            var outPin = n.AddOutput("Output", "", "", friendlyName: " ");
            constScalarNodes[q] = outPin;
            return outPin;
        }

        UeMatPin CreateConstantVector(Vector4 val, int numComps = 4, string? grp = null)
        {
            if (numComps <= 1 || (MathF.Abs(val.X - val.Y) < 1e-5f && MathF.Abs(val.X - val.Z) < 1e-5f && (numComps < 4 || MathF.Abs(val.X - val.W) < 1e-5f)))
                return CreateConstantScalar(val.X, grp);

            if (numComps == 2)
            {
                var n2 = CreateNode("MaterialExpressionConstant2Vector", $"Constant2 ({val.X:0.##}, {val.Y:0.##})", new Vector4(0.24f, 0.36f, 0.24f, 1f), grp);
                n2.ExpressionProperties.Add(("R", val.X.ToString("0.000000", CultureInfo.InvariantCulture)));
                n2.ExpressionProperties.Add(("G", val.Y.ToString("0.000000", CultureInfo.InvariantCulture)));
                n2.AddInput("X", "optional", "red", val.X.ToString("0.0####", CultureInfo.InvariantCulture), notConnectable: true);
                n2.AddInput("Y", "optional", "red", val.Y.ToString("0.0####", CultureInfo.InvariantCulture), notConnectable: true);
                var p = n2.AddOutput("Output", "", "", friendlyName: " ");
                n2.AddOutput("R", "mask", "red");
                n2.AddOutput("G", "mask", "green");
                return p;
            }
            if (numComps == 3)
            {
                var n3 = CreateNode("MaterialExpressionConstant3Vector", $"Constant3 ({val.X:0.##}, {val.Y:0.##}, {val.Z:0.##})", new Vector4(0.24f, 0.36f, 0.24f, 1f), grp);
                n3.PreviewColor = new Vector4(val.X, val.Y, val.Z, 1f);
                var cStr = $"(R={val.X.ToString("0.000000", CultureInfo.InvariantCulture)},G={val.Y.ToString("0.000000", CultureInfo.InvariantCulture)},B={val.Z.ToString("0.000000", CultureInfo.InvariantCulture)},A=1.000000)";
                n3.ExpressionProperties.Add(("Constant", cStr));
                n3.AddInput("Constant", "optional", "rgb", $"{val.X.ToString("0.0####", CultureInfo.InvariantCulture)},{val.Y.ToString("0.0####", CultureInfo.InvariantCulture)},{val.Z.ToString("0.0####", CultureInfo.InvariantCulture)}", notConnectable: true);
                var p = n3.AddOutput("Output", "", "", friendlyName: " ");
                n3.AddOutput("R", "mask", "red");
                n3.AddOutput("G", "mask", "green");
                n3.AddOutput("B", "mask", "blue");
                return p;
            }
            {
                var n4 = CreateNode("MaterialExpressionConstant4Vector", $"Constant4 ({val.X:0.##}, {val.Y:0.##}, {val.Z:0.##}, {val.W:0.##})", new Vector4(0.24f, 0.36f, 0.24f, 1f), grp);
                n4.PreviewColor = val;
                var cStr = $"(R={val.X.ToString("0.000000", CultureInfo.InvariantCulture)},G={val.Y.ToString("0.000000", CultureInfo.InvariantCulture)},B={val.Z.ToString("0.000000", CultureInfo.InvariantCulture)},A={val.W.ToString("0.000000", CultureInfo.InvariantCulture)})";
                n4.ExpressionProperties.Add(("Constant", cStr));
                var p = n4.AddOutput("Output", "", "", friendlyName: " ");
                n4.AddOutput("R", "mask", "red");
                n4.AddOutput("G", "mask", "green");
                n4.AddOutput("B", "mask", "blue");
                n4.AddOutput("A", "mask", "alpha");
                return p;
            }
        }

        UeMatNode CreateTextureSampleNode(string paramName, UTexture? tex, EMaterialParameterAssociation assoc = EMaterialParameterAssociation.GlobalParameter, int assocIdx = -1)
        {
            var isNormal = tex is not null && (tex.CompressionSettings == TextureCompressionSettings.TC_Normalmap ||
                                               paramName.Contains("Normal", StringComparison.OrdinalIgnoreCase));
            var isMasks = !isNormal && tex is not null && (tex.CompressionSettings == TextureCompressionSettings.TC_Masks ||
                                                           !tex.SRGB ||
                                                           paramName.Contains("ORM", StringComparison.OrdinalIgnoreCase) ||
                                                           paramName.Contains("ARM", StringComparison.OrdinalIgnoreCase) ||
                                                           paramName.Contains("MRS", StringComparison.OrdinalIgnoreCase) ||
                                                           paramName.Contains("Mask", StringComparison.OrdinalIgnoreCase) ||
                                                           paramName.Contains("Rough", StringComparison.OrdinalIgnoreCase) ||
                                                           paramName.Contains("Spec", StringComparison.OrdinalIgnoreCase));

            var engineTexPath = isNormal ? EngineTexNormal : (isMasks ? EngineTexMasks : EngineTexDiffuse);
            var samplerTypeEnum = isNormal ? "SAMPLERTYPE_Normal" : (isMasks ? "SAMPLERTYPE_Masks" : "SAMPLERTYPE_Color");
            var samplerTypeDisplay = isNormal ? "Normal" : (isMasks ? "Masks" : "Color");

            var hasParamName = !string.IsNullOrWhiteSpace(paramName) && !string.Equals(paramName, "None", StringComparison.OrdinalIgnoreCase);
            var className = hasParamName ? "MaterialExpressionTextureSampleParameter2D" : "MaterialExpressionTextureSample";
            var title = hasParamName ? $"Texture '{paramName}'" : $"TextureSample ({tex?.Name ?? "Default"})";
            var grp = ResolveGroupName(assoc, assocIdx, null);

            var n = CreateNode(className, title, new Vector4(0.12f, 0.36f, 0.48f, 1f), grp);
            n.Subtitle = tex?.Name ?? (isNormal ? "DefaultNormal" : "DefaultDiffuse");
            n.PreviewTexture = tex;
            n.HasAdvancedPins = true;
            n.CanRename = hasParamName;
            if (tex is not null)
                n.Description = $"Source Texture: {tex.Name}";

            if (hasParamName)
            {
                n.ParameterGuid = UeMatNode.NewUeGuid();
                n.ExpressionProperties.Add(("ParameterName", $"\"{EscapeT3D(paramName)}\""));
                n.ExpressionProperties.Add(("ExpressionGUID", n.ParameterGuid));
            }
            n.ExpressionProperties.Add(("Texture", $"\"{engineTexPath}\""));
            if (isNormal || isMasks)
                n.ExpressionProperties.Add(("SamplerType", samplerTypeEnum));
            if (tex is not null)
                n.ExpressionProperties.Add(("Desc", $"\"{EscapeT3D(tex.Name)}\""));

            n.AddInput("UVs", "optional", "byte", "0");
            n.AddInput("Apply View MipBias", "optional", "");
            n.AddInput("MipValueMode", "optional", "byte", "None (use computed mip level)", notConnectable: true, advanced: true, subCategoryObj: "/Script/CoreUObject.Enum'/Script/Engine.ETextureMipValueMode'");
            n.AddInput("Sampler Source", "optional", "byte", "From texture asset", notConnectable: true, advanced: true, subCategoryObj: "/Script/CoreUObject.Enum'/Script/Engine.ESamplerSourceMode'");
            n.AddInput("Sampler Type", "optional", "byte", samplerTypeDisplay, notConnectable: true, advanced: true, subCategoryObj: "/Script/CoreUObject.Enum'/Script/Engine.EMaterialSamplerType'");

            n.AddOutput("RGB", "mask", "");
            n.AddOutput("R", "mask", "red");
            n.AddOutput("G", "mask", "green");
            n.AddOutput("B", "mask", "blue");
            n.AddOutput("A", "mask", "alpha");
            n.AddOutput("RGBA", "mask", "rgba");

            if (hasParamName)
                textureParamNodes.TryAdd(paramName, n);
            return n;
        }

        UeMatPin GetOrCreateMpcNode(int colIdx, int regIdx, int compIdx, int numComps = 1)
        {
            var key = (colIdx, regIdx, numComps >= 3 ? -1 : compIdx);
            if (mpcSlotNodes.TryGetValue(key, out var existing))
                return existing;

            var resolved = ResolveMpcRegister(mpcMap, colIdx, regIdx, compIdx);
            var n = CreateNode("MaterialExpressionCollectionParameter", $"MPC '{resolved.ParameterName}'", new Vector4(0.48f, 0.26f, 0.52f, 1f), defaultCustomFuncName);
            n.Subtitle = $"{resolved.CollectionName} [{ (resolved.IsVector ? "Vector" : "Scalar") }]";
            n.ParameterGuid = resolved.ParameterId;
            if (!string.IsNullOrWhiteSpace(resolved.CollectionPath))
                n.ExpressionProperties.Add(("Collection", $"\"/Script/Engine.MaterialParameterCollection'{resolved.CollectionPath}'\""));
            n.ExpressionProperties.Add(("ParameterName", $"\"{EscapeT3D(resolved.ParameterName)}\""));
            n.ExpressionProperties.Add(("ParameterId", resolved.ParameterId));
            var outPin = n.AddOutput("Output", "", "", friendlyName: " ");
            n.AddOutput("R", "mask", "red");
            n.AddOutput("G", "mask", "green");
            n.AddOutput("B", "mask", "blue");
            n.AddOutput("A", "mask", "alpha");

            mpcSlotNodes[key] = outPin;
            if (resolved.IsVector && numComps == 1 && compIdx >= 0 && compIdx < 4)
            {
                var chPin = n.OutputPins[1 + compIdx];
                mpcSlotNodes[(colIdx, regIdx, compIdx)] = chPin;
                return chPin;
            }
            return outPin;
        }

        UeMatPin EmitBinaryOp(string exprClass, string title, UeMatPin a, UeMatPin b, string pinAName = "A", string pinBName = "B", string? grp = null)
        {
            var group = grp ?? a.Owner?.FunctionGroup ?? b.Owner?.FunctionGroup ?? defaultCustomFuncName;
            var n = CreateNode(exprClass, title, new Vector4(0.26f, 0.32f, 0.42f, 1f), group);
            var inA = n.AddInput(pinAName, "optional", "");
            var inB = n.AddInput(pinBName, "optional", "");
            var outPin = n.AddOutput("Output", "", "", friendlyName: " ");
            UeMatGraph.Connect(a, inA);
            UeMatGraph.Connect(b, inB);
            return outPin;
        }

        UeMatPin EmitTernaryOp(string exprClass, string title, UeMatPin a, UeMatPin b, UeMatPin c, string pinA, string pinB, string pinC, string? grp = null)
        {
            var group = grp ?? a.Owner?.FunctionGroup ?? b.Owner?.FunctionGroup ?? c.Owner?.FunctionGroup ?? defaultCustomFuncName;
            var n = CreateNode(exprClass, title, new Vector4(0.26f, 0.32f, 0.42f, 1f), group);
            var inA = n.AddInput(pinA, "optional", "");
            var inB = n.AddInput(pinB, "optional", "");
            var inC = n.AddInput(pinC, "optional", "");
            var outPin = n.AddOutput("Output", "", "", friendlyName: " ");
            UeMatGraph.Connect(a, inA);
            UeMatGraph.Connect(b, inB);
            UeMatGraph.Connect(c, inC);
            return outPin;
        }

        UeMatPin EmitUnaryOp(string exprClass, string title, UeMatPin input, string inPinName = "Input", string? grp = null)
        {
            var group = grp ?? input.Owner?.FunctionGroup ?? defaultCustomFuncName;
            var n = CreateNode(exprClass, title, new Vector4(0.26f, 0.32f, 0.42f, 1f), group);
            var inPin = n.AddInput(inPinName, "optional", "");
            var outPin = n.AddOutput("Output", "", "", friendlyName: " ");
            UeMatGraph.Connect(input, inPin);
            return outPin;
        }

        UeMatPin EmitSwizzleOrMask(UeMatPin src, int numElements, byte idxR, byte idxG, byte idxB, byte idxA)
        {
            if (src.Owner is { } owner && owner.OutputPins.Count >= 6 &&
                owner.ExpressionClass.Contains("VectorParameter", StringComparison.OrdinalIgnoreCase) ||
                (src.Owner is { } tOwner && tOwner.OutputPins.Count >= 6 && tOwner.ExpressionClass.Contains("TextureSample", StringComparison.OrdinalIgnoreCase)))
            {
                var pOwner = src.Owner!;
                if (numElements == 1 && idxR <= 3)
                    return pOwner.OutputPins[1 + idxR]; // R, G, B, A
                if (numElements == 3 && idxR == 0 && idxG == 1 && idxB == 2)
                    return pOwner.OutputPins[0]; // RGB
                if (numElements == 4 && idxR == 0 && idxG == 1 && idxB == 2 && idxA == 3)
                    return pOwner.OutputPins[5]; // RGBA
            }

            if (numElements >= 3 && idxR == 0 && idxG == 1 && idxB == 2)
                return src;

            var rOn = numElements >= 1 && idxR == 0 || (numElements == 1 && idxR == 0);
            var gOn = (numElements >= 2 && idxG == 1) || (numElements == 1 && idxR == 1);
            var bOn = (numElements >= 3 && idxB == 2) || (numElements == 1 && idxR == 2);
            var aOn = (numElements >= 4 && idxA == 3) || (numElements == 1 && idxR == 3);
            if (!rOn && !gOn && !bOn && !aOn)
                rOn = true;

            var maskLabel = $"Mask ( {(rOn ? "R " : "")}{(gOn ? "G " : "")}{(bOn ? "B " : "")}{(aOn ? "A " : "")})";
            var n = CreateNode("MaterialExpressionComponentMask", maskLabel, new Vector4(0.28f, 0.34f, 0.42f, 1f), src.Owner?.FunctionGroup);
            if (rOn) n.ExpressionProperties.Add(("R", "True"));
            if (gOn) n.ExpressionProperties.Add(("G", "True"));
            if (bOn) n.ExpressionProperties.Add(("B", "True"));
            if (aOn) n.ExpressionProperties.Add(("A", "True"));
            var inPin = n.AddInput("Input", "required", "");
            var outPin = n.AddOutput("Output", "", "", friendlyName: " ");
            UeMatGraph.Connect(src, inPin);
            return outPin;
        }

        UeMatPin EmitFlattenNormalEngineFunc(UeMatPin normalPin, UeMatPin flatnessPin)
        {
            var n = CreateNode("MaterialExpressionMaterialFunctionCall", "FlattenNormal", new Vector4(0.18f, 0.28f, 0.52f, 1f));
            n.Subtitle = "Function";
            n.ExpressionProperties.Add(("MaterialFunction", "\"/Script/Engine.MaterialFunction'/Engine/Functions/Engine_MaterialFunctions01/Texturing/FlattenNormal.FlattenNormal'\""));
            var inNorm = n.AddInput("Normal (V3)", "optional", "");
            var inFlat = n.AddInput("Flatness (S)", "optional", "");
            var outRes = n.AddOutput("Result", "", "");
            UeMatGraph.Connect(normalPin, inNorm);
            UeMatGraph.Connect(flatnessPin, inFlat);
            return outRes;
        }

        // 3. Decompile Preshader Bytecode -> Material Constant Buffer register map cb_mat[byteOffset / 4]
        var cbMatPins = new Dictionary<int, UeMatPin>(); // key = float offset (byteOffset / 4)

        if (primarySet?.UniformSet is { } uniforms)
        {
            // Pre-create all Scalar, Vector, and Texture parameter nodes from UniformSet
            for (var i = 0; i < uniforms.UniformScalarParameters.Length; i++)
            {
                var p = uniforms.UniformScalarParameters[i];
                var name = p.ParameterInfo?.Name.Text ?? p.ParameterName ?? $"Scalar_{i}";
                var assoc = p.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
                var idx = p.ParameterInfo?.Index ?? -1;
                GetOrCreateScalarParam(name, material.GetNativeScalar(p), assoc, idx);
            }
            for (var i = 0; i < uniforms.UniformVectorParameters.Length; i++)
            {
                var p = uniforms.UniformVectorParameters[i];
                var name = p.ParameterInfo?.Name.Text ?? p.ParameterName ?? $"Vector_{i}";
                var assoc = p.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
                var idx = p.ParameterInfo?.Index ?? -1;
                GetOrCreateVectorParam(name, material.GetNativeVector(p), assoc, idx);
            }
            for (var i = 0; i < uniforms.UniformNumericParameters.Length; i++)
            {
                var p = uniforms.UniformNumericParameters[i];
                var name = p.ParameterInfo?.Name.Text ?? $"Param_{i}";
                var assoc = p.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
                var idx = p.ParameterInfo?.Index ?? -1;
                var val = material.GetNativeNumeric(p);
                if (p.ParameterType == EMaterialParameterType.Scalar)
                    GetOrCreateScalarParam(name, val.X, assoc, idx);
                else if (p.ParameterType is EMaterialParameterType.Vector or EMaterialParameterType.DoubleVector)
                    GetOrCreateVectorParam(name, val, assoc, idx);
            }

            var (resolvedTextures, _) = material.ResolveNativeMaterialResourceLayout(primarySet);
            var allTexParams = uniforms.UniformTextureParameters.SelectMany(x => x ?? []).ToList();
            for (var i = 0; i < resolvedTextures.Length; i++)
            {
                if (resolvedTextures[i] is not { } tex)
                    continue;
                var paramInfo = allTexParams.FirstOrDefault(tp => tp.TextureIndex == i / 2);
                var pName = paramInfo?.ParameterInfo?.Name.Text ?? paramInfo?.ParameterName ?? tex.Name;
                var assoc = paramInfo?.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
                var assocIdx = paramInfo?.ParameterInfo?.Index ?? -1;
                var texNode = CreateTextureSampleNode(pName, tex, assoc, assocIdx);
                textureSlotNodes[i] = texNode;
            }
            foreach (var tp in allTexParams)
            {
                var pName = tp.ParameterInfo?.Name.Text ?? tp.ParameterName ?? "";
                if (!string.IsNullOrEmpty(pName) && pName != "None" && !textureParamNodes.ContainsKey(pName))
                {
                    material.TryGetTexture(out var foundTex, pName);
                    var assoc = tp.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
                    var assocIdx = tp.ParameterInfo?.Index ?? -1;
                    var texNode = CreateTextureSampleNode(pName, foundTex, assoc, assocIdx);
                    if (!textureSlotNodes.ContainsKey(tp.TextureIndex * 2))
                        textureSlotNodes[tp.TextureIndex * 2] = texNode;
                }
            }

            DecompilePreshadersToPins(
                uniforms,
                material,
                cbMatPins,
                GetOrCreateScalarParam,
                GetOrCreateVectorParam,
                CreateConstantScalar,
                CreateConstantVector,
                EmitBinaryOp,
                EmitTernaryOp,
                EmitUnaryOp,
                EmitSwizzleOrMask);
        }

        // 4. Decompile DXBC Pixel Shader (TBasePassPSFNoLightMapPolicy) & connect to MaterialGraphNode_Root pins!
        var connectedRootPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (primarySet is not null)
        {
            var ps = primarySet.Shaders.FirstOrDefault(s => s.ShaderType.Contains("TBasePassPS", StringComparison.OrdinalIgnoreCase))
                     ?? primarySet.Shaders.FirstOrDefault(s => s.ShaderType.Contains("PS", StringComparison.Ordinal));

            if (ps is { Code: not null, Container: not null, Bytecode.Length: > 0 })
            {
                DecompilePixelShaderToRoot(
                    ps,
                    graph,
                    cbMatPins,
                    textureSlotNodes,
                    textureParamNodes,
                    connectedRootPins,
                    CreateNode,
                    GetOrCreateMpcNode,
                    CreateConstantScalar,
                    CreateConstantVector,
                    CreateTextureSampleNode,
                    EmitBinaryOp,
                    EmitTernaryOp,
                    EmitUnaryOp,
                    EmitSwizzleOrMask,
                    EmitFlattenNormalEngineFunc,
                    defaultCustomFuncName);
            }
        }

        // 5. Ensure any remaining unconnected texture/scalar/vector parameters are wired to their semantic root pins
        WireFallbackSemanticPins(
            graph,
            material,
            functions,
            textureParamNodes,
            textureSlotNodes,
            scalarParamNodes,
            vectorParamNodes,
            connectedRootPins,
            CreateTextureSampleNode,
            GetOrCreateScalarParam,
            GetOrCreateVectorParam,
            EmitBinaryOp,
            EmitFlattenNormalEngineFunc);

        // 6. Auto-layout nodes in clean topological columns + build MaterialGraphNode_Comment boxes for custom Material Functions!
        LayoutGraphAndBuildFunctionComments(graph, functions);

        return graph;
    }

    private static (List<FunctionResolvedInfo> Functions, List<string> LayerNames, List<string> BlendNames, Dictionary<int, (string Path, string Name, List<(string Name, string Id, float Val)> Scalars, List<(string Name, string Id, Vector4 Val)> Vectors)> MpcMap)
        ExtractCachedMetadata(MaterialAsset material)
    {
        var functions = new List<FunctionResolvedInfo>();
        var seenFuncs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var layerNames = new List<string>();
        var blendNames = new List<string>();
        var mpcMap = new Dictionary<int, (string Path, string Name, List<(string Name, string Id, float Val)> Scalars, List<(string Name, string Id, Vector4 Val)> Vectors)>();

        var cachedList = new List<FStructFallback>();
        for (var node = material; node != null; node = node.Parent)
        {
            if (node.CookedMaterial is UMaterialInterface mi)
            {
                var c = mi.CachedExpressionData ?? mi.GetOrDefault<FStructFallback?>("CachedExpressionData");
                if (c is not null)
                    cachedList.Add(c);
            }
        }
        if (material.RootMaterial() is { } rootMat)
        {
            var c = rootMat.CachedExpressionData ?? rootMat.GetOrDefault<FStructFallback?>("CachedExpressionData");
            if (c is not null && !cachedList.Contains(c))
                cachedList.Add(c);
        }

        foreach (var c in cachedList)
        {
            if (c.GetOrDefault<FStructFallback[]>("FunctionInfos") is { Length: > 0 } fInfos)
            {
                foreach (var fi in fInfos)
                {
                    if (fi.GetOrDefault<FPackageIndex?>("Function") is { IsNull: false } fIdx)
                    {
                        var fName = fIdx.Name;
                        var fPath = fIdx.ResolvedObject?.GetPathName() ?? fName;
                        if (!string.IsNullOrWhiteSpace(fName) && seenFuncs.Add(fName))
                        {
                            var isEngine = fPath.StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase) ||
                                           fPath.StartsWith("Engine/", StringComparison.OrdinalIgnoreCase) ||
                                           fName is "FlattenNormal" or "BlendAngleCorrectedNormals" or "CheapContrast" or "Desaturation";
                            functions.Add(new FunctionResolvedInfo(fName, fPath, isEngine));
                        }
                    }
                }
            }

            if (c.TryGetValue(out FStructFallback matLayers, "MaterialLayers"))
            {
                if (layerNames.Count == 0 && matLayers.GetOrDefault<FPackageIndex?[]>("Layers") is { Length: > 0 } lArr)
                {
                    foreach (var l in lArr)
                        layerNames.Add(l is { IsNull: false } ? l.Name : "");
                }
                if (blendNames.Count == 0 && matLayers.GetOrDefault<FPackageIndex?[]>("Blends") is { Length: > 0 } bArr)
                {
                    foreach (var b in bArr)
                        blendNames.Add(b is { IsNull: false } ? b.Name : "");
                }
            }

            if (mpcMap.Count == 0 && c.GetOrDefault<FStructFallback[]>("ParameterCollectionInfos") is { Length: > 0 } pInfos)
            {
                for (var i = 0; i < pInfos.Length; i++)
                {
                    var pi = pInfos[i];
                    var stateId = pi.GetOrDefault<FGuid>("StateId");
                    var colIdx = pi.GetOrDefault<FPackageIndex?>("ParameterCollection");
                    var colName = colIdx is { IsNull: false } ? colIdx.Name : $"MPC_{i}";
                    var colPath = colIdx?.ResolvedObject?.GetPathName() ?? $"/Game/Materials/{colName}.{colName}";
                    var scalars = new List<(string Name, string Id, float Val)>();
                    var vectors = new List<(string Name, string Id, Vector4 Val)>();

                    if (colIdx is { IsNull: false } && colIdx.TryLoad<UObject>(out var colObj) && colObj is not null)
                    {
                        colPath = colObj.GetPathName();
                        foreach (var sp in colObj.GetOrDefault<FStructFallback[]>("ScalarParameters", []))
                        {
                            var sName = sp.GetOrDefault<FName>("ParameterName").Text;
                            var sId = sp.GetOrDefault<FGuid>("Id").ToString(EGuidFormats.Digits);
                            var sVal = sp.GetOrDefault<float>("DefaultValue");
                            scalars.Add((string.IsNullOrWhiteSpace(sName) ? $"Scalar_{scalars.Count}" : sName, sId, sVal));
                        }
                        foreach (var vp in colObj.GetOrDefault<FStructFallback[]>("VectorParameters", []))
                        {
                            var vName = vp.GetOrDefault<FName>("ParameterName").Text;
                            var vId = vp.GetOrDefault<FGuid>("Id").ToString(EGuidFormats.Digits);
                            var lc = vp.GetOrDefault<FLinearColor>("DefaultValue");
                            vectors.Add((string.IsNullOrWhiteSpace(vName) ? $"Vector_{vectors.Count}" : vName, vId, new Vector4(lc.R, lc.G, lc.B, lc.A)));
                        }
                    }

                    mpcMap[i] = (colPath, colName, scalars, vectors);
                }
            }
        }

        return (functions, layerNames, blendNames, mpcMap);
    }

    private static MpcResolvedParam ResolveMpcRegister(
        Dictionary<int, (string Path, string Name, List<(string Name, string Id, float Val)> Scalars, List<(string Name, string Id, Vector4 Val)> Vectors)> mpcMap,
        int colIdx,
        int regIdx,
        int compIdx)
    {
        if (mpcMap.TryGetValue(colIdx, out var col))
        {
            var scalarVecCount = (col.Scalars.Count + 3) / 4;
            if (regIdx < scalarVecCount)
            {
                var sIdx = regIdx * 4 + Math.Clamp(compIdx, 0, 3);
                if (sIdx < col.Scalars.Count)
                {
                    var s = col.Scalars[sIdx];
                    return new MpcResolvedParam(col.Path, col.Name, s.Name, s.Id, false, new Vector4(s.Val));
                }
            }
            var vIdx = regIdx - scalarVecCount;
            if (vIdx >= 0 && vIdx < col.Vectors.Count)
            {
                var v = col.Vectors[vIdx];
                return new MpcResolvedParam(col.Path, col.Name, v.Name, v.Id, true, v.Val);
            }
            return new MpcResolvedParam(col.Path, col.Name, $"{col.Name}_Param_{regIdx}_{compIdx}", UeMatNode.NewUeGuid(), compIdx < 0, Vector4.One);
        }

        return new MpcResolvedParam(
            $"/Engine/EngineMaterials/MPC_{colIdx}.MPC_{colIdx}",
            $"MaterialCollection{colIdx}",
            $"MPC{colIdx}_Reg{regIdx}_{(compIdx >= 0 && compIdx < 4 ? "RGBA"[compIdx] : 'V')}",
            UeMatNode.NewUeGuid(),
            compIdx < 0,
            Vector4.One);
    }

    private static void DecompilePreshadersToPins(
        FUniformExpressionSet set,
        MaterialAsset material,
        Dictionary<int, UeMatPin> cbMatPins,
        Func<string, float, EMaterialParameterAssociation, int, UeMatPin> getScalarParam,
        Func<string, Vector4, EMaterialParameterAssociation, int, UeMatNode> getVectorParam,
        Func<float, string?, UeMatPin> createConstScalar,
        Func<Vector4, int, string?, UeMatPin> createConstVec,
        Func<string, string, UeMatPin, UeMatPin, string, string, string?, UeMatPin> emitBinary,
        Func<string, string, UeMatPin, UeMatPin, UeMatPin, string, string, string, string?, UeMatPin> emitTernary,
        Func<string, string, UeMatPin, string, string?, UeMatPin> emitUnary,
        Func<UeMatPin, int, byte, byte, byte, byte, UeMatPin> emitSwizzle)
    {
        var virtualTypeIndex = set.UniformTextureParameters.Length switch
        {
            >= 6 => set.UniformTextureParameters.Length - 1,
            5 => 4,
            _ => -1
        };
        var virtualTexCount = virtualTypeIndex >= 0 && virtualTypeIndex < set.UniformTextureParameters.Length && set.UniformTextureParameters[virtualTypeIndex] is { } vtArr
            ? vtArr.Length
            : 0;
        var preshaderBaseFloatOffset = ((set.VTStacks?.Length ?? 0) * 2 + virtualTexCount) * 4;
        var code = set.UniformPreshaderData?.Data ?? [];
        var names = set.UniformPreshaderData?.Names ?? [];

        if (code.Length == 0)
            return;

        if (set.UniformPreshaders.Length == 0)
        {
            // UE 4.25–4.27 UniformVectorPreshaders + UniformScalarPreshaders
            var useIndexParams = DetectUe427IndexedEncoding(set, code);
            for (var i = 0; i < set.UniformVectorPreshaders.Length; i++)
            {
                var h = set.UniformVectorPreshaders[i];
                var pin = BuildUe427PreshaderAst(code, names, (int) h.OpcodeOffset, (int) h.OpcodeSize, useIndexParams, set, material,
                    getScalarParam, getVectorParam, createConstScalar, createConstVec, emitBinary, emitTernary, emitUnary, emitSwizzle);
                if (pin is not null)
                {
                    var floatOff = preshaderBaseFloatOffset + i * 4;
                    for (var c = 0; c < 4; c++)
                        cbMatPins[floatOff + c] = pin;
                }
            }

            var scalarBaseFloat = preshaderBaseFloatOffset + set.UniformVectorPreshaders.Length * 4;
            for (var i = 0; i < set.UniformScalarPreshaders.Length; i++)
            {
                var h = set.UniformScalarPreshaders[i];
                var pin = BuildUe427PreshaderAst(code, names, (int) h.OpcodeOffset, (int) h.OpcodeSize, useIndexParams, set, material,
                    getScalarParam, getVectorParam, createConstScalar, createConstVec, emitBinary, emitTernary, emitUnary, emitSwizzle);
                if (pin is not null)
                    cbMatPins[scalarBaseFloat + i] = pin;
            }
        }
        else
        {
            // UE 5.0–5.2+ UniformPreshaders + UniformPreshaderFields
            var fields = set.UniformPreshaderFields ?? [];
            for (var i = 0; i < set.UniformPreshaders.Length; i++)
            {
                var h = set.UniformPreshaders[i];
                var pin = BuildUe52PreshaderAst(code, (int) h.OpcodeOffset, (int) h.OpcodeSize, set, material,
                    getScalarParam, getVectorParam, createConstScalar, createConstVec, emitBinary, emitTernary, emitUnary, emitSwizzle);
                if (pin is null)
                    continue;

                switch (h)
                {
                    case FMaterialUniformPreshaderHeader_5_0 h50:
                    {
                        var floatOff = preshaderBaseFloatOffset + (int) h50.BufferOffset;
                        var count = Math.Clamp((int) (h50.NumComponents ?? 4), 1, 4);
                        for (var c = 0; c < count; c++)
                            cbMatPins[floatOff + c] = pin;
                        break;
                    }
                    case FMaterialUniformPreshaderHeader_5_1 h51:
                    {
                        for (var f = 0; f < h51.NumFields; f++)
                        {
                            var fIdx = (int) h51.FieldIndex + f;
                            if (fIdx < 0 || fIdx >= fields.Length) break;
                            var field = fields[fIdx];
                            var floatOff = preshaderBaseFloatOffset + (int) field.BufferOffset;
                            cbMatPins[floatOff] = pin;
                        }
                        break;
                    }
                    case FMaterialUniformPreshaderHeader_5_8 h58:
                    {
                        var floatOff = preshaderBaseFloatOffset + h58.BufferOffset;
                        cbMatPins[floatOff] = pin;
                        break;
                    }
                }
            }
        }
    }

    private static UeMatPin? BuildUe52PreshaderAst(
        byte[] code,
        int offset,
        int size,
        FUniformExpressionSet set,
        MaterialAsset material,
        Func<string, float, EMaterialParameterAssociation, int, UeMatPin> getScalarParam,
        Func<string, Vector4, EMaterialParameterAssociation, int, UeMatNode> getVectorParam,
        Func<float, string?, UeMatPin> createConstScalar,
        Func<Vector4, int, string?, UeMatPin> createConstVec,
        Func<string, string, UeMatPin, UeMatPin, string, string, string?, UeMatPin> emitBinary,
        Func<string, string, UeMatPin, UeMatPin, UeMatPin, string, string, string, string?, UeMatPin> emitTernary,
        Func<string, string, UeMatPin, string, string?, UeMatPin> emitUnary,
        Func<UeMatPin, int, byte, byte, byte, byte, UeMatPin> emitSwizzle)
    {
        var ptr = offset;
        var end = Math.Min(code.Length, offset + size);
        var stack = new Stack<UeMatPin>();

        while (ptr < end)
        {
            var op = code[ptr++];
            switch (op)
            {
                case 0: // Nop
                    break;
                case 1: // ConstantZero
                    if (ptr < end) ptr++; // type byte
                    stack.Push(createConstScalar(0f, null));
                    break;
                case 2: // Constant
                {
                    if (ptr >= end) return stack.Count > 0 ? stack.Pop() : null;
                    var valType = code[ptr++];
                    var numComps = valType switch { 1 => 1, 2 => 2, 3 => 3, 4 => 4, _ => 1 };
                    if (ptr + numComps * 4 > end) return stack.Count > 0 ? stack.Pop() : null;
                    var v = Vector4.Zero;
                    for (var c = 0; c < numComps; c++)
                        v[c] = BinaryPrimitives.ReadSingleLittleEndian(code.AsSpan(ptr + c * 4));
                    ptr += numComps * 4;
                    stack.Push(numComps == 1 ? createConstScalar(v.X, null) : createConstVec(v, numComps, null));
                    break;
                }
                case 3: // Parameter (uint16 index)
                {
                    if (ptr + 2 > end) return stack.Count > 0 ? stack.Pop() : null;
                    var idx = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(ptr));
                    ptr += 2;
                    if (idx < set.UniformNumericParameters.Length)
                    {
                        var np = set.UniformNumericParameters[idx];
                        var pName = np.ParameterInfo?.Name.Text ?? $"Param_{idx}";
                        var assoc = np.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
                        var assocIdx = np.ParameterInfo?.Index ?? -1;
                        var val = material.GetNativeNumeric(np);
                        if (np.ParameterType == EMaterialParameterType.Scalar)
                            stack.Push(getScalarParam(pName, val.X, assoc, assocIdx));
                        else
                            stack.Push(getVectorParam(pName, val, assoc, assocIdx).OutputPins[0]);
                    }
                    else
                    {
                        stack.Push(createConstScalar(0f, null));
                    }
                    break;
                }
                case 4: // Add
                case 5: // Sub
                case 6: // Mul
                case 7: // Div
                case 8: // Fmod
                case 9: // Min
                case 10: // Max
                case 18: // Atan2
                case 19: // Dot
                case 20: // Cross
                case 37: // AppendVector
                {
                    if (stack.Count < 2) return null;
                    var b = stack.Pop();
                    var a = stack.Pop();
                    var (cls, title) = op switch
                    {
                        4 => ("MaterialExpressionAdd", "Add"),
                        5 => ("MaterialExpressionSubtract", "Subtract"),
                        6 => ("MaterialExpressionMultiply", "Multiply"),
                        7 => ("MaterialExpressionDivide", "Divide"),
                        8 => ("MaterialExpressionFmod", "Fmod"),
                        9 => ("MaterialExpressionMin", "Min"),
                        10 => ("MaterialExpressionMax", "Max"),
                        18 => ("MaterialExpressionArctangent2", "Arctangent2"),
                        19 => ("MaterialExpressionDotProduct", "DotProduct"),
                        20 => ("MaterialExpressionCrossProduct", "CrossProduct"),
                        _ => ("MaterialExpressionAppendVector", "AppendVector")
                    };
                    stack.Push(emitBinary(cls, title, a, b, "A", "B", null));
                    break;
                }
                case 11: // Clamp
                {
                    if (stack.Count < 3) return null;
                    var maxP = stack.Pop();
                    var minP = stack.Pop();
                    var valP = stack.Pop();
                    stack.Push(emitTernary("MaterialExpressionClamp", "Clamp", valP, minP, maxP, "Input", "Min", "Max", null));
                    break;
                }
                case >= 12 and <= 35:
                {
                    if (stack.Count < 1) return null;
                    var inp = stack.Pop();
                    var (cls, title) = op switch
                    {
                        12 => ("MaterialExpressionSine", "Sine"),
                        13 => ("MaterialExpressionCosine", "Cosine"),
                        14 => ("MaterialExpressionTangent", "Tangent"),
                        15 => ("MaterialExpressionArcsine", "Arcsine"),
                        16 => ("MaterialExpressionArccosine", "Arccosine"),
                        17 => ("MaterialExpressionArctangent", "Arctangent"),
                        21 => ("MaterialExpressionSquareRoot", "SquareRoot"),
                        23 => ("MaterialExpressionLength", "Length"),
                        24 => ("MaterialExpressionNormalize", "Normalize"),
                        25 => ("MaterialExpressionSaturate", "Saturate"),
                        26 => ("MaterialExpressionAbs", "Abs"),
                        27 => ("MaterialExpressionFloor", "Floor"),
                        28 => ("MaterialExpressionCeil", "Ceil"),
                        29 => ("MaterialExpressionRound", "Round"),
                        30 => ("MaterialExpressionTruncate", "Truncate"),
                        31 => ("MaterialExpressionSign", "Sign"),
                        _ => ("MaterialExpressionFrac", "Frac")
                    };
                    stack.Push(emitUnary(cls, title, inp, "Input", null));
                    break;
                }
                case 36: // ComponentSwizzle
                {
                    if (ptr + 5 > end || stack.Count < 1) return null;
                    var numEl = code[ptr];
                    var iR = code[ptr + 1];
                    var iG = code[ptr + 2];
                    var iB = code[ptr + 3];
                    var iA = code[ptr + 4];
                    ptr += 5;
                    stack.Push(emitSwizzle(stack.Pop(), numEl, iR, iG, iB, iA));
                    break;
                }
                default:
                    return stack.Count > 0 ? stack.Pop() : null;
            }
        }
        return stack.Count > 0 ? stack.Pop() : null;
    }

    private static bool DetectUe427IndexedEncoding(FUniformExpressionSet set, ReadOnlySpan<byte> code)
    {
        foreach (var h in set.UniformVectorPreshaders.Concat(set.UniformScalarPreshaders))
        {
            if (h.OpcodeSize == 3 && h.OpcodeOffset + 3 <= code.Length && (code[(int) h.OpcodeOffset] == 3 || code[(int) h.OpcodeOffset] == 4))
                return true;
            if (h.OpcodeSize == 8 && h.OpcodeOffset + 8 <= code.Length && (code[(int) h.OpcodeOffset] == 3 || code[(int) h.OpcodeOffset] == 4))
                return false;
        }
        return true;
    }

    private static UeMatPin? BuildUe427PreshaderAst(
        byte[] code,
        FName[] names,
        int offset,
        int size,
        bool useIndexParams,
        FUniformExpressionSet set,
        MaterialAsset material,
        Func<string, float, EMaterialParameterAssociation, int, UeMatPin> getScalarParam,
        Func<string, Vector4, EMaterialParameterAssociation, int, UeMatNode> getVectorParam,
        Func<float, string?, UeMatPin> createConstScalar,
        Func<Vector4, int, string?, UeMatPin> createConstVec,
        Func<string, string, UeMatPin, UeMatPin, string, string, string?, UeMatPin> emitBinary,
        Func<string, string, UeMatPin, UeMatPin, UeMatPin, string, string, string, string?, UeMatPin> emitTernary,
        Func<string, string, UeMatPin, string, string?, UeMatPin> emitUnary,
        Func<UeMatPin, int, byte, byte, byte, byte, UeMatPin> emitSwizzle)
    {
        var ptr = offset;
        var end = Math.Min(code.Length, offset + size);
        var stack = new Stack<(UeMatPin Pin, Vector4? ConstVal)>();

        while (ptr < end)
        {
            var op = code[ptr++];
            switch (op)
            {
                case 0: // Nop
                    break;
                case 1: // ConstantZero
                    stack.Push((createConstScalar(0f, null), Vector4.Zero));
                    break;
                case 2: // Constant (float4)
                    if (ptr + 16 > end) return stack.Count > 0 ? stack.Pop().Pin : null;
                    var cx = BinaryPrimitives.ReadSingleLittleEndian(code.AsSpan(ptr));
                    var cy = BinaryPrimitives.ReadSingleLittleEndian(code.AsSpan(ptr + 4));
                    var cz = BinaryPrimitives.ReadSingleLittleEndian(code.AsSpan(ptr + 8));
                    var cw = BinaryPrimitives.ReadSingleLittleEndian(code.AsSpan(ptr + 12));
                    ptr += 16;
                    var cv = new Vector4(cx, cy, cz, cw);
                    stack.Push((createConstVec(cv, 4, null), cv));
                    break;
                case 3: // ScalarParameter
                {
                    string pName;
                    var assoc = EMaterialParameterAssociation.GlobalParameter;
                    var assocIdx = -1;
                    var val = 0f;
                    if (useIndexParams)
                    {
                        if (ptr + 2 > end) return null;
                        var idx = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(ptr));
                        ptr += 2;
                        if (idx < set.UniformScalarParameters.Length)
                        {
                            var sp = set.UniformScalarParameters[idx];
                            pName = sp.ParameterInfo?.Name.Text ?? sp.ParameterName ?? $"Scalar_{idx}";
                            assoc = sp.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
                            assocIdx = sp.ParameterInfo?.Index ?? -1;
                            val = material.GetNativeScalar(sp);
                        }
                        else pName = $"Scalar_{idx}";
                    }
                    else
                    {
                        if (ptr + 7 > end) return null;
                        var nameIdx = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(ptr));
                        assocIdx = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(ptr + 2));
                        assoc = (EMaterialParameterAssociation) code[ptr + 6];
                        ptr += 7;
                        pName = nameIdx < names.Length ? names[nameIdx].Text : $"Scalar_{nameIdx}";
                        material.TryGetScalar(out val, pName);
                    }
                    stack.Push((getScalarParam(pName, val, assoc, assocIdx), null));
                    break;
                }
                case 4: // VectorParameter
                {
                    string pName;
                    var assoc = EMaterialParameterAssociation.GlobalParameter;
                    var assocIdx = -1;
                    var val = Vector4.One;
                    if (useIndexParams)
                    {
                        if (ptr + 2 > end) return null;
                        var idx = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(ptr));
                        ptr += 2;
                        if (idx < set.UniformVectorParameters.Length)
                        {
                            var vp = set.UniformVectorParameters[idx];
                            pName = vp.ParameterInfo?.Name.Text ?? vp.ParameterName ?? $"Vector_{idx}";
                            assoc = vp.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
                            assocIdx = vp.ParameterInfo?.Index ?? -1;
                            val = material.GetNativeVector(vp);
                        }
                        else pName = $"Vector_{idx}";
                    }
                    else
                    {
                        if (ptr + 7 > end) return null;
                        var nameIdx = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(ptr));
                        assocIdx = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(ptr + 2));
                        assoc = (EMaterialParameterAssociation) code[ptr + 6];
                        ptr += 7;
                        pName = nameIdx < names.Length ? names[nameIdx].Text : $"Vector_{nameIdx}";
                        if (material.TryGetVector(out var lc, pName))
                            val = new Vector4(lc.R, lc.G, lc.B, lc.A);
                    }
                    var vNode = getVectorParam(pName, val, assoc, assocIdx);
                    stack.Push((vNode.OutputPins[0], null));
                    break;
                }
                case 5: // Add
                case 6: // Sub
                case 7: // Mul
                case 8: // Div
                case 9: // Fmod
                case 10: // Min
                case 11: // Max
                case 19: // Atan2
                {
                    if (stack.Count < 2) return null;
                    var b = stack.Pop();
                    var a = stack.Pop();
                    // Fold x * (2*PI) before Sin/Cos
                    if (op == 7 && b.ConstVal is { } bConst && MathF.Abs(bConst.X - 6.2831853f) < 0.01f && ptr < end && (code[ptr] == 13 || code[ptr] == 14))
                    {
                        stack.Push((a.Pin, null));
                        break;
                    }
                    var (cls, title) = op switch
                    {
                        5 => ("MaterialExpressionAdd", "Add"),
                        6 => ("MaterialExpressionSubtract", "Subtract"),
                        7 => ("MaterialExpressionMultiply", "Multiply"),
                        8 => ("MaterialExpressionDivide", "Divide"),
                        9 => ("MaterialExpressionFmod", "Fmod"),
                        10 => ("MaterialExpressionMin", "Min"),
                        11 => ("MaterialExpressionMax", "Max"),
                        _ => ("MaterialExpressionArctangent2", "Arctangent2")
                    };
                    stack.Push((emitBinary(cls, title, a.Pin, b.Pin, "A", "B", null), null));
                    break;
                }
                case 12: // Clamp
                {
                    if (stack.Count < 3) return null;
                    var maxP = stack.Pop();
                    var minP = stack.Pop();
                    var valP = stack.Pop();
                    stack.Push((emitTernary("MaterialExpressionClamp", "Clamp", valP.Pin, minP.Pin, maxP.Pin, "Input", "Min", "Max", null), null));
                    break;
                }
                case 13: // Sin
                case 14: // Cos
                case 15: // Tan
                case 16: // Asin
                case 17: // Acos
                case 18: // Atan
                case 22: // Sqrt
                case 23: // Length
                case 24: // Saturate
                case 25: // Abs
                case 26: // Floor
                case 27: // Ceil
                case 28: // Round
                case 29: // Trunc
                case 30: // Sign
                case 31: // Frac
                case 32: // Fractional
                case 33: // Log2
                case 34: // Log10
                {
                    if (stack.Count < 1) return null;
                    var inp = stack.Pop();
                    var (cls, title) = op switch
                    {
                        13 => ("MaterialExpressionSine", "Sine"),
                        14 => ("MaterialExpressionCosine", "Cosine"),
                        15 => ("MaterialExpressionTangent", "Tangent"),
                        16 => ("MaterialExpressionArcsine", "Arcsine"),
                        17 => ("MaterialExpressionArccosine", "Arccosine"),
                        18 => ("MaterialExpressionArctangent", "Arctangent"),
                        22 => ("MaterialExpressionSquareRoot", "SquareRoot"),
                        23 => ("MaterialExpressionLength", "Length"),
                        24 => ("MaterialExpressionSaturate", "Saturate"),
                        25 => ("MaterialExpressionAbs", "Abs"),
                        26 => ("MaterialExpressionFloor", "Floor"),
                        27 => ("MaterialExpressionCeil", "Ceil"),
                        28 => ("MaterialExpressionRound", "Round"),
                        29 => ("MaterialExpressionTruncate", "Truncate"),
                        30 => ("MaterialExpressionSign", "Sign"),
                        _ => ("MaterialExpressionFrac", "Frac")
                    };
                    stack.Push((emitUnary(cls, title, inp.Pin, "Input", null), null));
                    break;
                }
                case 20: // Dot
                case 21: // Cross
                {
                    if (ptr + 1 > end || stack.Count < 2) return null;
                    ptr += 1;
                    var b = stack.Pop();
                    var a = stack.Pop();
                    var (cls, title) = op == 20
                        ? ("MaterialExpressionDotProduct", "DotProduct")
                        : ("MaterialExpressionCrossProduct", "CrossProduct");
                    stack.Push((emitBinary(cls, title, a.Pin, b.Pin, "A", "B", null), null));
                    break;
                }
                case 35: // ComponentSwizzle
                {
                    if (ptr + 5 > end || stack.Count < 1) return null;
                    var numEl = code[ptr];
                    var iR = code[ptr + 1];
                    var iG = code[ptr + 2];
                    var iB = code[ptr + 3];
                    var iA = code[ptr + 4];
                    ptr += 5;
                    var src = stack.Pop();
                    stack.Push((emitSwizzle(src.Pin, numEl, iR, iG, iB, iA), src.ConstVal));
                    break;
                }
                case 36: // AppendVector
                {
                    if (ptr + 1 > end || stack.Count < 2) return null;
                    ptr += 1;
                    var b = stack.Pop();
                    var a = stack.Pop();
                    stack.Push((emitBinary("MaterialExpressionAppendVector", "AppendVector", a.Pin, b.Pin, "A", "B", null), null));
                    break;
                }
                case 37: // TextureSize
                case 38: // TexelSize
                    ptr += Math.Min(11, end - ptr);
                    stack.Push((createConstVec(new Vector4(1024f, 1024f, 0f, 0f), 2, null), null));
                    break;
                case 39:
                case 40:
                    ptr += Math.Min(22, end - ptr);
                    stack.Push((createConstVec(Vector4.UnitX, 2, null), null));
                    break;
                case 41:
                    ptr += Math.Min(15, end - ptr);
                    stack.Push((createConstVec(Vector4.One, 3, null), null));
                    break;
                default:
                    return stack.Count > 0 ? stack.Pop().Pin : null;
            }
        }

        return stack.Count > 0 ? stack.Pop().Pin : null;
    }

    private static void DecompilePixelShaderToRoot(
        NativeShaderEntry ps,
        UeMatGraph graph,
        Dictionary<int, UeMatPin> cbMatPins,
        Dictionary<int, UeMatNode> textureSlotNodes,
        Dictionary<string, UeMatNode> textureParamNodes,
        HashSet<string> connectedRootPins,
        Func<string, string, Vector4, string?, UeMatNode> createNode,
        Func<int, int, int, int, UeMatPin> getMpcNode,
        Func<float, string?, UeMatPin> createConstScalar,
        Func<Vector4, int, string?, UeMatPin> createConstVec,
        Func<string, UTexture?, EMaterialParameterAssociation, int, UeMatNode> createTexNode,
        Func<string, string, UeMatPin, UeMatPin, string, string, string?, UeMatPin> emitBinary,
        Func<string, string, UeMatPin, UeMatPin, UeMatPin, string, string, string, string?, UeMatPin> emitTernary,
        Func<string, string, UeMatPin, string, string?, UeMatPin> emitUnary,
        Func<UeMatPin, int, byte, byte, byte, byte, UeMatPin> emitSwizzle,
        Func<UeMatPin, UeMatPin, UeMatPin> emitFlattenNormal,
        string? defaultFuncGroup)
    {
        if (ps.Code is null || ps.Container is not { } psContainer)
            return;
        string disasm;
        try
        {
            disasm = ps.Disassembly ??= D3D11NativeShaderDevice.Disassemble(ps) ?? "";
            if (string.IsNullOrWhiteSpace(disasm))
                return;
        }
        catch
        {
            return;
        }

        var info = UnrealD3D11ShaderInfo.Parse(ps.Code!, psContainer);
        var matCbSlot = -1;
        var viewCbSlot = -1;
        var mpcCbSlots = new Dictionary<int, int>(); // cbSlot -> collectionIndex
        for (var s = 0; s < info.UniformBufferNames.Count; s++)
        {
            var ubn = info.UniformBufferNames[s];
            if (ubn == "Material")
                matCbSlot = s;
            else if (ubn == "View")
                viewCbSlot = s;
            else if (ubn.StartsWith("MaterialCollection", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(ubn.AsSpan("MaterialCollection".Length), out var colIdx))
            {
                mpcCbSlots[s] = colIdx;
            }
        }

        var texRegToMatResource = new Dictionary<int, int>();
        foreach (var t in info.Textures.Concat(info.ShaderResourceViews))
        {
            if (t.UniformBuffer >= 0 && t.UniformBuffer < info.UniformBufferNames.Count &&
                info.UniformBufferNames[t.UniformBuffer] == "Material")
            {
                texRegToMatResource[t.Register] = t.Resource;
            }
        }

        var regs = new Dictionary<string, UeMatPin>(StringComparer.OrdinalIgnoreCase);
        var regDiffPairs = new Dictionary<string, (UeMatPin A, UeMatPin B)>(StringComparer.OrdinalIgnoreCase);
        UeMatPin? texCoord0Pin = null;
        UeMatPin? timePin = null;
        UeMatPin? worldPosPin = null;

        UeMatPin GetTexCoord0()
        {
            if (texCoord0Pin is not null) return texCoord0Pin;
            var n = createNode("MaterialExpressionTextureCoordinate", "TexCoord[0]", new Vector4(0.52f, 0.18f, 0.18f, 1f), null);
            texCoord0Pin = n.AddOutput("Output", "", "", friendlyName: " ");
            return texCoord0Pin;
        }

        UeMatPin GetTime()
        {
            if (timePin is not null) return timePin;
            var n = createNode("MaterialExpressionTime", "Time", new Vector4(0.45f, 0.25f, 0.20f, 1f), defaultFuncGroup);
            timePin = n.AddOutput("Output", "", "", friendlyName: " ");
            return timePin;
        }

        UeMatPin GetWorldPos()
        {
            if (worldPosPin is not null) return worldPosPin;
            var n = createNode("MaterialExpressionWorldPosition", "WorldPosition", new Vector4(0.20f, 0.42f, 0.35f, 1f), defaultFuncGroup);
            worldPosPin = n.AddOutput("XYZ", "", "");
            return worldPosPin;
        }

        UeMatPin? ResolveOperand(string token)
        {
            token = token.Trim();
            if (token.StartsWith('-'))
                token = token[1..].Trim();
            if (token.StartsWith('|') && token.EndsWith('|'))
                token = token[1..^1].Trim();

            if (token.StartsWith("l(", StringComparison.OrdinalIgnoreCase) && token.EndsWith(')'))
            {
                var inner = token[2..^1];
                var parts = inner.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length == 1 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var f1))
                    return createConstScalar(f1, null);
                if (parts.Length >= 3 &&
                    float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var fx) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var fy) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var fz))
                {
                    var fw = parts.Length >= 4 && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var wv) ? wv : 1f;
                    return createConstVec(new Vector4(fx, fy, fz, fw), 3, null);
                }
            }

            // cbN[idx].swiz
            var cbMatch = Regex.Match(token, @"^cb(\d+)\[(\d+)\](?:\.([xyzwrgba]+))?$", RegexOptions.IgnoreCase);
            if (cbMatch.Success)
            {
                var cbSlot = int.Parse(cbMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                var regIdx = int.Parse(cbMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                var swiz = cbMatch.Groups[3].Success ? cbMatch.Groups[3].Value.ToLowerInvariant() : "xyzw";
                var compIdx = swiz.Length > 0 ? SwizCharToIndex(swiz[0]) : 0;

                if (cbSlot == matCbSlot)
                {
                    if (cbMatPins.TryGetValue(regIdx * 4 + compIdx, out var matPin))
                        return matPin;
                    if (cbMatPins.TryGetValue(regIdx * 4, out var matVecPin))
                        return matVecPin;
                }
                else if (mpcCbSlots.TryGetValue(cbSlot, out var colIdx))
                {
                    var distinctComps = swiz.Distinct().Count();
                    return getMpcNode(colIdx, regIdx, compIdx, distinctComps);
                }
                else if (cbSlot == viewCbSlot)
                {
                    if (regIdx == 142) return GetTime();
                    if (regIdx == 70) return GetWorldPos();
                }
                return null;
            }

            if (token.StartsWith("v2.", StringComparison.OrdinalIgnoreCase))
                return GetTexCoord0();

            var regBase = token.Split('.')[0];
            var regSwiz = token.Contains('.') ? token[(token.IndexOf('.') + 1)..].ToLowerInvariant() : "";
            if (regSwiz.Length == 1 && regs.TryGetValue($"{regBase}.{regSwiz[0]}", out var exactComp))
                return exactComp;
            if (regs.TryGetValue(regBase, out var basePin))
                return basePin;
            return null;
        }

        void SetDest(string destToken, UeMatPin pin)
        {
            destToken = destToken.Trim();
            var parts = destToken.Split('.');
            var regBase = parts[0];
            regs[regBase] = pin;
            if (parts.Length > 1)
            {
                foreach (var c in parts[1].ToLowerInvariant())
                    regs[$"{regBase}.{c}"] = pin;
            }
        }

        void ConnectRoot(string rootPinName, UeMatPin? srcPin)
        {
            if (srcPin is null || connectedRootPins.Contains(rootPinName))
                return;
            var dstPin = graph.RootNode.InputPins.FirstOrDefault(p => p.PinName.Equals(rootPinName, StringComparison.OrdinalIgnoreCase));
            if (dstPin is not null)
            {
                UeMatGraph.Connect(srcPin, dstPin);
                connectedRootPins.Add(rootPinName);
            }
        }

        var lines = disasm.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("//") || line.StartsWith("dcl_") || line.StartsWith("ps_"))
                continue;

            // Detect View override hooks in UE BasePassPixelShader:
            // cb0[138] = NormalOverrideParameter: mad r1.xyz, cb0[138].wwww, <NormalExpr>, cb0[138].xyzx
            if (viewCbSlot >= 0 && line.Contains($"cb{viewCbSlot}[138]", StringComparison.OrdinalIgnoreCase) && line.StartsWith("mad", StringComparison.OrdinalIgnoreCase))
            {
                var ops = SplitOperands(line);
                if (ops.Length >= 4)
                {
                    var normOp = ops[1].Contains($"cb{viewCbSlot}[138]") ? ops[2] : ops[1];
                    if (ResolveOperand(normOp) is { } normPin)
                        ConnectRoot("Normal", normPin);
                }
                continue;
            }

            // cb0[139] = RoughnessOverrideParameter: mad o2.z, <RoughnessExpr>, cb0[139].y, cb0[139].x
            if (viewCbSlot >= 0 && line.Contains($"cb{viewCbSlot}[139]", StringComparison.OrdinalIgnoreCase) && line.StartsWith("mad", StringComparison.OrdinalIgnoreCase))
            {
                var ops = SplitOperands(line);
                if (ops.Length >= 4)
                {
                    var roughOp = ops[1].Contains($"cb{viewCbSlot}[139]") ? ops[2] : ops[1];
                    if (ResolveOperand(roughOp) is { } roughPin)
                        ConnectRoot("Roughness", roughPin);
                }
                continue;
            }

            // sample / sample_b / sample_l
            if (line.StartsWith("sample", StringComparison.OrdinalIgnoreCase))
            {
                var ops = SplitOperands(line);
                if (ops.Length >= 4)
                {
                    var dst = ops[0];
                    var uvOp = ops[1];
                    var texOp = ops[2]; // e.g. t1.xyzw or t1.yzwx
                    var tMatch = Regex.Match(texOp, @"^t(\d+)(?:\.([xyzwrgba]+))?$", RegexOptions.IgnoreCase);
                    if (tMatch.Success)
                    {
                        var tReg = int.Parse(tMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                        var tSwiz = tMatch.Groups[2].Success ? tMatch.Groups[2].Value.ToLowerInvariant() : "xyzw";
                        UeMatNode? texNode = null;
                        if (texRegToMatResource.TryGetValue(tReg, out var matRes) && textureSlotNodes.TryGetValue(matRes, out var foundNode))
                            texNode = foundNode;
                        texNode ??= textureSlotNodes.Values.FirstOrDefault() ?? createTexNode($"Texture_{tReg}", null, EMaterialParameterAssociation.GlobalParameter, -1);

                        if (ResolveOperand(uvOp) is { } uvPin && uvPin != texCoord0Pin && texNode.InputPins.Count > 0 && texNode.InputPins[0].LinkedTo.Count == 0)
                            UeMatGraph.Connect(uvPin, texNode.InputPins[0]);

                        SetDest(dst, texNode.OutputPins[0]); // RGB
                        var dstBase = dst.Split('.')[0];
                        var dstMask = dst.Contains('.') ? dst[(dst.IndexOf('.') + 1)..].ToLowerInvariant() : "xyzw";
                        for (var c = 0; c < dstMask.Length && c < tSwiz.Length; c++)
                        {
                            var srcComp = SwizCharToIndex(tSwiz[c]);
                            if (srcComp >= 0 && srcComp < 4 && 1 + srcComp < texNode.OutputPins.Count)
                                regs[$"{dstBase}.{dstMask[c]}"] = texNode.OutputPins[1 + srcComp];
                        }
                    }
                }
                continue;
            }

            // Direct GBuffer output writes:
            // o3.xyz = Base Color, o2.x = Metallic, o2.y = Specular, o2.z = Roughness, o0.xyz = Emissive Color
            if (line.StartsWith("mov", StringComparison.OrdinalIgnoreCase) || line.StartsWith("mul", StringComparison.OrdinalIgnoreCase) || line.StartsWith("mad", StringComparison.OrdinalIgnoreCase))
            {
                var ops = SplitOperands(line);
                if (ops.Length >= 2)
                {
                    var dst = ops[0].ToLowerInvariant();
                    if (dst == "o3.xyz" && ResolveOperand(ops[1]) is { } bcPin)
                    {
                        ConnectRoot("Base Color", bcPin);
                        continue;
                    }
                    if (dst == "o2.x" && ResolveOperand(ops[1]) is { } metPin)
                    {
                        ConnectRoot("Metallic", metPin);
                        continue;
                    }
                    if (dst == "o2.y" && ResolveOperand(ops[1]) is { } specPin)
                    {
                        ConnectRoot("Specular", specPin);
                        continue;
                    }
                    if (dst == "o2.z" && ResolveOperand(ops[1]) is { } rghPin)
                    {
                        ConnectRoot("Roughness", rghPin);
                        continue;
                    }
                    if (dst == "o0.xyz" && ResolveOperand(ops[1]) is { } emPin)
                    {
                        ConnectRoot("Emissive Color", emPin);
                        continue;
                    }
                }
            }

            // Symbolic arithmetic tracking for user shader instructions (before GBuffer packing)
            var instr = line.Split(' ')[0].ToLowerInvariant();
            var baseInstr = instr.Replace("_sat", "");
            var operands = SplitOperands(line);
            if (operands.Length < 2)
                continue;

            if (baseInstr == "mov" && operands.Length >= 2)
            {
                if (ResolveOperand(operands[1]) is { } src)
                {
                    var outPin = instr.EndsWith("_sat", StringComparison.Ordinal) ? emitUnary("MaterialExpressionSaturate", "Saturate", src, "Input", null) : src;
                    SetDest(operands[0], outPin);
                }
            }
            else if (baseInstr is "add" or "mul" or "div" or "min" or "max" && operands.Length >= 3)
            {
                var op1Raw = operands[1].Trim();
                var op2Raw = operands[2].Trim();
                var a = ResolveOperand(op1Raw);
                var b = ResolveOperand(op2Raw);
                if (a is not null && b is not null && !ReferenceEquals(a, b))
                {
                    if (baseInstr == "add" && op2Raw.StartsWith('-'))
                    {
                        regDiffPairs[operands[0].Split('.')[0]] = (a, b);
                        SetDest(operands[0], emitBinary("MaterialExpressionSubtract", "Subtract", a, b, "A", "B", null));
                    }
                    else if (baseInstr == "add" && op1Raw.StartsWith('-'))
                    {
                        regDiffPairs[operands[0].Split('.')[0]] = (b, a);
                        SetDest(operands[0], emitBinary("MaterialExpressionSubtract", "Subtract", b, a, "A", "B", null));
                    }
                    else
                    {
                        var (cls, title) = baseInstr switch
                        {
                            "add" => ("MaterialExpressionAdd", "Add"),
                            "mul" => ("MaterialExpressionMultiply", "Multiply"),
                            "div" => ("MaterialExpressionDivide", "Divide"),
                            "min" => ("MaterialExpressionMin", "Min"),
                            _ => ("MaterialExpressionMax", "Max")
                        };
                        SetDest(operands[0], emitBinary(cls, title, a, b, "A", "B", null));
                    }
                }
            }
            else if (baseInstr == "mad" && operands.Length >= 4)
            {
                // Detect Lerp idiom: mad dst, alpha, (B - A), A
                var op1 = operands[1].Trim();
                var op2 = operands[2].Trim();
                var op3 = operands[3].Trim();
                var p1 = ResolveOperand(op1);
                var p2 = ResolveOperand(op2);
                var p3 = ResolveOperand(op3);

                var op2Base = op2.Split('.')[0];
                if (p1 is not null && p3 is not null && regDiffPairs.TryGetValue(op2Base, out var diff) && ReferenceEquals(diff.B, p3))
                {
                    SetDest(operands[0], emitTernary("MaterialExpressionLinearInterpolate", "Lerp", diff.B, diff.A, p1, "A", "B", "Alpha", null));
                }
                else if (p1 is not null && p2 is not null && p3 is not null &&
                         !op1.Contains($"cb{viewCbSlot}") && !op2.Contains($"cb{viewCbSlot}") && !op3.Contains($"cb{viewCbSlot}"))
                {
                    var mulPin = emitBinary("MaterialExpressionMultiply", "Multiply", p1, p2, "A", "B", null);
                    SetDest(operands[0], emitBinary("MaterialExpressionAdd", "Add", mulPin, p3, "A", "B", null));
                }
            }
        }
    }

    private static void WireFallbackSemanticPins(
        UeMatGraph graph,
        MaterialAsset material,
        List<FunctionResolvedInfo> functions,
        Dictionary<string, UeMatNode> textureParamNodes,
        Dictionary<int, UeMatNode> textureSlotNodes,
        Dictionary<string, UeMatPin> scalarParamNodes,
        Dictionary<string, UeMatNode> vectorParamNodes,
        HashSet<string> connectedRootPins,
        Func<string, UTexture?, EMaterialParameterAssociation, int, UeMatNode> createTexNode,
        Func<string, float, EMaterialParameterAssociation, int, UeMatPin> getScalarParam,
        Func<string, Vector4, EMaterialParameterAssociation, int, UeMatNode> getVectorParam,
        Func<string, string, UeMatPin, UeMatPin, string, string, string?, UeMatPin> emitBinary,
        Func<UeMatPin, UeMatPin, UeMatPin> emitFlattenNormal)
    {
        void ConnectRoot(string rootPinName, UeMatPin? srcPin)
        {
            if (srcPin is null || connectedRootPins.Contains(rootPinName))
                return;
            var dstPin = graph.RootNode.InputPins.FirstOrDefault(p => p.PinName.Equals(rootPinName, StringComparison.OrdinalIgnoreCase));
            if (dstPin is not null)
            {
                UeMatGraph.Connect(srcPin, dstPin);
                connectedRootPins.Add(rootPinName);
            }
        }

        // Base Color fallback
        if (!connectedRootPins.Contains("Base Color"))
        {
            var diffTexNode = textureParamNodes.FirstOrDefault(kv => kv.Key.Contains("Base", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("Diff", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("Albedo", StringComparison.OrdinalIgnoreCase)).Value;
            if (diffTexNode is null && material.TryGetTexture(out var dTex, EMaterialTextureKind.Diffuse))
                diffTexNode = textureSlotNodes.Values.FirstOrDefault(n => ReferenceEquals(n.PreviewTexture, dTex)) ?? createTexNode(dTex.Name, dTex, EMaterialParameterAssociation.GlobalParameter, -1);

            var tintNode = vectorParamNodes.FirstOrDefault(kv => kv.Key.Contains("Base", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("Tint", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("Albedo", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("Color", StringComparison.OrdinalIgnoreCase)).Value;

            if (diffTexNode is not null && tintNode is not null)
                ConnectRoot("Base Color", emitBinary("MaterialExpressionMultiply", "Multiply", diffTexNode.OutputPins[0], tintNode.OutputPins[0], "A", "B", null));
            else if (diffTexNode is not null)
                ConnectRoot("Base Color", diffTexNode.OutputPins[0]);
            else if (tintNode is not null)
                ConnectRoot("Base Color", tintNode.OutputPins[0]);
        }

        // Normal fallback (with FlattenNormal if FlattenNormal is in FunctionInfos or Flatness scalar exists)
        if (!connectedRootPins.Contains("Normal"))
        {
            var normTexNode = textureParamNodes.FirstOrDefault(kv => kv.Key.Contains("Normal", StringComparison.OrdinalIgnoreCase)).Value;
            if (normTexNode is null && material.TryGetTexture(out var nTex, EMaterialTextureKind.Normal))
                normTexNode = textureSlotNodes.Values.FirstOrDefault(n => ReferenceEquals(n.PreviewTexture, nTex)) ?? createTexNode(nTex.Name, nTex, EMaterialParameterAssociation.GlobalParameter, -1);

            if (normTexNode is not null)
            {
                var flatPin = scalarParamNodes.FirstOrDefault(kv => kv.Key.Contains("Flatness", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("NormalStrength", StringComparison.OrdinalIgnoreCase)).Value;
                if (flatPin is not null || functions.Any(f => f.Name.Contains("FlattenNormal", StringComparison.OrdinalIgnoreCase)))
                {
                    flatPin ??= getScalarParam("Flatness (S)", 1.0f, EMaterialParameterAssociation.GlobalParameter, -1);
                    ConnectRoot("Normal", emitFlattenNormal(normTexNode.OutputPins[0], flatPin));
                }
                else
                {
                    ConnectRoot("Normal", normTexNode.OutputPins[0]);
                }
            }
        }

        // SpecularMasks / ORM / ARM fallback -> Ambient Occlusion (R), Roughness (G), Metallic (B) or Specular (R), Metallic (G), Roughness (B)
        var maskTexNode = textureParamNodes.FirstOrDefault(kv => kv.Key.Contains("ARM", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("ORM", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("SpecularMasks", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("MRS", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("Mask", StringComparison.OrdinalIgnoreCase)).Value;
        if (maskTexNode is null && material.TryGetTexture(out var sTex, EMaterialTextureKind.SpecularMasks))
            maskTexNode = textureSlotNodes.Values.FirstOrDefault(n => ReferenceEquals(n.PreviewTexture, sTex)) ?? createTexNode(sTex.Name, sTex, EMaterialParameterAssociation.GlobalParameter, -1);

        if (maskTexNode is not null && maskTexNode.OutputPins.Count >= 5)
        {
            var isArm = maskTexNode.Title.Contains("ARM", StringComparison.OrdinalIgnoreCase) || maskTexNode.Title.Contains("ORM", StringComparison.OrdinalIgnoreCase);
            if (isArm)
            {
                ConnectRoot("Ambient Occlusion", maskTexNode.OutputPins[1]); // R
                ConnectRoot("Roughness", maskTexNode.OutputPins[2]);         // G
                ConnectRoot("Metallic", maskTexNode.OutputPins[3]);          // B
            }
            else
            {
                ConnectRoot("Specular", maskTexNode.OutputPins[1]);          // R
                ConnectRoot("Metallic", maskTexNode.OutputPins[2]);          // G
                ConnectRoot("Roughness", maskTexNode.OutputPins[3]);         // B
            }
        }

        // Scalar Metallic / Specular / Roughness fallbacks
        if (!connectedRootPins.Contains("Metallic"))
        {
            var p = scalarParamNodes.FirstOrDefault(kv => kv.Key.Contains("Metallic", StringComparison.OrdinalIgnoreCase)).Value;
            if (p is not null) ConnectRoot("Metallic", p);
        }
        if (!connectedRootPins.Contains("Specular"))
        {
            var p = scalarParamNodes.FirstOrDefault(kv => kv.Key.Contains("Spec", StringComparison.OrdinalIgnoreCase)).Value;
            if (p is not null) ConnectRoot("Specular", p);
        }
        if (!connectedRootPins.Contains("Roughness"))
        {
            var p = scalarParamNodes.FirstOrDefault(kv => kv.Key.Contains("Rough", StringComparison.OrdinalIgnoreCase)).Value;
            if (p is not null) ConnectRoot("Roughness", p);
        }

        // Emissive fallback
        if (!connectedRootPins.Contains("Emissive Color"))
        {
            var emTex = textureParamNodes.FirstOrDefault(kv => kv.Key.Contains("Emissive", StringComparison.OrdinalIgnoreCase)).Value;
            var emVec = vectorParamNodes.FirstOrDefault(kv => kv.Key.Contains("Emissive", StringComparison.OrdinalIgnoreCase)).Value;
            if (emTex is not null && emVec is not null)
                ConnectRoot("Emissive Color", emitBinary("MaterialExpressionMultiply", "Multiply", emTex.OutputPins[0], emVec.OutputPins[0], "A", "B", null));
            else if (emTex is not null)
                ConnectRoot("Emissive Color", emTex.OutputPins[0]);
            else if (emVec is not null)
                ConnectRoot("Emissive Color", emVec.OutputPins[0]);
        }
    }

    private static void LayoutGraphAndBuildFunctionComments(UeMatGraph graph, List<FunctionResolvedInfo> functions)
    {
        graph.RootNode.PosX = 640;
        graph.RootNode.PosY = -180;

        // 1. Find all nodes reachable backwards from RootNode
        var reachable = new HashSet<UeMatNode>(ReferenceEqualityComparer.Instance);
        var depthMap = new Dictionary<UeMatNode, int>(ReferenceEqualityComparer.Instance);
        var primaryLane = new Dictionary<UeMatNode, int>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(UeMatNode Node, int Depth, int Lane)>();

        for (var pinIdx = 0; pinIdx < graph.RootNode.InputPins.Count; pinIdx++)
        {
            var inPin = graph.RootNode.InputPins[pinIdx];
            foreach (var (srcNode, _) in inPin.LinkedTo)
            {
                reachable.Add(srcNode);
                if (!primaryLane.ContainsKey(srcNode))
                    primaryLane[srcNode] = pinIdx;
                if (!depthMap.TryGetValue(srcNode, out var cur) || cur < 1)
                {
                    depthMap[srcNode] = 1;
                    queue.Enqueue((srcNode, 1, primaryLane[srcNode]));
                }
            }
        }

        while (queue.Count > 0)
        {
            var (node, d, lane) = queue.Dequeue();
            if (d > 12) continue;
            foreach (var inPin in node.InputPins)
            {
                foreach (var (srcNode, _) in inPin.LinkedTo)
                {
                    reachable.Add(srcNode);
                    if (!primaryLane.ContainsKey(srcNode))
                        primaryLane[srcNode] = lane;
                    if (!depthMap.TryGetValue(srcNode, out var cur) || cur < d + 1)
                    {
                        depthMap[srcNode] = d + 1;
                        queue.Enqueue((srcNode, d + 1, primaryLane[srcNode]));
                    }
                }
            }
        }

        // 2. Prune unreachable intermediate math/swizzle/constant nodes so only connected nodes + real user parameters remain
        static bool IsNamedParameterNode(UeMatNode n) =>
            n.ExpressionClass.Contains("Parameter", StringComparison.OrdinalIgnoreCase) ||
            n.ExpressionClass.Contains("TextureSample", StringComparison.OrdinalIgnoreCase) ||
            n.ExpressionClass.Contains("FunctionCall", StringComparison.OrdinalIgnoreCase);

        var removedSet = new HashSet<UeMatNode>(ReferenceEqualityComparer.Instance);
        foreach (var n in graph.Nodes)
        {
            if (!reachable.Contains(n) && !IsNamedParameterNode(n))
                removedSet.Add(n);
        }

        if (removedSet.Count > 0)
        {
            graph.Nodes.RemoveAll(n => removedSet.Contains(n));
            foreach (var n in graph.Nodes)
            {
                foreach (var p in n.InputPins)
                    p.LinkedTo.RemoveAll(l => removedSet.Contains(l.Node));
                foreach (var p in n.OutputPins)
                    p.LinkedTo.RemoveAll(l => removedSet.Contains(l.Node));
            }
        }

        // Re-number node names cleanly
        for (var i = 0; i < graph.Nodes.Count; i++)
            graph.Nodes[i].NodeName = $"MaterialGraphNode_{i}";

        static int EstimateNodeHeight(UeMatNode n)
        {
            var visIn = n.InputPins.Count(p => !p.IsAdvancedView);
            var rows = Math.Max(visIn, n.OutputPins.Count);
            var extra = n.PreviewTexture is not null ? 64 : (n.PreviewColor is not null ? 30 : 16);
            return Math.Max(92, 42 + rows * 20 + extra);
        }

        // 3. Hierarchical Swimlane Tree Layout for connected nodes (grouped by Root Pin branch, then depth)
        var connectedNodes = graph.Nodes.Where(n => reachable.Contains(n)).ToList();
        var unconnectedParams = graph.Nodes.Where(n => !reachable.Contains(n)).ToList();

        var placed = new HashSet<UeMatNode>(ReferenceEqualityComparer.Instance);
        var laneGroups = connectedNodes
            .GroupBy(n => primaryLane.GetValueOrDefault(n, 0))
            .OrderBy(g => g.Key)
            .ToList();

        var currentLaneTopY = -360;
        foreach (var laneGrp in laneGroups)
        {
            var laneNodes = laneGrp.ToList();
            var byDepth = laneNodes
                .GroupBy(n => depthMap.GetValueOrDefault(n, 1))
                .OrderBy(g => g.Key)
                .ToList();

            var laneMaxBottomY = currentLaneTopY;
            foreach (var depthCol in byDepth)
            {
                var d = depthCol.Key;
                var colNodes = depthCol.ToList();
                // If a single depth column inside one branch has > 2 nodes, wrap into 2D sub-columns instead of a tall vertical stack!
                const int maxRowsPerSubCol = 2;
                for (var idx = 0; idx < colNodes.Count; idx++)
                {
                    var n = colNodes[idx];
                    var subCol = idx / maxRowsPerSubCol;
                    var rowInSubCol = idx % maxRowsPerSubCol;
                    var x = 640 - d * 320 - subCol * 250;

                    var y = currentLaneTopY;
                    for (var r = 0; r < rowInSubCol; r++)
                    {
                        var prevNode = colNodes[subCol * maxRowsPerSubCol + r];
                        y = prevNode.PosY + EstimateNodeHeight(prevNode) + 28;
                    }

                    n.PosX = x;
                    n.PosY = y;
                    placed.Add(n);
                    laneMaxBottomY = Math.Max(laneMaxBottomY, y + EstimateNodeHeight(n));
                }
            }

            currentLaneTopY = laneMaxBottomY + 56;
        }

        // 4. Lay out any remaining unconnected parameter nodes in a compact 4-column 2D grid below the main graph
        if (unconnectedParams.Count > 0)
        {
            const int gridCols = 4;
            var gridStartX = -720;
            var gridStartY = Math.Max(currentLaneTopY + 60, 260);
            var rowY = gridStartY;
            var rowMaxH = 0;

            for (var i = 0; i < unconnectedParams.Count; i++)
            {
                var n = unconnectedParams[i];
                var col = i % gridCols;
                if (col == 0 && i > 0)
                {
                    rowY += rowMaxH + 32;
                    rowMaxH = 0;
                }
                n.PosX = gridStartX + col * 260;
                n.PosY = rowY;
                rowMaxH = Math.Max(rowMaxH, EstimateNodeHeight(n));
            }
        }

        // 5. Build MaterialGraphNode_Comment boxes for custom Material Functions & Material Layers
        graph.Comments.Clear();
        var customFuncGroups = graph.Nodes
            .Where(n => !string.IsNullOrWhiteSpace(n.FunctionGroup) && reachable.Contains(n))
            .GroupBy(n => n.FunctionGroup!, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var commentIdx = 0;
        Vector4[] palette =
        [
            new(0.15f, 0.24f, 0.36f, 1f),
            new(0.16f, 0.32f, 0.24f, 1f),
            new(0.34f, 0.22f, 0.16f, 1f),
            new(0.28f, 0.18f, 0.34f, 1f)
        ];

        foreach (var grp in customFuncGroups)
        {
            var members = grp.ToList();
            if (members.Count == 0) continue;
            var minX = members.Min(n => n.PosX) - 32;
            var minY = members.Min(n => n.PosY) - 46;
            var maxX = members.Max(n => n.PosX + 230) + 32;
            var maxY = members.Max(n => n.PosY + EstimateNodeHeight(n)) + 28;

            graph.Comments.Add(new UeMatComment
            {
                NodeName = $"MaterialGraphNode_Comment_{commentIdx}",
                ExpressionName = $"MaterialExpressionComment_{commentIdx}",
                Text = grp.Key,
                PosX = minX,
                PosY = minY,
                SizeX = Math.Max(300, maxX - minX),
                SizeY = Math.Max(180, maxY - minY),
                Color = palette[commentIdx % palette.Length]
            });
            commentIdx++;
        }

        var nonEngineFuncs = functions.Where(f => !f.IsEngine).ToList();
        if (graph.Comments.Count == 0 && nonEngineFuncs.Count > 0 && connectedNodes.Count > 0)
        {
            var funcLabel = string.Join(" / ", nonEngineFuncs.Select(f => f.Name));
            var minX = connectedNodes.Min(n => n.PosX) - 32;
            var minY = connectedNodes.Min(n => n.PosY) - 46;
            var maxX = connectedNodes.Max(n => n.PosX + 230) + 32;
            var maxY = connectedNodes.Max(n => n.PosY + EstimateNodeHeight(n)) + 28;
            graph.Comments.Add(new UeMatComment
            {
                NodeName = $"MaterialGraphNode_Comment_{commentIdx}",
                ExpressionName = $"MaterialExpressionComment_{commentIdx}",
                Text = $"Material Function: {funcLabel}",
                PosX = minX,
                PosY = minY,
                SizeX = Math.Max(340, maxX - minX),
                SizeY = Math.Max(200, maxY - minY),
                Color = palette[0]
            });
            commentIdx++;
        }

        if (unconnectedParams.Count > 0)
        {
            var minX = unconnectedParams.Min(n => n.PosX) - 32;
            var minY = unconnectedParams.Min(n => n.PosY) - 46;
            var maxX = unconnectedParams.Max(n => n.PosX + 230) + 32;
            var maxY = unconnectedParams.Max(n => n.PosY + EstimateNodeHeight(n)) + 28;
            graph.Comments.Add(new UeMatComment
            {
                NodeName = $"MaterialGraphNode_Comment_{commentIdx}",
                ExpressionName = $"MaterialExpressionComment_{commentIdx}",
                Text = "Additional Material Parameters",
                PosX = minX,
                PosY = minY,
                SizeX = Math.Max(340, maxX - minX),
                SizeY = Math.Max(180, maxY - minY),
                Color = palette[commentIdx % palette.Length]
            });
        }
    }

    public static UeMatNode CreateRootNode()
    {
        var root = new UeMatNode
        {
            NodeName = "MaterialGraphNode_Root_0",
            ExpressionClass = "/Script/UnrealEd.MaterialGraphNode_Root",
            ExpressionName = "MaterialGraphNode_Root_0",
            Title = "Main Material Node",
            HeaderColor = new Vector4(0.18f, 0.20f, 0.24f, 1f),
            PosX = 544,
            PosY = -144,
            IsRoot = true
        };

        root.AddInput("Base Color", "materialinput", "rgba", "(R=0.000000,G=0.000000,B=0.000000,A=1.000000)");
        root.AddInput("Metallic", "materialinput", "red", "0.0");
        root.AddInput("Specular", "materialinput", "red", "0.5");
        root.AddInput("Roughness", "materialinput", "red", "0.5");
        root.AddInput("Anisotropy", "materialinput", "");
        root.AddInput("Emissive Color", "materialinput", "rgba", "(R=0.000000,G=0.000000,B=0.000000,A=0.000000)");
        root.AddInput("Opacity", "materialinput", "red", "1.0");
        root.AddInput("Opacity Mask", "materialinput", "red", "1.0");
        root.AddInput("Normal", "materialinput", "rgb", "0.0,0.0,1.0");
        root.AddInput("Tangent", "materialinput", "rgb", "1.0,0.0,0.0");
        root.AddInput("World Position Offset", "materialinput", "");
        root.AddInput("Subsurface Color", "materialinput", "rgba", "(R=1.000000,G=1.000000,B=1.000000,A=0.000000)");
        root.AddInput("Custom Data 0", "materialinput", "red", "1.0");
        root.AddInput("Custom Data 1", "materialinput", "red", "0.1");
        root.AddInput("Ambient Occlusion", "materialinput", "red", "1.0");
        root.AddInput("Refraction (Disabled)", "materialinput", "red", "1.0");
        for (var uv = 0; uv < 8; uv++)
            root.AddInput($"Customized UV{uv}", "materialinput", "rg", "(X=0.0,Y=0.0)");
        root.AddInput("Pixel Depth Offset", "materialinput", "");
        root.AddInput("Shading Model", "materialinput", "");
        root.AddInput("Surface Thickness", "materialinput", "red", "0.01");
        root.AddInput("Displacement", "materialinput", "");
        root.AddInput("Material Attributes", "materialinput", "");
        root.AddInput("Front Material", "materialinput", "");

        return root;
    }

    /// <summary>
    /// Exports the <see cref="UeMatGraph"/> (or a selected subset of nodes) to 1:1 Unreal Engine 5 T3D clipboard format,
    /// ready to paste (<c>Ctrl+V</c>) directly into the UE5 Material Editor or BlueprintUE.com.
    /// </summary>
    public static string ExportToUeT3D(UeMatGraph graph, IReadOnlySet<UeMatNode>? selectedOnly = null)
    {
        var sb = new StringBuilder(16384);
        var includeAll = selectedOnly is null || selectedOnly.Count == 0;
        bool IsIncluded(UeMatNode n) => includeAll || selectedOnly!.Contains(n);

        // 1. Root Node
        if (IsIncluded(graph.RootNode))
        {
            var r = graph.RootNode;
            sb.AppendLine($"Begin Object Class=/Script/UnrealEd.MaterialGraphNode_Root Name=\"{r.NodeName}\" ExportPath=\"/Script/UnrealEd.MaterialGraphNode_Root'/Engine/Transient.PreviewMaterial_0:MaterialGraph_0.{r.NodeName}'\"");
            sb.AppendLine("   Material=\"/Script/UnrealEd.PreviewMaterial'/Engine/Transient.PreviewMaterial_0'\"");
            sb.AppendLine($"   NodePosX={r.PosX}");
            sb.AppendLine($"   NodePosY={r.PosY}");
            sb.AppendLine($"   NodeGuid={r.NodeGuid}");
            foreach (var pin in r.InputPins)
                AppendPinT3D(sb, pin, IsIncluded);
            sb.AppendLine("End Object");
        }

        // 2. Expression Nodes
        foreach (var n in graph.Nodes)
        {
            if (!IsIncluded(n))
                continue;

            sb.AppendLine($"Begin Object Class=/Script/UnrealEd.MaterialGraphNode Name=\"{n.NodeName}\" ExportPath=\"/Script/UnrealEd.MaterialGraphNode'/Engine/Transient.PreviewMaterial_0:MaterialGraph_0.{n.NodeName}'\"");
            sb.AppendLine($"   Begin Object Class={n.ExpressionClass} Name=\"{n.ExpressionName}\" ExportPath=\"{n.ExpressionClass}'/Engine/Transient.PreviewMaterial_0:MaterialGraph_0.{n.NodeName}.{n.ExpressionName}'\"");
            sb.AppendLine("   End Object");
            sb.AppendLine($"   Begin Object Name=\"{n.ExpressionName}\" ExportPath=\"{n.ExpressionClass}'/Engine/Transient.PreviewMaterial_0:MaterialGraph_0.{n.NodeName}.{n.ExpressionName}'\"");

            foreach (var (k, v) in n.ExpressionProperties)
                sb.AppendLine($"      {k}={v}");

            // Emit FunctionInputs / FunctionOutputs for MaterialFunctionCall nodes
            if (n.ExpressionClass.EndsWith("MaterialExpressionMaterialFunctionCall", StringComparison.OrdinalIgnoreCase))
            {
                for (var i = 0; i < n.InputPins.Count; i++)
                {
                    var p = n.InputPins[i];
                    var cleanInName = p.PinName.Contains(" (") ? p.PinName[..p.PinName.IndexOf(" (", StringComparison.Ordinal)] : p.PinName;
                    if (p.LinkedTo.Count > 0 && IsIncluded(p.LinkedTo[0].Node))
                    {
                        var src = p.LinkedTo[0].Node;
                        var maskPart = p.LinkedTo[0].Pin.PinName == "RGB" ? ",Mask=1,MaskR=1,MaskG=1,MaskB=1" : "";
                        sb.AppendLine($"      FunctionInputs({i})=(ExpressionInputId={UeMatNode.NewUeGuid()},Input=(Expression=\"{src.ExpressionClass}'{src.NodeName}.{src.ExpressionName}'\",InputName=\"{cleanInName}\"{maskPart}))");
                    }
                }
                for (var i = 0; i < n.OutputPins.Count; i++)
                {
                    var p = n.OutputPins[i];
                    sb.AppendLine($"      FunctionOutputs({i})=(ExpressionOutputId={UeMatNode.NewUeGuid()},Output=(OutputName=\"{p.PinName}\"))");
                }
            }

            sb.AppendLine($"      MaterialExpressionEditorX={n.PosX}");
            sb.AppendLine($"      MaterialExpressionEditorY={n.PosY}");
            sb.AppendLine($"      MaterialExpressionGuid={n.ExpressionGuid}");
            sb.AppendLine("      Material=\"/Script/UnrealEd.PreviewMaterial'/Engine/Transient.PreviewMaterial_0'\"");
            if (n.ExpressionClass.EndsWith("MaterialExpressionMaterialFunctionCall", StringComparison.OrdinalIgnoreCase) && n.OutputPins.Count > 0)
                sb.AppendLine($"      Outputs(0)=(OutputName=\"{n.OutputPins[0].PinName}\")");
            sb.AppendLine("   End Object");

            sb.AppendLine($"   MaterialExpression=\"{n.ExpressionClass}'{n.ExpressionName}'\"");
            sb.AppendLine($"   NodePosX={n.PosX}");
            sb.AppendLine($"   NodePosY={n.PosY}");
            if (n.HasAdvancedPins)
                sb.AppendLine("   AdvancedPinDisplay=Hidden");
            if (n.CanRename)
                sb.AppendLine("   bCanRenameNode=True");
            sb.AppendLine($"   NodeGuid={n.NodeGuid}");

            foreach (var pin in n.InputPins)
                AppendPinT3D(sb, pin, IsIncluded);
            foreach (var pin in n.OutputPins)
                AppendPinT3D(sb, pin, IsIncluded);

            sb.AppendLine("End Object");
        }

        // 3. Function Comment Nodes (MaterialGraphNode_Comment)
        if (includeAll)
        {
            foreach (var c in graph.Comments)
            {
                var colStr = $"(R={c.Color.X.ToString("0.000000", CultureInfo.InvariantCulture)},G={c.Color.Y.ToString("0.000000", CultureInfo.InvariantCulture)},B={c.Color.Z.ToString("0.000000", CultureInfo.InvariantCulture)},A=1.000000)";
                sb.AppendLine($"Begin Object Class=/Script/UnrealEd.MaterialGraphNode_Comment Name=\"{c.NodeName}\" ExportPath=\"/Script/UnrealEd.MaterialGraphNode_Comment'/Engine/Transient.PreviewMaterial_0:MaterialGraph_0.{c.NodeName}'\"");
                sb.AppendLine($"   Begin Object Class=/Script/Engine.MaterialExpressionComment Name=\"{c.ExpressionName}\" ExportPath=\"/Script/Engine.MaterialExpressionComment'/Engine/Transient.PreviewMaterial_0:MaterialGraph_0.{c.NodeName}.{c.ExpressionName}'\"");
                sb.AppendLine("   End Object");
                sb.AppendLine($"   Begin Object Name=\"{c.ExpressionName}\" ExportPath=\"/Script/Engine.MaterialExpressionComment'/Engine/Transient.PreviewMaterial_0:MaterialGraph_0.{c.NodeName}.{c.ExpressionName}'\"");
                sb.AppendLine($"      SizeX={c.SizeX}");
                sb.AppendLine($"      SizeY={c.SizeY}");
                sb.AppendLine($"      Text=\"{EscapeT3D(c.Text)}\"");
                sb.AppendLine($"      CommentColor={colStr}");
                sb.AppendLine($"      MaterialExpressionEditorX={c.PosX}");
                sb.AppendLine($"      MaterialExpressionEditorY={c.PosY}");
                sb.AppendLine($"      MaterialExpressionGuid={c.ExpressionGuid}");
                sb.AppendLine("      Material=\"/Script/UnrealEd.PreviewMaterial'/Engine/Transient.PreviewMaterial_0'\"");
                sb.AppendLine("   End Object");
                sb.AppendLine($"   MaterialExpressionComment=\"/Script/Engine.MaterialExpressionComment'{c.ExpressionName}'\"");
                sb.AppendLine($"   CommentColor={colStr}");
                sb.AppendLine($"   NodePosX={c.PosX}");
                sb.AppendLine($"   NodePosY={c.PosY}");
                sb.AppendLine($"   NodeWidth={c.SizeX}");
                sb.AppendLine($"   NodeHeight={c.SizeY}");
                sb.AppendLine($"   NodeComment=\"{EscapeT3D(c.Text)}\"");
                sb.AppendLine($"   NodeGuid={c.NodeGuid}");
                sb.AppendLine("End Object");
            }
        }

        return sb.ToString();
    }

    private static void AppendPinT3D(StringBuilder sb, UeMatPin pin, Func<UeMatNode, bool> isNodeIncluded)
    {
        sb.Append($"   CustomProperties Pin (PinId={pin.PinId},PinName=\"{EscapeT3D(pin.PinName)}\"");
        if (pin.FriendlyName is not null)
            sb.Append($",PinFriendlyName=NSLOCTEXT(\"MaterialGraphNode\", \"Space\", \"{pin.FriendlyName}\")");
        if (pin.IsOutput)
            sb.Append(",Direction=\"EGPD_Output\"");
        sb.Append($",PinType.PinCategory=\"{pin.PinCategory}\",PinType.PinSubCategory=\"{pin.PinSubCategory}\"");
        if (pin.PinSubCategoryObject == "None" || string.IsNullOrEmpty(pin.PinSubCategoryObject))
            sb.Append(",PinType.PinSubCategoryObject=None");
        else
            sb.Append($",PinType.PinSubCategoryObject=\"{pin.PinSubCategoryObject}\"");
        sb.Append(",PinType.PinSubCategoryMemberReference=(),PinType.PinValueType=(),PinType.ContainerType=None,PinType.bIsReference=False,PinType.bIsConst=False,PinType.bIsWeakPointer=False,PinType.bIsUObjectWrapper=False,PinType.bSerializeAsSinglePrecisionFloat=False");
        if (!string.IsNullOrEmpty(pin.DefaultValue))
            sb.Append($",DefaultValue=\"{EscapeT3D(pin.DefaultValue)}\"");

        var validLinks = pin.LinkedTo.Where(l => isNodeIncluded(l.Node)).ToList();
        if (validLinks.Count > 0)
        {
            sb.Append(",LinkedTo=(");
            foreach (var (targetNode, targetPin) in validLinks)
                sb.Append($"{targetNode.NodeName} {targetPin.PinId},");
            sb.Append(')');
        }

        sb.Append($",PersistentGuid=00000000000000000000000000000000,bHidden=False,bNotConnectable={(pin.IsNotConnectable ? "True" : "False")},bDefaultValueIsReadOnly=False,bDefaultValueIsIgnored=False,bAdvancedView={(pin.IsAdvancedView ? "True" : "False")},bOrphanedPin=False,)");
        sb.AppendLine();
    }

    /// <summary>
    /// Parses Unreal Engine 5 T3D Material Graph clipboard text (<c>Begin Object Class=/Script/UnrealEd.MaterialGraphNode...</c>)
    /// into a live <see cref="UeMatGraph"/> so users can <c>Ctrl+V</c> UE nodes directly into the Material Graph Editor.
    /// </summary>
    public static bool TryImportFromUeT3D(string t3dText, out UeMatGraph? graph)
    {
        graph = null;
        if (string.IsNullOrWhiteSpace(t3dText) || !t3dText.Contains("MaterialGraphNode", StringComparison.OrdinalIgnoreCase))
            return false;

        var parsed = new UeMatGraph();
        var nodesByName = new Dictionary<string, UeMatNode>(StringComparer.OrdinalIgnoreCase);
        var pinsByKey = new Dictionary<(string NodeName, string PinId), UeMatPin>();
        var pendingLinks = new List<(UeMatPin SourcePin, string TargetNodeName, string TargetPinId)>();

        var lines = t3dText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            var topMatch = Regex.Match(line, @"^Begin Object Class=([^\s]+)\s+Name=""([^""]+)""", RegexOptions.IgnoreCase);
            if (!topMatch.Success)
            {
                i++;
                continue;
            }

            var topClass = topMatch.Groups[1].Value;
            var topName = topMatch.Groups[2].Value;
            var blockLines = new List<string>();
            var depth = 1;
            i++;
            while (i < lines.Length && depth > 0)
            {
                var cur = lines[i].Trim();
                if (cur.StartsWith("Begin Object", StringComparison.OrdinalIgnoreCase))
                    depth++;
                else if (cur.Equals("End Object", StringComparison.OrdinalIgnoreCase))
                {
                    depth--;
                    if (depth == 0)
                    {
                        i++;
                        break;
                    }
                }
                blockLines.Add(cur);
                i++;
            }

            if (topClass.EndsWith("MaterialGraphNode_Comment", StringComparison.OrdinalIgnoreCase))
            {
                var comment = new UeMatComment
                {
                    NodeName = topName,
                    ExpressionName = $"MaterialExpressionComment_{parsed.Comments.Count}",
                    Text = "Comment"
                };
                foreach (var bl in blockLines)
                {
                    if (TryMatchInt(bl, "NodePosX", out var px)) comment.PosX = px;
                    else if (TryMatchInt(bl, "NodePosY", out var py)) comment.PosY = py;
                    else if (TryMatchInt(bl, "NodeWidth", out var nw)) comment.SizeX = nw;
                    else if (TryMatchInt(bl, "NodeHeight", out var nh)) comment.SizeY = nh;
                    else if (TryMatchQuoted(bl, "NodeComment", out var nc)) comment.Text = nc;
                    else if (TryMatchQuoted(bl, "Text", out var tx)) comment.Text = tx;
                }
                parsed.Comments.Add(comment);
                continue;
            }

            var isRoot = topClass.EndsWith("MaterialGraphNode_Root", StringComparison.OrdinalIgnoreCase);
            var exprClass = isRoot ? topClass : "/Script/Engine.MaterialExpression";
            var exprName = topName;
            var paramName = "";
            var funcName = "";
            var posX = 0;
            var posY = 0;
            var nodeGuid = UeMatNode.NewUeGuid();
            var exprProps = new List<(string Key, string Value)>();

            var inInnerObject = false;
            foreach (var bl in blockLines)
            {
                var innerClassMatch = Regex.Match(bl, @"^Begin Object Class=([^\s]+)\s+Name=""([^""]+)""", RegexOptions.IgnoreCase);
                if (innerClassMatch.Success)
                {
                    exprClass = innerClassMatch.Groups[1].Value;
                    exprName = innerClassMatch.Groups[2].Value;
                    continue;
                }
                if (bl.StartsWith("Begin Object Name=", StringComparison.OrdinalIgnoreCase))
                {
                    inInnerObject = true;
                    continue;
                }
                if (bl.Equals("End Object", StringComparison.OrdinalIgnoreCase))
                {
                    inInnerObject = false;
                    continue;
                }

                if (inInnerObject)
                {
                    var eq = bl.IndexOf('=');
                    if (eq > 0)
                    {
                        var k = bl[..eq].Trim();
                        var v = bl[(eq + 1)..].Trim();
                        if (k is not ("MaterialExpressionEditorX" or "MaterialExpressionEditorY" or "MaterialExpressionGuid" or "Material"))
                            exprProps.Add((k, v));
                        if (k.Equals("ParameterName", StringComparison.OrdinalIgnoreCase))
                            paramName = v.Trim('"');
                        else if (k.Equals("MaterialFunction", StringComparison.OrdinalIgnoreCase))
                            funcName = ExtractAssetShortName(v);
                    }
                    continue;
                }

                if (TryMatchInt(bl, "NodePosX", out var x)) posX = x;
                else if (TryMatchInt(bl, "NodePosY", out var y)) posY = y;
                else if (bl.StartsWith("NodeGuid=", StringComparison.OrdinalIgnoreCase)) nodeGuid = bl["NodeGuid=".Length..].Trim();
            }

            var shortExpr = exprClass.Substring(exprClass.LastIndexOf('.') + 1).Replace("MaterialExpression", "");
            var title = isRoot
                ? "Main Material Node"
                : (!string.IsNullOrEmpty(paramName) ? $"{shortExpr} '{paramName}'" : (!string.IsNullOrEmpty(funcName) ? funcName : shortExpr));
            var color = isRoot
                ? new Vector4(0.18f, 0.20f, 0.24f, 1f)
                : (shortExpr.Contains("Texture", StringComparison.OrdinalIgnoreCase)
                    ? new Vector4(0.12f, 0.36f, 0.48f, 1f)
                    : (shortExpr.Contains("Parameter", StringComparison.OrdinalIgnoreCase)
                        ? new Vector4(0.16f, 0.45f, 0.24f, 1f)
                        : (shortExpr.Contains("Function", StringComparison.OrdinalIgnoreCase)
                            ? new Vector4(0.18f, 0.28f, 0.52f, 1f)
                            : new Vector4(0.26f, 0.32f, 0.42f, 1f))));

            var node = new UeMatNode
            {
                NodeName = topName,
                ExpressionClass = exprClass,
                ExpressionName = exprName,
                Title = title,
                HeaderColor = color,
                PosX = posX,
                PosY = posY,
                NodeGuid = nodeGuid,
                IsRoot = isRoot
            };
            node.ExpressionProperties.AddRange(exprProps);

            foreach (var bl in blockLines)
            {
                if (!bl.StartsWith("CustomProperties Pin", StringComparison.OrdinalIgnoreCase))
                    continue;
                var pinId = MatchPinField(bl, "PinId") ?? UeMatNode.NewUeGuid();
                var pinName = MatchPinQuoted(bl, "PinName") ?? "Pin";
                var dir = MatchPinQuoted(bl, "Direction") ?? "";
                var isOut = dir.Equals("EGPD_Output", StringComparison.OrdinalIgnoreCase);
                var cat = MatchPinQuoted(bl, "PinType.PinCategory") ?? "";
                var subCat = MatchPinQuoted(bl, "PinType.PinSubCategory") ?? "";
                var defVal = MatchPinQuoted(bl, "DefaultValue") ?? "";
                var notConn = bl.Contains("bNotConnectable=True", StringComparison.OrdinalIgnoreCase);
                var adv = bl.Contains("bAdvancedView=True", StringComparison.OrdinalIgnoreCase);

                var pin = isOut
                    ? node.AddOutput(pinName, cat, subCat)
                    : node.AddInput(pinName, cat, subCat, defVal, notConn, adv);

                pinsByKey[(node.NodeName, pinId)] = pin;

                var linkedMatch = Regex.Match(bl, @"LinkedTo=\(([^)]*)\)", RegexOptions.IgnoreCase);
                if (linkedMatch.Success)
                {
                    var entries = linkedMatch.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var entry in entries)
                    {
                        var pair = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (pair.Length == 2)
                            pendingLinks.Add((pin, pair[0], pair[1]));
                    }
                }
            }

            nodesByName[node.NodeName] = node;
            if (isRoot)
                parsed.RootNode = node;
            else
                parsed.Nodes.Add(node);
        }

        parsed.RootNode ??= CreateRootNode();

        foreach (var (srcPin, targetNodeName, targetPinId) in pendingLinks)
        {
            if (pinsByKey.TryGetValue((targetNodeName, targetPinId), out var dstPin))
            {
                if (srcPin.IsOutput && !dstPin.IsOutput)
                    UeMatGraph.Connect(srcPin, dstPin);
                else if (!srcPin.IsOutput && dstPin.IsOutput)
                    UeMatGraph.Connect(dstPin, srcPin);
            }
        }

        graph = parsed;
        return parsed.Nodes.Count > 0 || parsed.RootNode.InputPins.Any(p => p.LinkedTo.Count > 0);
    }

    private static int SwizCharToIndex(char c) => c switch
    {
        'x' or 'r' => 0,
        'y' or 'g' => 1,
        'z' or 'b' => 2,
        'w' or 'a' => 3,
        _ => 0
    };

    private static string[] SplitOperands(string instructionLine)
    {
        var firstSpace = instructionLine.IndexOf(' ');
        if (firstSpace < 0) return [];
        var rest = instructionLine[(firstSpace + 1)..];
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i] == '(') depth++;
            else if (rest[i] == ')') depth = Math.Max(0, depth - 1);
            else if (rest[i] == ',' && depth == 0)
            {
                result.Add(rest[start..i].Trim());
                start = i + 1;
            }
        }
        if (start < rest.Length)
            result.Add(rest[start..].Trim());
        return result.ToArray();
    }

    private static bool TryMatchInt(string line, string key, out int value)
    {
        value = 0;
        if (!line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(line[(key.Length + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryMatchQuoted(string line, string key, out string value)
    {
        value = "";
        if (!line.StartsWith(key + "=\"", StringComparison.OrdinalIgnoreCase))
            return false;
        var s = line[(key.Length + 2)..];
        var endQuote = s.LastIndexOf('"');
        value = endQuote >= 0 ? s[..endQuote] : s;
        return true;
    }

    private static string? MatchPinField(string pinLine, string key)
    {
        var m = Regex.Match(pinLine, $@"\b{Regex.Escape(key)}=([^,\)]+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string? MatchPinQuoted(string pinLine, string key)
    {
        var m = Regex.Match(pinLine, $@"\b{Regex.Escape(key)}=""([^""]*)""", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string ExtractAssetShortName(string assetRef)
    {
        var dot = assetRef.LastIndexOf('.');
        var quote = assetRef.LastIndexOf('\'');
        if (dot > 0 && quote > dot)
            return assetRef[(dot + 1)..quote];
        return assetRef.Trim('"', '\'');
    }

    private static string EscapeT3D(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
