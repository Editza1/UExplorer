using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Misc;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using ImGuiNET;

using UExplorer.Rendering;
namespace UExplorer.Scene;

public enum EMaterialPass
{
    Opaque,
    Masked,
    Translucent,
    Additive,
    Modulate,
    AlphaComposite
}

public enum EBlendFactor
{
    Zero,
    One,
    SrcAlpha,
    InvSrcAlpha,
    DestColor
}

public readonly record struct MaterialBlend(bool Enabled, EBlendFactor ColorSrc, EBlendFactor ColorDst, EBlendFactor AlphaSrc, EBlendFactor AlphaDst, bool WritesAlpha);

public enum EMaterialTextureKind
{
    Other,
    Diffuse,
    Normal,
    SpecularMasks,
    Emissive
}

public sealed partial class MaterialAsset
{
    public string Name { get; }
    public string ProjectName { get; }
    public UUnrealMaterial? CookedMaterial { get; }

    public CUE4Parse.UE4.Objects.Engine.UMaterialExpression[]? GetExpressions()
    {
        return CookedMaterial is CUE4Parse.UE4.Assets.Exports.Material.UMaterial mat ? mat.Expressions.Select(x => x.Load<CUE4Parse.UE4.Objects.Engine.UMaterialExpression>()).ToArray() : null;
    }

    private readonly Lazy<MaterialAsset?>? _parent;
    public MaterialAsset? Parent => _parent?.Value is { } parent && parent != this ? parent : null;

    private const EBlendMode DefaultBlendMode = EBlendMode.BLEND_Opaque;
    private readonly EBlendMode? _blendMode;
    public EBlendMode BlendMode => _blendMode ?? Parent?.BlendMode ?? DefaultBlendMode;

    private const EMaterialShadingModel DefaultShadingModel = EMaterialShadingModel.MSM_DefaultLit;
    private readonly EMaterialShadingModel? _shadingModel;
    public EMaterialShadingModel ShadingModel => _shadingModel ?? Parent?.ShadingModel ?? DefaultShadingModel;

    private const EMaterialDomain DefaultMaterialDomain = EMaterialDomain.MD_Surface;
    private readonly EMaterialDomain? _materialDomain;
    public EMaterialDomain MaterialDomain => _materialDomain ?? Parent?.MaterialDomain ?? DefaultMaterialDomain;

    private const bool DefaultTwoSided = false;
    private readonly bool? _twoSided;
    public bool TwoSided => _twoSided ?? Parent?.TwoSided ?? DefaultTwoSided;

    private const float DefaultOpacityMaskClipValue = 0.3333f;
    private readonly float? _opacityMaskClipValue;
    public float OpacityMaskClipValue => _opacityMaskClipValue ?? Parent?.OpacityMaskClipValue ?? DefaultOpacityMaskClipValue;

    private ulong? _propertyConnectedMask;
    public ulong? PropertyConnectedMask => _propertyConnectedMask ?? Parent?.PropertyConnectedMask;

    private ShaderParameters? _shaderParameters;
    private ShaderParameters? ActiveParameters => _shaderParameters ?? Parent?.ActiveParameters;

    private readonly record struct MaterialParameterKey
    {
        public string Name { get; }
        public EMaterialParameterAssociation Association { get; }
        public int Index { get; }

        public MaterialParameterKey(string name, EMaterialParameterAssociation association = EMaterialParameterAssociation.GlobalParameter, int index = -1)
        {
            Name = name;
            Association = association;
            Index = association == EMaterialParameterAssociation.GlobalParameter ? -1 : index;
        }

        public bool Equals(MaterialParameterKey other) =>
            Association == other.Association &&
            Index == other.Index &&
            string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Name ?? string.Empty), Association, Index);
    }

    private readonly Dictionary<string, TextureParameter> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MaterialParameterKey, TextureParameter> _texturesByKey = [];
    private readonly List<TextureParameter> _referencedTextures = [];
    private readonly Dictionary<string, float> _scalars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MaterialParameterKey, float> _scalarsByKey = [];
    private readonly Dictionary<string, FLinearColor> _vectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MaterialParameterKey, FLinearColor> _vectorsByKey = [];
    private readonly Dictionary<string, bool> _switches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MaterialParameterKey, bool> _switchesByKey = [];
    private readonly int[] _parentLayerIndexRemap = [];

    private readonly UMaterialInterface? _cookedStrong;
    private readonly IFileProvider? _provider;
    private IReadOnlyList<NativeMaterialShaders>? _nativeShaders;

    private sealed class ResolvedShaderCacheEntry
    {
        public readonly object Sync = new();
        public IReadOnlyList<NativeMaterialShaders>? Unvalidated;
        public IReadOnlyList<NativeMaterialShaders>? Validated;
        public D3D11NativeShaderDevice? ValidatedDevice;
    }

    private static readonly ConditionalWeakTable<UMaterialInterface, ResolvedShaderCacheEntry> _resolvedShaderCache = new();

    private static IReadOnlyList<NativeMaterialShaders> GetOrResolveCookedShaders(
        UMaterialInterface cooked,
        IFileProvider provider,
        bool validateOnDevice)
    {
        var entry = _resolvedShaderCache.GetOrCreateValue(cooked);
        lock (entry.Sync)
        {
            var device = validateOnDevice ? D3D11NativeShaderDevice.Shared : null;
            if (device is not null)
            {
                if (entry.Validated is not null && ReferenceEquals(entry.ValidatedDevice, device))
                    return entry.Validated;

                var resolved = entry.Unvalidated ?? NativeMaterialShaders.Resolve(cooked, provider);
                entry.Unvalidated = resolved;
                foreach (var set in resolved)
                    foreach (var shader in set.Shaders)
                        if (shader.Status == ENativeShaderStatus.Extracted)
                            device.Validate(shader);

                entry.Validated = resolved;
                entry.ValidatedDevice = device;
                return resolved;
            }

            entry.Unvalidated ??= NativeMaterialShaders.Resolve(cooked, provider);
            return entry.Unvalidated;
        }
    }

    public IReadOnlyList<NativeMaterialShaders> GetNativeShaders(bool validateOnDevice = true)
    {
        if (_nativeShaders is not null)
            return _nativeShaders;

        // Walk up the entire MaterialInstance -> Parent MI -> Root Material chain until we find a valid shader map
        for (var curr = this; curr != null; curr = curr.Parent)
        {
            if (curr._cookedStrong is { LoadedMaterialResources.Count: > 0 } cooked && curr._provider is not null)
            {
                var resolved = GetOrResolveCookedShaders(cooked, curr._provider, validateOnDevice);

                if (resolved.Any(x => x.Shaders.Count > 0 && x.UniformSet is not null))
                {
                    if (validateOnDevice)
                        _nativeShaders = resolved;
                    return resolved;
                }
            }
        }

        // Direct UObject fallback in case _parent Lazy failed to resolve through MaterialCache
        if (_cookedStrong is not null && _provider is not null)
        {
            var currentObj = _cookedStrong;
            for (var depth = 0; depth < 16 && currentObj is not null; depth++)
            {
                if (currentObj.LoadedMaterialResources is { Count: > 0 })
                {
                    var resolved = GetOrResolveCookedShaders(currentObj, _provider, validateOnDevice);

                    if (resolved.Any(x => x.Shaders.Count > 0 && x.UniformSet is not null))
                    {
                        if (validateOnDevice)
                            _nativeShaders = resolved;
                        return resolved;
                    }
                }
                currentObj = currentObj is UMaterialInstance { Parent: { IsNull: false } pIdx } ? pIdx.Load<UMaterialInterface>() : null;
            }

            return GetOrResolveCookedShaders(_cookedStrong, _provider, validateOnDevice: false);
        }

        return [];
    }

    private readonly object _overrideSync = new();
    private long _parameterVersion;
    private static readonly ConditionalWeakTable<IFileProvider, ConcurrentDictionary<string, UTexture?>> _textureOverrideCache = new();

    public Dictionary<string, float> NativeScalarOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, System.Numerics.Vector4> NativeVectorOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string?> NativeTextureOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
    public long ParameterVersion => Volatile.Read(ref _parameterVersion);

    public void NotifyParametersChanged() => Interlocked.Increment(ref _parameterVersion);

    public void CopyOverridesFrom(MaterialAsset source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (source._overrideSync)
        {
            lock (_overrideSync)
            {
                NativeScalarOverrides.Clear();
                foreach (var (k, v) in source.NativeScalarOverrides)
                    NativeScalarOverrides[k] = v;

                NativeVectorOverrides.Clear();
                foreach (var (k, v) in source.NativeVectorOverrides)
                    NativeVectorOverrides[k] = v;

                NativeTextureOverrides.Clear();
                foreach (var (k, v) in source.NativeTextureOverrides)
                    NativeTextureOverrides[k] = v;
            }
        }
        NotifyParametersChanged();
    }

    private bool TryGetScalarOverride(string name, out float value)
    {
        lock (_overrideSync)
            return NativeScalarOverrides.TryGetValue(name, out value);
    }

    private bool TryGetVectorOverride(string name, out System.Numerics.Vector4 value)
    {
        lock (_overrideSync)
            return NativeVectorOverrides.TryGetValue(name, out value);
    }

    public bool TryResolveTextureOverride(string name, IFileProvider? providerOverride, out UTexture? texture)
    {
        for (var node = this; node != null; node = node.Parent)
        {
            string? path;
            bool hasOverride;
            lock (node._overrideSync)
                hasOverride = node.NativeTextureOverrides.TryGetValue(name, out path);
            if (!hasOverride)
                continue;

            if (string.IsNullOrWhiteSpace(path))
            {
                texture = null;
                return true;
            }

            texture = LoadTextureByPath(providerOverride ?? node._provider ?? _provider, path);
            return true;
        }
        texture = null;
        return false;
    }

    private static UTexture? LoadTextureByPath(IFileProvider? provider, string path)
    {
        if (provider is null || string.IsNullOrWhiteSpace(path))
            return null;

        var norm = path.Trim().Replace('\\', '/');
        var cache = _textureOverrideCache.GetOrCreateValue(provider);
        return cache.GetOrAdd(norm, key =>
        {
            try
            {
                if (provider.TryLoadPackageObject<UTexture>(key, out var directTex))
                    return directTex;

                var pkgPath = key;
                string? objName = null;
                var lastSlash = key.LastIndexOf('/');
                var dotAfterSlash = key.LastIndexOf('.');
                if (dotAfterSlash > lastSlash &&
                    !key.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                    !key.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                {
                    pkgPath = key[..dotAfterSlash];
                    objName = key[(dotAfterSlash + 1)..];
                }

                if (provider.TryLoadPackage(pkgPath, out var pkg))
                {
                    if (!string.IsNullOrEmpty(objName))
                    {
                        var named = pkg.GetExports().OfType<UTexture>()
                            .FirstOrDefault(t => string.Equals(t.Name, objName, StringComparison.OrdinalIgnoreCase));
                        if (named is not null)
                            return named;
                    }
                    return pkg.GetExports().OfType<UTexture>().FirstOrDefault();
                }
            }
            catch
            {
            }
            return null;
        });
    }

    public float GetNativeScalar(FMaterialScalarParameterInfo p)
    {
        var name = p.ParameterInfo?.Name.Text ?? p.ParameterName;
        if (string.IsNullOrEmpty(name) || name == "None")
            return p.DefaultValue;
        if (TryGetScalarOverride(name, out var edited))
            return edited;
        var assoc = p.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
        var index = p.ParameterInfo?.Index ?? -1;
        return TryGetScalarByInfo(out var value, name, assoc, index) ? value : p.DefaultValue;
    }

    public System.Numerics.Vector4 GetNativeVector(FMaterialVectorParameterInfo p)
    {
        var name = p.ParameterInfo?.Name.Text ?? p.ParameterName;
        if (!string.IsNullOrEmpty(name) && name != "None")
        {
            if (TryGetVectorOverride(name, out var edited))
                return edited;
            var assoc = p.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
            var index = p.ParameterInfo?.Index ?? -1;
            if (TryGetVectorByInfo(out var value, name, assoc, index))
                return new System.Numerics.Vector4(value.R, value.G, value.B, value.A);
        }
        var c = p.DefaultValue;
        return new System.Numerics.Vector4(c.R, c.G, c.B, c.A);
    }

    public Vector4 GetNativeNumeric(FMaterialNumericParameterInfo p)
    {
        var name = p.ParameterInfo?.Name.Text;
        var hasName = !string.IsNullOrEmpty(name) && name != "None";
        var assoc = p.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
        var index = p.ParameterInfo?.Index ?? -1;

        switch (p.ParameterType)
        {
            case EMaterialParameterType.Scalar:
                {
                    if (hasName && TryGetScalarOverride(name!, out var edited))
                        return new Vector4(edited);
                    if (hasName && TryGetScalarByInfo(out var s, name!, assoc, index))
                        return new Vector4(s);
                    return new Vector4(p.Value is float f ? f : 0f);
                }
            case EMaterialParameterType.Vector or EMaterialParameterType.DoubleVector:
                {
                    if (hasName && TryGetVectorOverride(name!, out var edited))
                        return edited;
                    if (hasName && TryGetVectorByInfo(out var v, name!, assoc, index))
                        return new Vector4(v.R, v.G, v.B, v.A);
                    if (p.Value is FLinearColor lc)
                        return new Vector4(lc.R, lc.G, lc.B, lc.A);
                    if (p.Value is FVector4 v4)
                        return new Vector4(v4.X, v4.Y, v4.Z, v4.W);
                    return Vector4.Zero;
                }
            case EMaterialParameterType.StaticSwitch:
                {
                    if (hasName && TryGetSwitchByInfo(out var sw, name!, assoc, index))
                        return new Vector4(sw ? 1f : 0f);
                    return new Vector4(p.Value is bool b && b ? 1f : 0f);
                }
            default:
                return Vector4.Zero;
        }
    }

    private static bool TryRemapLayerIndex(int[] remap, EMaterialParameterAssociation assoc, ref int index)
    {
        if (assoc == EMaterialParameterAssociation.GlobalParameter || remap.Length == 0)
            return true;
        if (assoc == EMaterialParameterAssociation.LayerParameter)
        {
            if (index >= 0 && index < remap.Length && remap[index] != -1)
            {
                index = remap[index];
                return true;
            }
            return false;
        }
        if (assoc == EMaterialParameterAssociation.BlendParameter)
        {
            var remapIdx = index + 1;
            if (remapIdx >= 0 && remapIdx < remap.Length && remap[remapIdx] > 0)
            {
                index = remap[remapIdx] - 1;
                return true;
            }
            return false;
        }
        return true;
    }

    private bool TryGetScalarByInfo(out float value, string name, EMaterialParameterAssociation assoc, int index)
    {
        var curIndex = index;
        for (var node = this; node != null; node = node.Parent)
        {
            if (node._scalarsByKey.TryGetValue(new MaterialParameterKey(name, assoc, curIndex), out value))
                return true;
            if (!TryRemapLayerIndex(node._parentLayerIndexRemap, assoc, ref curIndex))
                break;
        }
        if (assoc != EMaterialParameterAssociation.GlobalParameter)
        {
            for (var node = this; node != null; node = node.Parent)
            {
                if (node._scalarsByKey.TryGetValue(new MaterialParameterKey(name, assoc, index), out value))
                    return true;
            }
        }
        return TryGetScalarUnfiltered(out value, name);
    }

    private bool TryGetVectorByInfo(out FLinearColor value, string name, EMaterialParameterAssociation assoc, int index)
    {
        var curIndex = index;
        for (var node = this; node != null; node = node.Parent)
        {
            if (node._vectorsByKey.TryGetValue(new MaterialParameterKey(name, assoc, curIndex), out value))
                return true;
            if (!TryRemapLayerIndex(node._parentLayerIndexRemap, assoc, ref curIndex))
                break;
        }
        if (assoc != EMaterialParameterAssociation.GlobalParameter)
        {
            for (var node = this; node != null; node = node.Parent)
            {
                if (node._vectorsByKey.TryGetValue(new MaterialParameterKey(name, assoc, index), out value))
                    return true;
            }
        }
        return TryGetVectorUnfiltered(out value, name);
    }

    private bool TryGetSwitchByInfo(out bool value, string name, EMaterialParameterAssociation assoc, int index)
    {
        var curIndex = index;
        for (var node = this; node != null; node = node.Parent)
        {
            if (node._switchesByKey.TryGetValue(new MaterialParameterKey(name, assoc, curIndex), out value))
                return true;
            if (!TryRemapLayerIndex(node._parentLayerIndexRemap, assoc, ref curIndex))
                break;
        }
        return TryGetSwitch(out value, name);
    }

    private bool TryGetScalarUnfiltered(out float value, string name)
    {
        for (var node = this; node != null; node = node.Parent)
            if (node._scalars.TryGetValue(name, out value))
                return true;
        value = default;
        return false;
    }

    private bool TryGetVectorUnfiltered(out FLinearColor value, string name)
    {
        for (var node = this; node != null; node = node.Parent)
            if (node._vectors.TryGetValue(name, out value))
                return true;
        value = default;
        return false;
    }

    public NativeMaterialUniforms.Result? BuildNativeMaterialBuffer(NativeMaterialShaders set)
    {
        if (set.UniformSet is not { } uniforms)
            return null;

        return NativeMaterialUniforms.Build(
            uniforms,
            index =>
            {
                if (index >= 0 && index < uniforms.UniformScalarParameters.Length)
                    return GetNativeScalar(uniforms.UniformScalarParameters[index]);
                if (index >= 0 && index < uniforms.UniformNumericParameters.Length)
                    return GetNativeNumeric(uniforms.UniformNumericParameters[index]).X;
                return 0f;
            },
            index =>
            {
                if (index >= 0 && index < uniforms.UniformVectorParameters.Length)
                    return GetNativeVector(uniforms.UniformVectorParameters[index]);
                if (index >= 0 && index < uniforms.UniformNumericParameters.Length)
                    return GetNativeNumeric(uniforms.UniformNumericParameters[index]);
                return System.Numerics.Vector4.Zero;
            },
            index =>
            {
                var tex = GetTextureByExpressionIndex(uniforms, index, _provider);
                return tex?.PlatformData is { SizeX: > 0, SizeY: > 0 } pd ? new Vector2(pd.SizeX, pd.SizeY) : new Vector2(1024, 1024);
            },
            (name, assoc, paramIdx, defaultVal) =>
            {
                if (!string.IsNullOrEmpty(name) && name != "None")
                {
                    if (TryGetScalarOverride(name, out var edited))
                        return edited;
                    if (TryGetScalarByInfo(out var s, name, assoc, paramIdx))
                        return s;
                }
                return defaultVal ?? 0f;
            },
            (name, assoc, paramIdx, defaultVal) =>
            {
                if (!string.IsNullOrEmpty(name) && name != "None")
                {
                    if (TryGetVectorOverride(name, out var edited))
                        return edited;
                    if (TryGetVectorByInfo(out var v, name, assoc, paramIdx))
                        return new Vector4(v.R, v.G, v.B, v.A);
                }
                return defaultVal ?? Vector4.Zero;
            },
            (name, assoc, paramIdx, texIdx) =>
            {
                UTexture? tex = null;
                var hasExplicitNullOverride = false;
                if (!string.IsNullOrEmpty(name) && name != "None")
                {
                    if (TryResolveTextureOverride(name, _provider, out tex))
                        hasExplicitNullOverride = tex is null;
                    else
                        tex = ResolveTextureParameterByInfo(name, assoc, paramIdx, _provider);
                }
                if (!hasExplicitNullOverride)
                    tex ??= ResolveTextureAtIndexInChain(texIdx);
                if (tex?.PlatformData is { SizeX: > 0, SizeY: > 0 } pd)
                    return new Vector3(pd.SizeX, pd.SizeY, Math.Max((int) (pd.PackedData & 0x1FFFFFFFu), 1));
                return new Vector3(1024f, 1024f, 0f);
            });
    }

    private UTexture? GetTextureByExpressionIndex(FUniformExpressionSet uniforms, int index, IFileProvider? provider = null)
    {
        var textures = uniforms.UniformTextureParameters.SelectMany(x => x ?? []).ToList();
        if (index >= 0 && index < textures.Count)
            return ResolveSingleTextureParameter(textures[index], provider);
        return ResolveTextureAtIndexInChain(index);
    }

    private UTexture? ResolveTextureParameterByInfo(string name, EMaterialParameterAssociation assoc, int index, IFileProvider? provider = null)
    {
        if (TryResolveTextureOverride(name, provider, out var overridden))
            return overridden;

        var curIndex = index;
        for (var node = this; node != null; node = node.Parent)
        {
            if (node._texturesByKey.TryGetValue(new MaterialParameterKey(name, assoc, curIndex), out var param) && param.Texture.Value is { } found)
                return found;
            if (!TryRemapLayerIndex(node._parentLayerIndexRemap, assoc, ref curIndex))
                break;
        }
        if (assoc != EMaterialParameterAssociation.GlobalParameter)
        {
            for (var node = this; node != null; node = node.Parent)
            {
                if (node._texturesByKey.TryGetValue(new MaterialParameterKey(name, assoc, index), out var param) && param.Texture.Value is { } found)
                    return found;
            }
        }
        for (var node = this; node != null; node = node.Parent)
        {
            if (node._textures.TryGetValue(name, out var param) && param.Texture.Value is { } found)
                return found;
        }
        return null;
    }

    private UTexture? ResolveSingleTextureParameter(FMaterialTextureParameterInfo parameter, IFileProvider? provider = null)
    {
        var name = parameter.ParameterInfo?.Name.Text ?? parameter.ParameterName;
        if (!string.IsNullOrEmpty(name) && name != "None")
        {
            if (TryResolveTextureOverride(name, provider, out var overridden))
                return overridden;
            var assoc = parameter.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
            var index = parameter.ParameterInfo?.Index ?? -1;
            var resolved = ResolveTextureParameterByInfo(name, assoc, index, provider);
            if (resolved is not null)
                return resolved;
        }
        return ResolveTextureAtIndexInChain(parameter.TextureIndex);
    }

    private UTexture? ResolveTextureAtIndexInChain(int texIndex)
    {
        if (texIndex < 0)
            return null;

        // 1. Find the shader-owning node in the parent chain first so TextureIndex matches the compiled shader map's ReferencedTextures
        for (var node = this; node != null; node = node.Parent)
        {
            if (node._cookedStrong is { LoadedMaterialResources.Count: > 0 } shaderOwner)
            {
                if (shaderOwner is UMaterial ownerRoot)
                {
                    try
                    {
                        var t = ownerRoot.GetTextureAtIndex(texIndex);
                        if (t is not null)
                            return t;
                    }
                    catch { }

                    if (texIndex < ownerRoot.ReferencedTextures.Count && ownerRoot.ReferencedTextures[texIndex] is { IsNull: false } refIdx && refIdx.TryLoad<UTexture>(out var refTex))
                        return refTex;
                }

                if (shaderOwner.CachedExpressionData is { } ownerCached && TryResolveCachedRefTexture(ownerCached, texIndex, shaderOwner.Owner?.Provider) is { } ownerCachedTex)
                    return ownerCachedTex;

                var cachedRefs = shaderOwner.GetOrDefault<FPackageIndex?[]>("CachedReferencedTextures", []);
                if (texIndex < cachedRefs.Length && cachedRefs[texIndex] is { IsNull: false } cIdx && cIdx.TryLoad<UTexture>(out var cTex))
                    return cTex;
            }
        }

        // 2. Check Root UMaterial
        if (RootMaterial() is { } root)
        {
            try
            {
                var t = root.GetTextureAtIndex(texIndex);
                if (t is not null)
                    return t;
            }
            catch { }

            if (texIndex < root.ReferencedTextures.Count && root.ReferencedTextures[texIndex] is { IsNull: false } refIdx && refIdx.TryLoad<UTexture>(out var refTex))
                return refTex;
        }

        // 3. Check CachedExpressionData.ReferencedTextures and CachedReferencedTextures on all nodes
        for (var node = this; node != null; node = node.Parent)
        {
            if (node._cookedStrong is { } cooked)
            {
                if (cooked.CachedExpressionData is { } nodeCached && TryResolveCachedRefTexture(nodeCached, texIndex, cooked.Owner?.Provider) is { } nodeCachedTex)
                    return nodeCachedTex;
                var cachedRefs = cooked.GetOrDefault<FPackageIndex?[]>("CachedReferencedTextures", []);
                if (texIndex < cachedRefs.Length && cachedRefs[texIndex] is { IsNull: false } cIdx && cIdx.TryLoad<UTexture>(out var cTex))
                    return cTex;
            }
            if (texIndex < node._referencedTextures.Count && node._referencedTextures[texIndex].Texture.Value is { } rTex)
                return rTex;
        }

        return null;
    }

    private static UTexture? TryResolveCachedRefTexture(FStructFallback cachedExprData, int texIndex, IFileProvider? provider)
    {
        if (texIndex < 0)
            return null;
        if (cachedExprData.TryGetValue(out FPackageIndex?[] pkgRefs, "ReferencedTextures") && pkgRefs is not null && texIndex < pkgRefs.Length)
        {
            if (pkgRefs[texIndex] is { IsNull: false } pIdx && pIdx.TryLoad<UTexture>(out var pTex))
                return pTex;
            return null;
        }
        if (provider is not null && cachedExprData.TryGetValue(out FSoftObjectPath[] softRefs, "ReferencedTextures") && softRefs is not null && texIndex < softRefs.Length)
        {
            if (softRefs[texIndex].TryLoad<UTexture>(provider, out var sTex))
                return sTex;
            return null;
        }
        return null;
    }

    /// <summary>
    /// Resolves both texture resources and per-slot sampler modes following the exact layout of
    /// <c>FUniformExpressionSet::CreateBufferStruct()</c> in UE 4.27 (<c>MaterialUniformExpressions.cpp:455-552</c>)
    /// and UE 5.2 (<c>MaterialUniformExpressions.cpp:460-552</c>).
    /// </summary>
    public (UTexture?[] Textures, ESamplerSourceMode?[] SamplerSources) ResolveNativeMaterialResourceLayout(NativeMaterialShaders set) =>
        ResolveNativeMaterialResourceLayout(set, null);

    public (UTexture?[] Textures, ESamplerSourceMode?[] SamplerSources) ResolveNativeMaterialResourceLayout(NativeMaterialShaders set, IFileProvider? providerOverride)
    {
        if (set.UniformSet is not { UniformBufferLayoutInitializer.Resources: { } resources } uniforms)
            return ([], []);

        var textures = new UTexture?[resources.Length];
        var samplers = new ESamplerSourceMode?[resources.Length];

        var texParams = uniforms.UniformTextureParameters ?? [];
        var virtualTypeIndex = texParams.Length switch
        {
            >= 6 => texParams.Length - 1,
            5 => 4,
            _ => -1
        };

        var slot = 0;
        void EmitTextureAndSampler(UTexture? tex, ESamplerSourceMode samplerSource)
        {
            if (slot < textures.Length)
                textures[slot] = tex;
            slot++;
            if (slot < samplers.Length)
                samplers[slot] = samplerSource;
            slot++;
        }

        // 1. Standard2D, Cube, Array2D, (ArrayCube in UE5), Volume, (SparseVolume in UE5.3+)
        var nonVirtualCount = virtualTypeIndex >= 0 ? virtualTypeIndex : texParams.Length;
        for (var typeIdx = 0; typeIdx < nonVirtualCount; typeIdx++)
        {
            foreach (var param in texParams[typeIdx] ?? [])
            {
                EmitTextureAndSampler(ResolveSingleTextureParameter(param, providerOverride), param.SamplerSource);
            }
        }

        // 2. UniformExternalTextureParameters (each is UBMT_TEXTURE + UBMT_SAMPLER)
        foreach (var ext in uniforms.UniformExternalTextureParameters ?? [])
        {
            EmitTextureAndSampler(ResolveTextureAtIndexInChain(ext.SourceTextureIndex), ESamplerSourceMode.SSM_Clamp_WorldGroupSettings);
        }

        // 3. VTStacks: PageTable0 (UBMT_TEXTURE), optional PageTable1 (UBMT_TEXTURE if NumLayers > 4), PageTableIndirection (UBMT_TEXTURE)
        foreach (var stack in uniforms.VTStacks ?? [])
        {
            slot++; // PageTable0
            if (stack.NumLayers > 4)
                slot++; // PageTable1
            slot++; // PageTableIndirection
        }

        // 4. Virtual Texture Physical SRVs + Samplers (UBMT_SRV + UBMT_SAMPLER)
        if (virtualTypeIndex >= 0 && virtualTypeIndex < texParams.Length)
        {
            foreach (var vtParam in texParams[virtualTypeIndex] ?? [])
            {
                EmitTextureAndSampler(ResolveSingleTextureParameter(vtParam, providerOverride), vtParam.SamplerSource);
            }
        }

        // 5. Wrap_WorldGroupSettings and Clamp_WorldGroupSettings
        if (slot < samplers.Length)
            samplers[slot++] = ESamplerSourceMode.SSM_Wrap_WorldGroupSettings;
        if (slot < samplers.Length)
            samplers[slot++] = ESamplerSourceMode.SSM_Clamp_WorldGroupSettings;

        // Fallback if a custom engine layout did not match the exact slot count
        if (slot != resources.Length)
        {
            var allTex = texParams.SelectMany(x => x ?? []).ToList();
            var nextAll = 0;
            for (var i = 0; i < resources.Length; i++)
            {
                var memberType = resources[i].MemberType;
                var isTextureOrSrv = memberType is EUniformBufferBaseType.UBMT_TEXTURE
                    or EUniformBufferBaseType.UBMT_SRV
                    or EUniformBufferBaseType.UBMT_RDG_TEXTURE
                    or EUniformBufferBaseType.UBMT_RDG_TEXTURE_SRV
                    || (byte) memberType is 5 or 6;
                if (!isTextureOrSrv)
                    continue;
                if (textures[i] is null && nextAll < allTex.Count)
                {
                    var p = allTex[nextAll++];
                    textures[i] = ResolveSingleTextureParameter(p, providerOverride);
                    if (i + 1 < samplers.Length && samplers[i + 1] is null)
                        samplers[i + 1] = p.SamplerSource;
                }
            }
        }

        return (textures, samplers);
    }

    public UTexture?[] ResolveNativeMaterialResources(NativeMaterialShaders set) =>
        ResolveNativeMaterialResourceLayout(set, null).Textures;

    public UTexture?[] ResolveNativeMaterialResources(NativeMaterialShaders set, IFileProvider? providerOverride) =>
        ResolveNativeMaterialResourceLayout(set, providerOverride).Textures;

    public Dictionary<string, byte[]> ResolveNativeParameterCollections(NativeMaterialShaders set)
    {
        var result = new Dictionary<string, byte[]>();
        if (set.UniformSet?.ParameterCollections is not { Length: > 0 } guids)
            return result;

        var infos = new List<FStructFallback>();
        for (var node = this; node != null; node = node.Parent)
        {
            var c = node._cookedStrong?.CachedExpressionData ?? node._cookedStrong?.GetOrDefault<FStructFallback?>("CachedExpressionData");
            if (c?.GetOrDefault<FStructFallback[]>("ParameterCollectionInfos") is { Length: > 0 } found)
                infos.AddRange(found);
        }

        if (infos.Count == 0 && RootMaterial() is { } rootMat)
        {
            var c = rootMat.CachedExpressionData ?? rootMat.GetOrDefault<FStructFallback?>("CachedExpressionData");
            if (c?.GetOrDefault<FStructFallback[]>("ParameterCollectionInfos") is { Length: > 0 } found)
                infos.AddRange(found);
        }

        for (var i = 0; i < guids.Length; i++)
        {
            var values = new float[256 * 4];
            Array.Fill(values, 1f);

            try
            {
                var info = infos.FirstOrDefault(x => x.GetOrDefault<FGuid>("StateId").Equals(guids[i])) ?? (i < infos.Count ? infos[i] : null);
                if (info?.GetOrDefault<FPackageIndex?>("ParameterCollection") is { IsNull: false } index && index.TryLoad<UObject>(out var collection))
                {
                    var scalars = collection.GetOrDefault<FStructFallback[]>("ScalarParameters") ?? [];
                    var vectors = collection.GetOrDefault<FStructFallback[]>("VectorParameters") ?? [];
                    var scalarVectors = (scalars.Length + 3) / 4;
                    var scalarSlots = scalarVectors * 4;

                    for (var s = 0; s < scalars.Length && s < values.Length; s++)
                    {
                        var val = scalars[s].GetOrDefault<float>("DefaultValue");
                        values[s] = val;
                    }
                    for (var v = 0; v < vectors.Length && scalarSlots + v * 4 + 3 < values.Length; v++)
                    {
                        var c = vectors[v].GetOrDefault<FLinearColor>("DefaultValue");
                        values[scalarSlots + v * 4] = c.R;
                        values[scalarSlots + v * 4 + 1] = c.G;
                        values[scalarSlots + v * 4 + 2] = c.B;
                        values[scalarSlots + v * 4 + 3] = c.A;
                    }
                }
            }
            catch (Exception e)
            {
                Serilog.Log.Warning(e, "Could not load material parameter collection {Index}", i);
            }

            var bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            result[$"MaterialCollection{i}"] = bytes;
        }
        return result;
    }

    internal UMaterial? RootMaterial()
    {
        for (var node = this; node != null; node = node.Parent)
        {
            if (node._cookedStrong is UMaterial rootMat)
                return rootMat;
        }

        var current = _cookedStrong;
        for (var depth = 0; depth < 16 && current is not null; depth++)
        {
            if (current is UMaterial root)
                return root;
            current = current is UMaterialInstance { Parent: { IsNull: false } parent } ? parent.Load<UMaterialInterface>() : null;
        }
        return null;
    }

    public MaterialAsset Node => this;

    public MaterialAsset(UUnrealMaterial material)
    {
        var provider = material.Owner?.Provider;
        _provider = provider;
        if (material is UMaterialInterface cookedInterface)
            _cookedStrong = cookedInterface;

        Name = material.Name;
        ProjectName = provider?.ProjectName ?? string.Empty;
        CookedMaterial = material;

        switch (material)
        {
            case UMaterialInstance instance:
                {
                    if (instance.Parent is { IsNull: false } && provider is not null)
                    {
                        var parentIndex = instance.Parent;
                        _parent = new Lazy<MaterialAsset?>(() =>
                            parentIndex.TryLoad<UUnrealMaterial>(out var loadedParent) ? new MaterialAsset(loadedParent) : null,
                            LazyThreadSafetyMode.ExecutionAndPublication);
                    }

                    var cachedInstData = instance.CachedData ?? instance.GetOrDefault<FStructFallback?>("CachedData");
                    if (cachedInstData?.GetOrDefault<int[]>("ParentLayerIndexRemap") is { Length: > 0 } remap)
                        _parentLayerIndexRemap = remap;

                    if (instance.GetOrDefault<FStructFallback?>("BasePropertyOverrides") is { } overrides)
                    {
                        if (overrides.GetOrDefault($"bOverride_{nameof(BlendMode)}", false))
                            _blendMode = overrides.GetOrDefault(nameof(BlendMode), DefaultBlendMode);
                        if (overrides.GetOrDefault($"bOverride_{nameof(ShadingModel)}", false))
                            _shadingModel = overrides.GetOrDefault(nameof(ShadingModel), DefaultShadingModel);
                        if (overrides.GetOrDefault($"bOverride_{nameof(TwoSided)}", false))
                            _twoSided = overrides.GetOrDefault(nameof(TwoSided), DefaultTwoSided);
                        if (overrides.GetOrDefault($"bOverride_{nameof(OpacityMaskClipValue)}", false))
                            _opacityMaskClipValue = overrides.GetOrDefault(nameof(OpacityMaskClipValue), DefaultOpacityMaskClipValue);
                    }

                    foreach (var parameter in instance.StaticParameters?.StaticSwitchParameters ?? [])
                    {
                        _switches[parameter.Name] = parameter.Value;
                        if (parameter.ParameterInfo is { } info)
                            _switchesByKey[new MaterialParameterKey(parameter.Name, info.Association, info.Index)] = parameter.Value;
                        else
                            _switchesByKey[new MaterialParameterKey(parameter.Name)] = parameter.Value;
                    }

                    if (instance is UMaterialInstanceConstant constant)
                        ReadParameterValues(constant.TextureParameterValues, constant.ScalarParameterValues, constant.VectorParameterValues);
                    else
                        ReadParameterValues(
                            instance.GetOrDefault("TextureParameterValues", Array.Empty<FTextureParameterValue>()),
                            instance.GetOrDefault("ScalarParameterValues", Array.Empty<FScalarParameterValue>()),
                            instance.GetOrDefault("VectorParameterValues", Array.Empty<FVectorParameterValue>()));

                    // DoubleVectorParameterValues (UE 5.0+ LWC vectors)
                    foreach (var dv in instance.GetOrDefault<FStructFallback[]>("DoubleVectorParameterValues", []))
                    {
                        var info = dv.GetOrDefault<FMaterialParameterInfo?>("ParameterInfo");
                        var name = info?.Name.Text ?? dv.GetOrDefault<FName>("ParameterName").Text;
                        if (string.IsNullOrEmpty(name) || name == "None")
                            continue;
                        if (dv.TryGetValue(out FVector4 v4, "ParameterValue"))
                        {
                            var lc = new FLinearColor(v4.X, v4.Y, v4.Z, v4.W);
                            var assoc = info?.Association ?? EMaterialParameterAssociation.GlobalParameter;
                            var idx = info?.Index ?? -1;
                            _vectorsByKey[new MaterialParameterKey(name, assoc, idx)] = lc;
                            if (assoc == EMaterialParameterAssociation.GlobalParameter)
                                _vectors[name] = lc;
                            else
                                _vectors.TryAdd(name, lc);
                        }
                    }

                    ReadCachedData(instance);
                    ReadShaderMaps(instance);

                    _ = _parent?.Value;
                    break;
                }
            case UMaterial root:
                {
                    _blendMode = root.GetOrDefault(nameof(BlendMode), DefaultBlendMode);
                    _shadingModel = root.GetOrDefault(nameof(ShadingModel), DefaultShadingModel);
                    _materialDomain = root.GetOrDefault(nameof(MaterialDomain), DefaultMaterialDomain);
                    _twoSided = root.GetOrDefault(nameof(TwoSided), DefaultTwoSided);
                    _opacityMaskClipValue = root.GetOrDefault(nameof(OpacityMaskClipValue), DefaultOpacityMaskClipValue);

                    for (var i = 0; i < root.ReferencedTextures.Count; i++)
                    {
                        if (root.ReferencedTextures[i] is not { IsNull: false } texture)
                            continue;
                        _referencedTextures.Add(new TextureParameter(texture, provider));
                    }

                    ReadExpressions(root);
                    ReadCachedData(root);
                    ReadShaderMaps(root);
                    break;
                }
        }

        void ReadParameterValues(FTextureParameterValue[] textures, FScalarParameterValue[] scalars, FVectorParameterValue[] vectors)
        {
            foreach (var parameter in textures)
            {
                if (parameter.ParameterValue is { IsNull: false } value)
                {
                    var texParam = new TextureParameter(value, provider);
                    var assoc = parameter.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
                    var idx = parameter.ParameterInfo?.Index ?? -1;
                    _texturesByKey[new MaterialParameterKey(parameter.Name, assoc, idx)] = texParam;
                    if (assoc == EMaterialParameterAssociation.GlobalParameter)
                        _textures[parameter.Name] = texParam;
                    else
                        _textures.TryAdd(parameter.Name, texParam);
                }
            }
            foreach (var parameter in scalars)
            {
                var assoc = parameter.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
                var idx = parameter.ParameterInfo?.Index ?? -1;
                _scalarsByKey[new MaterialParameterKey(parameter.Name, assoc, idx)] = parameter.ParameterValue;
                if (assoc == EMaterialParameterAssociation.GlobalParameter)
                    _scalars[parameter.Name] = parameter.ParameterValue;
                else
                    _scalars.TryAdd(parameter.Name, parameter.ParameterValue);
            }
            foreach (var parameter in vectors)
            {
                if (parameter.ParameterValue is { } value)
                {
                    var assoc = parameter.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
                    var idx = parameter.ParameterInfo?.Index ?? -1;
                    _vectorsByKey[new MaterialParameterKey(parameter.Name, assoc, idx)] = value;
                    if (assoc == EMaterialParameterAssociation.GlobalParameter)
                        _vectors[parameter.Name] = value;
                    else
                        _vectors.TryAdd(parameter.Name, value);
                }
            }
        }

        void ReadExpressions(UMaterial root)
        {
            foreach (var index in root.Expressions)
            {
                if (!index.TryLoad<UMaterialExpression>(out var expression))
                    continue;

                switch (expression)
                {
                    case UMaterialExpressionTextureSampleParameter { Texture: { IsNull: false } texture } sample:
                    {
                        var tp = new TextureParameter(texture, provider);
                        _textures.TryAdd(sample.ParameterName.Text, tp);
                        _texturesByKey.TryAdd(new MaterialParameterKey(sample.ParameterName.Text), tp);
                        break;
                    }
                    case UMaterialExpressionTextureBase { Texture: { IsNull: false } texture }:
                        if (!_referencedTextures.Exists(x => x.Name == texture.Name))
                            _referencedTextures.Add(new TextureParameter(texture, provider));
                        break;
                    case UMaterialExpressionVectorParameter vector:
                        _vectors.TryAdd(vector.ParameterName.Text, vector.DefaultValue);
                        _vectorsByKey.TryAdd(new MaterialParameterKey(vector.ParameterName.Text), vector.DefaultValue);
                        break;
                    case UMaterialExpressionScalarParameter scalar:
                        _scalars.TryAdd(scalar.ParameterName.Text, scalar.DefaultValue);
                        _scalarsByKey.TryAdd(new MaterialParameterKey(scalar.ParameterName.Text), scalar.DefaultValue);
                        break;
                    case UMaterialExpressionStaticBoolParameter @switch:
                        _switches.TryAdd(@switch.ParameterName.Text, @switch.DefaultValue);
                        _switchesByKey.TryAdd(new MaterialParameterKey(@switch.ParameterName.Text), @switch.DefaultValue);
                        break;
                }
            }
        }

        void ReadCachedData(UMaterialInterface cooked)
        {
            if (cooked.CachedExpressionData is { } cached)
                ReadParameterDefaults(cached);

            foreach (var index in cooked.GetOrDefault<FPackageIndex?[]>("CachedReferencedTextures", []))
            {
                if (index is { IsNull: false } && !_referencedTextures.Exists(x => x.Name == index.Name))
                    _referencedTextures.Add(new TextureParameter(index, provider));
            }
        }

        void ReadShaderMaps(UMaterialInterface cooked)
        {
            if (cooked.LoadedMaterialResources is not { Count: > 0 } resources)
                return;

            var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scalars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var vectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var resource in resources)
            {
                if (resource.LoadedShaderMap?.Content is not FMaterialShaderMapContent { MaterialCompilationOutput.UniformExpressionSet: { } uniforms })
                    continue;

                foreach (var parameters in uniforms.UniformTextureParameters)
                    foreach (var parameter in parameters ?? [])
                        Add(textures, parameter);

                foreach (var parameter in uniforms.UniformScalarParameters)
                    Add(scalars, parameter);

                foreach (var parameter in uniforms.UniformVectorParameters)
                    Add(vectors, parameter);

                foreach (var parameter in uniforms.UniformNumericParameters)
                {
                    switch (parameter.ParameterType)
                    {
                        case EMaterialParameterType.Scalar:
                            Add(scalars, parameter);
                            break;
                        case EMaterialParameterType.Vector or EMaterialParameterType.DoubleVector:
                            Add(vectors, parameter);
                            break;
                    }
                }
            }

            _shaderParameters = new ShaderParameters(textures, scalars, vectors);

            void Add(HashSet<string> names, object parameter)
            {
                var name = parameter switch
                {
                    FMaterialBaseParameterInfo { ParameterInfo.Name: { IsNone: false } value } => value.Text,
                    FMaterialBaseParameterInfo { ParameterName: not (null or "None") and var value } => value,
                    FMaterialNumericParameterInfo { ParameterInfo.Name: { IsNone: false } value } => value.Text,
                    _ => null
                };

                if (name != null)
                    names.Add(name);
            }
        }

        void ReadParameterDefaults(FStructFallback cached)
        {
            var legacy = cached.TryGetValue(out FStructFallback nested, "Parameters");
            var data = legacy ? nested : cached;
            var infos = legacy ? "ParameterInfos" : "ParameterInfoSet";

            if (data.TryGetValue(out ulong propertyConnectedMask, "PropertyConnectedMask"))
                _propertyConnectedMask = propertyConnectedMask;
            else if (data.TryGetValue(out uint propertyConnectedBitmask, "PropertyConnectedBitmask"))
                _propertyConnectedMask = propertyConnectedBitmask;

            if (!data.TryGetAllValues(out FStructFallback[] entries, "RuntimeEntries"))
                return;

            if (entries.Length > 0 && data.TryGetValue(out float[] scalars, "ScalarValues") &&
                entries[0].TryGetValue(out FMaterialParameterInfo[] scalarInfos, infos))
            {
                for (var i = 0; i < scalarInfos.Length && i < scalars.Length; i++)
                {
                    var info = scalarInfos[i];
                    _scalarsByKey.TryAdd(new MaterialParameterKey(info.Name.Text, info.Association, info.Index), scalars[i]);
                    if (info.Association == EMaterialParameterAssociation.GlobalParameter)
                        _scalars.TryAdd(info.Name.Text, scalars[i]);
                }
                for (var i = 0; i < scalarInfos.Length && i < scalars.Length; i++)
                    _scalars.TryAdd(scalarInfos[i].Name.Text, scalars[i]);
            }

            if (entries.Length > 1 && data.TryGetValue(out FLinearColor[] vectors, "VectorValues") &&
                entries[1].TryGetValue(out FMaterialParameterInfo[] vectorInfos, infos))
            {
                for (var i = 0; i < vectorInfos.Length && i < vectors.Length; i++)
                {
                    var info = vectorInfos[i];
                    _vectorsByKey.TryAdd(new MaterialParameterKey(info.Name.Text, info.Association, info.Index), vectors[i]);
                    if (info.Association == EMaterialParameterAssociation.GlobalParameter)
                        _vectors.TryAdd(info.Name.Text, vectors[i]);
                }
                for (var i = 0; i < vectorInfos.Length && i < vectors.Length; i++)
                    _vectors.TryAdd(vectorInfos[i].Name.Text, vectors[i]);
            }

            if (!legacy && entries.Length > 2 && data.TryGetValue(out FVector4[] doubleVectors, "DoubleVectorValues") &&
                entries[2].TryGetValue(out FMaterialParameterInfo[] doubleVectorInfos, infos))
            {
                for (var i = 0; i < doubleVectorInfos.Length && i < doubleVectors.Length; i++)
                {
                    var info = doubleVectorInfos[i];
                    var v = doubleVectors[i];
                    var lc = new FLinearColor(v.X, v.Y, v.Z, v.W);
                    _vectorsByKey.TryAdd(new MaterialParameterKey(info.Name.Text, info.Association, info.Index), lc);
                    if (info.Association == EMaterialParameterAssociation.GlobalParameter)
                        _vectors.TryAdd(info.Name.Text, lc);
                }
                for (var i = 0; i < doubleVectorInfos.Length && i < doubleVectors.Length; i++)
                {
                    var v = doubleVectors[i];
                    _vectors.TryAdd(doubleVectorInfos[i].Name.Text, new FLinearColor(v.X, v.Y, v.Z, v.W));
                }
            }

            if (!legacy)
            {
                var switchEntry = material.Owner?.Provider?.Versions.Game switch
                {
                    >= EGame.GAME_UE5_5 => (int) EMaterialParameterType.StaticSwitch,
                    >= EGame.GAME_UE5_2 => 7,
                    _ => 6
                };

                if (entries.Length > switchEntry && data.TryGetValue(out bool[] switches, "StaticSwitchValues") &&
                    entries[switchEntry].TryGetValue(out FMaterialParameterInfo[] switchInfos, infos))
                {
                    for (var i = 0; i < switchInfos.Length && i < switches.Length; i++)
                    {
                        var info = switchInfos[i];
                        _switchesByKey.TryAdd(new MaterialParameterKey(info.Name.Text, info.Association, info.Index), switches[i]);
                        _switches.TryAdd(info.Name.Text, switches[i]);
                    }
                }
            }

            var textureEntry = legacy ? 2 : 3;
            if (entries.Length <= textureEntry || !entries[textureEntry].TryGetValue(out FMaterialParameterInfo[] textureInfos, infos))
                return;

            if (legacy)
            {
                if (!data.TryGetValue(out FPackageIndex[] indices, "TextureValues"))
                    return;
                for (var i = 0; i < textureInfos.Length && i < indices.Length; i++)
                {
                    if (indices[i] is { IsNull: false } index)
                    {
                        var info = textureInfos[i];
                        var tp = new TextureParameter(index, provider);
                        _texturesByKey.TryAdd(new MaterialParameterKey(info.Name.Text, info.Association, info.Index), tp);
                        if (info.Association == EMaterialParameterAssociation.GlobalParameter)
                            _textures.TryAdd(info.Name.Text, tp);
                    }
                }
                for (var i = 0; i < textureInfos.Length && i < indices.Length; i++)
                {
                    if (indices[i] is { IsNull: false } index)
                        _textures.TryAdd(textureInfos[i].Name.Text, new TextureParameter(index, provider));
                }
            }
            else
            {
                if (!data.TryGetValue(out FSoftObjectPath[] paths, "TextureValues"))
                    return;
                for (var i = 0; i < textureInfos.Length && i < paths.Length; i++)
                {
                    var info = textureInfos[i];
                    var tp = new TextureParameter(paths[i], provider);
                    _texturesByKey.TryAdd(new MaterialParameterKey(info.Name.Text, info.Association, info.Index), tp);
                    if (info.Association == EMaterialParameterAssociation.GlobalParameter)
                        _textures.TryAdd(info.Name.Text, tp);
                }
                for (var i = 0; i < textureInfos.Length && i < paths.Length; i++)
                {
                    _textures.TryAdd(textureInfos[i].Name.Text, new TextureParameter(paths[i], provider));
                }
            }
        }
    }

    public bool UsesMaterialAttributes => PropertyConnectedMask is { } mask && mask != 0 && ((mask >> (int) EMaterialProperty.MP_MaterialAttributes) & 0x1) != 0;

    public bool IsPropertyConnected(EMaterialProperty property)
    {
        if (PropertyConnectedMask is not { } mask || mask == 0)
            return true;
        if (UsesMaterialAttributes)
            return true;
        return ((mask >> (int) property) & 0x1) != 0;
    }

    public bool IsUnlit => ShadingModel is EMaterialShadingModel.MSM_Unlit;
    public bool IsWater => ShadingModel.ToString() == "MSM_SingleLayerWater";
    public bool IsDecal => MaterialDomain is EMaterialDomain.MD_DeferredDecal;
    public bool IsUI => MaterialDomain is EMaterialDomain.MD_UI;
    public bool IsPostProcess => MaterialDomain is EMaterialDomain.MD_PostProcess;
    public bool IsLightFunction => MaterialDomain is EMaterialDomain.MD_LightFunction;
    public bool IsVolume => MaterialDomain is EMaterialDomain.MD_Volume;

    public bool IsEmissiveDriven => IsUnlit || IsUI || IsPostProcess || IsLightFunction || IsVolume ||
                                    (!IsPropertyConnected(EMaterialProperty.MP_BaseColor) && IsPropertyConnected(EMaterialProperty.MP_EmissiveColor));

    public bool IsTranslucentBlend => BlendMode is not (EBlendMode.BLEND_Opaque or EBlendMode.BLEND_Masked);

    public EMaterialPass Pass => BlendMode switch
    {
        EBlendMode.BLEND_Opaque => EMaterialPass.Opaque,
        EBlendMode.BLEND_Masked => EMaterialPass.Masked,
        EBlendMode.BLEND_Translucent => EMaterialPass.Translucent,
        EBlendMode.BLEND_Additive => EMaterialPass.Additive,
        EBlendMode.BLEND_Modulate => EMaterialPass.Modulate,
        var other when other.ToString().Contains("AlphaComposite", StringComparison.Ordinal) => EMaterialPass.AlphaComposite,
        _ => EMaterialPass.Translucent
    };

    public MaterialBlend Blend => Pass switch
    {
        EMaterialPass.Translucent => new MaterialBlend(true, EBlendFactor.SrcAlpha, EBlendFactor.InvSrcAlpha, EBlendFactor.Zero, EBlendFactor.InvSrcAlpha, false),
        EMaterialPass.Additive => new MaterialBlend(true, EBlendFactor.One, EBlendFactor.One, EBlendFactor.Zero, EBlendFactor.InvSrcAlpha, false),
        EMaterialPass.Modulate => new MaterialBlend(true, EBlendFactor.DestColor, EBlendFactor.Zero, EBlendFactor.One, EBlendFactor.Zero, false),
        EMaterialPass.AlphaComposite => new MaterialBlend(true, EBlendFactor.One, EBlendFactor.InvSrcAlpha, EBlendFactor.One, EBlendFactor.InvSrcAlpha, true),
        _ => new MaterialBlend(false, EBlendFactor.One, EBlendFactor.Zero, EBlendFactor.One, EBlendFactor.Zero, true)
    };

    public bool TryGetTexture([MaybeNullWhen(false)] out UTexture texture, params string[] names)
    {
        var active = Active(x => x.Textures);
        HashSet<string>? nulledOverrides = null;
        foreach (var name in names)
        {
            if (active != null && !active.Contains(name))
                continue;
            if (TryResolveTextureOverride(name, _provider, out var overridden))
            {
                if (overridden is not null)
                {
                    texture = overridden;
                    return true;
                }
                (nulledOverrides ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(name);
            }
        }

        for (var node = this; node != null; node = node.Parent)
        {
            if (node._textures.Count == 0)
                continue;
            foreach (var name in names)
            {
                if (nulledOverrides is not null && nulledOverrides.Contains(name))
                    continue;
                if (active != null && !active.Contains(name))
                    continue;
                if (node._textures.TryGetValue(name, out var parameter) && parameter.Texture.Value is { } found)
                {
                    texture = found;
                    return true;
                }
            }
        }

        texture = null;
        return false;
    }

    public bool TryGetTexture([MaybeNullWhen(false)] out UTexture texture, EMaterialTextureKind kind)
    {
        var active = Active(x => x.Textures);
        for (var node = this; node != null; node = node.Parent)
        {
            foreach (var (name, parameter) in node._textures)
            {
                if (active != null && !active.Contains(name))
                    continue;
                if (parameter.Texture.Value is UTexture2D found && Classify(name, found) == kind)
                {
                    texture = found;
                    return true;
                }
            }

            foreach (var referenced in node._referencedTextures)
            {
                if (referenced.Texture.Value is UTexture2D found && Classify(referenced.Name, found) == kind)
                {
                    texture = found;
                    return true;
                }
            }
        }

        texture = null;
        return false;
    }

    public bool HasTexture(string name)
    {
        if (Active(x => x.Textures) is { } active && !active.Contains(name))
            return false;

        for (var node = this; node != null; node = node.Parent)
        {
            if (node._textures.ContainsKey(name))
                return true;
        }

        return false;
    }

    public bool TryGetScalar(out float scalar, params string[] names) => TryGet(x => x._scalars, Active(x => x.Scalars), out scalar, names);
    public bool TryGetVector(out FLinearColor vector, params string[] names) => TryGet(x => x._vectors, Active(x => x.Vectors), out vector, names);
    public bool TryGetSwitch(out bool value, params string[] names) => TryGet(x => x._switches, null, out value, names);

    private IReadOnlySet<string>? Active(Func<ShaderParameters, HashSet<string>> kind)
    {
        return ActiveParameters is { } parameters && kind(parameters) is { Count: > 0 } active ? active : null;
    }

    private bool TryGet<T>(Func<MaterialAsset, Dictionary<string, T>> values, IReadOnlySet<string>? active, out T value, string[] names) where T : struct
    {
        for (var node = this; node != null; node = node.Parent)
        {
            var own = values(node);
            if (own.Count == 0)
                continue;

            foreach (var name in names)
            {
                if (active != null && !active.Contains(name))
                    continue;
                if (own.TryGetValue(name, out value))
                    return true;
            }
        }

        value = default;
        return false;
    }

    private EMaterialTextureKind Classify(string parameterName, UTexture texture)
    {
        if (texture.CompressionSettings is TextureCompressionSettings.TC_Normalmap || texture.LODGroup is
                TextureGroup.TEXTUREGROUP_WorldNormalMap or TextureGroup.TEXTUREGROUP_CharacterNormalMap or
                TextureGroup.TEXTUREGROUP_WeaponNormalMap or TextureGroup.TEXTUREGROUP_VehicleNormalMap)
            return EMaterialTextureKind.Normal;

        if (texture.CompressionSettings is TextureCompressionSettings.TC_Masks || texture.LODGroup is
                TextureGroup.TEXTUREGROUP_WorldSpecular or TextureGroup.TEXTUREGROUP_CharacterSpecular or
                TextureGroup.TEXTUREGROUP_WeaponSpecular or TextureGroup.TEXTUREGROUP_VehicleSpecular)
            return EMaterialTextureKind.SpecularMasks;

        if (texture.CompressionSettings is not (TextureCompressionSettings.TC_Default or TextureCompressionSettings.TC_BC7 or TextureCompressionSettings.TC_LQ) ||
            texture.LODGroup is TextureGroup.TEXTUREGROUP_UI or TextureGroup.TEXTUREGROUP_Lightmap or TextureGroup.TEXTUREGROUP_Shadowmap or
                TextureGroup.TEXTUREGROUP_Skybox or TextureGroup.TEXTUREGROUP_ImpostorNormalDepth || _occlusionName.IsMatch(parameterName))
            return EMaterialTextureKind.Other;

        if (_diffuseName.IsMatch(parameterName))
            return EMaterialTextureKind.Diffuse;
        if (!texture.SRGB)
        {
            if (_normalName.IsMatch(parameterName))
                return EMaterialTextureKind.Normal;
            if (_specularName.IsMatch(parameterName))
                return EMaterialTextureKind.SpecularMasks;
        }
        if (_emissiveName.IsMatch(parameterName))
            return EMaterialTextureKind.Emissive;

        return texture.SRGB ? EMaterialTextureKind.Diffuse : EMaterialTextureKind.Other;
    }

    [GeneratedRegex(@"Occlu|(?:^|_)AO(?:_|\d*$)|(?-i:(?<![A-Z])AO(?![a-z]))", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex RegexOcclusion();
    private static readonly Regex _occlusionName = RegexOcclusion();

    [GeneratedRegex(CMaterialParams2.RegexDiffuse, RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex RegexDiffuse();
    private static readonly Regex _diffuseName = RegexDiffuse();

    [GeneratedRegex(CMaterialParams2.RegexNormals, RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex RegexNormals();
    private static readonly Regex _normalName = RegexNormals();

    [GeneratedRegex(CMaterialParams2.RegexSpecularMasks, RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex RegexSpecularMasks();
    private static readonly Regex _specularName = RegexSpecularMasks();

    [GeneratedRegex(CMaterialParams2.RegexEmissive, RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex RegexEmissive();
    private static readonly Regex _emissiveName = RegexEmissive();

    private sealed class TextureParameter
    {
        public readonly string Name;
        public readonly string Path;
        public readonly Lazy<UTexture?> Texture;

        private TextureParameter(string name, FSoftObjectPath path, IFileProvider? provider)
        {
            Name = name;
            Path = path.ToString();
            Texture = new Lazy<UTexture?>(() => provider is not null && path.TryLoad<UTexture>(provider, out var texture) ? texture : null, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public TextureParameter(FSoftObjectPath path, IFileProvider? provider) : this(path.AssetPathName.Text.SubstringAfterLast('.'), new FSoftObjectPath(path.AssetPathName, ""), provider)
        {
        }

        public TextureParameter(FPackageIndex index, IFileProvider? provider)
        {
            Name = index.Name;
            Path = index.ResolvedObject?.GetPathName() ?? index.Name;
            Texture = new Lazy<UTexture?>(() => index.TryLoad<UTexture>(out var texture) ? texture : null, LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    private ParameterView? _view;
    internal void DrawControls(string filter)
    {
        var chain = new List<MaterialAsset>();
        for (var node = this; node != null; node = node.Parent)
        {
            chain.Add(node);
        }

        var version = ParameterVersion;
        if (_view is not { } view || view.Filter != filter || view.ChainLength != chain.Count || view.Version != version)
        {
            _view = view = Build();
        }

        foreach (var (title, open, rows) in view.Kinds)
        {
            if (rows.Count == 0)
                continue;

            if (!ImGui.TreeNodeEx($"{title} ({rows.Count})###{title}", (open ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None) | ImGuiTreeNodeFlags.SpanAvailWidth))
                continue;
            if (!ImGui.BeginTable($"##{title}", 2))
            {
                ImGui.TreePop();
                continue;
            }

            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 1f);

            for (var i = 0; i < rows.Count; i++)
            {
                DrawRow(title, rows[i]);
            }

            ImGui.EndTable();
            ImGui.TreePop();
        }

        ParameterView Build()
        {
            var parameters = chain.Find(node => node._shaderParameters != null)?._shaderParameters;
            var kinds = new (string Title, bool Open, Func<MaterialAsset, IEnumerable<Entry>> Read, IReadOnlySet<string>? Active)[]
            {
                ("Base", true, Base, null),
                ("Scalars", true, node => node._scalars.Select(x => new Entry(x.Key, x.Value.ToString("0.####"))), parameters?.Scalars),
                ("Switches", true, node => node._switches.Select(x => new Entry(x.Key, x.Value.ToString())), null),
                ("Vectors", true, node => node._vectors.Select(x => new Entry(x.Key, $"{x.Value.R:0.###}, {x.Value.G:0.###}, {x.Value.B:0.###}, {x.Value.A:0.###}", Color: x.Value)), parameters?.Vectors),
                ("Textures", true, node => node._textures.Select(x => new Entry(x.Key, x.Value.Name, x.Value.Path)), parameters?.Textures),
                ("Connected", false, Connected, null),
                ("Referenced", false, node => node._referencedTextures.Select(x => new Entry(x.Name, string.Empty, x.Path)), null),
            };

            var built = new (string Title, bool Open, List<Row> Rows)[kinds.Length];
            for (var k = 0; k < kinds.Length; k++)
            {
                var (title, open, read, active) = kinds[k];
                var rows = new List<Row>();
                var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in chain)
                {
                    foreach (var entry in read(node))
                    {
                        if (!index.TryGetValue(entry.Name, out var i))
                        {
                            index[entry.Name] = i = rows.Count;
                            rows.Add(new Row(entry.Name, [], active is { Count: > 0 } && !active.Contains(entry.Name)));
                        }
                        rows[i].Writers.Add((node, entry));
                    }
                }

                if (filter.Length > 0)
                    rows.RemoveAll(row => !row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) && !row.Writers.Exists(x => x.Entry.Display.Contains(filter, StringComparison.OrdinalIgnoreCase)));
                built[k] = (title, open, rows);
            }

            return new ParameterView(filter, chain.Count, version, built);
        }

        void DrawRow(string title, Row row)
        {
            var (name, writers, inactive) = row;
            var entry = writers[0].Entry;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (inactive)
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
            if (ImGui.Selectable($"{name}##{title}", false, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap))
            {
                var onName = ImGui.TableGetHoveredColumn() == 0;
                ImGui.SetClipboardText(onName ? name : entry.Path ?? entry.Value);
            }

            var replaced = writers.Skip(1).Where(x => x.Entry.Display != entry.Display).DistinctBy(x => x.Entry.Display).ToList();
            if (ImGui.IsItemHovered() && (replaced.Count > 0 || inactive))
            {
                ImGui.BeginTooltip();
                if (inactive)
                    ImGui.TextUnformatted("Not sampled by the shader");
                foreach (var (node, value) in replaced)
                {
                    ImGui.TextDisabled($"Parent {chain.IndexOf(node)}");
                    ImGui.SameLine();
                    if (value.Color is { } replacedColor)
                    {
                        ImGui.ColorButton($"##{title}{name}{node.Name}", new Vector4(replacedColor.R, replacedColor.G, replacedColor.B, replacedColor.A), ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoPicker, new Vector2(ImGui.GetTextLineHeight()));
                        ImGui.SameLine();
                    }
                    ImGui.TextUnformatted(value.Display);
                }
                ImGui.EndTooltip();
            }

            if (replaced.Count > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"+{replaced.Count}");
            }

            ImGui.TableNextColumn();
            if (title == "Switches")
            {
                var swVal = string.Equals(entry.Value, "True", StringComparison.OrdinalIgnoreCase);
                ImGui.BeginDisabled();
                ImGui.Checkbox($"##sw_{name}", ref swVal);
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.TextDisabled("(static)");
            }
            else
            {
                if (entry.Color is { } c)
                {
                    ImGui.ColorButton($"##{title}{name}", new Vector4(c.R, c.G, c.B, c.A), ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoPicker, new Vector2(ImGui.GetTextLineHeight()));
                    ImGui.SameLine();
                }
                ImGui.TextUnformatted(entry.Value);
            }
            if (inactive)
                ImGui.PopStyleColor();
        }

        static IEnumerable<Entry> Base(MaterialAsset node)
        {
            if (node._blendMode is { } blendMode)
                yield return new Entry("BlendMode", blendMode.ToString());
            if (node._shadingModel is { } shadingModel)
                yield return new Entry("ShadingModel", shadingModel.ToString());
            if (node._materialDomain is { } materialDomain)
                yield return new Entry("MaterialDomain", materialDomain.ToString());
            if (node._twoSided is { } twoSided)
                yield return new Entry("TwoSided", twoSided.ToString());
        }

        static IEnumerable<Entry> Connected(MaterialAsset node)
        {
            if (node._propertyConnectedMask is null or 0)
                yield break;

            var names = Enum.GetNames<EMaterialProperty>();
            var values = Enum.GetValues<EMaterialProperty>();
            for (var i = 0; i < names.Length; i++)
            {
                if (values[i] >= EMaterialProperty.MP_MAX ||
                    names[i] is nameof(EMaterialProperty.MP_LastCustomizedUVs) or nameof(EMaterialProperty.MP_NumCustomizedUVs))
                    continue;

                var connected = node.IsPropertyConnected(values[i]);
                if (!connected)
                    continue;

                yield return new Entry(names[i][3..], connected.ToString());
            }
        }
    }

    private readonly record struct Entry(string Name, string Value, string? Path = null, FLinearColor? Color = null)
    {
        public string Display => Value.Length > 0 ? Value : Path ?? string.Empty;
    }
    private readonly record struct Row(string Name, List<(MaterialAsset Node, Entry Entry)> Writers, bool Inactive);
    private sealed record ShaderParameters(HashSet<string> Textures, HashSet<string> Scalars, HashSet<string> Vectors);
    private sealed record ParameterView(string Filter, int ChainLength, long Version, (string Title, bool Open, List<Row> Rows)[] Kinds);
}
