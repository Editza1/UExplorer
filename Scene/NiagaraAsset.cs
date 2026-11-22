using System.Numerics;
using System.Text;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

using Serilog;





using UExplorer.Rendering;
namespace UExplorer.Scene;

public struct NiagaraSimulatedParticle
{
    public Vector3 Position;
    public Vector4 Color;
    public Vector2 SpriteSize;
    public float SpriteRotation;
    public float SubImageIndex;
    public Vector3 Scale;
    public Quaternion MeshOrientation;
    public Vector4 DynamicMaterialParameter;
    public float MaterialRandom;
    public float NormalizedAge;
    public Vector3 Velocity;
}

public readonly record struct NiagaraSpawnInfo(int Count, float InterpStartDt, float IntervalDt, int SpawnGroup);

public sealed class NiagaraParamDataSetBinding
{
    public List<(int ParamOffset, int DataSetOffset)> FloatOffsets { get; } = [];
    public List<(int ParamOffset, int DataSetOffset)> Int32Offsets { get; } = [];

    public static NiagaraParamDataSetBinding FromStruct(FStructFallback? fb)
    {
        var b = new NiagaraParamDataSetBinding();
        if (fb is null)
            return b;

        if (fb.TryGetValue(out FStructFallback[] floatArr, "FloatOffsets") && floatArr is not null)
        {
            foreach (var f in floatArr)
            {
                if (f is null) continue;
                b.FloatOffsets.Add((f.GetOrDefault("ParameterOffset", 0), f.GetOrDefault("DataSetComponentOffset", 0)));
            }
        }
        if (fb.TryGetValue(out FStructFallback[] intArr, "Int32Offsets") && intArr is not null)
        {
            foreach (var i in intArr)
            {
                if (i is null) continue;
                b.Int32Offsets.Add((i.GetOrDefault("ParameterOffset", 0), i.GetOrDefault("DataSetComponentOffset", 0)));
            }
        }
        return b;
    }

    public void ApplyTo(byte[] sourceData, int sliceOffset, int sliceLength, NiagaraDataSetBuffer dst, int instanceIdx = 0)
    {
        if (sourceData.Length == 0 || (FloatOffsets.Count == 0 && Int32Offsets.Count == 0))
            return;

        var maxFloat = 0;
        var maxInt = 0;
        foreach (var (_, dsOff) in FloatOffsets)
            if (dsOff + 1 > maxFloat) maxFloat = dsOff + 1;
        foreach (var (_, dsOff) in Int32Offsets)
            if (dsOff + 1 > maxInt) maxInt = dsOff + 1;

        if (maxFloat > dst.FloatColumns.Length || maxInt > dst.IntColumns.Length || instanceIdx >= dst.Capacity)
            dst.EnsureCapacity(maxFloat, maxInt, Math.Max(dst.Capacity, instanceIdx + 1));

        foreach (var (paramOff, dsOff) in FloatOffsets)
        {
            if (paramOff >= 0 && paramOff + 4 <= sliceLength && sliceOffset + paramOff + 4 <= sourceData.Length &&
                (uint) dsOff < (uint) dst.FloatColumns.Length)
            {
                dst.FloatColumns[dsOff][instanceIdx] = BitConverter.ToSingle(sourceData, sliceOffset + paramOff);
            }
        }
        foreach (var (paramOff, dsOff) in Int32Offsets)
        {
            if (paramOff >= 0 && paramOff + 4 <= sliceLength && sliceOffset + paramOff + 4 <= sourceData.Length &&
                (uint) dsOff < (uint) dst.IntColumns.Length)
            {
                dst.IntColumns[dsOff][instanceIdx] = BitConverter.ToInt32(sourceData, sliceOffset + paramOff);
            }
        }
    }
}

public sealed class NiagaraEmitterCompiledDataModel
{
    public int HandleIndex { get; init; }
    public string EmitterName { get; init; } = "";
    public string UniqueEmitterName { get; init; } = "";
    public string[] SpawnAttributes { get; init; } = [];
    public string EmitterSpawnIntervalVar { get; init; } = "";
    public string EmitterInterpSpawnStartDTVar { get; init; } = "";
    public string EmitterSpawnGroupVar { get; init; } = "";
    public string EmitterAgeVar { get; init; } = "";
    public string EmitterRandomSeedVar { get; init; } = "";
    public string EmitterInstanceSeedVar { get; init; } = "";
    public string EmitterTotalSpawnedParticlesVar { get; init; } = "";
    public NiagaraDataSetCompiledLayout? DataSetLayout { get; init; }

    public static string ExtractVarName(FStructFallback? compiled, string propName)
    {
        if (compiled is null) return "";
        if (compiled.TryGetValue(out CUE4Parse.UE4.Objects.Niagara.FNiagaraVariableBase nv, propName) && nv is not null && !nv.Name.IsNone)
            return nv.Name.Text ?? "";
        if (compiled.TryGetValue(out FStructFallback fb, propName) && fb is not null)
            return NiagaraParameterStoreData.SafeFName(fb, "Name");
        return "";
    }

    public static NiagaraEmitterCompiledDataModel FromStruct(FStructFallback compiled, int handleIndex, string emitterName, string uniqueName)
    {
        var spawnAttrList = new List<string>();
        if (compiled.TryGetValue(out FName[] spawnFNames, "SpawnAttributes") && spawnFNames is not null)
        {
            foreach (var fn in spawnFNames)
                if (!fn.IsNone && !string.IsNullOrEmpty(fn.Text))
                    spawnAttrList.Add(fn.Text);
        }
        else if (compiled.TryGetValue(out string[] spawnStrs, "SpawnAttributes") && spawnStrs is not null)
        {
            foreach (var s in spawnStrs)
                if (!string.IsNullOrEmpty(s) && !s.Equals("None", StringComparison.OrdinalIgnoreCase))
                    spawnAttrList.Add(s);
        }

        NiagaraDataSetCompiledLayout? dsLayout = null;
        if (compiled.TryGetValue(out FStructFallback dsFb, "DataSetCompiledData") && dsFb is not null)
            dsLayout = NiagaraDataSetCompiledLayout.FromStruct(dsFb);

        return new NiagaraEmitterCompiledDataModel
        {
            HandleIndex = handleIndex,
            EmitterName = emitterName,
            UniqueEmitterName = uniqueName,
            SpawnAttributes = [.. spawnAttrList],
            EmitterSpawnIntervalVar = ExtractVarName(compiled, "EmitterSpawnIntervalVar"),
            EmitterInterpSpawnStartDTVar = ExtractVarName(compiled, "EmitterInterpSpawnStartDTVar"),
            EmitterSpawnGroupVar = ExtractVarName(compiled, "EmitterSpawnGroupVar"),
            EmitterAgeVar = ExtractVarName(compiled, "EmitterAgeVar"),
            EmitterRandomSeedVar = ExtractVarName(compiled, "EmitterRandomSeedVar"),
            EmitterInstanceSeedVar = ExtractVarName(compiled, "EmitterInstanceSeedVar"),
            EmitterTotalSpawnedParticlesVar = ExtractVarName(compiled, "EmitterTotalSpawnedParticlesVar"),
            DataSetLayout = dsLayout
        };
    }
}

public sealed class NiagaraSystemCompiledDataModel
{
    public NiagaraParameterStoreData InstanceParamStore { get; init; } = new();
    public NiagaraDataSetCompiledLayout? DataSetLayout { get; init; }
    public NiagaraDataSetCompiledLayout? SpawnInstanceParamsLayout { get; init; }
    public NiagaraDataSetCompiledLayout? UpdateInstanceParamsLayout { get; init; }
    public NiagaraParamDataSetBinding SpawnGlobalBinding { get; init; } = new();
    public NiagaraParamDataSetBinding SpawnSystemBinding { get; init; } = new();
    public NiagaraParamDataSetBinding SpawnOwnerBinding { get; init; } = new();
    public List<NiagaraParamDataSetBinding> SpawnEmitterBindings { get; init; } = [];
    public NiagaraParamDataSetBinding UpdateGlobalBinding { get; init; } = new();
    public NiagaraParamDataSetBinding UpdateSystemBinding { get; init; } = new();
    public NiagaraParamDataSetBinding UpdateOwnerBinding { get; init; } = new();
    public List<NiagaraParamDataSetBinding> UpdateEmitterBindings { get; init; } = [];

    public static NiagaraSystemCompiledDataModel? FromStruct(FStructFallback? sysCompiled)
    {
        if (sysCompiled is null)
            return null;

        var spawnEmitters = new List<NiagaraParamDataSetBinding>();
        if (sysCompiled.TryGetValue(out FStructFallback[] spawnEmArr, "SpawnInstanceEmitterBindings") && spawnEmArr is not null)
            foreach (var fb in spawnEmArr)
                spawnEmitters.Add(NiagaraParamDataSetBinding.FromStruct(fb));

        var updateEmitters = new List<NiagaraParamDataSetBinding>();
        if (sysCompiled.TryGetValue(out FStructFallback[] updEmArr, "UpdateInstanceEmitterBindings") && updEmArr is not null)
            foreach (var fb in updEmArr)
                updateEmitters.Add(NiagaraParamDataSetBinding.FromStruct(fb));

        return new NiagaraSystemCompiledDataModel
        {
            InstanceParamStore = NiagaraParameterStoreData.FromStruct(sysCompiled.GetOrDefault<FStructFallback?>("InstanceParamStore")),
            DataSetLayout = NiagaraDataSetCompiledLayout.FromStruct(sysCompiled.GetOrDefault<FStructFallback?>("DataSetCompiledData")),
            SpawnInstanceParamsLayout = NiagaraDataSetCompiledLayout.FromStruct(sysCompiled.GetOrDefault<FStructFallback?>("SpawnInstanceParamsDataSetCompiledData")),
            UpdateInstanceParamsLayout = NiagaraDataSetCompiledLayout.FromStruct(sysCompiled.GetOrDefault<FStructFallback?>("UpdateInstanceParamsDataSetCompiledData")),
            SpawnGlobalBinding = NiagaraParamDataSetBinding.FromStruct(sysCompiled.GetOrDefault<FStructFallback?>("SpawnInstanceGlobalBinding")),
            SpawnSystemBinding = NiagaraParamDataSetBinding.FromStruct(sysCompiled.GetOrDefault<FStructFallback?>("SpawnInstanceSystemBinding")),
            SpawnOwnerBinding = NiagaraParamDataSetBinding.FromStruct(sysCompiled.GetOrDefault<FStructFallback?>("SpawnInstanceOwnerBinding")),
            SpawnEmitterBindings = spawnEmitters,
            UpdateGlobalBinding = NiagaraParamDataSetBinding.FromStruct(sysCompiled.GetOrDefault<FStructFallback?>("UpdateInstanceGlobalBinding")),
            UpdateSystemBinding = NiagaraParamDataSetBinding.FromStruct(sysCompiled.GetOrDefault<FStructFallback?>("UpdateInstanceSystemBinding")),
            UpdateOwnerBinding = NiagaraParamDataSetBinding.FromStruct(sysCompiled.GetOrDefault<FStructFallback?>("UpdateInstanceOwnerBinding")),
            UpdateEmitterBindings = updateEmitters
        };
    }
}

public sealed class NiagaraSystemVmSimulation
{
    public sealed class EmitterSlot
    {
        public int HandleIndex;
        public string EmitterName = "";
        public string UniqueName = "";
        public NiagaraEmitterCompiledDataModel? CompiledInfo;
        public NiagaraEmitterVmSimulation? EmitterSim;
        public int ExecutionState;
        public int TotalSpawned;
        public float EmitterAge;
    }

    private readonly NiagaraVmProgram? _systemSpawnScript;
    private readonly NiagaraVmProgram? _systemUpdateScript;
    private readonly NiagaraSystemCompiledDataModel? _compiledData;
    private readonly NiagaraParameterStoreData? _exposedParameters;
    private readonly List<EmitterSlot> _slots;
    private readonly Dictionary<NiagaraEmitterVmSimulation, NiagaraEmitterVmSimulation>? _templateToCloned;
    private readonly object _stepLock = new();

    private NiagaraDataSetBuffer? _mainDataSet;
    private NiagaraDataSetBuffer? _spawnParamsDataSet;
    private NiagaraDataSetBuffer? _updateParamsDataSet;
    private bool _initialized;
    private float _lastWallTime = float.NaN;
    private float _accumulator;
    private float _systemAge;
    private int _tick;
    internal long SystemStepVersion { get; private set; }

    private const float FixedDt = 1f / 60f;
    private const int MaxStepsPerFrame = 4;

    public bool HasSystemScripts => (_systemSpawnScript?.IsValid ?? false) || (_systemUpdateScript?.IsValid ?? false);
    public NiagaraVmProgram? SystemSpawnScript => _systemSpawnScript;
    public NiagaraVmProgram? SystemUpdateScript => _systemUpdateScript;
    public NiagaraSystemCompiledDataModel? CompiledData => _compiledData;

    public NiagaraSystemVmSimulation(
        NiagaraVmProgram? systemSpawnScript,
        NiagaraVmProgram? systemUpdateScript,
        NiagaraSystemCompiledDataModel? compiledData,
        NiagaraParameterStoreData? exposedParameters,
        List<EmitterSlot> slots,
        Dictionary<NiagaraEmitterVmSimulation, NiagaraEmitterVmSimulation>? templateToCloned = null)
    {
        _systemSpawnScript = systemSpawnScript;
        _systemUpdateScript = systemUpdateScript;
        _compiledData = compiledData;
        _exposedParameters = exposedParameters;
        _slots = slots;
        _templateToCloned = templateToCloned;
        foreach (var s in _slots)
        {
            if (s.EmitterSim is not null)
                s.EmitterSim.SystemSimulation = this;
        }
    }

    public NiagaraSystemVmSimulation Clone()
    {
        var clonedSlots = new List<EmitterSlot>(_slots.Count);
        var map = new Dictionary<NiagaraEmitterVmSimulation, NiagaraEmitterVmSimulation>(ReferenceEqualityComparer.Instance);
        foreach (var s in _slots)
        {
            NiagaraEmitterVmSimulation? clonedSim = null;
            if (s.EmitterSim is not null)
            {
                clonedSim = s.EmitterSim.Clone(detachSystem: true);
                map[s.EmitterSim] = clonedSim;
            }
            clonedSlots.Add(new EmitterSlot
            {
                HandleIndex = s.HandleIndex,
                EmitterName = s.EmitterName,
                UniqueName = s.UniqueName,
                CompiledInfo = s.CompiledInfo,
                EmitterSim = clonedSim
            });
        }
        return new NiagaraSystemVmSimulation(
            _systemSpawnScript?.Clone(),
            _systemUpdateScript?.Clone(),
            _compiledData,
            _exposedParameters?.Clone(),
            clonedSlots,
            map);
    }

    public NiagaraEmitterVmSimulation? GetClonedSim(NiagaraEmitterVmSimulation? templateSim)
    {
        if (templateSim is null)
            return null;
        if (_templateToCloned is not null && _templateToCloned.TryGetValue(templateSim, out var mapped))
            return mapped;
        foreach (var s in _slots)
        {
            if (ReferenceEquals(s.EmitterSim, templateSim))
                return s.EmitterSim;
        }
        return templateSim.Clone(detachSystem: true);
    }

    public void Reset()
    {
        _mainDataSet = null;
        _spawnParamsDataSet = null;
        _updateParamsDataSet = null;
        _initialized = false;
        _accumulator = 0f;
        _systemAge = 0f;
        _tick = 0;
        SystemStepVersion = 0;
        foreach (var s in _slots)
        {
            s.ExecutionState = 0;
            s.TotalSpawned = 0;
            s.EmitterAge = 0f;
            s.EmitterSim?.ResetFromSystem();
        }
    }

    public void StepToTime(float timeSeconds)
    {
        lock (_stepLock)
        {
            if (!_initialized || float.IsNaN(_lastWallTime) || timeSeconds < _lastWallTime || timeSeconds - _lastWallTime > 2f)
            {
                Reset();
                InitSystem();
                var maxLife = 1.5f;
                foreach (var s in _slots)
                    if (s.EmitterSim is not null && s.EmitterSim.Lifetime > maxLife)
                        maxLife = s.EmitterSim.Lifetime;
                var warmupSteps = Math.Clamp((int) MathF.Ceiling(MathF.Min(maxLife, 3f) / FixedDt), 1, 180);
                for (var i = 0; i < warmupSteps; i++)
                    Step(FixedDt);
                _lastWallTime = timeSeconds;
                return;
            }

            _accumulator += timeSeconds - _lastWallTime;
            _lastWallTime = timeSeconds;
            var steps = 0;
            while (_accumulator >= FixedDt && steps < MaxStepsPerFrame)
            {
                Step(FixedDt);
                _accumulator -= FixedDt;
                steps++;
            }
            if (_accumulator > FixedDt * MaxStepsPerFrame)
                _accumulator = 0f;
        }
    }

    private void EnsureBuffers()
    {
        if (_mainDataSet is null)
        {
            var layout = _compiledData?.DataSetLayout;
            var minF = Math.Max(layout?.TotalFloatComponents ?? 0, 64);
            var minI = Math.Max(layout?.TotalInt32Components ?? 0, 32);
            var minH = Math.Max(layout?.TotalHalfComponents ?? 0, 8);
            _mainDataSet = new NiagaraDataSetBuffer(1) { NumParticles = 1 };
            _mainDataSet.EnsureCapacity(minF, minI, 1, minH);
        }
        if (_spawnParamsDataSet is null)
        {
            var layout = _compiledData?.SpawnInstanceParamsLayout;
            _spawnParamsDataSet = new NiagaraDataSetBuffer(1) { NumParticles = 1 };
            _spawnParamsDataSet.EnsureCapacity(Math.Max(layout?.TotalFloatComponents ?? 0, 16), Math.Max(layout?.TotalInt32Components ?? 0, 16), 1, 4);
        }
        if (_updateParamsDataSet is null)
        {
            var layout = _compiledData?.UpdateInstanceParamsLayout;
            _updateParamsDataSet = new NiagaraDataSetBuffer(1) { NumParticles = 1 };
            _updateParamsDataSet.EnsureCapacity(Math.Max(layout?.TotalFloatComponents ?? 0, 16), Math.Max(layout?.TotalInt32Components ?? 0, 16), 1, 4);
        }
    }

    private void PopulateInstanceParamsDataSet(NiagaraDataSetBuffer ds1, NiagaraDataSetCompiledLayout? ds1Layout, bool isSpawn, float dt)
    {
        if (ds1Layout is not null)
        {
            Span<float> floatScratch = stackalloc float[16];
            Span<int> intScratch = stackalloc int[16];
            foreach (var v in ds1Layout.Variables)
            {
                if (v.FloatCount > 0)
                {
                    var vals = v.FloatCount <= 16 ? floatScratch[..v.FloatCount] : new float[v.FloatCount];
                    var copied = 0;
                    if (_exposedParameters is not null)
                        copied = _exposedParameters.TryGetFloats(v.Name, vals);
                    if (copied == 0 && _compiledData?.InstanceParamStore is not null)
                        copied = _compiledData.InstanceParamStore.TryGetFloats(v.Name, vals);
                    if (copied > 0)
                    {
                        ds1.EnsureCapacity(v.FloatStart + copied, 0, 1);
                        for (var c = 0; c < copied; c++)
                            ds1.FloatColumns[v.FloatStart + c][0] = vals[c];
                    }
                }
                if (v.IntCount > 0)
                {
                    var vals = v.IntCount <= 16 ? intScratch[..v.IntCount] : new int[v.IntCount];
                    var copied = 0;
                    if (_exposedParameters is not null)
                        copied = _exposedParameters.TryGetInts(v.Name, vals);
                    if (copied == 0 && _compiledData?.InstanceParamStore is not null)
                        copied = _compiledData.InstanceParamStore.TryGetInts(v.Name, vals);
                    if (copied > 0)
                    {
                        ds1.EnsureCapacity(0, v.IntStart + copied, 1);
                        for (var c = 0; c < copied; c++)
                            ds1.IntColumns[v.IntStart + c][0] = vals[c];
                    }
                }
            }
        }

        if (_compiledData is null)
            return;

        var isUe5 = (_systemUpdateScript?.IsUe5 ?? _systemSpawnScript?.IsUe5) ?? false;
        var sysSize = isUe5 ? 64 : 48;
        var ownerSize = isUe5 ? 512 : 496;
        var headerBuf = new byte[32 + sysSize + ownerSize + 32];
        var emitterCount = Math.Max(1, _slots.Count);
        var aliveCount = Math.Max(1, _slots.Count(s => s.ExecutionState == 0));

        NiagaraVmInterpreter.WriteEngineConstantHeader(
            headerBuf, 0, isUe5, dt, _systemAge, _systemAge, _systemAge,
            1, 0, 1, Matrix4x4.Identity, Matrix4x4.Identity,
            emitterCount, aliveCount, _tick);

        var globalB = isSpawn ? _compiledData.SpawnGlobalBinding : _compiledData.UpdateGlobalBinding;
        var systemB = isSpawn ? _compiledData.SpawnSystemBinding : _compiledData.UpdateSystemBinding;
        var ownerB = isSpawn ? _compiledData.SpawnOwnerBinding : _compiledData.UpdateOwnerBinding;
        var emitterBs = isSpawn ? _compiledData.SpawnEmitterBindings : _compiledData.UpdateEmitterBindings;

        globalB.ApplyTo(headerBuf, 0, 32, ds1, 0);
        systemB.ApplyTo(headerBuf, 32, sysSize, ds1, 0);
        ownerB.ApplyTo(headerBuf, 32 + sysSize, ownerSize, ds1, 0);

        var emOff = 32 + sysSize + ownerSize;
        foreach (var s in _slots)
        {
            if ((uint) s.HandleIndex >= (uint) emitterBs.Count)
                continue;
            NiagaraVmInterpreter.WriteEngineConstantHeader(
                headerBuf, 0, isUe5, dt, _systemAge, _systemAge, s.EmitterAge,
                s.EmitterSim?.LiveParticles ?? 0, s.TotalSpawned, s.HandleIndex + 1, Matrix4x4.Identity, Matrix4x4.Identity,
                emitterCount, aliveCount, _tick);
            emitterBs[s.HandleIndex].ApplyTo(headerBuf, emOff, 32, ds1, 0);
        }
    }

    private void InitSystem()
    {
        EnsureBuffers();
        _initialized = true;
        if (_systemSpawnScript is { IsValid: true })
        {
            PopulateInstanceParamsDataSet(_spawnParamsDataSet!, _compiledData?.SpawnInstanceParamsLayout, isSpawn: true, dt: FixedDt);
            var cb = NiagaraVmInterpreter.BuildConstantBuffer(
                _systemSpawnScript, FixedDt, 0f, 0f, 0f, 1, 0, 1, Matrix4x4.Identity,
                isSystemScript: true, emitterCount: Math.Max(1, _slots.Count), aliveEmitterCount: Math.Max(1, _slots.Count), tickCount: 0);
            NiagaraVmInterpreter.Execute(_systemSpawnScript, _mainDataSet!, cb, 0, 1, 1, _spawnParamsDataSet);
        }
        TransferSystemSimResults(0f, isInitial: true);
    }

    private void Step(float dt)
    {
        EnsureBuffers();
        _systemAge += dt;
        _tick++;
        SystemStepVersion++;

        if (_systemUpdateScript is { IsValid: true })
        {
            PopulateInstanceParamsDataSet(_updateParamsDataSet!, _compiledData?.UpdateInstanceParamsLayout, isSpawn: false, dt: dt);
            var cb = NiagaraVmInterpreter.BuildConstantBuffer(
                _systemUpdateScript, dt, _systemAge, _systemAge, _systemAge, 1, 0, _tick, Matrix4x4.Identity,
                isSystemScript: true, emitterCount: Math.Max(1, _slots.Count),
                aliveEmitterCount: Math.Max(1, _slots.Count(s => s.ExecutionState == 0)), tickCount: _tick);
            NiagaraVmInterpreter.Execute(_systemUpdateScript, _mainDataSet!, cb, 0, 1, _tick * 2 + 1, _updateParamsDataSet);
        }

        var spawnLists = TransferSystemSimResults(dt, isInitial: false);

        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            if (slot.EmitterSim is null)
                continue;
            var spawns = i < spawnLists.Count ? spawnLists[i] : null;
            slot.EmitterSim.StepFromSystem(dt, _systemAge, slot.EmitterAge, spawns, ref slot.TotalSpawned);
        }
    }

    private List<List<NiagaraSpawnInfo>?> TransferSystemSimResults(float dt, bool isInitial)
    {
        var spawnLists = new List<List<NiagaraSpawnInfo>?>(_slots.Count);
        var layout = _compiledData?.DataSetLayout;
        var main = _mainDataSet!;

        foreach (var slot in _slots)
        {
            List<NiagaraSpawnInfo>? slotSpawns = null;
            if (layout is not null)
            {
                if (!string.IsNullOrEmpty(slot.CompiledInfo?.EmitterAgeVar) &&
                    layout.TryGetSlot(slot.CompiledInfo.EmitterAgeVar, out var ageSlot) &&
                    ageSlot.FloatCount > 0 && (uint) ageSlot.FloatStart < (uint) main.FloatColumns.Length)
                {
                    slot.EmitterAge = main.FloatColumns[ageSlot.FloatStart][0];
                }
                else
                {
                    slot.EmitterAge += dt;
                }

                if (layout.TryGetSlot($"{slot.UniqueName}.ExecutionState", out var execSlot) &&
                    execSlot.IntCount > 0 && (uint) execSlot.IntStart < (uint) main.IntColumns.Length)
                {
                    slot.ExecutionState = main.IntColumns[execSlot.IntStart][0];
                }

                if (slot.EmitterSim is not null &&
                    layout.TryGetSlot($"{slot.UniqueName}.Localspace", out var lsSlot) &&
                    lsSlot.IntCount > 0 && (uint) lsSlot.IntStart < (uint) main.IntColumns.Length &&
                    main.IntColumns[lsSlot.IntStart][0] != 0)
                {
                    slot.EmitterSim.RuntimeLocalSpace = true;
                }

                if (!isInitial && slot.CompiledInfo is { SpawnAttributes.Length: > 0 } ci)
                {
                    slotSpawns = new List<NiagaraSpawnInfo>(ci.SpawnAttributes.Length);
                    foreach (var spawnAttr in ci.SpawnAttributes)
                    {
                        if (!layout.TryGetSlot(spawnAttr, out var siSlot))
                            continue;
                        if (siSlot.IntCount < 2 || siSlot.FloatCount < 2)
                            continue;
                        var count = (uint) siSlot.IntStart < (uint) main.IntColumns.Length
                            ? main.IntColumns[siSlot.IntStart][0]
                            : 0;
                        var group = (uint) (siSlot.IntStart + 1) < (uint) main.IntColumns.Length
                            ? main.IntColumns[siSlot.IntStart + 1][0]
                            : 0;
                        var interpStart = (uint) siSlot.FloatStart < (uint) main.FloatColumns.Length
                            ? main.FloatColumns[siSlot.FloatStart][0]
                            : 0f;
                        var interval = (uint) (siSlot.FloatStart + 1) < (uint) main.FloatColumns.Length
                            ? main.FloatColumns[siSlot.FloatStart + 1][0]
                            : 0f;
                        if (count > 0)
                            slotSpawns.Add(new NiagaraSpawnInfo(Math.Clamp(count, 0, 512), interpStart, interval, group));
                    }
                }

                if (slot.EmitterSim is { } emSim)
                {
                    emSim.ApplySystemDataSetToParamStores(main, layout, _exposedParameters, _compiledData?.InstanceParamStore, slot, isInitial);
                }
            }
            else
            {
                slot.EmitterAge += dt;
            }

            spawnLists.Add(slotSpawns);
        }

        return spawnLists;
    }
}

public sealed class NiagaraEmitterVmSimulation
{
    private readonly NiagaraVmProgram? _spawnProgram;
    private readonly NiagaraVmProgram? _updateProgram;
    private readonly NiagaraVmProgram? _emitterSpawnProgram;
    private readonly NiagaraVmProgram? _emitterUpdateProgram;
    private readonly NiagaraDataSetCompiledLayout? _dataSetLayout;
    private readonly int _targetParticles;
    private readonly float _lifetime;
    private readonly string _posAttr;
    private readonly string _colorAttr;
    private readonly string _spriteSizeAttr;
    private readonly string _spriteRotAttr;
    private readonly string _subImgAttr;
    private readonly string _scaleAttr;
    private readonly string _orientAttr;
    private readonly string _dynParamAttr;
    private readonly string _matRandomAttr;
    private readonly string _normAgeAttr;
    private readonly string _velAttr;

    private readonly float? _spawnRate;
    private readonly int? _burstCount;
    private readonly int _capacity;

    // Persistent simulation state (FNiagaraEmitterInstance): particles live across frames instead of being re-simulated from t = 0.
    private NiagaraDataSetBuffer? _dataSet;
    private int _live;
    private float _lastWallTime = float.NaN;
    private float _accumulator;
    private float _emitterAge;
    private float _spawnAccumulator;
    private float _loopAge;
    private int _totalSpawned;
    private int _tick;
    private long _lastSyncedSystemStep = -1;
    private NiagaraSimulatedParticle[] _cached = [];

    internal NiagaraSystemVmSimulation? SystemSimulation { get; set; }
    public bool? RuntimeLocalSpace { get; internal set; }
    public bool SimulatedInWorldSpace => false;

    /// <summary>Fixed simulation step. UE ticks Niagara with the world delta; a fixed step keeps the VM deterministic and cheap.</summary>
    private const float FixedDt = 1f / 60f;
    private const int MaxStepsPerFrame = 4;

    public bool HasVmPrograms => (_spawnProgram?.IsValid ?? false) || (_updateProgram?.IsValid ?? false);
    public NiagaraDataSetCompiledLayout? DataSetLayout => _dataSetLayout;
    public int TargetParticles => _targetParticles;
    public float Lifetime => _lifetime;
    public int LiveParticles => _live;
    public bool HasDynamicMaterialParameterAttr { get; }

    public NiagaraEmitterVmSimulation(
        NiagaraVmProgram? spawnProgram,
        NiagaraVmProgram? updateProgram,
        NiagaraDataSetCompiledLayout? dataSetLayout,
        int targetParticles,
        float lifetime,
        string posAttr = "Position",
        string colorAttr = "Color",
        string spriteSizeAttr = "SpriteSize",
        string spriteRotAttr = "SpriteRotation",
        string subImgAttr = "SubImageIndex",
        string scaleAttr = "Scale",
        string orientAttr = "MeshOrientation",
        float? spawnRate = null,
        int? burstCount = null,
        string dynParamAttr = "DynamicMaterialParameter",
        string matRandomAttr = "MaterialRandom",
        NiagaraVmProgram? emitterSpawnProgram = null,
        NiagaraVmProgram? emitterUpdateProgram = null,
        string normAgeAttr = "NormalizedAge",
        string velAttr = "Velocity")
    {
        _spawnProgram = spawnProgram;
        _updateProgram = updateProgram;
        _emitterSpawnProgram = emitterSpawnProgram;
        _emitterUpdateProgram = emitterUpdateProgram;
        _dataSetLayout = dataSetLayout;
        _targetParticles = Math.Clamp(targetParticles, 1, 512);
        _lifetime = MathF.Max(0.1f, lifetime);
        _posAttr = posAttr;
        _colorAttr = colorAttr;
        _spriteSizeAttr = spriteSizeAttr;
        _spriteRotAttr = spriteRotAttr;
        _subImgAttr = subImgAttr;
        _scaleAttr = scaleAttr;
        _orientAttr = orientAttr;
        _dynParamAttr = dynParamAttr;
        _matRandomAttr = matRandomAttr;
        _normAgeAttr = normAgeAttr;
        _velAttr = velAttr;
        _spawnRate = spawnRate is > 0f ? spawnRate : null;
        _burstCount = burstCount is > 0 ? burstCount : null;
        _capacity = Math.Clamp(Math.Max(_targetParticles * 2, (_burstCount ?? 0) * 2), 256, 2048);

        var attrs = _updateProgram is { IsValid: true, Attributes.Length: > 0 }
            ? _updateProgram.Attributes
            : (_spawnProgram?.Attributes ?? []);
        HasDynamicMaterialParameterAttr =
            (_dataSetLayout is not null && _dataSetLayout.TryGetSlot(_dynParamAttr, out _)) ||
            FindAttrIndex(attrs, _dynParamAttr) >= 0;
    }

    public NiagaraEmitterVmSimulation Clone(bool detachSystem = false) => new(
        _spawnProgram?.Clone(),
        _updateProgram?.Clone(),
        _dataSetLayout,
        _targetParticles,
        _lifetime,
        _posAttr,
        _colorAttr,
        _spriteSizeAttr,
        _spriteRotAttr,
        _subImgAttr,
        _scaleAttr,
        _orientAttr,
        _spawnRate,
        _burstCount,
        _dynParamAttr,
        _matRandomAttr,
        _emitterSpawnProgram?.Clone(),
        _emitterUpdateProgram?.Clone(),
        _normAgeAttr,
        _velAttr)
    {
        SystemSimulation = detachSystem ? null : SystemSimulation,
        RuntimeLocalSpace = RuntimeLocalSpace
    };

    /// <summary>Restart the emitter (UNiagaraComponent::ResetSystem).</summary>
    public void Reset()
    {
        if (SystemSimulation is not null)
        {
            SystemSimulation.Reset();
            return;
        }
        ResetFromSystem();
    }

    internal void ResetFromSystem()
    {
        _dataSet = null;
        _live = 0;
        _accumulator = 0f;
        _emitterAge = 0f;
        _spawnAccumulator = 0f;
        _loopAge = 0f;
        _totalSpawned = 0;
        _tick = 0;
        _lastSyncedSystemStep = -1;
        _cached = [];
    }

    internal void ApplySystemDataSetToParamStores(
        NiagaraDataSetBuffer main,
        NiagaraDataSetCompiledLayout layout,
        NiagaraParameterStoreData? exposedParams,
        NiagaraParameterStoreData? instanceParams,
        NiagaraSystemVmSimulation.EmitterSlot slot,
        bool isInitial)
    {
        void CopyIntoStore(NiagaraParameterStoreData? dstStore)
        {
            if (dstStore is null || dstStore.ParameterData.Length == 0)
                return;

            dstStore.ApplyOverridesFrom(instanceParams);
            dstStore.ApplyOverridesFrom(exposedParams);

            foreach (var v in layout.Variables)
            {
                if (!dstStore.Entries.TryGetValue(v.Name, out var entry))
                    continue;
                if (v.FloatCount > 0 && v.FloatStart >= 0 && v.FloatStart + v.FloatCount <= main.FloatColumns.Length)
                {
                    for (var c = 0; c < v.FloatCount; c++)
                    {
                        var byteOff = entry.Offset + c * 4;
                        if (byteOff >= 0 && byteOff + 4 <= dstStore.ParameterData.Length)
                            BitConverter.TryWriteBytes(dstStore.ParameterData.AsSpan(byteOff, 4), main.FloatColumns[v.FloatStart + c][0]);
                    }
                }
                else if (v.IntCount > 0 && v.IntStart >= 0 && v.IntStart + v.IntCount <= main.IntColumns.Length)
                {
                    for (var c = 0; c < v.IntCount; c++)
                    {
                        var byteOff = entry.Offset + c * 4;
                        if (byteOff >= 0 && byteOff + 4 <= dstStore.ParameterData.Length)
                            BitConverter.TryWriteBytes(dstStore.ParameterData.AsSpan(byteOff, 4), main.IntColumns[v.IntStart + c][0]);
                    }
                }
            }

            if (slot.CompiledInfo is { } ci)
            {
                if (!string.IsNullOrEmpty(ci.EmitterAgeVar))
                    dstStore.SetFloat(ci.EmitterAgeVar, slot.EmitterAge);
                if (!string.IsNullOrEmpty(ci.EmitterTotalSpawnedParticlesVar))
                    dstStore.SetInt(ci.EmitterTotalSpawnedParticlesVar, slot.TotalSpawned);
            }

            if (isInitial)
                dstStore.PostTickCopyCurrentToPrev();
        }

        CopyIntoStore(_spawnProgram?.ParamStore);
        CopyIntoStore(_updateProgram?.ParamStore);
    }

    internal void StepFromSystem(
        float dt,
        float systemAge,
        float emitterAge,
        List<NiagaraSpawnInfo>? spawnInfos,
        ref int totalSpawned)
    {
        var ds = EnsureDataSet();
        _emitterAge = emitterAge;
        _tick++;

        if (_emitterUpdateProgram is { IsValid: true })
        {
            var eUpdCb = NiagaraVmInterpreter.BuildConstantBuffer(
                _emitterUpdateProgram, dt, systemAge, systemAge, emitterAge, _live, totalSpawned, _tick, Matrix4x4.Identity);
            NiagaraVmInterpreter.Execute(_emitterUpdateProgram, ds, eUpdCb, 0, 1, _tick * 2 + 1);
        }

        if (_live > 0 && _updateProgram is { IsValid: true })
        {
            var updCb = NiagaraVmInterpreter.BuildConstantBuffer(
                _updateProgram, dt, systemAge, systemAge, emitterAge, _live, totalSpawned, _tick, Matrix4x4.Identity);
            _live = Math.Clamp(NiagaraVmInterpreter.Execute(_updateProgram, ds, updCb, 0, _live, _tick * 2 + 1), 0, _live);
        }

        if (spawnInfos is not null && spawnInfos.Count > 0)
        {
            foreach (var si in spawnInfos)
            {
                var spawn = si.Count;
                if (spawn <= 0)
                    continue;

                var overflow = _live + spawn - _capacity;
                if (overflow > 0)
                    RetireOldest(ds, Math.Min(overflow, _live));
                spawn = Math.Min(spawn, _capacity - _live);
                if (spawn <= 0)
                    continue;

                if (_spawnProgram is { IsValid: true })
                {
                    _spawnProgram.ParamStore.SetInt("Engine.ExecutionCount", spawn);
                    foreach (var k in _spawnProgram.ParamStore.Entries.Keys)
                    {
                        if (k.EndsWith(".SpawnInterval", StringComparison.OrdinalIgnoreCase) && !k.StartsWith("PREV_", StringComparison.OrdinalIgnoreCase))
                            _spawnProgram.ParamStore.SetFloat(k, si.IntervalDt);
                        else if (k.EndsWith(".InterpSpawnStartDt", StringComparison.OrdinalIgnoreCase) && !k.StartsWith("PREV_", StringComparison.OrdinalIgnoreCase))
                            _spawnProgram.ParamStore.SetFloat(k, si.InterpStartDt);
                        else if (k.EndsWith(".SpawnGroup", StringComparison.OrdinalIgnoreCase) && !k.StartsWith("PREV_", StringComparison.OrdinalIgnoreCase))
                            _spawnProgram.ParamStore.SetInt(k, si.SpawnGroup);
                    }

                    var spawnCb = NiagaraVmInterpreter.BuildConstantBuffer(
                        _spawnProgram, dt, systemAge, systemAge, emitterAge, _live, totalSpawned, _tick, Matrix4x4.Identity);
                    var spawned = NiagaraVmInterpreter.Execute(_spawnProgram, ds, spawnCb, _live, spawn, _tick * 2 + 2);
                    _live += Math.Clamp(spawned, 0, spawn);
                }
                else
                {
                    _live += spawn;
                }
                totalSpawned += spawn;
                _totalSpawned = totalSpawned;
            }
        }

        _spawnProgram?.ParamStore.PostTickCopyCurrentToPrev();
        _updateProgram?.ParamStore.PostTickCopyCurrentToPrev();
    }

    private string GetEmitterParamPrefix()
    {
        if (_spawnProgram is null) return "";
        foreach (var k in _spawnProgram.ParamStore.Entries.Keys)
        {
            if (k.EndsWith(".SpawnInterval", StringComparison.OrdinalIgnoreCase) && !k.StartsWith("PREV_", StringComparison.OrdinalIgnoreCase))
                return k[..^"SpawnInterval".Length];
        }
        return "";
    }

    private NiagaraDataSetBuffer EnsureDataSet()
    {
        if (_dataSet is not null)
            return _dataSet;
        var activeProg = _spawnProgram is { IsValid: true } ? _spawnProgram : _updateProgram!;
        var attrs = activeProg.Attributes;
        var minFloats = Math.Max(_dataSetLayout?.TotalFloatComponents ?? 0, Math.Max(attrs.Length + 8, 32));
        var minInts = Math.Max(_dataSetLayout?.TotalInt32Components ?? 0, 16);
        var minHalfs = Math.Max(_dataSetLayout?.TotalHalfComponents ?? 0, 8);
        _dataSet = new NiagaraDataSetBuffer(_capacity);
        _dataSet.EnsureCapacity(minFloats, minInts, _capacity, minHalfs);
        return _dataSet;
    }

    /// <summary>
    /// One emitter tick in UE 5.2 order (FNiagaraEmitterInstance::Tick / PostTick): run the particle update script on existing particles
    /// (Particle State kills expired particles through acquireindex, which compacts the data set), then spawn this frame's new particles
    /// at the end of the buffer and run the spawn script on just those.
    /// </summary>
    private void Step(float dt)
    {
        var ds = EnsureDataSet();
        _emitterAge += dt;
        _loopAge += dt;
        _tick++;

        if (_live > 0 && _updateProgram is { IsValid: true })
        {
            var updCb = NiagaraVmInterpreter.BuildConstantBuffer(
                _updateProgram, dt, _emitterAge, _emitterAge, _emitterAge, _live, _totalSpawned, _tick, Matrix4x4.Identity);
            _live = Math.Clamp(NiagaraVmInterpreter.Execute(_updateProgram, ds, updCb, 0, _live, _tick * 2 + 1), 0, _live);
        }

        // Spawn count: SpawnRate module (rate * dt, fractional carry) or a SpawnBurst at the start of each loop.
        var spawn = 0;
        if (_spawnRate is { } rate)
        {
            _spawnAccumulator += rate * dt;
            spawn = (int) MathF.Floor(_spawnAccumulator);
            _spawnAccumulator -= spawn;
        }
        else if (_burstCount is { } burst)
        {
            var loopDuration = MathF.Max(_lifetime, 0.5f);
            if (_totalSpawned == 0 || _loopAge >= loopDuration)
            {
                _loopAge = 0f;
                spawn = burst;
            }
        }
        else
        {
            // No spawn info could be read: keep a steady population of TargetParticles over one lifetime.
            _spawnAccumulator += _targetParticles / _lifetime * dt;
            spawn = (int) MathF.Floor(_spawnAccumulator);
            _spawnAccumulator -= spawn;
        }

        if (spawn <= 0)
        {
            _spawnProgram?.ParamStore.PostTickCopyCurrentToPrev();
            _updateProgram?.ParamStore.PostTickCopyCurrentToPrev();
            return;
        }

        // Scripts without a kill (no Particle State module) never shrink the buffer: retire the oldest particles first.
        var overflow = _live + spawn - _capacity;
        if (overflow > 0)
            RetireOldest(ds, Math.Min(overflow, _live));
        spawn = Math.Min(spawn, _capacity - _live);
        if (spawn <= 0)
        {
            _spawnProgram?.ParamStore.PostTickCopyCurrentToPrev();
            _updateProgram?.ParamStore.PostTickCopyCurrentToPrev();
            return;
        }

        if (_spawnProgram is { IsValid: true })
        {
            _spawnProgram.ParamStore.SetInt("Engine.ExecutionCount", spawn);
            var spawnCb = NiagaraVmInterpreter.BuildConstantBuffer(
                _spawnProgram, dt, _emitterAge, _emitterAge, _emitterAge, _live, _totalSpawned, _tick, Matrix4x4.Identity);
            var spawned = NiagaraVmInterpreter.Execute(_spawnProgram, ds, spawnCb, _live, spawn, _tick * 2 + 2);
            _live += Math.Clamp(spawned, 0, spawn);
        }
        else
        {
            _live += spawn;
        }
        _totalSpawned += spawn;
        _spawnProgram?.ParamStore.PostTickCopyCurrentToPrev();
        _updateProgram?.ParamStore.PostTickCopyCurrentToPrev();
    }

    private void RetireOldest(NiagaraDataSetBuffer ds, int count)
    {
        if (count <= 0)
            return;
        var remain = _live - count;
        foreach (var col in ds.FloatColumns)
            Array.Copy(col, count, col, 0, Math.Max(0, remain));
        foreach (var col in ds.IntColumns)
            Array.Copy(col, count, col, 0, Math.Max(0, remain));
        foreach (var col in ds.HalfColumns)
            Array.Copy(col, count, col, 0, Math.Max(0, remain));
        _live = Math.Max(0, remain);
    }

    public bool TrySimulate(float timeSeconds, out NiagaraSimulatedParticle[] particles)
    {
        particles = _cached;
        if (!HasVmPrograms)
            return false;

        try
        {
            return TrySimulateCore(timeSeconds, out particles);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Niagara VM simulation step failed");
            particles = _cached;
            return particles.Length > 0;
        }
    }

    private bool TrySimulateCore(float timeSeconds, out NiagaraSimulatedParticle[] particles)
    {
        particles = _cached;
        if (!HasVmPrograms)
            return false;

        var changed = false;
        if (SystemSimulation is not null)
        {
            SystemSimulation.StepToTime(timeSeconds);
            if (_lastSyncedSystemStep != SystemSimulation.SystemStepVersion)
            {
                _lastSyncedSystemStep = SystemSimulation.SystemStepVersion;
                changed = true;
            }
        }
        else if (_dataSet is null || float.IsNaN(_lastWallTime) || timeSeconds < _lastWallTime || timeSeconds - _lastWallTime > 2f)
        {
            ResetFromSystem();
            // Warm up so an effect that has been running in the level is already in steady state (Niagara "WarmupTime").
            var warmupSteps = Math.Clamp((int) MathF.Ceiling(MathF.Min(_lifetime, 3f) / FixedDt), 1, 180);
            for (var s = 0; s < warmupSteps; s++)
                Step(FixedDt);
            _lastWallTime = timeSeconds;
            changed = true;
        }
        else
        {
            _accumulator += timeSeconds - _lastWallTime;
            _lastWallTime = timeSeconds;
            var steps = 0;
            while (_accumulator >= FixedDt && steps < MaxStepsPerFrame)
            {
                Step(FixedDt);
                _accumulator -= FixedDt;
                steps++;
                changed = true;
            }
            if (_accumulator > FixedDt * MaxStepsPerFrame)
                _accumulator = 0f; // too slow to keep up: drop time instead of spiralling
        }

        if (!changed)
            return _cached.Length > 0;

        var count = _live;
        var dataSet = _dataSet;
        if (count <= 0 || dataSet is null)
        {
            _cached = [];
            particles = _cached;
            return false;
        }

        var result = new NiagaraSimulatedParticle[count];
        var attrSource = _updateProgram is { IsValid: true } && _updateProgram.Attributes.Length > 0
            ? _updateProgram.Attributes
            : _spawnProgram?.Attributes ?? [];

        for (var i = 0; i < count; i++)
        {
            var pos = ReadAttrVec3(dataSet, attrSource, _posAttr, i, Vector3.Zero);
            var col = ReadAttrVec4(dataSet, attrSource, _colorAttr, i, Vector4.One);
            var sz = ReadAttrVec2(dataSet, attrSource, _spriteSizeAttr, i, new Vector2(20f, 20f));
            var rot = ReadAttrFloat(dataSet, attrSource, _spriteRotAttr, i, 0f);
            var sub = ReadAttrFloat(dataSet, attrSource, _subImgAttr, i, 0f);
            var scl = ReadAttrVec3(dataSet, attrSource, _scaleAttr, i, Vector3.One);
            var ori = ReadAttrQuat(dataSet, attrSource, _orientAttr, i);
            var dyn = ReadAttrVec4(dataSet, attrSource, _dynParamAttr, i, Vector4.One);
            var rnd = ReadAttrFloat(dataSet, attrSource, _matRandomAttr, i, (i * 0.61803398875f) % 1f);
            var normAge = ReadAttrFloat(dataSet, attrSource, _normAgeAttr, i, 0.5f);
            var vel = ReadAttrVec3(dataSet, attrSource, _velAttr, i, Vector3.Zero);

            result[i] = new NiagaraSimulatedParticle
            {
                Position = pos,
                Color = col,
                SpriteSize = sz,
                SpriteRotation = rot,
                SubImageIndex = sub,
                Scale = scl,
                MeshOrientation = ori,
                DynamicMaterialParameter = dyn,
                MaterialRandom = rnd,
                NormalizedAge = normAge,
                Velocity = vel,
            };
        }

        _cached = result;
        particles = result;
        return true;
    }

    private float ReadComponent(NiagaraDataSetBuffer ds, int idx, int p, bool isHalf, float def = 0f)
    {
        var cols = isHalf ? ds.HalfColumns : ds.FloatColumns;
        if ((uint) idx >= (uint) cols.Length || (uint) p >= (uint) cols[idx].Length)
            return def;
        var v = cols[idx][p];
        return float.IsFinite(v) ? v : def;
    }

    private bool ResolveAttr(string[] attrFallback, string target, out int startCol, out int compCount, out bool isHalf)
    {
        if (_dataSetLayout is not null && _dataSetLayout.TryGetSlot(target, out var slot))
        {
            if (slot.HalfCount > 0 && slot.FloatCount == 0)
            {
                startCol = slot.HalfStart;
                compCount = slot.HalfCount;
                isHalf = true;
                return true;
            }
            if (slot.FloatCount > 0)
            {
                startCol = slot.FloatStart;
                compCount = slot.FloatCount;
                isHalf = false;
                return true;
            }
        }

        isHalf = false;
        compCount = 4;
        startCol = FindAttrIndex(attrFallback, target);
        return startCol >= 0;
    }

    private float ReadAttrFloat(NiagaraDataSetBuffer ds, string[] attrs, string name, int p, float def)
    {
        if (!ResolveAttr(attrs, name, out var col, out _, out var half))
            return def;
        return ReadComponent(ds, col, p, half, def);
    }

    private Vector2 ReadAttrVec2(NiagaraDataSetBuffer ds, string[] attrs, string name, int p, Vector2 def)
    {
        if (!ResolveAttr(attrs, name, out var col, out var cnt, out var half) || cnt < 1)
            return def;
        if (cnt == 1)
        {
            var s = ReadComponent(ds, col, p, half, def.X);
            return new Vector2(s, s);
        }
        return new Vector2(ReadComponent(ds, col, p, half, def.X), ReadComponent(ds, col + 1, p, half, def.Y));
    }

    private Vector3 ReadAttrVec3(NiagaraDataSetBuffer ds, string[] attrs, string name, int p, Vector3 def)
    {
        if (!ResolveAttr(attrs, name, out var col, out var cnt, out var half) || cnt < 1)
            return def;
        if (cnt == 1)
        {
            var s = ReadComponent(ds, col, p, half, def.X);
            return new Vector3(s, s, s);
        }
        if (cnt == 2)
            return new Vector3(ReadComponent(ds, col, p, half, def.X), ReadComponent(ds, col + 1, p, half, def.Y), def.Z);
        return new Vector3(ReadComponent(ds, col, p, half, def.X), ReadComponent(ds, col + 1, p, half, def.Y), ReadComponent(ds, col + 2, p, half, def.Z));
    }

    private Vector4 ReadAttrVec4(NiagaraDataSetBuffer ds, string[] attrs, string name, int p, Vector4 def)
    {
        if (!ResolveAttr(attrs, name, out var col, out var cnt, out var half) || cnt < 4)
            return def;
        return new Vector4(
            ReadComponent(ds, col, p, half, def.X),
            ReadComponent(ds, col + 1, p, half, def.Y),
            ReadComponent(ds, col + 2, p, half, def.Z),
            ReadComponent(ds, col + 3, p, half, def.W));
    }

    private Quaternion ReadAttrQuat(NiagaraDataSetBuffer ds, string[] attrs, string name, int p)
    {
        if (!ResolveAttr(attrs, name, out var col, out var cnt, out var half) || cnt < 4)
            return Quaternion.Identity;
        var q = new Quaternion(
            ReadComponent(ds, col, p, half, 0f),
            ReadComponent(ds, col + 1, p, half, 0f),
            ReadComponent(ds, col + 2, p, half, 0f),
            ReadComponent(ds, col + 3, p, half, 1f));
        return q.LengthSquared() > 1e-6f ? Quaternion.Normalize(q) : Quaternion.Identity;
    }

    private static int FindAttrIndex(string[] attrs, string target)
    {
        if (attrs.Length == 0 || string.IsNullOrEmpty(target))
            return -1;

        var floatSlot = 0;
        for (var i = 0; i < attrs.Length; i++)
        {
            var a = attrs[i];
            if (a.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                a.EndsWith("." + target, StringComparison.OrdinalIgnoreCase))
                return floatSlot;

            if (a.Contains("Color", StringComparison.OrdinalIgnoreCase) ||
                a.Contains("Orientation", StringComparison.OrdinalIgnoreCase) ||
                a.Contains("Quat", StringComparison.OrdinalIgnoreCase) ||
                a.Contains("DynamicMaterialParameter", StringComparison.OrdinalIgnoreCase))
                floatSlot += 4;
            else if (a.Contains("Position", StringComparison.OrdinalIgnoreCase) ||
                     a.Contains("Velocity", StringComparison.OrdinalIgnoreCase) ||
                     a.Contains("Scale", StringComparison.OrdinalIgnoreCase))
                floatSlot += 3;
            else if (a.Contains("Size", StringComparison.OrdinalIgnoreCase))
                floatSlot += 2;
            else
                floatSlot += 1;
        }
        return -1;
    }
}

public sealed class NiagaraFloatCurve
{
    public required string Name { get; init; }
    public required float[] Lut { get; init; }
    public float MinTime { get; init; } = 0f;
    public float MaxTime { get; init; } = 1f;
    public float InvTimeRange { get; init; } = 1f;

    public float Sample(float normalizedAge)
    {
        if (Lut.Length == 0)
            return 1f;
        if (Lut.Length == 1)
            return Lut[0];

        var t = Math.Clamp((normalizedAge - MinTime) * InvTimeRange, 0f, 1f);
        var scaled = t * (Lut.Length - 1);
        var idx0 = (int) MathF.Floor(scaled);
        var idx1 = Math.Min(Lut.Length - 1, idx0 + 1);
        var frac = scaled - idx0;
        return Lut[idx0] + (Lut[idx1] - Lut[idx0]) * frac;
    }
}

public sealed class NiagaraColorCurve
{
    public required string Name { get; init; }
    public required float[] Lut { get; init; }
    public float MinTime { get; init; } = 0f;
    public float MaxTime { get; init; } = 1f;
    public float InvTimeRange { get; init; } = 1f;

    public Vector4 Sample(float normalizedAge)
    {
        var sampleCount = Lut.Length / 4;
        if (sampleCount == 0)
            return Vector4.One;
        if (sampleCount == 1)
            return new Vector4(Lut[0], Lut[1], Lut[2], Lut[3]);

        var t = Math.Clamp((normalizedAge - MinTime) * InvTimeRange, 0f, 1f);
        var scaled = t * (sampleCount - 1);
        var idx0 = Math.Clamp((int) MathF.Floor(scaled), 0, sampleCount - 1);
        var idx1 = Math.Min(sampleCount - 1, idx0 + 1);
        var frac = scaled - idx0;

        var o0 = idx0 * 4;
        var o1 = idx1 * 4;
        var c0 = new Vector4(Lut[o0], Lut[o0 + 1], Lut[o0 + 2], Lut[o0 + 3]);
        var c1 = new Vector4(Lut[o1], Lut[o1 + 1], Lut[o1 + 2], Lut[o1 + 3]);
        return Vector4.Lerp(c0, c1, frac);
    }
}

public sealed class NiagaraMeshEmitterDesc
{
    public required string EmitterName { get; init; }
    public required UStaticMesh Mesh { get; init; }
    public MaterialAsset? Material { get; init; }
    public bool LocalSpace { get; set; } = true;
    public Vector3 Scale { get; set; } = Vector3.One;
    public float ScaleMultiplier { get; set; } = 1.0f;
    public Vector3 StartOffset { get; set; } = Vector3.Zero;
    public Vector3 TravelVector { get; set; } = new(0f, 0f, 15f);
    public float PositionDirection { get; set; } = -1f;
    public Vector3 RotationEuler { get; set; } = Vector3.Zero;
    public Vector3 SpinSpeedEuler { get; set; } = Vector3.Zero;
    public Vector4 Color { get; set; } = Vector4.One;
    public float ScaleRgbBoost { get; set; } = 3.0f;
    public float AlphaMultiplier { get; set; } = 1.0f;
    public int MaxParticles { get; set; } = 4;
    public float Lifetime { get; set; } = 4.0f;
    public float TimeScale { get; set; } = 1.0f;
    public float PhaseSpread { get; set; } = 1.0f;
    public float GravityZ { get; set; } = 0f;
    public bool UseScaleCurve { get; set; } = true;
    public bool UsePositionCurve { get; set; } = true;
    public bool UseColorCurve { get; set; } = true;
    public NiagaraColorCurve? ColorCurve { get; init; }
    public NiagaraFloatCurve? PositionCurve { get; init; }
    public NiagaraFloatCurve? ScaleFactorCurve { get; init; }
    public NiagaraFloatCurve? ScaleRgbCurve { get; init; }
    public NiagaraFloatCurve? AlphaCurve { get; init; }
    public NiagaraFloatCurve? DynamicParam1Curve { get; init; }
    public NiagaraFloatCurve? DynamicParam2Curve { get; init; }
    public uint FacingMode { get; init; }
    public bool LockedAxisEnable { get; init; }
    public Vector3 LockedAxis { get; init; } = Vector3.UnitZ;
    public uint LockedAxisSpace { get; init; }
    public NiagaraEmitterVmSimulation? VmSimulation { get; init; }
    public bool Enabled { get; set; } = true;

    public float VerticalTravel
    {
        get => TravelVector.Z;
        set => TravelVector = new Vector3(TravelVector.X, TravelVector.Y, value);
    }

    public (Matrix4x4 LocalTransform, Vector4 ParticleColor) EvaluateInstance(int instanceIndex, float timeSeconds)
    {
        if (VmSimulation is not null && VmSimulation.TrySimulate(timeSeconds * TimeScale, out var vmParticles) &&
            instanceIndex < vmParticles.Length)
        {
            var vp = vmParticles[instanceIndex];
            var vmScale = Scale * vp.Scale * ScaleMultiplier;
            var vmRot = Matrix4x4.CreateFromQuaternion(vp.MeshOrientation);
            var vmMat = Matrix4x4.CreateScale(vmScale) * vmRot * Matrix4x4.CreateTranslation(vp.Position + StartOffset);
            var vmColor = new Vector4(
                vp.Color.X * ScaleRgbBoost,
                vp.Color.Y * ScaleRgbBoost,
                vp.Color.Z * ScaleRgbBoost,
                Math.Clamp(vp.Color.W * AlphaMultiplier, 0f, 1f));
            return (vmMat, vmColor);
        }

        var count = Math.Max(1, MaxParticles);
        var phase = ((float) instanceIndex / count) * PhaseSpread;
        var life = MathF.Max(0.05f, Lifetime);
        var scaledTime = timeSeconds * TimeScale;
        var age = ((scaledTime / life) + phase) % 1.0f;
        if (age < 0f)
            age += 1f;
        var elapsed = age * life;

        var color = (UseColorCurve && ColorCurve is not null) ? ColorCurve.Sample(age) : Color;
        var rgbScale = (UseColorCurve && ScaleRgbCurve is not null) ? ScaleRgbCurve.Sample(age) * ScaleRgbBoost : ScaleRgbBoost;
        var alphaScale = AlphaCurve is not null ? Math.Clamp(AlphaCurve.Sample(age), 0f, 1f) : MathF.Sin(age * MathF.PI);

        var finalColor = new Vector4(
            color.X * MathF.Max(0.05f, rgbScale),
            color.Y * MathF.Max(0.05f, rgbScale),
            color.Z * MathF.Max(0.05f, rgbScale),
            Math.Clamp(color.W * alphaScale * AlphaMultiplier, 0f, 1f));

        var scaleFactor = (UseScaleCurve && ScaleFactorCurve is not null) ? ScaleFactorCurve.Sample(age) : 1f;
        var finalScale = Scale * (ScaleMultiplier * MathF.Max(0.02f, scaleFactor));

        var posFactor = (UsePositionCurve && PositionCurve is not null) ? PositionCurve.Sample(age) : age;
        var dirFactor = PositionDirection < 0f ? (-1f + posFactor) : posFactor;
        var posOffset = StartOffset + TravelVector * dirFactor + new Vector3(0f, 0f, 0.5f * GravityZ * elapsed * elapsed);

        var pitchRad = (RotationEuler.X + scaledTime * SpinSpeedEuler.X) * (MathF.PI / 180f);
        var yawRad = (RotationEuler.Y + scaledTime * SpinSpeedEuler.Y) * (MathF.PI / 180f);
        var rollRad = (RotationEuler.Z + scaledTime * SpinSpeedEuler.Z) * (MathF.PI / 180f);

        var rotMat = Matrix4x4.CreateFromYawPitchRoll(yawRad, pitchRad, rollRad);
        var scaleMat = Matrix4x4.CreateScale(finalScale);
        var transMat = Matrix4x4.CreateTranslation(posOffset);

        return (scaleMat * rotMat * transMat, finalColor);
    }
}

public sealed class NiagaraSpriteEmitterDesc
{
    public required string EmitterName { get; init; }
    public MaterialAsset? Material { get; init; }
    public UTexture? SpriteTexture { get; init; }
    public bool LocalSpace { get; set; } = true;
    public Vector2 SubImageSize { get; set; } = Vector2.One;
    public Vector2 SpriteSize { get; set; } = new(20f, 20f);
    public float SizeMultiplier { get; set; } = 1.0f;
    public Vector4 Color { get; set; } = Vector4.One;
    public float ColorBrightness { get; set; } = 1.5f;
    public float AlphaMultiplier { get; set; } = 1.0f;
    public int MaxParticles { get; set; } = 56;
    public Vector3 OriginOffset { get; set; } = Vector3.Zero;
    public float SpawnRadius { get; set; } = 455f;
    public float CylinderRadiusMin { get; set; } = 0.4f;
    public float CylinderRadiusMax { get; set; } = 1.0f;
    public float VerticalLineLength { get; set; } = 455f;
    public float VerticalOffsetZ { get; set; } = 0f;
    public float VPosStart { get; set; } = 0.6f;
    public float VPosEnd { get; set; } = 0.95f;
    public float VelocitySpeed { get; set; } = 75f;
    public float TurbulenceStrength { get; set; } = 0.15f;
    public float GravityZ { get; set; } = 0f;
    public float Lifetime { get; set; } = 3.25f;
    public float TimeScale { get; set; } = 1.0f;
    public bool IsAdditive { get; set; } = true;
    public bool IsSoftBubble { get; set; }
    public bool UseSphericalDome { get; set; }
    public NiagaraColorCurve? ColorCurve { get; init; }
    public NiagaraFloatCurve? UniformSizeCurve { get; init; }
    public NiagaraFloatCurve? ScaleFactorCurve { get; init; }
    public NiagaraFloatCurve? ScaleRgbCurve { get; init; }
    public NiagaraFloatCurve? AlphaCurve { get; init; }
    public NiagaraFloatCurve? CylinderRadiusCurve { get; init; }
    public NiagaraFloatCurve? LatheProfileCurve { get; init; }
    public NiagaraFloatCurve? VPositionCurve { get; init; }
    public NiagaraFloatCurve? VelocityCurve { get; init; }
    public Vector2 PivotInUVSpace { get; set; } = new(0.5f, 0.5f);
    public bool SubImageBlend { get; set; }
    public bool IsRibbon { get; set; }
    public float RibbonWidth { get; set; } = 12f;
    public NiagaraEmitterVmSimulation? VmSimulation { get; init; }
    public bool HasExplicitSimData { get; init; }
    public bool IsGpuCompute { get; init; }
    public bool Enabled { get; set; } = true;
}

public sealed class NiagaraLightEmitterDesc
{
    public string EmitterName { get; init; } = "LightEmitter";
    public Vector3 Color { get; set; } = Vector3.One;
    public float RadiusScale { get; set; } = 1.0f;
    public float DefaultRadius { get; set; } = 350.0f;
    public float Intensity { get; set; } = 5.0f;
    public bool Enabled { get; set; } = true;
}

public class NiagaraAsset
{
    public static float GlobalTimeScale = 1.0f;
    public static bool GlobalPaused;

    public UObject? SystemAsset { get; private set; }
    public NiagaraSystemVmSimulation? SystemSimulation { get; private set; }
    public List<NiagaraMeshEmitterDesc> MeshEmitters { get; } = [];
    public List<NiagaraSpriteEmitterDesc> SpriteEmitters { get; } = [];
    public List<NiagaraLightEmitterDesc> LightEmitters { get; } = [];
    public List<string> DiscoveredEmitters { get; } = [];
    public bool IsVisible { get; set; } = true;

    public NiagaraAsset(UObject component)
    {
        try
        {
            var sysObj = ResolveSystemAssetFromComponent(component);
            if (sysObj is null && UnrealSpace.TryGetInherited(component, "Asset", out FPackageIndex inheritedIdx) &&
                !inheritedIdx.IsNull && inheritedIdx.TryLoad(out var inheritedAsset))
            {
                sysObj = ResolveSystemAssetFromComponent(inheritedAsset) ?? inheritedAsset;
            }
            SystemAsset = sysObj;
            if (sysObj is not null)
                ParseNiagaraAsset(sysObj);
            else
                SnooperDiagnostics.LogInfo("Niagara", component.Name, $"Skipped component '{component.Name}' ({component.ExportType}): no NiagaraSystem/NiagaraEmitter resolved");
        }
        catch (Exception e)
        {
            SnooperDiagnostics.LogError("Niagara", component.Name, $"Failed to parse NiagaraAsset: {e}");
            Log.Warning(e, "Failed to parse NiagaraAsset {Name}", component.Name);
        }
    }

    public NiagaraAsset(UObject niagaraAsset, string name)
    {
        SystemAsset = niagaraAsset;
        try
        {
            ParseNiagaraAsset(niagaraAsset);
        }
        catch (Exception e)
        {
            SnooperDiagnostics.LogError("Niagara", name, $"Failed to parse standalone Niagara asset: {e}");
            Log.Warning(e, "Failed to parse standalone Niagara asset {Name}", name);
        }
    }

    private static string SafeGetName(FStructFallback s, string propName, string fallback = "")
    {
        try
        {
            if (s.TryGetValue(out FName fn, propName) && !fn.IsNone)
                return fn.Text;
            if (s.TryGetValue(out string str, propName) && !string.IsNullOrEmpty(str))
                return str;
        }
        catch
        {
        }
        return fallback;
    }

    public static UObject? ResolveSystemAssetFromComponent(UObject componentOrActor) =>
        ResolveSystemAssetInternal(componentOrActor, 0);

    private static bool IsNiagaraAssetType(UObject obj) =>
        obj.ExportType.Contains("NiagaraSystem", StringComparison.OrdinalIgnoreCase) ||
        obj.ExportType.Contains("NiagaraEmitter", StringComparison.OrdinalIgnoreCase);

    private static UObject? ResolveSystemAssetInternal(UObject? componentOrActor, int depth)
    {
        if (componentOrActor is null || depth > 3)
            return null;

        if (IsNiagaraAssetType(componentOrActor))
            return componentOrActor;

        // Explicitly reject Cascade ParticleSystemComponents unless they also hold a Niagara property
        foreach (var propName in new[] { "Asset", "NiagaraSystem", "System", "Template" })
        {
            if (componentOrActor.TryGetValue(out FPackageIndex idx, propName) && idx is { IsNull: false } && idx.TryLoad(out var loaded))
            {
                if (IsNiagaraAssetType(loaded))
                    return loaded;
                if (loaded.ExportType.Contains("NiagaraAsset", StringComparison.OrdinalIgnoreCase))
                {
                    var nested = ResolveSystemAssetInternal(loaded, depth + 1);
                    if (nested is not null)
                        return nested;
                }
            }
        }

        if (componentOrActor.Template is { } tmplResolved && tmplResolved.TryLoad(out var tmplObj))
        {
            var fromTmpl = ResolveSystemAssetInternal(tmplObj, depth + 1);
            if (fromTmpl is not null)
                return fromTmpl;
        }

        if (componentOrActor.TryGetValue(out FPackageIndex compIdx, "NiagaraAsset") && compIdx is { IsNull: false } && compIdx.TryLoad(out var subComp))
        {
            var res = ResolveSystemAssetInternal(subComp, depth + 1);
            if (res is not null)
                return res;
        }

        foreach (var prop in componentOrActor.Properties)
        {
            if (prop.Tag?.GenericValue is FPackageIndex pIdx && !pIdx.IsNull &&
                (pIdx.Name.Contains("NS_", StringComparison.OrdinalIgnoreCase) ||
                 pIdx.Name.Contains("Niagara", StringComparison.OrdinalIgnoreCase) ||
                 pIdx.Name.Contains("FX_", StringComparison.OrdinalIgnoreCase)))
            {
                if (pIdx.TryLoad(out var target))
                {
                    var res = ResolveSystemAssetInternal(target, depth + 1);
                    if (res is not null)
                        return res;
                }
            }
        }

        if (depth == 0 && componentOrActor is USceneComponent && componentOrActor.Outer is { } outerResolved && outerResolved.TryLoad(out var outerActor))
        {
            var fromOuter = ResolveSystemAssetInternal(outerActor, depth + 1);
            if (fromOuter is not null)
                return fromOuter;
        }

        return null;
    }

    private void ParseNiagaraAsset(UObject assetObj)
    {
        if (!IsNiagaraAssetType(assetObj))
        {
            SnooperDiagnostics.LogInfo("Niagara", assetObj.Name, $"Ignored non-Niagara object '{assetObj.Name}' ({assetObj.ExportType})");
            return;
        }

        var seenRenderers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emitterParams = ExtractPackageEmitterData(assetObj, out var systemSpawnProg, out var systemUpdateProg, out var exposedStore);
        var disabledEmitters = new List<string>();
        var disabledRenderers = new List<string>();

        if (assetObj.ExportType.Contains("NiagaraEmitter", StringComparison.OrdinalIgnoreCase))
        {
            DiscoveredEmitters.Add(assetObj.Name);
            ParseEmitterObject(assetObj, assetObj.Name, seenRenderers, emitterParams, disabledRenderers, null);
            DumpNiagaraPackage(assetObj, emitterParams, disabledEmitters, disabledRenderers, systemSpawnProg, systemUpdateProg);
            return;
        }

        var sysCompiled = NiagaraSystemCompiledDataModel.FromStruct(
            assetObj.GetOrDefault<FStructFallback?>("SystemCompiledData"));
        var compiledStructs = (assetObj as CUE4Parse.UE4.Objects.Niagara.UNiagaraSystem)?.NiagaraEmitterCompiledDataStructs?.ToArray()
                              ?? assetObj.GetOrDefault<FStructFallback[]>("NiagaraEmitterCompiledDataStructs", []);
        var handles = assetObj.GetOrDefault<FStructFallback[]>("EmitterHandles", []);
        var systemSlots = new List<NiagaraSystemVmSimulation.EmitterSlot>();

        for (var i = 0; i < handles.Length; i++)
        {
            var handle = handles[i];
            try
            {
                var isEnabled = handle.GetOrDefault("bIsEnabled", true);
                var emitterName = SafeGetName(handle, "Name", SafeGetName(handle, "IdName", $"Emitter_{i}"));
                DiscoveredEmitters.Add($"{emitterName}{(isEnabled ? "" : " [off]")}");

                if (!isEnabled)
                {
                    disabledEmitters.Add(emitterName);
                    continue;
                }

                UObject? emitterObj = null;
                if (handle.GetOrDefault<FPackageIndex?>("Instance") is { IsNull: false } instIdx)
                    instIdx.TryLoad(out emitterObj);
                else if (handle.TryGetValue(out FStructFallback versionedInst, "VersionedInstance") &&
                         versionedInst.GetOrDefault<FPackageIndex?>("Emitter") is { IsNull: false } vEmIdx)
                {
                    vEmIdx.TryLoad(out emitterObj);
                }

                if (emitterObj is not null)
                {
                    var uniqueName = emitterObj.GetOrDefault("UniqueEmitterName", emitterName);
                    NiagaraEmitterCompiledDataModel? compiledModel = null;
                    if (i < compiledStructs.Length && compiledStructs[i] is not null)
                    {
                        compiledModel = NiagaraEmitterCompiledDataModel.FromStruct(compiledStructs[i], i, emitterName, uniqueName);
                        if (compiledModel.DataSetLayout is { } layout)
                        {
                            if (!emitterParams.TryGetValue(uniqueName, out var epByUnique))
                            {
                                epByUnique = new ExtractedEmitterData();
                                emitterParams[uniqueName] = epByUnique;
                            }
                            epByUnique.DataSetLayout = layout;
                            if (!uniqueName.Equals(emitterName, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!emitterParams.TryGetValue(emitterName, out var epByName))
                                {
                                    epByName = new ExtractedEmitterData();
                                    emitterParams[emitterName] = epByName;
                                }
                                epByName.DataSetLayout ??= layout;
                            }
                        }
                    }

                    var createdSims = new List<NiagaraEmitterVmSimulation>();
                    ParseEmitterObject(emitterObj, emitterName, seenRenderers, emitterParams, disabledRenderers, createdSims);
                    if (createdSims.Count > 0)
                    {
                        foreach (var sim in createdSims)
                        {
                            systemSlots.Add(new NiagaraSystemVmSimulation.EmitterSlot
                            {
                                HandleIndex = i,
                                EmitterName = emitterName,
                                UniqueName = uniqueName,
                                CompiledInfo = compiledModel,
                                EmitterSim = sim
                            });
                        }
                    }
                    else
                    {
                        systemSlots.Add(new NiagaraSystemVmSimulation.EmitterSlot
                        {
                            HandleIndex = i,
                            EmitterName = emitterName,
                            UniqueName = uniqueName,
                            CompiledInfo = compiledModel,
                            EmitterSim = null
                        });
                    }
                }
            }
            catch (Exception e)
            {
                SnooperDiagnostics.LogWarning("Niagara", assetObj.Name, $"Failed to parse EmitterHandle[{i}]: {e}");
                Log.Warning(e, "Failed to parse EmitterHandle in {Name}", assetObj.Name);
            }
        }

        // Only fall back to package export sweep if no EmitterHandles array existed
        if (handles.Length == 0 && assetObj.Owner is { } pkg)
        {
            foreach (var exp in pkg.GetExports())
            {
                try
                {
                    if (exp.ExportType.Contains("NiagaraEmitter", StringComparison.OrdinalIgnoreCase))
                    {
                        var uniqueName = exp.GetOrDefault("UniqueEmitterName", exp.Name);
                        if (!DiscoveredEmitters.Any(x => x.Equals(uniqueName, StringComparison.OrdinalIgnoreCase) || x.StartsWith(uniqueName + " ", StringComparison.OrdinalIgnoreCase)))
                            DiscoveredEmitters.Add(uniqueName);
                        ParseEmitterObject(exp, uniqueName, seenRenderers, emitterParams, disabledRenderers, null);
                    }
                }
                catch (Exception e)
                {
                    Log.Warning(e, "Failed to parse Niagara export {ExpName}", exp.Name);
                }
            }
        }

        if (systemSpawnProg is not null || systemUpdateProg is not null)
        {
            SystemSimulation = new NiagaraSystemVmSimulation(
                systemSpawnProg,
                systemUpdateProg,
                sysCompiled,
                exposedStore,
                systemSlots);
        }

        DumpNiagaraPackage(assetObj, emitterParams, disabledEmitters, disabledRenderers, systemSpawnProg, systemUpdateProg);
    }

    private sealed class ExtractedEmitterData
    {
        public Vector3? MeshScale;
        public Vector2? SpriteSize;
        public Vector4? Color;
        public float? GravityZ;
        public float? VelocityZ;
        public float? Lifetime;
        public float? LineLength;
        public float? CylinderRadius;
        public float? CylinderHeight;
        public float? VerticalOffsetZ;
        public float? ScaleRgbBoost;
        public float? PositionDirection;
        public int? MeshInstanceCount;
        public float? SpawnRate;
        public float? RibbonWidth;
        public float? SizeScalar;
        public float? OpacityScalar;
        public bool IsGpuSim;
        public NiagaraDataSetCompiledLayout? DataSetLayout;
        public NiagaraVmProgram? SpawnScript;
        public NiagaraVmProgram? UpdateScript;
        public NiagaraVmProgram? EmitterSpawnScript;
        public NiagaraVmProgram? EmitterUpdateScript;
        public readonly List<NiagaraParameterStoreData> RapidStores = [];
        public readonly List<NiagaraColorCurve> ColorCurves = [];
        public readonly List<NiagaraFloatCurve> FloatCurves = [];
        public readonly List<string> RawParameterLog = [];
    }

    private static void ReadParamStore(FStructFallback? storeStruct, ExtractedEmitterData ep, string prefix)
    {
        if (storeStruct is null)
            return;

        var store = NiagaraParameterStoreData.FromStruct(storeStruct);
        foreach (var (name, entry) in store.Entries)
        {
            var typeName = entry.TypeName;
            if (typeName.Contains("LinearColor", StringComparison.OrdinalIgnoreCase) || typeName.Contains("Vector4", StringComparison.OrdinalIgnoreCase))
            {
                if (store.TryGetVector4(name, out var v4))
                {
                    ep.RawParameterLog.Add($"[{prefix}] {name} ({typeName}) @ {entry.Offset} = {v4}");
                    if (name.Contains("Color", StringComparison.OrdinalIgnoreCase) && v4.LengthSquared() > 0.001f)
                        ep.Color ??= v4;
                }
            }
            else if (typeName.Contains("Vector3", StringComparison.OrdinalIgnoreCase) || typeName.Equals("Vector", StringComparison.OrdinalIgnoreCase) || typeName.Equals("NiagaraPosition", StringComparison.OrdinalIgnoreCase))
            {
                if (store.TryGetVector3(name, out var v3))
                {
                    ep.RawParameterLog.Add($"[{prefix}] {name} ({typeName}) @ {entry.Offset} = {v3}");
                    if (name.Contains("Scale", StringComparison.OrdinalIgnoreCase) && v3.LengthSquared() > 0.0001f && v3.LengthSquared() < 1e8f)
                        ep.MeshScale ??= v3;
                    else if (name.Contains("Gravity", StringComparison.OrdinalIgnoreCase) && MathF.Abs(v3.Z) < 1e5f)
                        ep.GravityZ ??= v3.Z;
                    else if (name.Contains("Velocity", StringComparison.OrdinalIgnoreCase) && MathF.Abs(v3.Z) < 1e5f)
                        ep.VelocityZ ??= v3.Z;
                }
            }
            else if (typeName.Contains("Vector2", StringComparison.OrdinalIgnoreCase))
            {
                if (store.TryGetVector2(name, out var v2))
                {
                    ep.RawParameterLog.Add($"[{prefix}] {name} ({typeName}) @ {entry.Offset} = {v2}");
                    if (name.Contains("Size", StringComparison.OrdinalIgnoreCase) && v2.LengthSquared() > 0.01f && v2.LengthSquared() < 1e8f)
                        ep.SpriteSize ??= v2;
                }
            }
            else if (typeName.Contains("Float", StringComparison.OrdinalIgnoreCase) || typeName.Equals("float", StringComparison.OrdinalIgnoreCase))
            {
                if (store.TryGetFloat(name, out var f))
                {
                    ep.RawParameterLog.Add($"[{prefix}] {name} ({typeName}) @ {entry.Offset} = {f}");
                    if (name.Contains("Lifetime", StringComparison.OrdinalIgnoreCase) && f is > 0.05f and < 60f)
                        ep.Lifetime ??= f;
                    else if (name.Contains("SpawnRate", StringComparison.OrdinalIgnoreCase) && f is > 0f and < 10000f)
                        ep.SpawnRate ??= f;
                    else if (name.Contains("RibbonWidth", StringComparison.OrdinalIgnoreCase) && f is > 0.1f and < 5000f)
                        ep.RibbonWidth ??= f;
                    else if (name.Contains("LineLength", StringComparison.OrdinalIgnoreCase) && MathF.Abs(f) > 0.1f && MathF.Abs(f) < 50000f)
                        ep.LineLength ??= f;
                    else if (name.Contains("Radius", StringComparison.OrdinalIgnoreCase) && f is > 0.1f and < 50000f)
                        ep.CylinderRadius ??= f;
                    else if (name.Contains("Height", StringComparison.OrdinalIgnoreCase) && f is > 0.1f and < 50000f)
                        ep.CylinderHeight ??= f;
                    else if (name.Contains("SizeScalar", StringComparison.OrdinalIgnoreCase) && f is > 0.01f and < 100f)
                        ep.SizeScalar ??= f;
                    else if (name.Contains("OpacityScalar", StringComparison.OrdinalIgnoreCase) && f is >= 0f and <= 10f)
                        ep.OpacityScalar ??= f;
                }
            }
            else if (typeName.Contains("Int32", StringComparison.OrdinalIgnoreCase) || typeName.Equals("int32", StringComparison.OrdinalIgnoreCase))
            {
                if (store.TryGetInt(name, out var iv))
                {
                    ep.RawParameterLog.Add($"[{prefix}] {name} ({typeName}) @ {entry.Offset} = {iv}");
                    if ((name.Contains("BurstCount", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("SpawnCount", StringComparison.OrdinalIgnoreCase)) && iv is > 0 and <= 512)
                        ep.MeshInstanceCount ??= iv;
                }
            }
        }
    }

    private static Dictionary<string, ExtractedEmitterData> ExtractPackageEmitterData(
        UObject assetObj,
        out NiagaraVmProgram? systemSpawnProg,
        out NiagaraVmProgram? systemUpdateProg,
        out NiagaraParameterStoreData? exposedStore)
    {
        systemSpawnProg = null;
        systemUpdateProg = null;
        exposedStore = null;

        var result = new Dictionary<string, ExtractedEmitterData>(StringComparer.OrdinalIgnoreCase);

        ExtractedEmitterData GetOrAdd(string key)
        {
            if (!result.TryGetValue(key, out var d))
            {
                d = new ExtractedEmitterData();
                result[key] = d;
            }
            return d;
        }

        var globalData = GetOrAdd("Global");

        if (assetObj.TryGetValue(out FStructFallback exposedFb, "ExposedParameters"))
        {
            exposedStore = NiagaraParameterStoreData.FromStruct(exposedFb);
            ReadParamStore(exposedFb, globalData, "ExposedParameters");
        }

        if (assetObj.TryGetValue(out FStructFallback sysCompiledFb, "SystemCompiledData") &&
            sysCompiledFb.TryGetValue(out FStructFallback instStoreFb, "InstanceParamStore"))
        {
            ReadParamStore(instStoreFb, globalData, "SystemCompiledData.InstanceParamStore");
        }

        UObject? sysSpawnObj = null;
        UObject? sysUpdateObj = null;
        if (assetObj.TryGetValue(out FPackageIndex sysSpawnIdx, "SystemSpawnScript") && sysSpawnIdx is { IsNull: false } && sysSpawnIdx.TryLoad(out sysSpawnObj))
        {
            systemSpawnProg = NiagaraVmProgram.FromScriptObject(sysSpawnObj);
            if (systemSpawnProg is not null && exposedStore is not null)
                systemSpawnProg.ParamStore.ApplyOverridesFrom(exposedStore);
        }
        if (assetObj.TryGetValue(out FPackageIndex sysUpdateIdx, "SystemUpdateScript") && sysUpdateIdx is { IsNull: false } && sysUpdateIdx.TryLoad(out sysUpdateObj))
        {
            systemUpdateProg = NiagaraVmProgram.FromScriptObject(sysUpdateObj);
            if (systemUpdateProg is not null && exposedStore is not null)
                systemUpdateProg.ParamStore.ApplyOverridesFrom(exposedStore);
        }

        if (assetObj.Owner is not { } pkg)
            return result;

        foreach (var exp in pkg.GetExports())
        {
            try
            {
                if (!exp.ExportType.Contains("NiagaraScript", StringComparison.OrdinalIgnoreCase))
                    continue;

                var isSystemOuter = exp == sysSpawnObj || exp == sysUpdateObj ||
                                    (exp.Outer is { } outerSysCheck && outerSysCheck.TryLoad(out var outerSysObj) && outerSysObj == assetObj);
                if (isSystemOuter)
                {
                    if (exp.Name.Contains("Spawn", StringComparison.OrdinalIgnoreCase))
                        systemSpawnProg ??= NiagaraVmProgram.FromScriptObject(exp);
                    else if (exp.Name.Contains("Update", StringComparison.OrdinalIgnoreCase))
                        systemUpdateProg ??= NiagaraVmProgram.FromScriptObject(exp);
                    continue;
                }

                var scriptOwnerName = exp.Outer is { } outerIdx && !outerIdx.Name.IsNone ? (outerIdx.Name.Text ?? "Global") : "Global";
                if (exp.Outer is { } outerResolved && outerResolved.TryLoad(out var outerObj) && outerObj is not null)
                    scriptOwnerName = outerObj.GetOrDefault("UniqueEmitterName", scriptOwnerName);

                var ep = GetOrAdd(scriptOwnerName);
                ep.LineLength ??= globalData.LineLength;
                ep.SizeScalar ??= globalData.SizeScalar;
                ep.OpacityScalar ??= globalData.OpacityScalar;

                IngestNiagaraScript(exp, ep, exposedStore);
            }
            catch (Exception ex)
            {
                GetOrAdd("Global").RawParameterLog.Add($"[ScriptERR {exp.Name}] {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Propagate RapidIterationParameters across EmitterSpawn/EmitterUpdate -> Particle Spawn/Update scripts
        foreach (var ep in result.Values)
            PropagateRapidStoresToVm(ep);

        return result;
    }

    private static void PropagateRapidStoresToVm(ExtractedEmitterData ep)
    {
        foreach (var store in ep.RapidStores)
        {
            ep.SpawnScript?.ParamStore.ApplyOverridesFrom(store);
            ep.UpdateScript?.ParamStore.ApplyOverridesFrom(store);
            ep.SpawnScript?.ParamStore.ApplyZeroSlotSuffixFallbacksFrom(store);
            ep.UpdateScript?.ParamStore.ApplyZeroSlotSuffixFallbacksFrom(store);
        }
    }

    private static void IngestNiagaraScript(UObject exp, ExtractedEmitterData ep, NiagaraParameterStoreData? exposedStore)
    {
        if (exp.TryGetValue(out FStructFallback rapidFb, "RapidIterationParameters") && rapidFb is not null)
        {
            ReadParamStore(rapidFb, ep, $"{exp.Name}.RapidIterationParameters");
            ep.RapidStores.Add(NiagaraParameterStoreData.FromStruct(rapidFb));
        }
        if (exp.TryGetValue(out FStructFallback execFb, "ScriptExecutionParamStore") && execFb is not null)
            ReadParamStore(execFb, ep, $"{exp.Name}.ScriptExecutionParamStore");

        var vmProg = NiagaraVmProgram.FromScriptObject(exp, out var vmFailReason);
        if (vmProg is not null)
        {
            if (exp.TryGetValue(out FStructFallback riFb, "RapidIterationParameters") && riFb is not null)
            {
                var riStore = NiagaraParameterStoreData.FromStruct(riFb);
                vmProg.ParamStore.ApplyOverridesFrom(riStore);
                vmProg.ParamStore.ApplyZeroSlotSuffixFallbacksFrom(riStore);
            }
            vmProg.ParamStore.ApplyOverridesFrom(exposedStore);
            vmProg.ParamStore.ApplyZeroSlotSuffixFallbacksFrom(exposedStore);

            if (exp.Name.Contains("EmitterSpawn", StringComparison.OrdinalIgnoreCase))
                ep.EmitterSpawnScript ??= vmProg;
            else if (exp.Name.Contains("EmitterUpdate", StringComparison.OrdinalIgnoreCase))
                ep.EmitterUpdateScript ??= vmProg;
            else if (exp.Name.Contains("Spawn", StringComparison.OrdinalIgnoreCase))
                ep.SpawnScript ??= vmProg;
            else if (exp.Name.Contains("Update", StringComparison.OrdinalIgnoreCase))
                ep.UpdateScript ??= vmProg;

            ep.RawParameterLog.Add($"[{exp.Name}.VM] ByteCode={vmProg.ByteCode.Length}B, TempRegs={vmProg.NumTempRegisters}, Literals={vmProg.ScriptLiterals.Length}B, Attrs={vmProg.Attributes.Length}, ExtFuncs={vmProg.ExternalFunctions.Length}");
        }
        else
        {
            ep.RawParameterLog.Add($"[{exp.Name}.VM_NONE] {vmFailReason}");
        }

        var defaultDis = exp.GetOrDefault<FStructFallback[]>("CachedDefaultDataInterfaces", []);
        foreach (var diEntry in defaultDis)
        {
            try
            {
                var diName = SafeGetName(diEntry, "Name", "Curve");
                if (diEntry.GetOrDefault<FPackageIndex?>("DataInterface") is not { IsNull: false } diIdx || !diIdx.TryLoad(out var diObj))
                    continue;

                var lut = diObj.GetOrDefault<float[]>("ShaderLUT", []);
                if (lut.Length == 0)
                    continue;

                var minTime = diObj.GetOrDefault("LUTMinTime", 0f);
                var maxTime = diObj.GetOrDefault("LUTMaxTime", 1f);
                var invRange = diObj.GetOrDefault("LUTInvTimeRange", 1f);

                if (diObj.ExportType.Contains("ColorCurve", StringComparison.OrdinalIgnoreCase) && lut.Length >= 4)
                {
                    var cc = new NiagaraColorCurve
                    {
                        Name = diName,
                        Lut = lut,
                        MinTime = minTime,
                        MaxTime = maxTime,
                        InvTimeRange = invRange
                    };
                    if (!ep.ColorCurves.Any(x => x.Name.Equals(diName, StringComparison.OrdinalIgnoreCase)))
                    {
                        ep.ColorCurves.Add(cc);
                        var sample0 = cc.Sample(0f);
                        var sampleMid = cc.Sample(0.5f);
                        ep.RawParameterLog.Add($"ColorCurve '{diName}' ({lut.Length / 4} samples): t0={sample0}, t0.5={sampleMid}");
                        ep.Color ??= sample0.LengthSquared() > sampleMid.LengthSquared() ? sample0 : sampleMid;
                    }
                }
                else if (diObj.ExportType.Contains("Curve", StringComparison.OrdinalIgnoreCase))
                {
                    var fc = new NiagaraFloatCurve
                    {
                        Name = diName,
                        Lut = lut,
                        MinTime = minTime,
                        MaxTime = maxTime,
                        InvTimeRange = invRange
                    };
                    if (!ep.FloatCurves.Any(x => x.Name.Equals(diName, StringComparison.OrdinalIgnoreCase)))
                    {
                        ep.FloatCurves.Add(fc);
                        ep.RawParameterLog.Add($"FloatCurve '{diName}' ({lut.Length} samples, [{minTime}..{maxTime}]): t0={fc.Sample(0f):0.###}, t0.5={fc.Sample(0.5f):0.###}, t1={fc.Sample(1f):0.###}");
                    }
                }
            }
            catch
            {
            }
        }
    }

    private static string ResolveBindingAttribute(UObject renderer, string bindingPropName, string defaultAttr)
    {
        if (!renderer.TryGetValue(out FStructFallback binding, bindingPropName) || binding is null)
            return defaultAttr;

        foreach (var varProp in new[] { "DataSetBindableVariable", "BoundVariable", "RootVariable" })
        {
            if (binding.TryGetValue(out CUE4Parse.UE4.Objects.Niagara.FNiagaraVariableBase nv, varProp) && nv is not null && !nv.Name.IsNone)
            {
                var n = nv.Name.Text;
                if (!string.IsNullOrEmpty(n) && !string.Equals(n, "None", StringComparison.OrdinalIgnoreCase))
                    return n;
            }
            if (binding.TryGetValue(out FStructFallback fbVar, varProp) && fbVar is not null)
            {
                var n = NiagaraParameterStoreData.SafeFName(fbVar, "Name");
                if (!string.IsNullOrEmpty(n) && !string.Equals(n, "None", StringComparison.OrdinalIgnoreCase))
                    return n;
            }
        }
        return defaultAttr;
    }

    private void ParseEmitterObject(
        UObject emitterObj,
        string emitterName,
        HashSet<string> seenRenderers,
        Dictionary<string, ExtractedEmitterData> emitterParams,
        List<string> disabledRenderers,
        List<NiagaraEmitterVmSimulation>? createdSims)
    {
        var uniqueName = emitterObj.GetOrDefault("UniqueEmitterName", emitterName);
        var bLocalSpace = emitterObj.GetOrDefault("bLocalSpace", false);
        var isGpuSim = emitterObj.TryGetValue(out FName simTargetName, "SimTarget") && !simTargetName.IsNone &&
                       (simTargetName.Text?.Contains("GPU", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!isGpuSim && emitterObj.TryGetValue(out byte simTargetByte, "SimTarget"))
            isGpuSim = simTargetByte == 1;

        var renderers = emitterObj.GetOrDefault<FPackageIndex[]>("RendererProperties", []) ?? [];
        if (emitterObj.TryGetValue(out FStructFallback[] versionedData, "VersionedEmitterData") && versionedData is { Length: > 0 } && versionedData[0] is not null)
        {
            if (renderers.Length == 0)
                renderers = versionedData[0].GetOrDefault<FPackageIndex[]>("RendererProperties", []) ?? [];
            bLocalSpace = versionedData[0].GetOrDefault("bLocalSpace", bLocalSpace);
            if (!isGpuSim && versionedData[0].TryGetValue(out FName vSimName, "SimTarget") && !vSimName.IsNone)
                isGpuSim = vSimName.Text?.Contains("GPU", StringComparison.OrdinalIgnoreCase) ?? false;
        }

        if (isGpuSim)
        {
            var ep = FindEmitterData(emitterParams, uniqueName) ?? FindEmitterData(emitterParams, emitterName);
            if (ep is not null)
                ep.IsGpuSim = true;
        }

        // Also ingest scripts directly referenced by UE4/UE5 emitter ScriptProps (handles external/inherited scripts)
        var targetEp = FindEmitterData(emitterParams, uniqueName) ?? FindEmitterData(emitterParams, emitterName);
        if (targetEp is not null)
        {
            void TryIngestScriptProp(FStructFallback? holder, string propName)
            {
                if (holder is null) return;
                if (holder.TryGetValue(out FStructFallback props, propName) &&
                    props.GetOrDefault<FPackageIndex?>("Script") is { IsNull: false } sIdx &&
                    sIdx.TryLoad(out var sObj))
                {
                    IngestNiagaraScript(sObj, targetEp, null);
                }
            }
            void TryIngestFromObj(UObject obj, string propName)
            {
                if (obj.TryGetValue(out FStructFallback props, propName) &&
                    props.GetOrDefault<FPackageIndex?>("Script") is { IsNull: false } sIdx &&
                    sIdx.TryLoad(out var sObj))
                {
                    IngestNiagaraScript(sObj, targetEp, null);
                }
            }
            TryIngestFromObj(emitterObj, "SpawnScriptProps");
            TryIngestFromObj(emitterObj, "UpdateScriptProps");
            TryIngestFromObj(emitterObj, "EmitterSpawnScriptProps");
            TryIngestFromObj(emitterObj, "EmitterUpdateScriptProps");
            if (emitterObj.TryGetValue(out FStructFallback[] vData, "VersionedEmitterData") && vData is { Length: > 0 })
            {
                TryIngestScriptProp(vData[0], "SpawnScriptProps");
                TryIngestScriptProp(vData[0], "UpdateScriptProps");
                TryIngestScriptProp(vData[0], "EmitterSpawnScriptProps");
                TryIngestScriptProp(vData[0], "EmitterUpdateScriptProps");
            }
            PropagateRapidStoresToVm(targetEp);
        }

        targetEp?.RawParameterLog.Add($"[RendererProperties] count={renderers.Length} on {emitterObj.Name} ({emitterObj.ExportType})");

        foreach (var rIdx in renderers)
        {
            if (rIdx is null || rIdx.IsNull || !rIdx.TryLoad(out var rendererObj))
                continue;
            if (!seenRenderers.Add(rendererObj.GetPathName()))
                continue;

            var enabled = rendererObj.GetOrDefault("bIsEnabled", true);
            targetEp?.RawParameterLog.Add($"  Renderer '{rendererObj.Name}' ({rendererObj.ExportType}) enabled={enabled}");
            if (!enabled)
            {
                disabledRenderers.Add($"{uniqueName}/{rendererObj.Name} ({rendererObj.ExportType})");
                continue;
            }

            var exportType = rendererObj.ExportType;
            if (exportType.Contains("NiagaraMeshRendererProperties", StringComparison.OrdinalIgnoreCase))
            {
                ParseMeshRenderer(rendererObj, uniqueName, bLocalSpace, emitterParams, createdSims);
            }
            else if (exportType.Contains("NiagaraSpriteRendererProperties", StringComparison.OrdinalIgnoreCase) ||
                     exportType.Contains("NiagaraRibbonRendererProperties", StringComparison.OrdinalIgnoreCase))
            {
                ParseSpriteRenderer(rendererObj, uniqueName, bLocalSpace, emitterParams, createdSims);
            }
            else if (exportType.Contains("NiagaraLightRendererProperties", StringComparison.OrdinalIgnoreCase))
            {
                ParseLightRenderer(rendererObj, uniqueName, emitterParams);
            }
        }
    }

    private void ParseLightRenderer(UObject lightRenderer, string emitterName, Dictionary<string, ExtractedEmitterData> emitterParams)
    {
        var ep = FindEmitterData(emitterParams, emitterName);
        var radiusScale = lightRenderer.GetOrDefault("RadiusScale", 1.0f);
        var defCol = lightRenderer.GetOrDefault("DefaultColor", new FVector(1, 1, 1));
        var col = ep?.Color is { } ec && ec.LengthSquared() > 0.001f
            ? new Vector3(ec.X, ec.Y, ec.Z)
            : new Vector3(defCol.X, defCol.Y, defCol.Z);

        LightEmitters.Add(new NiagaraLightEmitterDesc
        {
            EmitterName = emitterName,
            Color = col,
            RadiusScale = radiusScale,
            DefaultRadius = Math.Clamp(300f * radiusScale, 50f, 3000f),
            Intensity = 6.0f,
            Enabled = true
        });
    }

    private static ExtractedEmitterData? FindEmitterData(Dictionary<string, ExtractedEmitterData> map, string emitterName)
    {
        if (map.TryGetValue(emitterName, out var exact))
            return exact;
        foreach (var (k, v) in map)
        {
            if (k.Equals(emitterName, StringComparison.OrdinalIgnoreCase) ||
                k.StartsWith(emitterName + "_", StringComparison.OrdinalIgnoreCase) ||
                emitterName.StartsWith(k + "_", StringComparison.OrdinalIgnoreCase))
                return v;
        }
        return map.TryGetValue("Global", out var g) ? g : null;
    }

    private void ParseMeshRenderer(UObject meshRenderer, string emitterName, bool bLocalSpace, Dictionary<string, ExtractedEmitterData> emitterParams, List<NiagaraEmitterVmSimulation>? createdSims)
    {
        var meshes = new List<(UStaticMesh Mesh, Vector3 Scale, Vector3 Rot)>();

        if (meshRenderer.TryGetValue(out FPackageIndex singleMeshIdx, "ParticleMesh") && !singleMeshIdx.IsNull && singleMeshIdx.TryLoad<UStaticMesh>(out var singleMesh))
        {
            meshes.Add((singleMesh, Vector3.One, Vector3.Zero));
        }

        if (meshRenderer.TryGetValue(out FStructFallback[] meshProps, "Meshes"))
        {
            foreach (var mp in meshProps)
            {
                if (mp.GetOrDefault<FPackageIndex?>("Mesh") is { IsNull: false } mIdx && mIdx.TryLoad<UStaticMesh>(out var m))
                {
                    var sc = mp.GetOrDefault("Scale", new FVector(1, 1, 1));
                    var rot = mp.GetOrDefault("Rotation", new FRotator(0, 0, 0));
                    meshes.Add((m, new Vector3(sc.X, sc.Y, sc.Z), new Vector3(rot.Pitch, rot.Yaw, rot.Roll)));
                }
            }
        }

        if (meshes.Count == 0)
            return;

        MaterialAsset? overrideMat = null;
        if (meshRenderer.TryGetValue(out FStructFallback[] overMats, "OverrideMaterials"))
        {
            foreach (var om in overMats)
            {
                var matIdx = om.GetOrDefault<FPackageIndex?>("ExplicitMat")
                             ?? om.GetOrDefault<FPackageIndex?>("Material")
                             ?? om.GetOrDefault<FPackageIndex?>("Mic");
                if (matIdx is { IsNull: false } && matIdx.TryLoad<UUnrealMaterial>(out var loadedMat))
                {
                    overrideMat = new MaterialAsset(loadedMat);
                    break;
                }
            }
        }

        var ep = FindEmitterData(emitterParams, emitterName);
        var rapidScale = ep?.MeshScale ?? Vector3.One;
        var rapidColor = ep?.Color ?? Vector4.One;
        var colorCurve = ep?.ColorCurves.FirstOrDefault();
        var posCurve = ep?.FloatCurves.FirstOrDefault(c => c.Name.Contains("Position", StringComparison.OrdinalIgnoreCase));

        var scaleFactorCurve = ep?.FloatCurves.FirstOrDefault(c => c.Name.Contains("Scale Factor", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("ScaleFactor", StringComparison.OrdinalIgnoreCase));
        var scaleRgbCurve = ep?.FloatCurves.FirstOrDefault(c => c.Name.Contains("Scale RGB", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("ScaleRGB", StringComparison.OrdinalIgnoreCase));
        var alphaCurve = ep?.FloatCurves.FirstOrDefault(c => c.Name.Contains("Scale Alpha", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("Alpha", StringComparison.OrdinalIgnoreCase));
        var dynParam1Curve = ep?.FloatCurves.FirstOrDefault(c => c.Name.Contains("Param 1", StringComparison.OrdinalIgnoreCase));
        var dynParam2Curve = ep?.FloatCurves.FirstOrDefault(c => c.Name.Contains("Param 2", StringComparison.OrdinalIgnoreCase));

        foreach (var (mesh, scale, rot) in meshes)
        {
            var mat = overrideMat;
            if (mat is null && mesh.Materials is { Length: > 0 })
            {
                foreach (var mRef in mesh.Materials)
                {
                    if (mRef is { IsNull: false } && mRef.TryLoad<UUnrealMaterial>(out var defMat))
                    {
                        mat = new MaterialAsset(defMat);
                        break;
                    }
                }
            }

            if (mat is not null)
                NativeAssetCache.DumpNiagaraMaterialOnce(mat);

            var lineLength = ep?.LineLength ?? (posCurve is not null ? 15f : 0f);
            var instanceCount = ep?.MeshInstanceCount ?? (ep?.SpawnRate is { } sr ? Math.Clamp((int) MathF.Round(sr * (ep.Lifetime ?? 2.0f)), 1, 64) : 2);
            var lifetime = ep?.Lifetime ?? 2.0f;

            var posAttr = ResolveBindingAttribute(meshRenderer, "PositionBinding", "Position");
            var colAttr = ResolveBindingAttribute(meshRenderer, "ColorBinding", "Color");
            var sclAttr = ResolveBindingAttribute(meshRenderer, "ScaleBinding", "Scale");
            var oriAttr = ResolveBindingAttribute(meshRenderer, "MeshOrientationBinding", "MeshOrientation");
            var dynAttr = ResolveBindingAttribute(meshRenderer, "DynamicMaterialBinding", "DynamicMaterialParameter");
            var rndAttr = ResolveBindingAttribute(meshRenderer, "MaterialRandomBinding", "MaterialRandom");
            var ageAttr = ResolveBindingAttribute(meshRenderer, "NormalizedAgeBinding", "NormalizedAge");
            var velAttr = ResolveBindingAttribute(meshRenderer, "VelocityBinding", "Velocity");

            uint facingMode = 0;
            if (meshRenderer.TryGetValue(out FName fmName, "FacingMode"))
            {
                var fmStr = fmName.Text;
                facingMode = fmStr.Contains("Velocity", StringComparison.OrdinalIgnoreCase) ? 1u
                    : fmStr.Contains("CameraPosition", StringComparison.OrdinalIgnoreCase) ? 2u
                    : fmStr.Contains("CameraPlane", StringComparison.OrdinalIgnoreCase) ? 3u
                    : 0u;
            }
            var lockedAxisEnable = meshRenderer.GetOrDefault("bLockedAxisEnable", false);
            var lockedAxisF = meshRenderer.GetOrDefault("LockedAxis", new FVector(0, 0, 1));
            var lockedAxis = new Vector3(lockedAxisF.X, lockedAxisF.Y, lockedAxisF.Z);
            if (lockedAxis.LengthSquared() < 1e-6f)
                lockedAxis = Vector3.UnitZ;
            uint lockedAxisSpace = 0;
            if (meshRenderer.TryGetValue(out FName lasName, "LockedAxisSpace"))
            {
                var lasStr = lasName.Text;
                lockedAxisSpace = lasStr.Contains("Simulation", StringComparison.OrdinalIgnoreCase) ? 0u
                    : lasStr.Contains("World", StringComparison.OrdinalIgnoreCase) ? 1u
                    : 2u;
            }

            var vmSim = new NiagaraEmitterVmSimulation(
                ep?.SpawnScript,
                ep?.UpdateScript,
                ep?.DataSetLayout,
                instanceCount,
                lifetime,
                posAttr: posAttr,
                colorAttr: colAttr,
                scaleAttr: sclAttr,
                spawnRate: ep?.SpawnRate,
                burstCount: ep?.MeshInstanceCount,
                orientAttr: oriAttr,
                dynParamAttr: dynAttr,
                matRandomAttr: rndAttr,
                emitterSpawnProgram: ep?.EmitterSpawnScript,
                emitterUpdateProgram: ep?.EmitterUpdateScript,
                normAgeAttr: ageAttr,
                velAttr: velAttr)
            {
                RuntimeLocalSpace = bLocalSpace
            };
            if (vmSim.HasVmPrograms)
                createdSims?.Add(vmSim);

            MeshEmitters.Add(new NiagaraMeshEmitterDesc
            {
                EmitterName = emitterName,
                Mesh = mesh,
                Material = mat,
                LocalSpace = bLocalSpace,
                Scale = scale * rapidScale,
                RotationEuler = rot,
                Color = rapidColor,
                ScaleRgbBoost = ep?.ScaleRgbBoost ?? 3.0f,
                MaxParticles = instanceCount,
                Lifetime = lifetime,
                TravelVector = new Vector3(0f, 0f, lineLength),
                PositionDirection = ep?.PositionDirection ?? -1f,
                GravityZ = ep?.GravityZ ?? 0f,
                ColorCurve = colorCurve,
                PositionCurve = posCurve,
                ScaleFactorCurve = scaleFactorCurve,
                ScaleRgbCurve = scaleRgbCurve,
                AlphaCurve = alphaCurve,
                DynamicParam1Curve = dynParam1Curve,
                DynamicParam2Curve = dynParam2Curve,
                FacingMode = facingMode,
                LockedAxisEnable = lockedAxisEnable,
                LockedAxis = lockedAxis,
                LockedAxisSpace = lockedAxisSpace,
                VmSimulation = vmSim.HasVmPrograms ? vmSim : null,
                Enabled = true
            });
        }
    }

    private void ParseSpriteRenderer(UObject spriteRenderer, string emitterName, bool bLocalSpace, Dictionary<string, ExtractedEmitterData> emitterParams, List<NiagaraEmitterVmSimulation>? createdSims)
    {
        MaterialAsset? matNode = null;
        UTexture? spriteTex = null;
        var isAdditive = true;

        if (spriteRenderer.GetOrDefault<FPackageIndex?>("Material") is { IsNull: false } matIdx && matIdx.TryLoad<UUnrealMaterial>(out var uMat))
        {
            matNode = new MaterialAsset(uMat);
            NativeAssetCache.DumpNiagaraMaterialOnce(matNode);
            isAdditive = matNode.BlendMode is EBlendMode.BLEND_Additive or EBlendMode.BLEND_AlphaComposite;

            if (matNode.TryGetTexture(out var dTex, EMaterialTextureKind.Diffuse) ||
                matNode.TryGetTexture(out dTex, EMaterialTextureKind.Emissive))
            {
                spriteTex = dTex;
            }
            else
            {
                var sets = matNode.GetNativeShaders(validateOnDevice: false);
                foreach (var set in sets)
                {
                    spriteTex = matNode.ResolveNativeMaterialResources(set)
                        .FirstOrDefault(x => x is not null &&
                                             !x.Name.Contains("Noise", StringComparison.OrdinalIgnoreCase) &&
                                             !x.Name.Contains("Streaks", StringComparison.OrdinalIgnoreCase));
                    if (spriteTex is not null)
                        break;
                }
            }
        }

        var subImg = spriteRenderer.GetOrDefault("SubImageSize", new FVector2D(1, 1));
        var subImageSize = new Vector2(MathF.Max(1f, subImg.X), MathF.Max(1f, subImg.Y));

        var ep = FindEmitterData(emitterParams, emitterName);
        var colorCurve = ep?.ColorCurves.FirstOrDefault();

        var uniformSizeCurve = ep?.FloatCurves.FirstOrDefault(c => c.Name.Contains("Sprite Size", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("SpriteSize", StringComparison.OrdinalIgnoreCase));
        var scaleFactorCurve = ep?.FloatCurves.FirstOrDefault(c => c.Name.Contains("Scale Factor", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("ScaleFactor", StringComparison.OrdinalIgnoreCase));
        var scaleRgbCurve = ep?.FloatCurves.FirstOrDefault(c => c.Name.Contains("Scale RGB", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("ScaleRGB", StringComparison.OrdinalIgnoreCase));
        var alphaCurve = ep?.FloatCurves.FirstOrDefault(c => c.Name.Contains("Scale Alpha", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("Alpha", StringComparison.OrdinalIgnoreCase));
        var cylRadiusCurve = ep?.FloatCurves.FirstOrDefault(c => c.Name.Contains("Cylinder Radius", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("Radius", StringComparison.OrdinalIgnoreCase));
        var latheCurve = ep?.FloatCurves.FirstOrDefault(c => c.Name.Contains("Lathe", StringComparison.OrdinalIgnoreCase));
        var vPosCurve = ep?.FloatCurves.FirstOrDefault(c => c.Name.Contains("V Position", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("Position", StringComparison.OrdinalIgnoreCase));
        var velCurve = ep?.FloatCurves.FirstOrDefault(c => c.Name.Contains("Velocity", StringComparison.OrdinalIgnoreCase));

        var sizeScalar = ep?.SizeScalar ?? 1f;
        var baseSize = 20f;
        if (uniformSizeCurve is not null)
        {
            baseSize = Math.Clamp(uniformSizeCurve.Sample(0.5f) * sizeScalar, 1f, 500f);
        }
        else if (ep?.SpriteSize is { } ss)
        {
            baseSize = ss.X * sizeScalar;
        }

        var tint = ep?.Color ?? Vector4.One;
        if (ep?.Color is null && matNode is not null && matNode.TryGetVector(out var vCol, "Color", "EmissiveColor", "Tint", "ParticleColor"))
        {
            if (vCol.R + vCol.G + vCol.B > 0.01f)
                tint = new Vector4(vCol.R, vCol.G, vCol.B, Math.Clamp(vCol.A, 0.1f, 1f));
        }

        var lineLen = ep?.CylinderHeight ?? ep?.LineLength ?? 0f;
        var cylRad = ep?.CylinderRadius ?? 0f;
        var lifetime = ep?.Lifetime ?? 2.0f;
        var hasVmPrograms = (ep?.SpawnScript?.IsValid ?? false) || (ep?.UpdateScript?.IsValid ?? false);
        var hasExplicitCurveOrSpawn = ep?.SpawnRate is > 0 || ep?.MeshInstanceCount is > 0 || uniformSizeCurve is not null || colorCurve is not null;
        var activeCount = ep?.SpawnRate is { } sr
            ? Math.Clamp((int) MathF.Round(sr * lifetime), 1, 512)
            : (ep?.MeshInstanceCount ?? (hasVmPrograms ? 16 : (hasExplicitCurveOrSpawn ? 16 : 1)));

        var pivot = spriteRenderer.GetOrDefault("PivotInUVSpace", new FVector2D(0.5f, 0.5f));
        var subImageBlend = spriteRenderer.GetOrDefault("bSubImageBlend", false);
        var isRibbon = spriteRenderer.ExportType.Contains("NiagaraRibbonRendererProperties", StringComparison.OrdinalIgnoreCase);
        var ribbonWidth = ep?.RibbonWidth ?? Math.Clamp(baseSize * 0.6f, 4f, 120f);
        if (isRibbon && activeCount < 16)
            activeCount = 24;

        var posAttr = ResolveBindingAttribute(spriteRenderer, "PositionBinding", "Position");
        var colAttr = ResolveBindingAttribute(spriteRenderer, "ColorBinding", "Color");
        var szAttr = isRibbon
            ? ResolveBindingAttribute(spriteRenderer, "RibbonWidthBinding", "RibbonWidth")
            : ResolveBindingAttribute(spriteRenderer, "SpriteSizeBinding", "SpriteSize");
        var rotAttr = ResolveBindingAttribute(spriteRenderer, "SpriteRotationBinding", "SpriteRotation");
        var subAttr = ResolveBindingAttribute(spriteRenderer, "SubImageIndexBinding", "SubImageIndex");
        var dynAttr = ResolveBindingAttribute(spriteRenderer, "DynamicMaterialBinding", "DynamicMaterialParameter");
        var rndAttr = ResolveBindingAttribute(spriteRenderer, "MaterialRandomBinding", "MaterialRandom");
        var ageAttr = ResolveBindingAttribute(spriteRenderer, "NormalizedAgeBinding", "NormalizedAge");
        var velAttr = ResolveBindingAttribute(spriteRenderer, "VelocityBinding", "Velocity");

        var vmSim = new NiagaraEmitterVmSimulation(
            ep?.SpawnScript,
            ep?.UpdateScript,
            ep?.DataSetLayout,
            Math.Max(1, activeCount),
            lifetime,
            posAttr: posAttr,
            colorAttr: colAttr,
            spriteSizeAttr: szAttr,
            spawnRate: ep?.SpawnRate,
            burstCount: ep?.MeshInstanceCount,
            spriteRotAttr: rotAttr,
            subImgAttr: subAttr,
            dynParamAttr: dynAttr,
            matRandomAttr: rndAttr,
            emitterSpawnProgram: ep?.EmitterSpawnScript,
            emitterUpdateProgram: ep?.EmitterUpdateScript,
            normAgeAttr: ageAttr,
            velAttr: velAttr)
        {
            RuntimeLocalSpace = bLocalSpace
        };
        if (vmSim.HasVmPrograms)
            createdSims?.Add(vmSim);

        SpriteEmitters.Add(new NiagaraSpriteEmitterDesc
        {
            EmitterName = emitterName,
            Material = matNode,
            SpriteTexture = spriteTex,
            LocalSpace = bLocalSpace,
            SubImageSize = subImageSize,
            SpriteSize = new Vector2(baseSize, baseSize),
            Color = tint,
            ColorCurve = colorCurve,
            UniformSizeCurve = uniformSizeCurve,
            ScaleFactorCurve = scaleFactorCurve,
            ScaleRgbCurve = scaleRgbCurve,
            AlphaCurve = alphaCurve,
            CylinderRadiusCurve = cylRadiusCurve,
            LatheProfileCurve = latheCurve,
            VPositionCurve = vPosCurve,
            VelocityCurve = velCurve,
            MaxParticles = activeCount,
            SpawnRadius = cylRad,
            CylinderRadiusMin = cylRadiusCurve is not null ? cylRadiusCurve.Sample(0f) : 0.4f,
            CylinderRadiusMax = cylRadiusCurve is not null ? cylRadiusCurve.Sample(1f) : 1.0f,
            VerticalLineLength = lineLen,
            VerticalOffsetZ = ep?.VerticalOffsetZ ?? 0f,
            VPosStart = vPosCurve is not null ? vPosCurve.Sample(0f) : 0.6f,
            VPosEnd = vPosCurve is not null ? vPosCurve.Sample(1f) : 0.95f,
            VelocitySpeed = ep?.VelocityZ ?? 0f,
            GravityZ = ep?.GravityZ ?? 0f,
            Lifetime = lifetime,
            IsAdditive = isAdditive,
            IsSoftBubble = false,
            UseSphericalDome = vPosCurve is not null && cylRadiusCurve is null,
            PivotInUVSpace = new Vector2(pivot.X, pivot.Y),
            SubImageBlend = subImageBlend,
            IsRibbon = isRibbon,
            RibbonWidth = ribbonWidth,
            VmSimulation = vmSim.HasVmPrograms ? vmSim : null,
            HasExplicitSimData = hasVmPrograms || hasExplicitCurveOrSpawn,
            IsGpuCompute = (ep?.IsGpuSim == true) || (ep?.DataSetLayout?.IsGpuSim == true),
            Enabled = true
        });
    }

    private void DumpNiagaraPackage(
        UObject assetObj,
        Dictionary<string, ExtractedEmitterData> emitterData,
        List<string> disabledEmitters,
        List<string> disabledRenderers,
        NiagaraVmProgram? systemSpawnProg,
        NiagaraVmProgram? systemUpdateProg)
    {
        var sysSimClone = SystemSimulation?.Clone();
        _ = Task.Run(() =>
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"=== Niagara System Diagnostics: {assetObj.Name} ({assetObj.ExportType}) ===");
                sb.AppendLine($"Path: {assetObj.GetPathName()}");
                sb.AppendLine($"Discovered EmitterHandles ({DiscoveredEmitters.Count}): {string.Join(", ", DiscoveredEmitters)}");
                if (disabledEmitters.Count > 0)
                    sb.AppendLine($"Skipped Disabled Emitters ({disabledEmitters.Count}): {string.Join(", ", disabledEmitters)}");
                if (disabledRenderers.Count > 0)
                    sb.AppendLine($"Skipped Disabled Renderers ({disabledRenderers.Count}): {string.Join(", ", disabledRenderers)}");

                sb.AppendLine();
                sb.AppendLine($"Active Mesh Renderers ({MeshEmitters.Count}):");
                foreach (var me in MeshEmitters)
                {
                    var vmOk = me.VmSimulation?.HasVmPrograms ?? false;
                    sb.AppendLine($"  - [Mesh] Emitter={me.EmitterName} | Enabled={me.Enabled} | Cnt={me.MaxParticles} | HasVM={vmOk} | Mesh={me.Mesh.GetPathName()} | Mat={me.Material?.Name ?? "null"} | Scale={me.Scale} | Lifetime={me.Lifetime}");
                    var simToTest = sysSimClone is not null ? sysSimClone.GetClonedSim(me.VmSimulation) : me.VmSimulation?.Clone();
                    if (simToTest is not null)
                    {
                        var simOk = simToTest.TrySimulate(0.5f, out var sample);
                        sb.AppendLine($"    VM Test @ t=0.5s: ok={simOk}, particles={sample.Length}");
                        for (var i = 0; i < Math.Min(3, sample.Length); i++)
                            sb.AppendLine($"      p[{i}]: Pos={sample[i].Position}, Scale={sample[i].Scale}, Col={sample[i].Color}, Rot={sample[i].MeshOrientation}, Dyn={sample[i].DynamicMaterialParameter}");
                        if (!simOk)
                            SnooperDiagnostics.LogWarning("Niagara", assetObj.Name, $"Mesh emitter '{me.EmitterName}' VM simulation produced 0 particles at t=0.5s");
                    }
                }

                sb.AppendLine();
                sb.AppendLine($"Sprite Renderers ({SpriteEmitters.Count}):");
                foreach (var se in SpriteEmitters)
                {
                    var vmOk = se.VmSimulation?.HasVmPrograms ?? false;
                    sb.AppendLine($"  - [Sprite] Emitter={se.EmitterName} | Enabled={se.Enabled} | GPU={se.IsGpuCompute} | HasVM={vmOk} | ExplicitData={se.HasExplicitSimData} | Cnt={se.MaxParticles} | Mat={se.Material?.Name ?? "null"} | Tex={se.SpriteTexture?.GetPathName() ?? "null"} | Size={se.SpriteSize} | Additive={se.IsAdditive}");
                    var simToTest = sysSimClone is not null ? sysSimClone.GetClonedSim(se.VmSimulation) : se.VmSimulation?.Clone();
                    if (simToTest is not null)
                    {
                        var simOk = simToTest.TrySimulate(0.5f, out var sample);
                        sb.AppendLine($"    VM Test @ t=0.5s: ok={simOk}, particles={sample.Length}");
                        for (var i = 0; i < Math.Min(3, sample.Length); i++)
                            sb.AppendLine($"      p[{i}]: Pos={sample[i].Position}, Size={sample[i].SpriteSize}, Col={sample[i].Color}, Rot={sample[i].SpriteRotation:0.###}, Dyn={sample[i].DynamicMaterialParameter}");
                        if (!simOk)
                            SnooperDiagnostics.LogWarning("Niagara", assetObj.Name, $"Sprite emitter '{se.EmitterName}' VM simulation produced 0 particles at t=0.5s (GPU={se.IsGpuCompute})");
                    }
                    else if (se.IsGpuCompute)
                    {
                        SnooperDiagnostics.LogWarning("Niagara", assetObj.Name, $"Sprite emitter '{se.EmitterName}' targets GPUComputeSim (no CPU VectorVM bytecode); enabled={se.Enabled}");
                    }
                }

                if (systemSpawnProg is not null || systemUpdateProg is not null)
                {
                    sb.AppendLine();
                    sb.AppendLine("=== System VM ===");
                    AppendScriptDisassembly(sb, "SystemSpawn", systemSpawnProg);
                    AppendScriptDisassembly(sb, "SystemUpdate", systemUpdateProg);
                }

                if (emitterData.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("=== Emitter Compiled Layouts, Parameters & VectorVM Bytecode Disassembly ===");
                    foreach (var (owner, ep) in emitterData)
                    {
                        sb.AppendLine($"--- Emitter '{owner}' (IsGpuSim={ep.IsGpuSim}, SpawnRate={ep.SpawnRate}, MeshCnt={ep.MeshInstanceCount}, Life={ep.Lifetime}) ---");
                        if (ep.DataSetLayout is { } dsl)
                        {
                            sb.AppendLine($"  DataSetCompiledData: SimTarget={dsl.SimTarget}, Floats={dsl.TotalFloatComponents}, Ints={dsl.TotalInt32Components}, Halfs={dsl.TotalHalfComponents}, Vars={dsl.Variables.Count}");
                            foreach (var v in dsl.Variables)
                                sb.AppendLine($"    var {v.Name} ({v.TypeName}): F[{v.FloatStart}+{v.FloatCount}], I[{v.IntStart}+{v.IntCount}], H[{v.HalfStart}+{v.HalfCount}]");
                        }

                        foreach (var line in ep.RawParameterLog)
                            sb.AppendLine($"  {line}");

                        AppendScriptDisassembly(sb, "EmitterSpawn", ep.EmitterSpawnScript);
                        AppendScriptDisassembly(sb, "EmitterUpdate", ep.EmitterUpdateScript);
                        AppendScriptDisassembly(sb, "Spawn", ep.SpawnScript);
                        AppendScriptDisassembly(sb, "Update", ep.UpdateScript);
                    }
                }

                var report = sb.ToString();
                SnooperDiagnostics.WriteNiagaraLog(assetObj.Name, report);

                if (NativeAssetCache.EnableDiskDumps)
                {
                    var invalidChars = System.IO.Path.GetInvalidFileNameChars();
                    var safeName = string.Concat(assetObj.Name.Select(c => invalidChars.Contains(c) ? '_' : c));
                    var dumpDir = System.IO.Path.Combine(NativeAssetCache.DumpDirectory, $"Niagara_{safeName}");
                    Directory.CreateDirectory(dumpDir);
                    File.WriteAllText(System.IO.Path.Combine(dumpDir, "niagara_dump.txt"), report);
                }
            }
            catch (Exception e)
            {
                SnooperDiagnostics.LogError("Niagara", assetObj.Name, $"Failed to write Niagara diagnostic log: {e}");
                Log.Warning(e, "Failed to write Niagara dump");
            }
        });
    }

    private static void AppendScriptDisassembly(StringBuilder sb, string stage, NiagaraVmProgram? prog)
    {
        if (prog is null)
            return;
        sb.AppendLine($"  [{stage} VM] ByteCode={prog.ByteCode.Length}B, TempRegs={prog.NumTempRegisters}, UserPtrs={prog.NumUserPtrs}, Literals={prog.ScriptLiterals.Length}B, HasInterp={prog.HasInterpolationParameters}, UE5={prog.IsUe5}");
        if (prog.Attributes.Length > 0)
            sb.AppendLine($"    Attributes ({prog.Attributes.Length}): {string.Join(", ", prog.Attributes)}");
        if (prog.DataInterfaceInfos.Length > 0)
            sb.AppendLine($"    DataInterfaceInfos ({prog.DataInterfaceInfos.Length}): {string.Join(", ", prog.DataInterfaceInfos.Select(d => $"{d.Name}@{d.UserPtrIdx}"))}");
        if (prog.ExternalFunctions.Length > 0)
            sb.AppendLine($"    ExternalFunctions ({prog.ExternalFunctions.Length}): {string.Join(", ", prog.ExternalFunctions.Select(f => $"{f.OwnerName}.{f.Name}(in={f.InputParamLocations.Length},out={f.NumOutputs})"))}");
        if (prog.ByteCode.Length > 0)
            sb.Append(NiagaraVmInterpreter.Disassemble(prog));
    }

    private string BuildClipboardSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== Tuned Niagara Settings: {SystemAsset?.Name ?? "NiagaraSystem"} (GlobalTimeScale={GlobalTimeScale:0.##}) ===");
        foreach (var me in MeshEmitters)
        {
            sb.AppendLine($"[Mesh: {me.EmitterName}] Enabled={me.Enabled}, Cnt={me.MaxParticles}, Lifetime={me.Lifetime:0.###}, TimeScale={me.TimeScale:0.##}, PhaseSpread={me.PhaseSpread:0.##}");
            sb.AppendLine($"  Scale={me.Scale}, ScaleMult={me.ScaleMultiplier:0.###}, StartOffset={me.StartOffset}, Travel={me.TravelVector}, PosDir={me.PositionDirection:0.##}, GravityZ={me.GravityZ:0.##}");
            sb.AppendLine($"  RotEuler={me.RotationEuler}, SpinEuler={me.SpinSpeedEuler}, Color={me.Color}, RgbBoost={me.ScaleRgbBoost:0.##}, AlphaMult={me.AlphaMultiplier:0.##}");
        }
        foreach (var se in SpriteEmitters)
        {
            sb.AppendLine($"[Sprite: {se.EmitterName}] Enabled={se.Enabled}, Cnt={se.MaxParticles}, Lifetime={se.Lifetime:0.###}, TimeScale={se.TimeScale:0.##}, Dome={se.UseSphericalDome}, Bubble={se.IsSoftBubble}, Additive={se.IsAdditive}");
            sb.AppendLine($"  Size={se.SpriteSize}, SizeMult={se.SizeMultiplier:0.###}, SpawnRadius={se.SpawnRadius:0.##}, CylRad=[{se.CylinderRadiusMin:0.###}..{se.CylinderRadiusMax:0.###}]");
            sb.AppendLine($"  OriginOffset={se.OriginOffset}, LineLen={se.VerticalLineLength:0.##}, OffsetZ={se.VerticalOffsetZ:0.##}, VPos=[{se.VPosStart:0.###}..{se.VPosEnd:0.###}]");
            sb.AppendLine($"  VelSpeed={se.VelocitySpeed:0.##}, GravityZ={se.GravityZ:0.##}, Turbulence={se.TurbulenceStrength:0.###}, Color={se.Color}, Brightness={se.ColorBrightness:0.##}, AlphaMult={se.AlphaMultiplier:0.##}");
        }
        return sb.ToString();
    }

    }
