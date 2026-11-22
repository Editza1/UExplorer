using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Niagara;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using Serilog;

namespace UExplorer.Rendering;

/// <summary>
/// 1:1 Unreal Engine 4.27 / 5.2 Niagara parameter store, data interface, and VectorVM bytecode interpreter.
/// </summary>
public sealed class NiagaraParameterStoreData
{
    public sealed record Entry(string Name, string TypeName, int Offset);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UObject?> _dataInterfacesByName = new(StringComparer.OrdinalIgnoreCase);
    public byte[] ParameterData { get; private set; } = [];
    public List<UObject?> DataInterfaces { get; } = [];

    public IReadOnlyDictionary<string, Entry> Entries => _entries;

    public static NiagaraParameterStoreData FromStruct(FStructFallback? store)
    {
        var result = new NiagaraParameterStoreData();
        if (store is null)
            return result;

        if (store.TryGetValue(out byte[] paramData, "ParameterData") && paramData is not null)
            result.ParameterData = (byte[]) paramData.Clone();

        if (store.TryGetValue(out FPackageIndex[] diIndices, "DataInterfaces") && diIndices is not null)
        {
            foreach (var idx in diIndices)
            {
                UObject? loaded = null;
                if (idx is { IsNull: false })
                    idx.TryLoad(out loaded);
                result.DataInterfaces.Add(loaded);
                if (loaded is not null)
                {
                    var exposedName = SafeFName(loaded, "ExposedName");
                    if (!string.IsNullOrEmpty(exposedName) && !string.Equals(exposedName, "None", StringComparison.OrdinalIgnoreCase))
                        result._dataInterfacesByName[exposedName] = loaded;
                }
            }
        }

        if (store.TryGetValue(out FNiagaraVariableWithOffset[] sortedOffsets, "SortedParameterOffsets") && sortedOffsets is not null)
        {
            foreach (var v in sortedOffsets)
            {
                if (v is null) continue;
                var name = v.Name.IsNone ? "" : v.Name.Text;
                if (string.IsNullOrEmpty(name) || string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
                    continue;
                var typeName = ExtractTypeName(v.TypeDef);
                result._entries[name] = new Entry(name, typeName, v.Offset);
            }
        }
        else if (store.TryGetValue(out FStructFallback[] fallbackOffsets, "SortedParameterOffsets") && fallbackOffsets is not null)
        {
            foreach (var fb in fallbackOffsets)
            {
                if (fb is null) continue;
                var name = SafeFName(fb, "Name");
                if (string.IsNullOrEmpty(name) || string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
                    continue;
                var offset = fb.GetOrDefault("Offset", -1);
                var typeName = fb.TryGetValue(out FStructFallback td, "TypeDef") ? ExtractTypeName(td) : "";
                if (offset >= 0)
                    result._entries[name] = new Entry(name, typeName, offset);
            }
        }

        // Older UE4 parameter stores also use UScriptMap ParameterOffsets
        if (result._entries.Count == 0 && store.TryGetValue(out UScriptMap paramOffsetsMap, "ParameterOffsets") && paramOffsetsMap is not null)
        {
            foreach (var kv in paramOffsetsMap.Properties)
            {
                var keyVar = kv.Key?.GenericValue;
                var offset = kv.Value?.GenericValue is int o ? o : -1;
                if (offset < 0)
                    continue;

                if (keyVar is FNiagaraVariableBase nv)
                {
                    var name = nv.Name.IsNone ? "" : nv.Name.Text;
                    if (!string.IsNullOrEmpty(name))
                        result._entries[name] = new Entry(name, ExtractTypeName(nv.TypeDef), offset);
                }
                else if (keyVar is FStructFallback fb)
                {
                    var name = SafeFName(fb, "Name");
                    var typeName = fb.TryGetValue(out FStructFallback td, "TypeDef") ? ExtractTypeName(td) : "";
                    if (!string.IsNullOrEmpty(name))
                        result._entries[name] = new Entry(name, typeName, offset);
                }
            }
        }

        return result;
    }

    internal static string SafeFName(IPropertyHolder? holder, string propName)
    {
        if (holder is null) return "";
        if (holder.TryGetValue(out FName fn, propName) && !fn.IsNone)
            return fn.Text ?? "";
        if (holder.TryGetValue(out string s, propName) && !string.IsNullOrEmpty(s))
            return s;
        return "";
    }

    public NiagaraParameterStoreData Clone()
    {
        var copy = new NiagaraParameterStoreData
        {
            ParameterData = ParameterData.Length > 0 ? (byte[]) ParameterData.Clone() : []
        };
        foreach (var (k, v) in _entries)
            copy._entries[k] = v;
        copy.DataInterfaces.AddRange(DataInterfaces);
        foreach (var (k, v) in _dataInterfacesByName)
            copy._dataInterfacesByName[k] = v;
        return copy;
    }

    public void PostTickCopyCurrentToPrev()
    {
        if (ParameterData.Length < 8)
            return;
        var hasPrev = false;
        foreach (var k in _entries.Keys)
        {
            if (k.StartsWith("PREV_", StringComparison.OrdinalIgnoreCase))
            {
                hasPrev = true;
                break;
            }
        }
        if (!hasPrev)
            return;
        var half = ParameterData.Length / 2;
        if (half > 0)
            Buffer.BlockCopy(ParameterData, 0, ParameterData, half, half);
    }

    public bool SetFloat(string name, float value)
    {
        if (!_entries.TryGetValue(name, out var e) || e.Offset < 0 || e.Offset + 4 > ParameterData.Length)
            return false;
        return BitConverter.TryWriteBytes(ParameterData.AsSpan(e.Offset, 4), value);
    }

    public bool SetInt(string name, int value)
    {
        if (!_entries.TryGetValue(name, out var e) || e.Offset < 0 || e.Offset + 4 > ParameterData.Length)
            return false;
        return BitConverter.TryWriteBytes(ParameterData.AsSpan(e.Offset, 4), value);
    }

    public bool SetRawBytes(string name, ReadOnlySpan<byte> bytes)
    {
        if (!_entries.TryGetValue(name, out var e) || e.Offset < 0 || e.Offset + bytes.Length > ParameterData.Length)
            return false;
        bytes.CopyTo(ParameterData.AsSpan(e.Offset, bytes.Length));
        return true;
    }

    public void ApplyOverridesFrom(NiagaraParameterStoreData? overrides)
    {
        if (overrides is null || overrides.ParameterData.Length == 0 || ParameterData.Length == 0)
            return;

        foreach (var (name, srcEntry) in overrides._entries)
        {
            if (!_entries.TryGetValue(name, out var dstEntry))
                continue;

            var size = GetTypeByteSize(srcEntry.TypeName);
            if (size <= 0)
                size = GetTypeByteSize(dstEntry.TypeName);
            if (size <= 0)
                size = 4;

            if (srcEntry.Offset >= 0 && srcEntry.Offset + size <= overrides.ParameterData.Length &&
                dstEntry.Offset >= 0 && dstEntry.Offset + size <= ParameterData.Length)
            {
                Buffer.BlockCopy(overrides.ParameterData, srcEntry.Offset, ParameterData, dstEntry.Offset, size);
            }
        }
    }

    /// <summary>
    /// Propagates authored RapidIterationParameters (e.g. Constants.Rings.CylinderLocation.CylinderHeight or Constants.Rings.LineLength)
    /// into runtime Emitter/Particle ScriptExecutionParamStore slots (e.g. Rings.LineLength, PREV_Rings.LineLength) when those slots are still zero.
    /// </summary>
    public void ApplyZeroSlotSuffixFallbacksFrom(NiagaraParameterStoreData? source)
    {
        if (source is null || source.ParameterData.Length == 0 || ParameterData.Length == 0)
            return;

        foreach (var (dstName, dstEntry) in _entries)
        {
            var size = GetTypeByteSize(dstEntry.TypeName);
            if (size <= 0 || dstEntry.Offset < 0 || dstEntry.Offset + size > ParameterData.Length)
                continue;

            var allZero = true;
            for (var b = 0; b < size; b++)
            {
                if (ParameterData[dstEntry.Offset + b] != 0) { allZero = false; break; }
            }
            if (!allZero)
                continue;

            var tail = dstName.StartsWith("PREV_", StringComparison.OrdinalIgnoreCase) ? dstName[5..] : dstName;
            var lastDot = tail.LastIndexOf('.');
            var leaf = lastDot >= 0 ? tail[(lastDot + 1)..] : tail;
            if (string.IsNullOrEmpty(leaf) || leaf.Equals("ExecutionCount", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var (srcName, srcEntry) in source._entries)
            {
                if (GetTypeByteSize(srcEntry.TypeName) != size || srcEntry.Offset < 0 || srcEntry.Offset + size > source.ParameterData.Length)
                    continue;
                if (srcName.Equals(tail, StringComparison.OrdinalIgnoreCase) ||
                    srcName.EndsWith("." + tail, StringComparison.OrdinalIgnoreCase) ||
                    srcName.EndsWith("." + leaf, StringComparison.OrdinalIgnoreCase))
                {
                    Buffer.BlockCopy(source.ParameterData, srcEntry.Offset, ParameterData, dstEntry.Offset, size);
                    allZero = false;
                    break;
                }
            }

            // SystemUpdateScript often computes Emitter.LineLength = length(LineEnd - LineStart); derive or guard against 0 divide
            if (allZero && size == 4 && leaf.Equals("LineLength", StringComparison.OrdinalIgnoreCase))
            {
                var emitPrefix = lastDot > 0 ? tail[..lastDot] : "";
                Vector3? lineStart = null, lineEnd = null;
                foreach (var (srcName, _) in source._entries)
                {
                    if (!string.IsNullOrEmpty(emitPrefix) && !srcName.Contains("." + emitPrefix + ".", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if ((srcName.EndsWith(".LineStart", StringComparison.OrdinalIgnoreCase) || srcName.EndsWith(".StartPoint", StringComparison.OrdinalIgnoreCase)) &&
                        source.TryGetVector3(srcName, out var sv))
                        lineStart = sv;
                    else if ((srcName.EndsWith(".LineEnd", StringComparison.OrdinalIgnoreCase) || srcName.EndsWith(".EndPoint", StringComparison.OrdinalIgnoreCase)) &&
                             source.TryGetVector3(srcName, out var ev))
                        lineEnd = ev;
                }
                var computedLen = lineEnd.HasValue ? Vector3.Distance(lineStart ?? Vector3.Zero, lineEnd.Value) : 0f;
                if (computedLen <= 1e-3f) computedLen = 100f;
                BitConverter.TryWriteBytes(ParameterData.AsSpan(dstEntry.Offset, 4), computedLen);
            }
        }
    }

    public bool TryGetFloat(string name, out float value)
    {
        value = 0f;
        if (!_entries.TryGetValue(name, out var e) || e.Offset < 0 || e.Offset + 4 > ParameterData.Length)
            return false;
        value = BitConverter.ToSingle(ParameterData, e.Offset);
        return float.IsFinite(value);
    }

    public int TryGetFloats(string name, Span<float> dst)
    {
        if (dst.IsEmpty || !_entries.TryGetValue(name, out var e) || e.Offset < 0 || e.Offset + 4 > ParameterData.Length)
            return 0;
        var count = Math.Min(dst.Length, (ParameterData.Length - e.Offset) / 4);
        for (var c = 0; c < count; c++)
        {
            var f = BitConverter.ToSingle(ParameterData, e.Offset + c * 4);
            if (!float.IsFinite(f))
                return 0;
            dst[c] = f;
        }
        return count;
    }

    public bool TryGetInt(string name, out int value)
    {
        value = 0;
        if (!_entries.TryGetValue(name, out var e) || e.Offset < 0 || e.Offset + 4 > ParameterData.Length)
            return false;
        value = BitConverter.ToInt32(ParameterData, e.Offset);
        return true;
    }

    public int TryGetInts(string name, Span<int> dst)
    {
        if (dst.IsEmpty || !_entries.TryGetValue(name, out var e) || e.Offset < 0 || e.Offset + 4 > ParameterData.Length)
            return 0;
        var count = Math.Min(dst.Length, (ParameterData.Length - e.Offset) / 4);
        for (var c = 0; c < count; c++)
            dst[c] = BitConverter.ToInt32(ParameterData, e.Offset + c * 4);
        return count;
    }

    public bool TryGetBool(string name, out bool value)
    {
        value = false;
        if (!TryGetInt(name, out var iv))
            return false;
        value = iv != 0;
        return true;
    }

    public bool TryGetVector2(string name, out Vector2 value)
    {
        value = Vector2.Zero;
        if (!_entries.TryGetValue(name, out var e) || e.Offset < 0 || e.Offset + 8 > ParameterData.Length)
            return false;
        value = new Vector2(
            BitConverter.ToSingle(ParameterData, e.Offset),
            BitConverter.ToSingle(ParameterData, e.Offset + 4));
        return float.IsFinite(value.X) && float.IsFinite(value.Y);
    }

    public bool TryGetVector3(string name, out Vector3 value)
    {
        value = Vector3.Zero;
        if (!_entries.TryGetValue(name, out var e) || e.Offset < 0 || e.Offset + 12 > ParameterData.Length)
            return false;
        value = new Vector3(
            BitConverter.ToSingle(ParameterData, e.Offset),
            BitConverter.ToSingle(ParameterData, e.Offset + 4),
            BitConverter.ToSingle(ParameterData, e.Offset + 8));
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    public bool TryGetVector4(string name, out Vector4 value)
    {
        value = Vector4.Zero;
        if (!_entries.TryGetValue(name, out var e) || e.Offset < 0 || e.Offset + 16 > ParameterData.Length)
            return false;
        value = new Vector4(
            BitConverter.ToSingle(ParameterData, e.Offset),
            BitConverter.ToSingle(ParameterData, e.Offset + 4),
            BitConverter.ToSingle(ParameterData, e.Offset + 8),
            BitConverter.ToSingle(ParameterData, e.Offset + 12));
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    }

    public static string ExtractTypeName(FStructFallback? typeDef)
    {
        if (typeDef is null)
            return "";
        if (typeDef.TryGetValue(out FPackageIndex cls, "ClassStructOrEnum") && cls is { IsNull: false })
            return cls.Name;
        if (typeDef.TryGetValue(out FName name, "Name") && !name.IsNone)
            return name.Text;
        return "";
    }

    public static int GetTypeByteSize(string typeName) => typeName switch
    {
        "NiagaraFloat" or "float" or "NiagaraInt32" or "int32" or "NiagaraBool" or "bool" => 4,
        "Vector2D" or "Vector2f" or "FVector2D" => 8,
        "Vector" or "Vector3f" or "FVector" or "NiagaraPosition" => 12,
        "Vector4" or "Vector4f" or "LinearColor" or "Quat" or "Quat4f" or "NiagaraID" => 16,
        "Matrix" or "Matrix44f" => 64,
        _ => 4
    };
}

public sealed class NiagaraRichCurveData
{
    public readonly struct Key(float time, float value, float arriveTangent, float leaveTangent, byte interpMode)
    {
        public readonly float Time = time;
        public readonly float Value = value;
        public readonly float ArriveTangent = arriveTangent;
        public readonly float LeaveTangent = leaveTangent;
        public readonly byte InterpMode = interpMode;
    }

    public Key[] Keys { get; }
    public float DefaultValue { get; }

    public NiagaraRichCurveData(Key[] keys, float defaultValue)
    {
        Keys = keys;
        DefaultValue = defaultValue;
    }

    public static NiagaraRichCurveData? FromStruct(FStructFallback? curve)
    {
        if (curve is null)
            return null;

        var defaultValue = curve.GetOrDefault("DefaultValue", 0f);
        var keysArr = curve.GetOrDefault<FStructFallback[]>("Keys", []);
        if (keysArr.Length == 0)
            return new NiagaraRichCurveData([], defaultValue);

        var keys = new Key[keysArr.Length];
        for (var i = 0; i < keysArr.Length; i++)
        {
            var k = keysArr[i];
            if (k is null) continue;
            var modeStr = NiagaraParameterStoreData.SafeFName(k, "InterpMode");
            byte mode = modeStr.Contains("Constant", StringComparison.OrdinalIgnoreCase) ? (byte) 1
                : modeStr.Contains("Cubic", StringComparison.OrdinalIgnoreCase) ? (byte) 2
                : (byte) 0;
            keys[i] = new Key(
                k.GetOrDefault("Time", 0f),
                k.GetOrDefault("Value", 0f),
                k.GetOrDefault("ArriveTangent", 0f),
                k.GetOrDefault("LeaveTangent", 0f),
                mode);
        }

        Array.Sort(keys, static (a, b) => a.Time.CompareTo(b.Time));
        return new NiagaraRichCurveData(keys, defaultValue);
    }

    public float Eval(float time)
    {
        if (Keys.Length == 0)
            return DefaultValue;
        if (Keys.Length == 1 || time <= Keys[0].Time)
            return Keys[0].Value;
        if (time >= Keys[^1].Time)
            return Keys[^1].Value;

        for (var i = 0; i < Keys.Length - 1; i++)
        {
            var a = Keys[i];
            var b = Keys[i + 1];
            if (time <= b.Time)
            {
                var dt = b.Time - a.Time;
                if (dt <= 1e-6f)
                    return a.Value;
                if (a.InterpMode == 1)
                    return a.Value;

                var alpha = Math.Clamp((time - a.Time) / dt, 0f, 1f);
                if (a.InterpMode == 2)
                {
                    var t2 = alpha * alpha;
                    var t3 = t2 * alpha;
                    var h00 = 2f * t3 - 3f * t2 + 1f;
                    var h10 = t3 - 2f * t2 + alpha;
                    var h01 = -2f * t3 + 3f * t2;
                    var h11 = t3 - t2;
                    return h00 * a.Value + h10 * (a.LeaveTangent * dt) + h01 * b.Value + h11 * (b.ArriveTangent * dt);
                }
                return a.Value + (b.Value - a.Value) * alpha;
            }
        }
        return Keys[^1].Value;
    }
}

public sealed class NiagaraDataInterfaceRuntime
{
    public string ClassName { get; }
    public string ExposedName { get; }
    public float[] ShaderLut { get; }
    public float LutMinTime { get; }
    public float LutMaxTime { get; }
    public int LutNumSamples { get; }
    public bool UseLut { get; }
    public NiagaraRichCurveData?[] Curves { get; }
    public int Seed { get; }
    public float Frequency { get; }

    private NiagaraDataInterfaceRuntime(
        string className,
        string exposedName,
        float[] shaderLut,
        float lutMinTime,
        float lutMaxTime,
        int lutNumSamples,
        bool useLut,
        NiagaraRichCurveData?[] curves,
        int seed,
        float frequency)
    {
        ClassName = className;
        ExposedName = exposedName;
        ShaderLut = shaderLut;
        LutMinTime = lutMinTime;
        LutMaxTime = lutMaxTime;
        LutNumSamples = lutNumSamples;
        UseLut = useLut;
        Curves = curves;
        Seed = seed;
        Frequency = frequency;
    }

    public static NiagaraDataInterfaceRuntime? FromObject(UObject? obj)
    {
        if (obj is null)
            return null;

        var cls = obj.ExportType;
        var exposed = NiagaraParameterStoreData.SafeFName(obj, "ExposedName");
        var lut = obj.GetOrDefault<float[]>("ShaderLUT", []);
        var lutMin = obj.GetOrDefault("LUTMinTime", 0f);
        var lutMax = obj.GetOrDefault("LUTMaxTime", 1f);
        var lutSamples = obj.GetOrDefault("LUTNumSamples", 0);
        // UNiagaraDataInterfaceCurveBase::bUseLUT defaults to true in CDO (omitted by delta serialization in cooked assets, where FRichCurve is stripped)
        var useLut = lut.Length > 0 && obj.GetOrDefault("bUseLUT", true);

        NiagaraRichCurveData?[] curves = [];
        if (cls.Contains("ColorCurve", StringComparison.OrdinalIgnoreCase))
        {
            curves =
            [
                NiagaraRichCurveData.FromStruct(obj.GetOrDefault<FStructFallback?>("RedCurve")),
                NiagaraRichCurveData.FromStruct(obj.GetOrDefault<FStructFallback?>("GreenCurve")),
                NiagaraRichCurveData.FromStruct(obj.GetOrDefault<FStructFallback?>("BlueCurve")),
                NiagaraRichCurveData.FromStruct(obj.GetOrDefault<FStructFallback?>("AlphaCurve")),
            ];
        }
        else if (cls.Contains("Vector4Curve", StringComparison.OrdinalIgnoreCase))
        {
            curves =
            [
                NiagaraRichCurveData.FromStruct(obj.GetOrDefault<FStructFallback?>("XCurve")),
                NiagaraRichCurveData.FromStruct(obj.GetOrDefault<FStructFallback?>("YCurve")),
                NiagaraRichCurveData.FromStruct(obj.GetOrDefault<FStructFallback?>("ZCurve")),
                NiagaraRichCurveData.FromStruct(obj.GetOrDefault<FStructFallback?>("WCurve")),
            ];
        }
        else if (cls.Contains("VectorCurve", StringComparison.OrdinalIgnoreCase))
        {
            curves =
            [
                NiagaraRichCurveData.FromStruct(obj.GetOrDefault<FStructFallback?>("XCurve")),
                NiagaraRichCurveData.FromStruct(obj.GetOrDefault<FStructFallback?>("YCurve")),
                NiagaraRichCurveData.FromStruct(obj.GetOrDefault<FStructFallback?>("ZCurve")),
            ];
        }
        else if (cls.Contains("Vector2DCurve", StringComparison.OrdinalIgnoreCase))
        {
            curves =
            [
                NiagaraRichCurveData.FromStruct(obj.GetOrDefault<FStructFallback?>("XCurve")),
                NiagaraRichCurveData.FromStruct(obj.GetOrDefault<FStructFallback?>("YCurve")),
            ];
        }
        else if (cls.Contains("Curve", StringComparison.OrdinalIgnoreCase))
        {
            curves = [NiagaraRichCurveData.FromStruct(obj.GetOrDefault<FStructFallback?>("Curve"))];
        }

        var seed = obj.GetOrDefault("Seed", 0);
        var freq = obj.GetOrDefault("Frequency", 1f);
        return new NiagaraDataInterfaceRuntime(cls, exposed, lut, lutMin, lutMax, lutSamples, useLut, curves, seed, freq);
    }

    public float SampleCurve1(float t)
    {
        if (UseLut && ShaderLut.Length > 0)
            return SampleLut(t, 1, 0);
        return Curves.Length > 0 && Curves[0] is not null ? Curves[0]!.Eval(t) : 0f;
    }

    public Vector2 SampleCurve2(float t)
    {
        if (UseLut && ShaderLut.Length >= 2)
            return new Vector2(SampleLut(t, 2, 0), SampleLut(t, 2, 1));
        return new Vector2(
            Curves.Length > 0 && Curves[0] is not null ? Curves[0]!.Eval(t) : 0f,
            Curves.Length > 1 && Curves[1] is not null ? Curves[1]!.Eval(t) : 0f);
    }

    public Vector3 SampleCurve3(float t)
    {
        if (UseLut && ShaderLut.Length >= 3)
            return new Vector3(SampleLut(t, 3, 0), SampleLut(t, 3, 1), SampleLut(t, 3, 2));
        return new Vector3(
            Curves.Length > 0 && Curves[0] is not null ? Curves[0]!.Eval(t) : 0f,
            Curves.Length > 1 && Curves[1] is not null ? Curves[1]!.Eval(t) : 0f,
            Curves.Length > 2 && Curves[2] is not null ? Curves[2]!.Eval(t) : 0f);
    }

    public Vector4 SampleCurve4(float t)
    {
        if (UseLut && ShaderLut.Length >= 4)
            return new Vector4(SampleLut(t, 4, 0), SampleLut(t, 4, 1), SampleLut(t, 4, 2), SampleLut(t, 4, 3));
        return new Vector4(
            Curves.Length > 0 && Curves[0] is not null ? Curves[0]!.Eval(t) : 1f,
            Curves.Length > 1 && Curves[1] is not null ? Curves[1]!.Eval(t) : 1f,
            Curves.Length > 2 && Curves[2] is not null ? Curves[2]!.Eval(t) : 1f,
            Curves.Length > 3 && Curves[3] is not null ? Curves[3]!.Eval(t) : 1f);
    }

    private float SampleLut(float t, int stride, int channel)
    {
        var count = ShaderLut.Length / stride;
        if (count <= 0)
            return 0f;
        if (count == 1)
            return ShaderLut[channel];
        var span = MathF.Max(1e-6f, LutMaxTime - LutMinTime);
        var norm = Math.Clamp((t - LutMinTime) / span, 0f, 1f);
        var pos = norm * (count - 1);
        var i0 = Math.Clamp((int) MathF.Floor(pos), 0, count - 1);
        var i1 = Math.Min(i0 + 1, count - 1);
        var frac = pos - i0;
        var v0 = ShaderLut[i0 * stride + channel];
        var v1 = ShaderLut[i1 * stride + channel];
        return v0 + (v1 - v0) * frac;
    }

    public Vector3 SampleCurlNoise(Vector3 p)
    {
        var sp = p * Frequency + new Vector3(Seed * 17.31f, Seed * 31.7f, Seed * 47.13f);
        const float eps = 0.01f;
        var n1 = Noise3(sp + new Vector3(0, eps, 0)) - Noise3(sp - new Vector3(0, eps, 0));
        var n2 = Noise3(sp + new Vector3(0, 0, eps)) - Noise3(sp - new Vector3(0, 0, eps));
        var n3 = Noise3(sp + new Vector3(eps, 0, 0)) - Noise3(sp - new Vector3(eps, 0, 0));
        return new Vector3(n1 - n2, n2 - n3, n3 - n1) / (2f * eps);
    }

    private static float Noise3(Vector3 p)
    {
        return MathF.Sin(p.X * 1.13f + MathF.Cos(p.Y * 1.37f)) * MathF.Cos(p.Z * 1.71f + MathF.Sin(p.X * 0.89f));
    }
}

public sealed class NiagaraVmExternalFuncInfo
{
    public string Name { get; init; } = "";
    public string OwnerName { get; init; } = "";
    public bool[] InputParamLocations { get; init; } = [];
    public int NumOutputs { get; init; }
}

public sealed record NiagaraDataInterfaceCompileInfo(string Name, int UserPtrIdx);

public sealed class NiagaraVmProgram
{
    public string ScriptName { get; set; } = "";
    public byte[] ByteCode { get; init; } = [];
    public int NumTempRegisters { get; init; }
    public int NumUserPtrs { get; init; }
    public byte[] ScriptLiterals { get; init; } = [];
    public string[] Attributes { get; init; } = [];
    public NiagaraVmExternalFuncInfo[] ExternalFunctions { get; init; } = [];
    public NiagaraParameterStoreData ParamStore { get; init; } = new();
    public NiagaraDataInterfaceRuntime?[] DataInterfaces { get; init; } = [];
    public NiagaraDataInterfaceCompileInfo[] DataInterfaceInfos { get; init; } = [];
    public bool IsUe5 { get; init; }
    public bool HasInterpolationParameters => ParamStore.Entries.Keys.Any(k => k.StartsWith("PREV_", StringComparison.OrdinalIgnoreCase));

    public bool IsValid => ByteCode.Length > 0;

    public NiagaraVmProgram Clone() => new()
    {
        ScriptName = ScriptName,
        ByteCode = ByteCode,
        NumTempRegisters = NumTempRegisters,
        NumUserPtrs = NumUserPtrs,
        ScriptLiterals = ScriptLiterals,
        Attributes = Attributes,
        ExternalFunctions = ExternalFunctions,
        ParamStore = ParamStore.Clone(),
        DataInterfaces = DataInterfaces,
        DataInterfaceInfos = DataInterfaceInfos,
        IsUe5 = IsUe5,
    };

    public static NiagaraVmProgram? FromScriptObject(UObject? scriptObj) =>
        FromScriptObject(scriptObj, out _);

    public static NiagaraVmProgram? FromScriptObject(UObject? scriptObj, out string? failReason)
    {
        failReason = null;
        if (scriptObj is null)
        {
            failReason = "scriptObj is null";
            return null;
        }

        var isUe5 = scriptObj.Owner?.Summary.FileVersionUE >= EUnrealEngineObjectUE5Version.LARGE_WORLD_COORDINATES ||
                    scriptObj.Owner?.Provider?.Versions.Game >= EGame.GAME_UE5_0;

        var vmStruct = scriptObj.GetOrDefault<FStructFallback?>("CachedScriptVM");
        if (vmStruct is null)
        {
            var propNames = string.Join(", ", scriptObj.Properties.Select(p => $"{p.Name.Text}({p.PropertyType})"));
            failReason = $"CachedScriptVM is null; props=[{propNames}]";
            return null;
        }

        byte[] byteCode = [];
        if (vmStruct.TryGetValue(out FStructFallback bcStruct, "ByteCode") && bcStruct is not null)
        {
            var compressed = bcStruct.GetOrDefault<byte[]>("Data", []);
            var uncompressedSize = bcStruct.GetOrDefault("UncompressedSize", -1);
            byteCode = DecompressIfNeeded(compressed, uncompressedSize);
            if (byteCode.Length == 0)
            {
                var bcProps = string.Join(", ", bcStruct.Properties.Select(p => $"{p.Name.Text}({p.PropertyType})"));
                failReason = $"ByteCode struct empty/decompress failed (dataLen={compressed.Length}, uncomp={uncompressedSize}, props=[{bcProps}])";
            }
        }
        else if (vmStruct.TryGetValue(out byte[] rawBc, "ByteCode") && rawBc is not null)
        {
            byteCode = rawBc;
            if (byteCode.Length == 0)
                failReason = "ByteCode byte[] length is 0";
        }
        else
        {
            var vmProps = string.Join(", ", vmStruct.Properties.Select(p => $"{p.Name.Text}({p.PropertyType})"));
            failReason = $"CachedScriptVM has no ByteCode; props=[{vmProps}]";
        }

        if (byteCode.Length == 0)
            return null;

        var numTemp = vmStruct.GetOrDefault("NumTempRegisters", 0);
        var numUserPtrs = vmStruct.GetOrDefault("NumUserPtrs", 0);
        var literals = vmStruct.GetOrDefault<byte[]>("ScriptLiterals", []);

        string[] attrs;
        if (vmStruct.TryGetValue(out FNiagaraVariableBase[] attrsNv, "Attributes") && attrsNv is { Length: > 0 } && attrsNv.Any(x => x is not null))
        {
            attrs = new string[attrsNv.Length];
            for (var i = 0; i < attrsNv.Length; i++)
                attrs[i] = attrsNv[i] is { } nv && !nv.Name.IsNone ? (nv.Name.Text ?? "") : "";
        }
        else
        {
            var attrsRaw = vmStruct.GetOrDefault<FStructFallback[]>("Attributes", []);
            attrs = new string[attrsRaw.Length];
            for (var i = 0; i < attrsRaw.Length; i++)
                attrs[i] = NiagaraParameterStoreData.SafeFName(attrsRaw[i], "Name");
        }

        var funcsRaw = vmStruct.GetOrDefault<FStructFallback[]>("CalledVMExternalFunctions", []);
        var funcsList = new List<NiagaraVmExternalFuncInfo>(funcsRaw.Length);
        for (var i = 0; i < funcsRaw.Length; i++)
        {
            var f = funcsRaw[i];
            if (f is null) continue;
            funcsList.Add(new NiagaraVmExternalFuncInfo
            {
                Name = NiagaraParameterStoreData.SafeFName(f, "Name"),
                OwnerName = NiagaraParameterStoreData.SafeFName(f, "OwnerName"),
                InputParamLocations = f.GetOrDefault<bool[]>("InputParamLocations", []) ?? [],
                NumOutputs = f.GetOrDefault("NumOutputs", 0),
            });
        }
        var funcs = funcsList.ToArray();

        var store = NiagaraParameterStoreData.FromStruct(
            scriptObj.GetOrDefault<FStructFallback?>("ScriptExecutionParamStore"));

        NiagaraDataInterfaceRuntime?[] diRuntimes;
        var diInfos = ExtractDataInterfaceInfos(scriptObj);
        if (store.DataInterfaces.Count > 0)
        {
            diRuntimes = new NiagaraDataInterfaceRuntime?[store.DataInterfaces.Count];
            for (var i = 0; i < store.DataInterfaces.Count; i++)
                diRuntimes[i] = NiagaraDataInterfaceRuntime.FromObject(store.DataInterfaces[i]);
        }
        else
        {
            var cachedDis = scriptObj.GetOrDefault<FStructFallback[]>("CachedDefaultDataInterfaces", []) ?? [];
            diRuntimes = new NiagaraDataInterfaceRuntime?[cachedDis.Length];
            var fallbackInfos = new List<NiagaraDataInterfaceCompileInfo>(cachedDis.Length);
            for (var i = 0; i < cachedDis.Length; i++)
            {
                var cdi = cachedDis[i];
                if (cdi is null) continue;
                var cdiName = NiagaraParameterStoreData.SafeFName(cdi, "Name");
                var uPtr = cdi.GetOrDefault("UserPtrIdx", i);
                fallbackInfos.Add(new NiagaraDataInterfaceCompileInfo(cdiName, uPtr));
                if (cdi.GetOrDefault<FPackageIndex?>("DataInterface") is { IsNull: false } diIdx && diIdx.TryLoad(out var diObj))
                    diRuntimes[i] = NiagaraDataInterfaceRuntime.FromObject(diObj);
            }
            if (diInfos.Length == 0 && fallbackInfos.Count > 0)
                diInfos = [.. fallbackInfos];
        }

        return new NiagaraVmProgram
        {
            ScriptName = scriptObj.Name,
            ByteCode = byteCode,
            NumTempRegisters = numTemp,
            NumUserPtrs = numUserPtrs,
            ScriptLiterals = literals ?? [],
            Attributes = attrs,
            ExternalFunctions = funcs,
            ParamStore = store,
            DataInterfaces = diRuntimes,
            DataInterfaceInfos = diInfos,
            IsUe5 = isUe5,
        };
    }

    private static NiagaraDataInterfaceCompileInfo[] ExtractDataInterfaceInfos(UObject scriptObj)
    {
        var list = new List<NiagaraDataInterfaceCompileInfo>();
        if (!scriptObj.TryGetValue(out FStructFallback vmData, "CachedScriptVM") || vmData is null)
            return [];

        if (vmData.TryGetValue(out FStructFallback[] diInfos, "DataInterfaceInfo") && diInfos is not null)
        {
            foreach (var di in diInfos)
            {
                if (di is null) continue;
                var name = NiagaraParameterStoreData.SafeFName(di, "Name");
                var userPtrIdx = di.GetOrDefault("UserPtrIdx", -1);
                list.Add(new NiagaraDataInterfaceCompileInfo(name, userPtrIdx));
            }
        }

        return [.. list];
    }

    private static byte[] DecompressIfNeeded(byte[] data, int uncompressedSize)
    {
        if (data.Length == 0)
            return [];
        if (uncompressedSize <= 0 || uncompressedSize == data.Length)
            return data;

        try
        {
            using var ms = new MemoryStream(data);
            using var zs = new ZLibStream(ms, CompressionMode.Decompress);
            var output = new byte[uncompressedSize];
            var total = 0;
            while (total < uncompressedSize)
            {
                var read = zs.Read(output, total, uncompressedSize - total);
                if (read <= 0)
                    break;
                total += read;
            }
            return total == uncompressedSize ? output : output[..total];
        }
        catch (Exception e)
        {
            Log.Debug(e, "Failed to zlib-decompress Niagara VM bytecode (uncompressedSize={Size})", uncompressedSize);
            return [];
        }
    }
}

/// <summary>
/// 1:1 parsed layout from FNiagaraDataSetCompiledData (exact Float / Int32 / Half component indices per variable).
/// </summary>
public sealed class NiagaraDataSetCompiledLayout
{
    public sealed record VarSlot(string Name, string TypeName, int FloatStart, int FloatCount, int IntStart, int IntCount, int HalfStart, int HalfCount);

    private readonly Dictionary<string, VarSlot> _byName = new(StringComparer.OrdinalIgnoreCase);
    public List<VarSlot> Variables { get; } = [];
    public int TotalFloatComponents { get; private set; }
    public int TotalInt32Components { get; private set; }
    public int TotalHalfComponents { get; private set; }
    public string SimTarget { get; private set; } = "CPUSim";
    public bool IsGpuSim => SimTarget.Contains("GPU", StringComparison.OrdinalIgnoreCase);

    public static NiagaraDataSetCompiledLayout? FromStruct(FStructFallback? compiledData)
    {
        if (compiledData is null)
            return null;

        var layout = new NiagaraDataSetCompiledLayout
        {
            TotalFloatComponents = (int) compiledData.GetOrDefault<uint>("TotalFloatComponents", 0),
            TotalInt32Components = (int) compiledData.GetOrDefault<uint>("TotalInt32Components", 0),
            TotalHalfComponents = (int) compiledData.GetOrDefault<uint>("TotalHalfComponents", 0),
        };

        if (compiledData.TryGetValue(out FName simTargetName, "SimTarget") && !simTargetName.IsNone)
            layout.SimTarget = simTargetName.Text ?? "CPUSim";
        else if (compiledData.TryGetValue(out byte simTargetByte, "SimTarget"))
            layout.SimTarget = simTargetByte == 1 ? "GPUComputeSim" : "CPUSim";
        else if (compiledData.TryGetValue(out int simTargetInt, "SimTarget"))
            layout.SimTarget = simTargetInt == 1 ? "GPUComputeSim" : "CPUSim";

        var varLayouts = compiledData.GetOrDefault<FStructFallback[]>("VariableLayouts", []) ?? [];
        FNiagaraVariableBase[]? varsNv = null;
        FStructFallback[]? varsFb = null;
        int varCount;
        if (compiledData.TryGetValue(out FNiagaraVariableBase[] parsedNv, "Variables") && parsedNv is { Length: > 0 } && parsedNv.Any(x => x is not null))
        {
            varsNv = parsedNv;
            varCount = parsedNv.Length;
        }
        else
        {
            varsFb = compiledData.GetOrDefault<FStructFallback[]>("Variables", []) ?? [];
            varCount = varsFb.Length;
        }

        var n = Math.Min(varCount, varLayouts.Length);
        for (var i = 0; i < n; i++)
        {
            var vl = varLayouts[i];
            if (vl is null) continue;

            string name = "";
            string typeName = "";
            if (varsNv is not null)
            {
                var nv = varsNv[i];
                if (nv is null || nv.Name.IsNone) continue;
                name = nv.Name.Text ?? "";
                typeName = NiagaraParameterStoreData.ExtractTypeName(nv.TypeDef);
            }
            else if (varsFb is not null && varsFb[i] is { } fb)
            {
                name = NiagaraParameterStoreData.SafeFName(fb, "Name");
                if (fb.TryGetValue(out FStructFallback td, "TypeDef"))
                    typeName = NiagaraParameterStoreData.ExtractTypeName(td);
            }

            if (string.IsNullOrEmpty(name) || string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
                continue;

            var fStart = ReadIntOrUInt(vl, "FloatComponentStart", 0);
            var iStart = ReadIntOrUInt(vl, "Int32ComponentStart", 0);
            var hStart = ReadIntOrUInt(vl, "HalfComponentStart", 0);

            var fCount = ReadIntOrUInt(vl, "NumFloatComponents", 0);
            var iCount = ReadIntOrUInt(vl, "NumInt32Components", 0);
            var hCount = ReadIntOrUInt(vl, "NumHalfComponents", 0);

            if (fCount == 0 && iCount == 0 && hCount == 0 && vl.TryGetValue(out FStructFallback li, "LayoutInfo") && li is not null)
            {
                fCount = ReadArrayLen(li, "FloatComponentByteOffsets");
                iCount = ReadArrayLen(li, "Int32ComponentByteOffsets");
                hCount = ReadArrayLen(li, "HalfComponentByteOffsets");
            }

            if (fCount == 0 && iCount == 0 && hCount == 0)
            {
                (fCount, iCount) = InferComponentCounts(typeName);
            }

            var slot = new VarSlot(name, typeName, fStart, fCount, iStart, iCount, hStart, hCount);
            layout.Variables.Add(slot);
            layout._byName[name] = slot;
        }

        return layout.Variables.Count > 0 ? layout : null;
    }

    private static int ReadIntOrUInt(FStructFallback fb, string prop, int def)
    {
        if (fb.TryGetValue(out uint u, prop)) return u == uint.MaxValue ? -1 : (int) u;
        if (fb.TryGetValue(out int iv, prop)) return iv;
        if (fb.TryGetValue(out ushort us, prop)) return us == ushort.MaxValue ? -1 : us;
        return def;
    }

    private static int ReadArrayLen(FStructFallback fb, string prop)
    {
        if (fb.TryGetValue(out uint[] ua, prop) && ua is not null) return ua.Length;
        if (fb.TryGetValue(out int[] ia, prop) && ia is not null) return ia.Length;
        if (fb.TryGetValue(out ushort[] usa, prop) && usa is not null) return usa.Length;
        return 0;
    }

    private static (int Floats, int Ints) InferComponentCounts(string typeName) => typeName switch
    {
        "NiagaraInt32" or "int32" or "NiagaraBool" or "bool" => (0, 1),
        "NiagaraID" => (0, 2),
        "Vector2D" or "Vector2f" or "FVector2D" => (2, 0),
        "Vector" or "Vector3f" or "FVector" or "NiagaraPosition" => (3, 0),
        "Vector4" or "Vector4f" or "LinearColor" or "Quat" or "Quat4f" => (4, 0),
        "Matrix" or "Matrix44f" => (16, 0),
        _ => (1, 0)
    };

    public bool TryGetSlot(string attrName, out VarSlot slot)
    {
        if (_byName.TryGetValue(attrName, out slot!))
            return true;

        var suffix = "." + attrName;
        foreach (var v in Variables)
        {
            if (v.Name.Equals(attrName, StringComparison.OrdinalIgnoreCase) ||
                v.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                slot = v;
                return true;
            }
        }

        slot = null!;
        return false;
    }
}

/// <summary>
/// Per-emitter particle data set stored as struct-of-arrays (float/int/half registers per attribute).
/// </summary>
public sealed class NiagaraDataSetBuffer
{
    public readonly Dictionary<string, int> FloatAttributeIndex = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, int> IntAttributeIndex = new(StringComparer.OrdinalIgnoreCase);
    public float[][] FloatColumns = [];
    public int[][] IntColumns = [];
    public float[][] HalfColumns = [];
    public int NumParticles;
    public int Capacity { get; private set; }

    public NiagaraDataSetBuffer(int capacity)
    {
        Capacity = Math.Max(1, capacity);
    }

    public void EnsureCapacity(int minFloatCols, int minIntCols, int capacity, int minHalfCols = 0)
    {
        if (capacity > Capacity)
        {
            Capacity = capacity;
            for (var i = 0; i < FloatColumns.Length; i++)
                Array.Resize(ref FloatColumns[i], Capacity);
            for (var i = 0; i < IntColumns.Length; i++)
                Array.Resize(ref IntColumns[i], Capacity);
            for (var i = 0; i < HalfColumns.Length; i++)
                Array.Resize(ref HalfColumns[i], Capacity);
        }

        if (minFloatCols > FloatColumns.Length)
        {
            var old = FloatColumns.Length;
            Array.Resize(ref FloatColumns, minFloatCols);
            for (var i = old; i < minFloatCols; i++)
                FloatColumns[i] = new float[Capacity];
        }

        if (minIntCols > IntColumns.Length)
        {
            var old = IntColumns.Length;
            Array.Resize(ref IntColumns, minIntCols);
            for (var i = old; i < minIntCols; i++)
                IntColumns[i] = new int[Capacity];
        }

        if (minHalfCols > HalfColumns.Length)
        {
            var old = HalfColumns.Length;
            Array.Resize(ref HalfColumns, minHalfCols);
            for (var i = old; i < minHalfCols; i++)
                HalfColumns[i] = new float[Capacity];
        }
    }
}

/// <summary>
/// Executes VectorVM bytecode 1:1 against UE 4.27 / UE 5.2 opcode specifications.
/// </summary>
public static class NiagaraVmInterpreter
{
    private const ushort ExtFuncInputLocBit = 0x8000;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _loggedWarnings = new(StringComparer.Ordinal);

    private static void WarnOnce(string key, string asset, string msg)
    {
        if (_loggedWarnings.TryAdd(key, 0))
            SnooperDiagnostics.LogWarning("NiagaraVM", asset, msg);
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RegVal
    {
        [FieldOffset(0)] public float F;
        [FieldOffset(0)] public int I;
        [FieldOffset(0)] public uint U;
    }

    private static void WriteGlobalHeaderOnly(
        byte[] buf,
        int baseOff,
        bool isUe5,
        float deltaTime,
        float engineTime)
    {
        var invDt = deltaTime > 1e-6f ? 1f / deltaTime : 0f;
        if (isUe5)
        {
            WriteF(buf, baseOff + 0, deltaTime);     // WorldDeltaTime
            WriteF(buf, baseOff + 4, deltaTime);     // EngineDeltaTime
            WriteF(buf, baseOff + 8, invDt);         // EngineInvDeltaTime
            WriteF(buf, baseOff + 12, engineTime);   // EngineTime
            WriteF(buf, baseOff + 16, engineTime);   // EngineRealTime
        }
        else
        {
            WriteF(buf, baseOff + 0, deltaTime);     // EngineDeltaTime
            WriteF(buf, baseOff + 4, invDt);         // EngineInvDeltaTime
            WriteF(buf, baseOff + 8, engineTime);    // EngineTime
            WriteF(buf, baseOff + 12, engineTime);   // EngineRealTime
        }
    }

    public static void WriteEngineConstantHeader(
        byte[] buf,
        int baseOff,
        bool isUe5,
        float deltaTime,
        float engineTime,
        float systemAge,
        float emitterAge,
        int numParticles,
        int totalSpawned,
        int randomSeed,
        Matrix4x4 localToWorld,
        Matrix4x4 worldToLocal,
        int emitterCount = 1,
        int aliveEmitterCount = 1,
        int tickCount = 0)
    {
        var globalSize = 32;
        var systemSize = isUe5 ? 64 : 48;
        var ownerSize = isUe5 ? 512 : 496;
        WriteGlobalHeaderOnly(buf, baseOff, isUe5, deltaTime, engineTime);

        // SystemParameters
        var sysOff = baseOff + globalSize;
        WriteF(buf, sysOff + 12, systemAge); // EngineSystemAge
        WriteI(buf, sysOff + 20, tickCount); // EngineTickCount
        WriteI(buf, sysOff + 24, emitterCount); // EngineEmitterCount
        WriteI(buf, sysOff + 28, aliveEmitterCount); // EngineAliveEmitterCount
        if (isUe5)
            WriteI(buf, sysOff + 36, randomSeed);

        // OwnerParameters
        var ownOff = baseOff + globalSize + systemSize;
        WriteMatrix44(buf, ownOff + 0, localToWorld);
        WriteMatrix44(buf, ownOff + 64, worldToLocal);
        WriteMatrix44(buf, ownOff + 128, Matrix4x4.Transpose(localToWorld));
        WriteMatrix44(buf, ownOff + 192, Matrix4x4.Transpose(worldToLocal));
        WriteMatrix44(buf, ownOff + 256, Matrix4x4.Identity);
        WriteMatrix44(buf, ownOff + 320, Matrix4x4.Identity);
        WriteF(buf, ownOff + 384 + 12, 1f); // Quat Identity
        var posOff = ownOff + 400;
        WriteF(buf, posOff + 0, localToWorld.M41);
        WriteF(buf, posOff + 4, localToWorld.M42);
        WriteF(buf, posOff + 8, localToWorld.M43);
        WriteF(buf, posOff + 32, 1f); // XAxis.X
        WriteF(buf, posOff + 52, 1f); // YAxis.Y
        WriteF(buf, posOff + 72, 1f); // ZAxis.Z
        WriteF(buf, posOff + 80, 1f); // Scale.X
        WriteF(buf, posOff + 84, 1f); // Scale.Y
        WriteF(buf, posOff + 88, 1f); // Scale.Z

        // EmitterParameters
        var emOff = baseOff + globalSize + systemSize + ownerSize;
        WriteI(buf, emOff + 0, numParticles);
        WriteI(buf, emOff + 4, totalSpawned);
        WriteF(buf, emOff + 8, 1f); // EmitterSpawnCountScale
        WriteF(buf, emOff + 12, emitterAge);
        WriteI(buf, emOff + 16, randomSeed);
        WriteI(buf, emOff + 20, randomSeed);
    }

    public static byte[] BuildConstantBuffer(
        NiagaraVmProgram program,
        float deltaTime,
        float engineTime,
        float systemAge,
        float emitterAge,
        int numParticles,
        int totalSpawned,
        int randomSeed,
        Matrix4x4 localToWorld,
        bool isSystemScript = false,
        int emitterCount = 1,
        int aliveEmitterCount = 1,
        int tickCount = 0)
    {
        // System scripts bind ONLY [Global(32B), ParamStore, ScriptLiterals] (NiagaraSystemSimulation.cpp:2157).
        // Emitter/Particle scripts bind [Global(32) + System(48/64) + Owner(496/512) + Emitter(32)] = 608 (UE4) / 640 (UE5).
        var headerSize = isSystemScript
            ? 32
            : 32 + (program.IsUe5 ? 64 : 48) + (program.IsUe5 ? 512 : 496) + 32;
        var extData = program.ParamStore.ParameterData;
        var litData = program.ScriptLiterals;
        var hasInterp = !isSystemScript && program.HasInterpolationParameters && extData.Length >= 2;
        var headerCount = hasInterp ? 2 : 1;
        var total = headerSize * headerCount + extData.Length + litData.Length;
        var buf = new byte[total];

        if (isSystemScript)
        {
            WriteGlobalHeaderOnly(buf, 0, program.IsUe5, deltaTime, engineTime);
            if (extData.Length > 0)
                Buffer.BlockCopy(extData, 0, buf, headerSize, extData.Length);
            if (litData.Length > 0)
                Buffer.BlockCopy(litData, 0, buf, headerSize + extData.Length, litData.Length);
            return buf;
        }

        Matrix4x4.Invert(localToWorld, out var worldToLocal);
        WriteEngineConstantHeader(
            buf, 0, program.IsUe5, deltaTime, engineTime, systemAge, emitterAge,
            numParticles, totalSpawned, randomSeed, localToWorld, worldToLocal,
            emitterCount, aliveEmitterCount, tickCount);

        if (hasInterp)
        {
            var extHalf = extData.Length / 2;
            if (extHalf > 0)
                Buffer.BlockCopy(extData, 0, buf, headerSize, extHalf);

            var prevOff = headerSize + extHalf;
            WriteEngineConstantHeader(
                buf, prevOff, program.IsUe5, deltaTime,
                MathF.Max(0f, engineTime - deltaTime),
                MathF.Max(0f, systemAge - deltaTime),
                MathF.Max(0f, emitterAge - deltaTime),
                numParticles, totalSpawned, randomSeed, localToWorld, worldToLocal,
                emitterCount, aliveEmitterCount, Math.Max(0, tickCount - 1));

            var remExt = extData.Length - extHalf;
            if (remExt > 0)
                Buffer.BlockCopy(extData, extHalf, buf, prevOff + headerSize, remExt);

            if (litData.Length > 0)
                Buffer.BlockCopy(litData, 0, buf, headerSize * 2 + extData.Length, litData.Length);
        }
        else
        {
            if (extData.Length > 0)
                Buffer.BlockCopy(extData, 0, buf, headerSize, extData.Length);
            if (litData.Length > 0)
                Buffer.BlockCopy(litData, 0, buf, headerSize + extData.Length, litData.Length);
        }

        return buf;
    }

    public static int Execute(
        NiagaraVmProgram program,
        NiagaraDataSetBuffer dataSet,
        byte[] constBuf,
        int startInstance,
        int numInstances,
        int seedOffset = 1,
        NiagaraDataSetBuffer? secondaryDataSet = null)
    {
        if (!program.IsValid || numInstances <= 0)
            return numInstances;

        var bc = program.ByteCode;
        var numRegs = Math.Max(program.NumTempRegisters + 16, 128);
        var regs = new RegVal[numRegs];
        var tempDiIdx = 0;

        var sawPrimaryAcquire = false;
        var nextAcquiredIdx = startInstance;
        Dictionary<int, int>? secondaryCounters = null;

        for (var inst = 0; inst < numInstances; inst++)
        {
            var particleIdx = startInstance + inst;
            var ip = 0;
            var rngState = (uint) (particleIdx * 747796405 + seedOffset * 2891336453 + 1);

            while (ip < bc.Length)
            {
                var op = bc[ip++];
                if (op == 0) // done
                    break;

                switch (op)
                {
                    // 1-input ops: [uint8 srcMask][uint16 src0][uint16 dst]
                    case 7:  // rcp
                    case 8:  // rsq
                    case 9:  // sqrt
                    case 10: // neg
                    case 11: // abs
                    case 12: // exp
                    case 13: // exp2
                    case 14: // log
                    case 15: // log2
                    case 16: // sin
                    case 17: // cos
                    case 18: // tan
                    case 19: // asin
                    case 20: // acos
                    case 21: // atan
                    case 23: // ceil
                    case 24: // floor
                    case 26: // frac
                    case 27: // trunc
                    case 32: // round
                    case 33: // sign
                    case 35: // random
                    case 36: // noise
                    case 51: // absi
                    case 52: // negi
                    case 53: // signi
                    case 54: // randomi
                    case 64: // bit_not
                    case 70: // logic_not
                    case 71: // f2i
                    case 72: // i2f
                    case 73: // f2b
                    case 74: // b2f
                    case 75: // i2b
                    case 76: // b2i
                    case 95: // half_to_float
                    {
                        var mask = ReadU8(bc, ref ip);
                        var src0 = ReadU16(bc, ref ip);
                        var dst = ReadU16(bc, ref ip);
                        var v0 = ReadOp(regs, constBuf, (mask & 1) != 0, src0);
                        RegVal res = default;
                        switch (op)
                        {
                            case 7: res.F = v0.F != 0f ? 1f / v0.F : 0f; break;
                            case 8: res.F = v0.F > 0f ? 1f / MathF.Sqrt(v0.F) : 0f; break;
                            case 9: res.F = v0.F >= 0f ? MathF.Sqrt(v0.F) : 0f; break;
                            case 10: res.F = -v0.F; break;
                            case 11: res.F = MathF.Abs(v0.F); break;
                            case 12: res.F = MathF.Exp(v0.F); break;
                            case 13: res.F = MathF.Pow(2f, v0.F); break;
                            case 14: res.F = v0.F > 0f ? MathF.Log(v0.F) : 0f; break;
                            case 15: res.F = v0.F > 0f ? MathF.Log2(v0.F) : 0f; break;
                            case 16: res.F = MathF.Sin(v0.F); break;
                            case 17: res.F = MathF.Cos(v0.F); break;
                            case 18: res.F = MathF.Tan(v0.F); break;
                            case 19: res.F = float.IsNaN(v0.F) ? 0f : MathF.Asin(MathF.Min(MathF.Max(v0.F, -1f), 1f)); break;
                            case 20: res.F = float.IsNaN(v0.F) ? 0f : MathF.Acos(MathF.Min(MathF.Max(v0.F, -1f), 1f)); break;
                            case 21: res.F = MathF.Atan(v0.F); break;
                            case 23: res.F = MathF.Ceiling(v0.F); break;
                            case 24: res.F = MathF.Floor(v0.F); break;
                            case 26: res.F = v0.F - MathF.Floor(v0.F); break;
                            case 27: res.F = MathF.Truncate(v0.F); break;
                            case 32: res.F = MathF.Round(v0.F); break;
                            case 33: res.F = MathF.Sign(v0.F); break;
                            case 35:
                                rngState = NextRng(rngState);
                                res.F = ((rngState & 0xFFFFFF) / 16777216f) * v0.F;
                                break;
                            case 36:
                                res.F = MathF.Sin(v0.F * 1.37f) * 0.5f;
                                break;
                            case 51: res.I = Math.Abs(v0.I); break;
                            case 52: res.I = -v0.I; break;
                            case 53: res.I = Math.Sign(v0.I); break;
                            case 54:
                                rngState = NextRng(rngState);
                                res.I = v0.I > 0 ? (int) (rngState % (uint) v0.I) : 0;
                                break;
                            case 64: res.I = ~v0.I; break;
                            case 70: res.I = v0.I == 0 ? -1 : 0; break;
                            case 71: res.I = (int) v0.F; break;
                            case 72: res.F = v0.I; break;
                            case 73: res.I = v0.F != 0f ? -1 : 0; break;
                            case 74: res.F = v0.I != 0 ? 1f : 0f; break;
                            case 75: res.I = v0.I != 0 ? -1 : 0; break;
                            case 76: res.I = v0.I != 0 ? 1 : 0; break;
                            case 95:
                                res.F = (float) BitConverter.UInt16BitsToHalf((ushort) v0.I);
                                break;
                        }
                        if (dst < regs.Length) regs[dst] = res;
                        break;
                    }

                    // 2-input ops: [uint8 mask][u16 s0][u16 s1][u16 dst]
                    case 1:  // add
                    case 2:  // sub
                    case 3:  // mul
                    case 4:  // div
                    case 22: // atan2
                    case 25: // fmod
                    case 29: // min
                    case 30: // max
                    case 31: // pow
                    case 34: // step
                    case 37: // cmplt
                    case 38: // cmple
                    case 39: // cmpgt
                    case 40: // cmpge
                    case 41: // cmpeq
                    case 42: // cmpneq
                    case 44: // addi
                    case 45: // subi
                    case 46: // muli
                    case 47: // divi
                    case 49: // mini
                    case 50: // maxi
                    case 55: // cmplti
                    case 56: // cmplei
                    case 57: // cmpgti
                    case 58: // cmpgei
                    case 59: // cmpeqi
                    case 60: // cmpneqi
                    case 61: // bit_and
                    case 62: // bit_or
                    case 63: // bit_xor
                    case 65: // bit_lshift
                    case 66: // bit_rshift
                    case 67: // logic_and
                    case 68: // logic_or
                    case 69: // logic_xor
                    case 89: // noise2D
                    {
                        var mask = ReadU8(bc, ref ip);
                        var s0 = ReadU16(bc, ref ip);
                        var s1 = ReadU16(bc, ref ip);
                        var dst = ReadU16(bc, ref ip);
                        var v0 = ReadOp(regs, constBuf, (mask & 1) != 0, s0);
                        var v1 = ReadOp(regs, constBuf, (mask & 2) != 0, s1);
                        RegVal res = default;
                        switch (op)
                        {
                            case 1: res.F = v0.F + v1.F; break;
                            case 2: res.F = v0.F - v1.F; break;
                            case 3: res.F = v0.F * v1.F; break;
                            case 4: res.F = v1.F != 0f ? v0.F / v1.F : 0f; break;
                            case 22: res.F = MathF.Atan2(v0.F, v1.F); break;
                            case 25: res.F = v1.F != 0f ? v0.F % v1.F : 0f; break;
                            case 29: res.F = MathF.Min(v0.F, v1.F); break;
                            case 30: res.F = MathF.Max(v0.F, v1.F); break;
                            case 31: res.F = MathF.Pow(MathF.Max(0f, v0.F), v1.F); break;
                            case 34: res.F = v0.F >= v1.F ? 1f : 0f; break;
                            case 37: res.I = v0.F < v1.F ? -1 : 0; break;
                            case 38: res.I = v0.F <= v1.F ? -1 : 0; break;
                            case 39: res.I = v0.F > v1.F ? -1 : 0; break;
                            case 40: res.I = v0.F >= v1.F ? -1 : 0; break;
                            case 41: res.I = v0.F == v1.F ? -1 : 0; break;
                            case 42: res.I = v0.F != v1.F ? -1 : 0; break;
                            case 44: res.I = v0.I + v1.I; break;
                            case 45: res.I = v0.I - v1.I; break;
                            case 46: res.I = v0.I * v1.I; break;
                            case 47: res.I = v1.I != 0 ? v0.I / v1.I : 0; break;
                            case 49: res.I = Math.Min(v0.I, v1.I); break;
                            case 50: res.I = Math.Max(v0.I, v1.I); break;
                            case 55: res.I = v0.I < v1.I ? -1 : 0; break;
                            case 56: res.I = v0.I <= v1.I ? -1 : 0; break;
                            case 57: res.I = v0.I > v1.I ? -1 : 0; break;
                            case 58: res.I = v0.I >= v1.I ? -1 : 0; break;
                            case 59: res.I = v0.I == v1.I ? -1 : 0; break;
                            case 60: res.I = v0.I != v1.I ? -1 : 0; break;
                            case 61: res.I = v0.I & v1.I; break;
                            case 62: res.I = v0.I | v1.I; break;
                            case 63: res.I = v0.I ^ v1.I; break;
                            case 65: res.I = v0.I << (v1.I & 31); break;
                            case 66: res.I = v0.I >> (v1.I & 31); break;
                            case 67: res.I = (v0.I != 0 && v1.I != 0) ? -1 : 0; break;
                            case 68: res.I = (v0.I != 0 || v1.I != 0) ? -1 : 0; break;
                            case 69: res.I = ((v0.I != 0) ^ (v1.I != 0)) ? -1 : 0; break;
                            case 89: res.F = MathF.Sin(v0.F * 1.13f + MathF.Cos(v1.F * 1.37f)) * 0.5f; break;
                        }
                        if (dst < regs.Length) regs[dst] = res;
                        break;
                    }

                    // 3-input ops: [uint8 mask][u16 s0][u16 s1][u16 s2][u16 dst]
                    case 5:  // mad
                    case 6:  // lerp
                    case 28: // clamp
                    case 43: // select
                    case 48: // clampi
                    case 90: // noise3D
                    {
                        var mask = ReadU8(bc, ref ip);
                        var s0 = ReadU16(bc, ref ip);
                        var s1 = ReadU16(bc, ref ip);
                        var s2 = ReadU16(bc, ref ip);
                        var dst = ReadU16(bc, ref ip);
                        var v0 = ReadOp(regs, constBuf, (mask & 1) != 0, s0);
                        var v1 = ReadOp(regs, constBuf, (mask & 2) != 0, s1);
                        var v2 = ReadOp(regs, constBuf, (mask & 4) != 0, s2);
                        RegVal res = default;
                        switch (op)
                        {
                            case 5: res.F = v0.F * v1.F + v2.F; break;
                            case 6: res.F = v0.F + (v1.F - v0.F) * v2.F; break;
                            case 28: res.F = float.IsNaN(v0.F) ? v1.F : MathF.Min(MathF.Max(v0.F, v1.F), v2.F); break;
                            case 43: res = v0.I != 0 ? v1 : v2; break;
                            case 48: res.I = Math.Min(Math.Max(v0.I, v1.I), v2.I); break;
                            case 90: res.F = MathF.Sin(v0.F * 1.13f + MathF.Cos(v1.F * 1.37f)) * MathF.Cos(v2.F * 1.71f) * 0.5f; break;
                        }
                        if (dst < regs.Length) regs[dst] = res;
                        break;
                    }

                    // DataSet input: [u16 dsIdx][u16 inRegIdx][u16 dstRegIdx] (6 bytes)
                    case 77: // inputdata_float
                    case 80: // inputdata_noadvance_float
                    {
                        var dsIdx = ReadU16(bc, ref ip);
                        var inReg = ReadU16(bc, ref ip);
                        var dst = ReadU16(bc, ref ip);
                        if (dsIdx == 0)
                        {
                            dataSet.EnsureCapacity(inReg + 1, 0, Math.Max(dataSet.Capacity, particleIdx + 1));
                            if (dst < regs.Length)
                                regs[dst] = new RegVal { F = dataSet.FloatColumns[inReg][particleIdx] };
                        }
                        else if (dsIdx == 1 && secondaryDataSet is not null)
                        {
                            var secIdx = Math.Clamp(particleIdx, 0, Math.Max(0, secondaryDataSet.NumParticles - 1));
                            var val = (uint) inReg < (uint) secondaryDataSet.FloatColumns.Length &&
                                      (uint) secIdx < (uint) secondaryDataSet.FloatColumns[inReg].Length
                                ? secondaryDataSet.FloatColumns[inReg][secIdx]
                                : 0f;
                            if (dst < regs.Length)
                                regs[dst] = new RegVal { F = val };
                        }
                        else if (dst < regs.Length)
                        {
                            regs[dst] = default;
                        }
                        break;
                    }
                    case 78: // inputdata_int32
                    case 81: // inputdata_noadvance_int32
                    {
                        var dsIdx = ReadU16(bc, ref ip);
                        var inReg = ReadU16(bc, ref ip);
                        var dst = ReadU16(bc, ref ip);
                        if (dsIdx == 0)
                        {
                            dataSet.EnsureCapacity(0, inReg + 1, Math.Max(dataSet.Capacity, particleIdx + 1));
                            if (dst < regs.Length)
                                regs[dst] = new RegVal { I = dataSet.IntColumns[inReg][particleIdx] };
                        }
                        else if (dsIdx == 1 && secondaryDataSet is not null)
                        {
                            var secIdx = Math.Clamp(particleIdx, 0, Math.Max(0, secondaryDataSet.NumParticles - 1));
                            var val = (uint) inReg < (uint) secondaryDataSet.IntColumns.Length &&
                                      (uint) secIdx < (uint) secondaryDataSet.IntColumns[inReg].Length
                                ? secondaryDataSet.IntColumns[inReg][secIdx]
                                : 0;
                            if (dst < regs.Length)
                                regs[dst] = new RegVal { I = val };
                        }
                        else if (dst < regs.Length)
                        {
                            regs[dst] = default;
                        }
                        break;
                    }
                    case 79: // inputdata_half
                    case 82: // inputdata_noadvance_half
                    {
                        var dsIdx = ReadU16(bc, ref ip);
                        var inReg = ReadU16(bc, ref ip);
                        var dst = ReadU16(bc, ref ip);
                        if (dsIdx == 0)
                        {
                            dataSet.EnsureCapacity(0, 0, Math.Max(dataSet.Capacity, particleIdx + 1), inReg + 1);
                            if (dst < regs.Length)
                                regs[dst] = new RegVal { F = dataSet.HalfColumns[inReg][particleIdx] };
                        }
                        else if (dsIdx == 1 && secondaryDataSet is not null)
                        {
                            var secIdx = Math.Clamp(particleIdx, 0, Math.Max(0, secondaryDataSet.NumParticles - 1));
                            var val = (uint) inReg < (uint) secondaryDataSet.HalfColumns.Length &&
                                      (uint) secIdx < (uint) secondaryDataSet.HalfColumns[inReg].Length
                                ? secondaryDataSet.HalfColumns[inReg][secIdx]
                                : 0f;
                            if (dst < regs.Length)
                                regs[dst] = new RegVal { F = val };
                        }
                        else if (dst < regs.Length)
                        {
                            regs[dst] = default;
                        }
                        break;
                    }

                    // DataSet output: [u8 mask][u16 dsIdx][u16 dstIdxReg][u16 src][u16 dstAttrReg] (9 bytes)
                    case 83: // outputdata_float
                    {
                        var mask = ReadU8(bc, ref ip);
                        var dsIdx = ReadU16(bc, ref ip);
                        var dstIdxReg = ReadU16(bc, ref ip);
                        var src = ReadU16(bc, ref ip);
                        var dstAttr = ReadU16(bc, ref ip);
                        var v = ReadOp(regs, constBuf, (mask & 1) != 0, src);
                        var outIdx = dstIdxReg < regs.Length ? regs[dstIdxReg].I : particleIdx;
                        if (dsIdx == 0 && outIdx >= 0)
                        {
                            dataSet.EnsureCapacity(dstAttr + 1, 0, Math.Max(dataSet.Capacity, outIdx + 1));
                            dataSet.FloatColumns[dstAttr][outIdx] = v.F;
                        }
                        break;
                    }
                    case 85: // outputdata_half
                    {
                        var mask = ReadU8(bc, ref ip);
                        var dsIdx = ReadU16(bc, ref ip);
                        var dstIdxReg = ReadU16(bc, ref ip);
                        var src = ReadU16(bc, ref ip);
                        var dstAttr = ReadU16(bc, ref ip);
                        var v = ReadOp(regs, constBuf, (mask & 1) != 0, src);
                        var outIdx = dstIdxReg < regs.Length ? regs[dstIdxReg].I : particleIdx;
                        if (dsIdx == 0 && outIdx >= 0)
                        {
                            dataSet.EnsureCapacity(0, 0, Math.Max(dataSet.Capacity, outIdx + 1), dstAttr + 1);
                            dataSet.HalfColumns[dstAttr][outIdx] = v.F;
                        }
                        break;
                    }
                    case 84: // outputdata_int32
                    {
                        var mask = ReadU8(bc, ref ip);
                        var dsIdx = ReadU16(bc, ref ip);
                        var dstIdxReg = ReadU16(bc, ref ip);
                        var src = ReadU16(bc, ref ip);
                        var dstAttr = ReadU16(bc, ref ip);
                        var v = ReadOp(regs, constBuf, (mask & 1) != 0, src);
                        var outIdx = dstIdxReg < regs.Length ? regs[dstIdxReg].I : particleIdx;
                        if (dsIdx == 0 && outIdx >= 0)
                        {
                            dataSet.EnsureCapacity(0, dstAttr + 1, Math.Max(dataSet.Capacity, outIdx + 1));
                            dataSet.IntColumns[dstAttr][outIdx] = v.I;
                        }
                        break;
                    }

                    case 86: // acquireindex: [u8 mask][u16 dsIdx][u16 validOp][u16 dstIdxReg] (7 bytes)
                    {
                        var mask = ReadU8(bc, ref ip);
                        var dsIdx = ReadU16(bc, ref ip);
                        var validOp = ReadU16(bc, ref ip);
                        var dst = ReadU16(bc, ref ip);
                        var valid = ReadOp(regs, constBuf, (mask & 1) != 0, validOp);
                        int outIdx;
                        if (dsIdx == 0)
                        {
                            sawPrimaryAcquire = true;
                            outIdx = valid.I != 0 ? nextAcquiredIdx++ : -1;
                        }
                        else
                        {
                            secondaryCounters ??= [];
                            secondaryCounters.TryGetValue(dsIdx, out var sec);
                            outIdx = valid.I != 0 ? sec++ : -1;
                            secondaryCounters[dsIdx] = sec;
                        }
                        if (dst < regs.Length)
                            regs[dst] = new RegVal { I = outIdx };
                        break;
                    }

                    case 87: // external_func_call: [u8 funcIdx][u16 args...]
                    {
                        var funcIdx = ReadU8(bc, ref ip);
                        if (funcIdx < program.ExternalFunctions.Length)
                        {
                            var fInfo = program.ExternalFunctions[funcIdx];
                            var inCount = fInfo.InputParamLocations.Length;
                            var outCount = fInfo.NumOutputs;
                            var inVals = new RegVal[inCount];
                            for (var a = 0; a < inCount; a++)
                            {
                                var rawArg = ReadU16(bc, ref ip);
                                var isReg = (rawArg & ExtFuncInputLocBit) != 0;
                                var idx = (ushort) (rawArg & ~ExtFuncInputLocBit);
                                inVals[a] = ReadOp(regs, constBuf, !isReg, idx);
                            }
                            var outRegs = new ushort[outCount];
                            for (var o = 0; o < outCount; o++)
                                outRegs[o] = (ushort) (ReadU16(bc, ref ip) & ~ExtFuncInputLocBit);

                            ExecuteExternalFunc(program, fInfo, inVals, outRegs, regs, ref rngState, ref tempDiIdx);
                        }
                        else
                        {
                            WarnOnce($"badfunc:{funcIdx}", program.ScriptName, $"External function index {funcIdx} out of bounds (len={program.ExternalFunctions.Length}) at ip={ip - 2}");
                            ip = bc.Length;
                        }
                        break;
                    }

                    case 88: // exec_index: [u16 dst]
                    {
                        var dst = ReadU16(bc, ref ip);
                        if (dst < regs.Length)
                            regs[dst] = new RegVal { I = inst };
                        break;
                    }

                    case 91: // enter_stat_scope: [u16 scopeIdx]
                        _ = ReadU16(bc, ref ip);
                        break;
                    case 92: // exit_stat_scope
                        break;
                    case 93: // update_id: [u16 dsIdx][u16 idIdxReg][u16 dataIdxReg] (6 bytes)
                    {
                        _ = ReadU16(bc, ref ip);
                        _ = ReadU16(bc, ref ip);
                        _ = ReadU16(bc, ref ip);
                        break;
                    }
                    case 94: // acquire_id: [u16 dsIdx][u16 dstIdIdxReg][u16 dstIdTagReg] (6 bytes)
                    {
                        _ = ReadU16(bc, ref ip);
                        var dstId = ReadU16(bc, ref ip);
                        var dstTag = ReadU16(bc, ref ip);
                        if (dstId < regs.Length) regs[dstId] = new RegVal { I = particleIdx };
                        if (dstTag < regs.Length) regs[dstTag] = new RegVal { I = 1 };
                        break;
                    }

                    case 96: // exec_indexf: [u8 mask][u16 src][u16 dst]
                    {
                        var mask = ReadU8(bc, ref ip);
                        var src = ReadU16(bc, ref ip);
                        var dst = ReadU16(bc, ref ip);
                        var v = ReadOp(regs, constBuf, (mask & 1) != 0, src);
                        if (dst < regs.Length)
                            regs[dst] = new RegVal { F = inst + v.F };
                        break;
                    }
                    case 97: // exec_index_addi: [u8 mask][u16 src][u16 dst]
                    {
                        var mask = ReadU8(bc, ref ip);
                        var src = ReadU16(bc, ref ip);
                        var dst = ReadU16(bc, ref ip);
                        var v = ReadOp(regs, constBuf, (mask & 1) != 0, src);
                        if (dst < regs.Length)
                            regs[dst] = new RegVal { I = inst + v.I };
                        break;
                    }

                    default:
                        WarnOnce($"op:{op}", program.ScriptName, $"Unsupported VectorVM opcode {op} at ip={ip - 1}/{bc.Length}; halting instance execution");
                        ip = bc.Length;
                        break;
                }
            }
        }

        return sawPrimaryAcquire ? Math.Max(0, nextAcquiredIdx - startInstance) : numInstances;
    }

    private static NiagaraDataInterfaceRuntime? ResolveDataInterface(NiagaraVmProgram program, string ownerName, int diCursor)
    {
        if (!string.IsNullOrEmpty(ownerName) && program.DataInterfaceInfos.Length > 0)
        {
            for (var i = 0; i < program.DataInterfaceInfos.Length; i++)
            {
                var info = program.DataInterfaceInfos[i];
                if (info.Name.Equals(ownerName, StringComparison.OrdinalIgnoreCase))
                {
                    var idx = info.UserPtrIdx >= 0 ? info.UserPtrIdx : i;
                    if (idx >= 0 && idx < program.DataInterfaces.Length && program.DataInterfaces[idx] is not null)
                        return program.DataInterfaces[idx];
                    if (i < program.DataInterfaces.Length && program.DataInterfaces[i] is not null)
                        return program.DataInterfaces[i];
                }
            }
        }

        if (program.DataInterfaces.Length > 0)
        {
            var clamped = Math.Clamp(diCursor % program.DataInterfaces.Length, 0, program.DataInterfaces.Length - 1);
            return program.DataInterfaces[clamped];
        }

        return null;
    }

    private static void ExecuteExternalFunc(
        NiagaraVmProgram program,
        NiagaraVmExternalFuncInfo fInfo,
        RegVal[] inputs,
        ushort[] outRegs,
        RegVal[] regs,
        ref uint rngState,
        ref int diCursor)
    {
        var name = fInfo.Name;
        var di = ResolveDataInterface(program, fInfo.OwnerName, diCursor++);

        if (name.Contains("SampleColorCurve", StringComparison.OrdinalIgnoreCase) && inputs.Length >= 1 && outRegs.Length >= 4)
        {
            var c = di?.SampleCurve4(inputs[0].F) ?? Vector4.One;
            WriteOutF(regs, outRegs, 0, c.X);
            WriteOutF(regs, outRegs, 1, c.Y);
            WriteOutF(regs, outRegs, 2, c.Z);
            WriteOutF(regs, outRegs, 3, c.W);
            return;
        }

        if (name.Contains("SampleVectorCurve", StringComparison.OrdinalIgnoreCase) && inputs.Length >= 1 && outRegs.Length >= 3)
        {
            var v = di?.SampleCurve3(inputs[0].F) ?? Vector3.Zero;
            WriteOutF(regs, outRegs, 0, v.X);
            WriteOutF(regs, outRegs, 1, v.Y);
            WriteOutF(regs, outRegs, 2, v.Z);
            return;
        }

        if (name.Contains("SampleVector2DCurve", StringComparison.OrdinalIgnoreCase) && inputs.Length >= 1 && outRegs.Length >= 2)
        {
            var v = di?.SampleCurve2(inputs[0].F) ?? Vector2.Zero;
            WriteOutF(regs, outRegs, 0, v.X);
            WriteOutF(regs, outRegs, 1, v.Y);
            return;
        }

        if ((name.Contains("SampleCurve", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("SampleFloatCurve", StringComparison.OrdinalIgnoreCase)) &&
            inputs.Length >= 1 && outRegs.Length >= 1)
        {
            var v = di?.SampleCurve1(inputs[0].F) ?? 1f;
            WriteOutF(regs, outRegs, 0, v);
            return;
        }

        if ((name.Contains("SampleNoiseField", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("SampleCurlNoise", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("SampleField", StringComparison.OrdinalIgnoreCase)) &&
            inputs.Length >= 3 && outRegs.Length >= 3)
        {
            var p = new Vector3(inputs[0].F, inputs[1].F, inputs[2].F);
            var n = di?.SampleCurlNoise(p) ?? new Vector3(
                MathF.Sin(p.Y * 0.02f) * 8f,
                MathF.Cos(p.X * 0.02f) * 8f,
                MathF.Sin((p.X + p.Y) * 0.015f) * 4f);
            WriteOutF(regs, outRegs, 0, n.X);
            WriteOutF(regs, outRegs, 1, n.Y);
            WriteOutF(regs, outRegs, 2, n.Z);
            return;
        }

        // UNiagaraDataInterfaceParticleRead / Spline / Mesh / SkeletalMesh fallback queries
        if (fInfo.OwnerName.Contains("Particle", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("By Index", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("By ID", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Contains("Num", StringComparison.OrdinalIgnoreCase) && outRegs.Length >= 1)
            {
                if (outRegs[0] < regs.Length) regs[outRegs[0]] = new RegVal { I = 16 };
                return;
            }
            var idx = inputs.Length >= 1 ? Math.Clamp(inputs[0].I, 0, 64) : 0;
            if (outRegs.Length >= 1 && outRegs[0] < regs.Length)
                regs[outRegs[0]] = new RegVal { I = -1 }; // bValid = true
            if (outRegs.Length == 4)
            {
                WriteOutF(regs, outRegs, 1, MathF.Sin(idx * 0.8f) * 12f);
                WriteOutF(regs, outRegs, 2, MathF.Cos(idx * 0.8f) * 12f);
                WriteOutF(regs, outRegs, 3, idx * 14f);
                return;
            }
            if (outRegs.Length == 5)
            {
                WriteOutF(regs, outRegs, 1, 1f);
                WriteOutF(regs, outRegs, 2, 1f);
                WriteOutF(regs, outRegs, 3, 1f);
                WriteOutF(regs, outRegs, 4, 1f);
                return;
            }
            if (outRegs.Length == 2)
            {
                WriteOutF(regs, outRegs, 1, 1f);
                return;
            }
        }

        if (fInfo.OwnerName.Contains("Spline", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Spline", StringComparison.OrdinalIgnoreCase))
        {
            var u = inputs.Length >= 1 ? MathF.Min(MathF.Max(inputs[0].F, 0f), 1f) : 0f;
            if (name.Contains("Position", StringComparison.OrdinalIgnoreCase) && outRegs.Length >= 3)
            {
                WriteOutF(regs, outRegs, 0, 0f);
                WriteOutF(regs, outRegs, 1, 0f);
                WriteOutF(regs, outRegs, 2, u * 250f);
                return;
            }
            if ((name.Contains("Tangent", StringComparison.OrdinalIgnoreCase) || name.Contains("Direction", StringComparison.OrdinalIgnoreCase) || name.Contains("Up", StringComparison.OrdinalIgnoreCase)) && outRegs.Length >= 3)
            {
                WriteOutF(regs, outRegs, 0, 0f);
                WriteOutF(regs, outRegs, 1, 0f);
                WriteOutF(regs, outRegs, 2, 1f);
                return;
            }
            if (outRegs.Length == 4)
            {
                WriteOutF(regs, outRegs, 0, 0f);
                WriteOutF(regs, outRegs, 1, 0f);
                WriteOutF(regs, outRegs, 2, 0f);
                WriteOutF(regs, outRegs, 3, 1f);
                return;
            }
        }

        if (name.Contains("FastPathDot4", StringComparison.OrdinalIgnoreCase) && inputs.Length >= 8 && outRegs.Length >= 1)
        {
            var d = inputs[0].F * inputs[4].F + inputs[1].F * inputs[5].F + inputs[2].F * inputs[6].F + inputs[3].F * inputs[7].F;
            WriteOutF(regs, outRegs, 0, d);
            return;
        }

        if (name.Contains("TransformPosition", StringComparison.OrdinalIgnoreCase) && inputs.Length >= 19 && outRegs.Length >= 3)
        {
            // Matrix (16 floats column/row) + vec3(x,y,z)
            var x = inputs[16].F;
            var y = inputs[17].F;
            var z = inputs[18].F;
            WriteOutF(regs, outRegs, 0, x * inputs[0].F + y * inputs[4].F + z * inputs[8].F + inputs[12].F);
            WriteOutF(regs, outRegs, 1, x * inputs[1].F + y * inputs[5].F + z * inputs[9].F + inputs[13].F);
            WriteOutF(regs, outRegs, 2, x * inputs[2].F + y * inputs[6].F + z * inputs[10].F + inputs[14].F);
            return;
        }

        if (name.Contains("TransformVector", StringComparison.OrdinalIgnoreCase) && inputs.Length >= 19 && outRegs.Length >= 3)
        {
            var x = inputs[16].F;
            var y = inputs[17].F;
            var z = inputs[18].F;
            WriteOutF(regs, outRegs, 0, x * inputs[0].F + y * inputs[4].F + z * inputs[8].F);
            WriteOutF(regs, outRegs, 1, x * inputs[1].F + y * inputs[5].F + z * inputs[9].F);
            WriteOutF(regs, outRegs, 2, x * inputs[2].F + y * inputs[6].F + z * inputs[10].F);
            return;
        }

        if (name.Contains("MatrixToQuaternion", StringComparison.OrdinalIgnoreCase) && inputs.Length >= 12 && outRegs.Length >= 4)
        {
            var m = new Matrix4x4(
                inputs[0].F, inputs[1].F, inputs[2].F, 0f,
                inputs[4].F, inputs[5].F, inputs[6].F, 0f,
                inputs[8].F, inputs[9].F, inputs[10].F, 0f,
                0f, 0f, 0f, 1f);
            var q = Quaternion.CreateFromRotationMatrix(m);
            WriteOutF(regs, outRegs, 0, q.X);
            WriteOutF(regs, outRegs, 1, q.Y);
            WriteOutF(regs, outRegs, 2, q.Z);
            WriteOutF(regs, outRegs, 3, q.W);
            return;
        }

        if ((name.Contains("Rand", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("DeterministicRandom", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("SeededRandom", StringComparison.OrdinalIgnoreCase)) && outRegs.Length >= 1)
        {
            rngState = NextRng(rngState);
            var unit = (rngState & 0xFFFFFF) / 16777216f;
            if (name.Contains("Int", StringComparison.OrdinalIgnoreCase))
            {
                var lo = inputs.Length >= 2 ? inputs[0].I : 0;
                var hi = inputs.Length >= 2 ? inputs[1].I : (inputs.Length == 1 ? inputs[0].I : 1);
                var span = Math.Max(1, hi - lo + 1);
                var val = lo + (int) (rngState % (uint) span);
                if (outRegs[0] < regs.Length) regs[outRegs[0]] = new RegVal { I = val };
            }
            else if (inputs.Length >= 2 && !name.Contains("Deterministic", StringComparison.OrdinalIgnoreCase))
            {
                WriteOutF(regs, outRegs, 0, inputs[0].F + (inputs[1].F - inputs[0].F) * unit);
            }
            else if (inputs.Length >= 3)
            {
                // DeterministicRandom(seed1, seed2, seed3, maxVal) or (seed, min, max)
                var seedMix = (uint) (inputs[0].I * 747796405 + inputs[1].I * 2891336453 + inputs[2].I * 1597334677);
                seedMix = NextRng(seedMix ^ rngState);
                var u2 = (seedMix & 0xFFFFFF) / 16777216f;
                var val = inputs.Length >= 4 ? inputs[3].F * u2 : inputs[1].F + (inputs[2].F - inputs[1].F) * u2;
                WriteOutF(regs, outRegs, 0, val);
            }
            else if (inputs.Length == 1)
            {
                WriteOutF(regs, outRegs, 0, inputs[0].F * unit);
            }
            else
            {
                WriteOutF(regs, outRegs, 0, unit);
            }
            return;
        }

        WarnOnce($"ext:{fInfo.OwnerName}.{name}", program.ScriptName, $"Unimplemented Niagara ExternalFunction '{fInfo.OwnerName}.{name}' (in={inputs.Length}, out={outRegs.Length})");

        for (var o = 0; o < outRegs.Length; o++)
        {
            if (outRegs[o] < regs.Length)
                regs[outRegs[o]] = default;
        }
    }

    /// <summary>
    /// Disassembles a VectorVM bytecode stream into human-readable instructions for diagnostics logs.
    /// </summary>
    public static string Disassemble(NiagaraVmProgram program, int maxInstructions = 128)
    {
        var sb = new System.Text.StringBuilder();
        var bc = program.ByteCode;
        var ip = 0;
        var count = 0;
        var terminatedCleanly = false;

        while (ip < bc.Length && count < maxInstructions)
        {
            var off = ip;
            var op = bc[ip++];
            count++;

            if (op == 0)
            {
                sb.AppendLine($"  {off:X4}: done");
                terminatedCleanly = true;
                break;
            }

            switch (op)
            {
                case >= 7 and <= 21 or 23 or 24 or 26 or 27 or 32 or 33 or 35 or 36 or 51 or 52 or 53 or 54 or 64 or >= 70 and <= 76 or 95:
                {
                    var mask = ReadU8(bc, ref ip);
                    var s0 = ReadU16(bc, ref ip);
                    var dst = ReadU16(bc, ref ip);
                    sb.AppendLine($"  {off:X4}: op_{op}({GetOpName(op)}) mask=0x{mask:X2} s0={FmtOp((mask & 1) != 0, s0)} -> r{dst}");
                    break;
                }
                case 1 or 2 or 3 or 4 or 22 or 25 or 29 or 30 or 31 or 34 or >= 37 and <= 42 or >= 44 and <= 47 or 49 or 50 or >= 55 and <= 63 or >= 65 and <= 69 or 89:
                {
                    var mask = ReadU8(bc, ref ip);
                    var s0 = ReadU16(bc, ref ip);
                    var s1 = ReadU16(bc, ref ip);
                    var dst = ReadU16(bc, ref ip);
                    sb.AppendLine($"  {off:X4}: op_{op}({GetOpName(op)}) mask=0x{mask:X2} s0={FmtOp((mask & 1) != 0, s0)} s1={FmtOp((mask & 2) != 0, s1)} -> r{dst}");
                    break;
                }
                case 5 or 6 or 28 or 43 or 48 or 90:
                {
                    var mask = ReadU8(bc, ref ip);
                    var s0 = ReadU16(bc, ref ip);
                    var s1 = ReadU16(bc, ref ip);
                    var s2 = ReadU16(bc, ref ip);
                    var dst = ReadU16(bc, ref ip);
                    sb.AppendLine($"  {off:X4}: op_{op}({GetOpName(op)}) mask=0x{mask:X2} s0={FmtOp((mask & 1) != 0, s0)} s1={FmtOp((mask & 2) != 0, s1)} s2={FmtOp((mask & 4) != 0, s2)} -> r{dst}");
                    break;
                }
                case >= 77 and <= 82:
                {
                    var ds = ReadU16(bc, ref ip);
                    var inReg = ReadU16(bc, ref ip);
                    var dst = ReadU16(bc, ref ip);
                    sb.AppendLine($"  {off:X4}: {GetOpName(op)} ds={ds} col={inReg} -> r{dst}");
                    break;
                }
                case >= 83 and <= 85:
                {
                    var mask = ReadU8(bc, ref ip);
                    var ds = ReadU16(bc, ref ip);
                    var idxReg = ReadU16(bc, ref ip);
                    var src = ReadU16(bc, ref ip);
                    var col = ReadU16(bc, ref ip);
                    sb.AppendLine($"  {off:X4}: {GetOpName(op)} ds={ds} idx=r{idxReg} src={FmtOp((mask & 1) != 0, src)} -> col={col}");
                    break;
                }
                case 86:
                {
                    var mask = ReadU8(bc, ref ip);
                    var ds = ReadU16(bc, ref ip);
                    var valid = ReadU16(bc, ref ip);
                    var dst = ReadU16(bc, ref ip);
                    sb.AppendLine($"  {off:X4}: acquireindex ds={ds} valid={FmtOp((mask & 1) != 0, valid)} -> r{dst}");
                    break;
                }
                case 87:
                {
                    var funcIdx = ReadU8(bc, ref ip);
                    if (funcIdx < program.ExternalFunctions.Length)
                    {
                        var fInfo = program.ExternalFunctions[funcIdx];
                        for (var a = 0; a < fInfo.InputParamLocations.Length + fInfo.NumOutputs; a++)
                            _ = ReadU16(bc, ref ip);
                        sb.AppendLine($"  {off:X4}: ext_call [{funcIdx}] {fInfo.OwnerName}.{fInfo.Name} (in={fInfo.InputParamLocations.Length}, out={fInfo.NumOutputs})");
                    }
                    else
                    {
                        sb.AppendLine($"  {off:X4}: ext_call [{funcIdx}] <INVALID>");
                        ip = bc.Length;
                    }
                    break;
                }
                case 88:
                    sb.AppendLine($"  {off:X4}: exec_index -> r{ReadU16(bc, ref ip)}");
                    break;
                case 91:
                    sb.AppendLine($"  {off:X4}: enter_stat_scope {ReadU16(bc, ref ip)}");
                    break;
                case 92:
                    sb.AppendLine($"  {off:X4}: exit_stat_scope");
                    break;
                case 93 or 94:
                    sb.AppendLine($"  {off:X4}: {GetOpName(op)} ds={ReadU16(bc, ref ip)} r{ReadU16(bc, ref ip)} r{ReadU16(bc, ref ip)}");
                    break;
                case 96 or 97:
                {
                    var mask = ReadU8(bc, ref ip);
                    var s0 = ReadU16(bc, ref ip);
                    var dst = ReadU16(bc, ref ip);
                    sb.AppendLine($"  {off:X4}: {GetOpName(op)} s0={FmtOp((mask & 1) != 0, s0)} -> r{dst}");
                    break;
                }
                default:
                    sb.AppendLine($"  {off:X4}: UNKNOWN_OP_{op} (halting disassembly at byte {off}/{bc.Length})");
                    ip = bc.Length;
                    break;
            }
        }

        if (!terminatedCleanly && ip < bc.Length)
            sb.AppendLine($"  ... (truncated after {count} instructions, {ip}/{bc.Length} bytes decoded)");

        return sb.ToString();
    }

    private static string FmtOp(bool isConst, ushort idx) => isConst ? $"c[0x{idx:X}]" : $"r{idx}";

    private static string GetOpName(byte op) => op switch
    {
        1 => "add", 2 => "sub", 3 => "mul", 4 => "div", 5 => "mad", 6 => "lerp",
        7 => "rcp", 8 => "rsq", 9 => "sqrt", 10 => "neg", 11 => "abs", 12 => "exp",
        13 => "exp2", 14 => "log", 15 => "log2", 16 => "sin", 17 => "cos", 18 => "tan",
        19 => "asin", 20 => "acos", 21 => "atan", 22 => "atan2", 23 => "ceil", 24 => "floor",
        25 => "fmod", 26 => "frac", 27 => "trunc", 28 => "clamp", 29 => "min", 30 => "max",
        31 => "pow", 32 => "round", 33 => "sign", 34 => "step", 35 => "random", 36 => "noise",
        37 => "cmplt", 38 => "cmple", 39 => "cmpgt", 40 => "cmpge", 41 => "cmpeq", 42 => "cmpneq",
        43 => "select", 44 => "addi", 45 => "subi", 46 => "muli", 47 => "divi", 48 => "clampi",
        49 => "mini", 50 => "maxi", 51 => "absi", 52 => "negi", 53 => "signi", 54 => "randomi",
        55 => "cmplti", 56 => "cmplei", 57 => "cmpgti", 58 => "cmpgei", 59 => "cmpeqi", 60 => "cmpneqi",
        61 => "bit_and", 62 => "bit_or", 63 => "bit_xor", 64 => "bit_not", 65 => "bit_lshift", 66 => "bit_rshift",
        67 => "logic_and", 68 => "logic_or", 69 => "logic_xor", 70 => "logic_not",
        71 => "f2i", 72 => "i2f", 73 => "f2b", 74 => "b2f", 75 => "i2b", 76 => "b2i",
        77 => "inputdata_float", 78 => "inputdata_int32", 79 => "inputdata_half",
        80 => "inputdata_noadv_float", 81 => "inputdata_noadv_int32", 82 => "inputdata_noadv_half",
        83 => "outputdata_float", 84 => "outputdata_int32", 85 => "outputdata_half",
        86 => "acquireindex", 87 => "external_func_call", 88 => "exec_index", 89 => "noise2D", 90 => "noise3D",
        91 => "enter_stat_scope", 92 => "exit_stat_scope", 93 => "update_id", 94 => "acquire_id",
        95 => "half_to_float", 96 => "exec_indexf", 97 => "exec_index_addi",
        _ => $"op_{op}"
    };

    private static void WriteOutF(RegVal[] regs, ushort[] outRegs, int idx, float val)
    {
        if (idx < outRegs.Length && outRegs[idx] < regs.Length)
            regs[outRegs[idx]] = new RegVal { F = val };
    }

    private static RegVal ReadOp(RegVal[] regs, byte[] constBuf, bool isConst, ushort index)
    {
        if (isConst)
        {
            if (index + 4 <= constBuf.Length)
                return new RegVal { I = BitConverter.ToInt32(constBuf, index) };
            return default;
        }
        return index < regs.Length ? regs[index] : default;
    }

    private static byte ReadU8(byte[] bc, ref int ip) => ip < bc.Length ? bc[ip++] : (byte) 0;

    private static ushort ReadU16(byte[] bc, ref int ip)
    {
        if (ip + 2 > bc.Length) { ip = bc.Length; return 0; }
        var v = (ushort) (bc[ip] | (bc[ip + 1] << 8));
        ip += 2;
        return v;
    }

    private static uint NextRng(uint state)
    {
        state = state * 747796405u + 2891336453u;
        var word = ((state >> ((int) ((state >> 28) + 4))) ^ state) * 277803737u;
        return (word >> 22) ^ word;
    }

    private static void WriteF(byte[] buf, int off, float val)
    {
        if (off + 4 <= buf.Length)
            BitConverter.TryWriteBytes(buf.AsSpan(off, 4), val);
    }

    private static void WriteI(byte[] buf, int off, int val)
    {
        if (off + 4 <= buf.Length)
            BitConverter.TryWriteBytes(buf.AsSpan(off, 4), val);
    }

    private static void WriteMatrix44(byte[] buf, int off, Matrix4x4 m)
    {
        WriteF(buf, off + 0, m.M11); WriteF(buf, off + 4, m.M12); WriteF(buf, off + 8, m.M13); WriteF(buf, off + 12, m.M14);
        WriteF(buf, off + 16, m.M21); WriteF(buf, off + 20, m.M22); WriteF(buf, off + 24, m.M23); WriteF(buf, off + 28, m.M24);
        WriteF(buf, off + 32, m.M31); WriteF(buf, off + 36, m.M32); WriteF(buf, off + 40, m.M33); WriteF(buf, off + 44, m.M34);
        WriteF(buf, off + 48, m.M41); WriteF(buf, off + 52, m.M42); WriteF(buf, off + 56, m.M43); WriteF(buf, off + 60, m.M44);
    }
}
