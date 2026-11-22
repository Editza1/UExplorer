using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.Core.Math;

namespace UExplorer.Rendering;

/// <summary>
/// 1:1 Unreal Engine Material Uniform Buffer &amp; Preshader evaluator supporting both:
/// - UE 4.25–4.27 (<c>FMaterialUniformExpressionSet::FillUniformBuffer</c> &amp; <c>EvaluatePreshader</c> in UE 4.27 <c>MaterialUniformExpressions.cpp</c>)
/// - UE 5.0–5.2+ (<c>FUniformExpressionSet::FillUniformBuffer</c> in UE 5.2 <c>MaterialUniformExpressions.cpp</c> &amp; <c>UE::Shader::EvaluatePreshader</c> in <c>Preshader.cpp</c>)
/// </summary>
public static class NativeMaterialUniforms
{
    public sealed record Result(byte[] Bytes, IReadOnlyList<(int Offset, Vector4 Value, string Source)> Evaluated, bool UsesRuntimeVirtualTexture)
    {
        public byte[] Constants => Bytes;

        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"ConstantBufferSize={Bytes.Length} UsesRVT={UsesRuntimeVirtualTexture} Evaluated={Evaluated.Count}");
            foreach (var (offset, value, source) in Evaluated)
                sb.AppendLine($"  +{offset,-4} ({value.X:0.###}, {value.Y:0.###}, {value.Z:0.###}, {value.W:0.###})  {source}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// UE 4.25–4.27 <c>EMaterialPreshaderOpcode</c> (<c>Engine/Source/Runtime/Engine/Public/MaterialShared.h:216-260</c>).
    /// </summary>
    private enum EMaterialPreshaderOpcodeUe427 : byte
    {
        Nop = 0,
        ConstantZero = 1,
        Constant = 2,
        ScalarParameter = 3,
        VectorParameter = 4,
        Add = 5,
        Sub = 6,
        Mul = 7,
        Div = 8,
        Fmod = 9,
        Min = 10,
        Max = 11,
        Clamp = 12,
        Sin = 13,
        Cos = 14,
        Tan = 15,
        Asin = 16,
        Acos = 17,
        Atan = 18,
        Atan2 = 19,
        Dot = 20,
        Cross = 21,
        Sqrt = 22,
        Length = 23,
        Saturate = 24,
        Abs = 25,
        Floor = 26,
        Ceil = 27,
        Round = 28,
        Trunc = 29,
        Sign = 30,
        Frac = 31,
        Fractional = 32,
        Log2 = 33,
        Log10 = 34,
        ComponentSwizzle = 35,
        AppendVector = 36,
        TextureSize = 37,
        TexelSize = 38,
        ExternalTextureCoordinateScaleRotation = 39,
        ExternalTextureCoordinateOffset = 40,
        RuntimeVirtualTextureUniform = 41
    }

    /// <summary>
    /// UE 5.0–5.2 <c>UE::Shader::EPreshaderOpcode</c> (<c>Engine/Source/Runtime/Engine/Public/Shader/Preshader.h:19-76</c>).
    /// </summary>
    private enum EPreshaderOpcodeUe52 : byte
    {
        Nop = 0,
        ConstantZero = 1,
        Constant = 2,
        Parameter = 3,
        Add = 4,
        Sub = 5,
        Mul = 6,
        Div = 7,
        Fmod = 8,
        Min = 9,
        Max = 10,
        Clamp = 11,
        Sin = 12,
        Cos = 13,
        Tan = 14,
        Asin = 15,
        Acos = 16,
        Atan = 17,
        Atan2 = 18,
        Dot = 19,
        Cross = 20,
        Sqrt = 21,
        Rcp = 22,
        Length = 23,
        Normalize = 24,
        Saturate = 25,
        Abs = 26,
        Floor = 27,
        Ceil = 28,
        Round = 29,
        Trunc = 30,
        Sign = 31,
        Frac = 32,
        Fractional = 33,
        Log2 = 34,
        Log10 = 35,
        ComponentSwizzle = 36,
        AppendVector = 37,
        TextureSize = 38,
        TexelSize = 39,
        ExternalTextureCoordinateScaleRotation = 40,
        ExternalTextureCoordinateOffset = 41,
        RuntimeVirtualTextureUniform = 42,
        SparseVolumeTextureUniform = 43,
        GetField = 44,
        SetField = 45,
        Neg = 46,
        Jump = 47,
        JumpIfFalse = 48,
        PushValue = 49,
        Less = 50,
        Assign = 51,
        Greater = 52,
        LessEqual = 53,
        GreaterEqual = 54
    }

    private const byte MCT_Float1 = 2;
    private const byte MCT_Float2 = 4;
    private const byte MCT_Float3 = 8;
    private const byte MCT_Float4 = 16;
    private const byte MCT_Float = MCT_Float4 | MCT_Float3 | MCT_Float2 | MCT_Float1;
    private const float Delta = 0.00001f;
    private const float LwcTileSize = 2097152.0f;

    public static Result Build(
        FUniformExpressionSet set,
        Func<int, float> scalarParameter,
        Func<int, Vector4> vectorParameter,
        Func<int, Vector2>? textureSize = null,
        Func<string?, EMaterialParameterAssociation, int, float?, float>? scalarByInfo = null,
        Func<string?, EMaterialParameterAssociation, int, Vector4?, Vector4>? vectorByInfo = null,
        Func<string?, EMaterialParameterAssociation, int, int, Vector3>? textureSizeByInfo = null)
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
        var preshaderBaseOffset = ((set.VTStacks?.Length ?? 0) * 2 + virtualTexCount) * 16;

        var layoutSize = (int) (set.UniformBufferLayoutInitializer?.ConstantBufferSize ?? 0);
        var minRequiredSize = preshaderBaseOffset + (int) set.UniformPreshaderBufferSize * 16 +
                              set.UniformVectorPreshaders.Length * 16 + set.UniformScalarPreshaders.Length * 4;
        var size = Math.Max(layoutSize, Math.Max(minRequiredSize, 64));
        size = (size + 15) & ~15;

        var constants = new byte[size];
        var evaluated = new List<(int, Vector4, string)>();
        var usesRvt = false;

        // Fill VTPageTableUniform (2 * uint4 per VTStack) and VTPackedUniform (1 * uint4 per VirtualTexture)
        // at the beginning of the uniform buffer, matching FUniformExpressionSet::FillUniformBuffer in UE 4.27 & UE 5.2.
        WriteVirtualTextureHeaderDefaults(constants, set.VTStacks?.Length ?? 0, virtualTexCount);

        var code = set.UniformPreshaderData?.Data ?? [];
        var names = set.UniformPreshaderData?.Names ?? [];

        float ResolveScalarByInfo(string? name, EMaterialParameterAssociation assoc, int paramIndex)
        {
            float? defaultVal = null;
            for (var i = 0; i < set.UniformScalarParameters.Length; i++)
            {
                var p = set.UniformScalarParameters[i];
                var pName = p.ParameterInfo?.Name.Text ?? p.ParameterName;
                var pAssoc = p.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
                var pIdx = p.ParameterInfo?.Index ?? -1;
                if (string.Equals(pName, name, StringComparison.OrdinalIgnoreCase) && pAssoc == assoc && pIdx == paramIndex)
                    return scalarParameter(i);
                if (defaultVal is null && string.Equals(pName, name, StringComparison.OrdinalIgnoreCase))
                    defaultVal = p.DefaultValue;
            }
            for (var i = 0; i < set.UniformNumericParameters.Length; i++)
            {
                var p = set.UniformNumericParameters[i];
                if (p.ParameterType != EMaterialParameterType.Scalar)
                    continue;
                var pName = p.ParameterInfo?.Name.Text;
                var pAssoc = p.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
                var pIdx = p.ParameterInfo?.Index ?? -1;
                if (string.Equals(pName, name, StringComparison.OrdinalIgnoreCase) && pAssoc == assoc && pIdx == paramIndex)
                    return scalarParameter(i);
                if (defaultVal is null && string.Equals(pName, name, StringComparison.OrdinalIgnoreCase) && p.Value is float f)
                    defaultVal = f;
            }
            if (scalarByInfo is not null)
                return scalarByInfo(name, assoc, paramIndex, defaultVal);
            return defaultVal ?? 0f;
        }

        Vector4 ResolveVectorByInfo(string? name, EMaterialParameterAssociation assoc, int paramIndex)
        {
            Vector4? defaultVal = null;
            for (var i = 0; i < set.UniformVectorParameters.Length; i++)
            {
                var p = set.UniformVectorParameters[i];
                var pName = p.ParameterInfo?.Name.Text ?? p.ParameterName;
                var pAssoc = p.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
                var pIdx = p.ParameterInfo?.Index ?? -1;
                if (string.Equals(pName, name, StringComparison.OrdinalIgnoreCase) && pAssoc == assoc && pIdx == paramIndex)
                    return vectorParameter(i);
                if (defaultVal is null && string.Equals(pName, name, StringComparison.OrdinalIgnoreCase))
                {
                    var c = p.DefaultValue;
                    defaultVal = new Vector4(c.R, c.G, c.B, c.A);
                }
            }
            for (var i = 0; i < set.UniformNumericParameters.Length; i++)
            {
                var p = set.UniformNumericParameters[i];
                if (p.ParameterType is not (EMaterialParameterType.Vector or EMaterialParameterType.DoubleVector))
                    continue;
                var pName = p.ParameterInfo?.Name.Text;
                var pAssoc = p.ParameterInfo?.Association ?? EMaterialParameterAssociation.GlobalParameter;
                var pIdx = p.ParameterInfo?.Index ?? -1;
                if (string.Equals(pName, name, StringComparison.OrdinalIgnoreCase) && pAssoc == assoc && pIdx == paramIndex)
                    return vectorParameter(i);
                if (defaultVal is null && string.Equals(pName, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (p.Value is FLinearColor lc)
                        defaultVal = new Vector4(lc.R, lc.G, lc.B, lc.A);
                    else if (p.Value is FVector4 v4)
                        defaultVal = new Vector4(v4.X, v4.Y, v4.Z, v4.W);
                }
            }
            if (vectorByInfo is not null)
                return vectorByInfo(name, assoc, paramIndex, defaultVal);
            return defaultVal ?? Vector4.Zero;
        }

        Vector3 ResolveTextureSize(string? name, EMaterialParameterAssociation assoc, int paramIndex, int texIndex)
        {
            if (textureSizeByInfo is not null)
                return textureSizeByInfo(name, assoc, paramIndex, texIndex);
            var sz2 = textureSize?.Invoke(texIndex) ?? new Vector2(1024f, 1024f);
            return new Vector3(sz2.X, sz2.Y, 0f);
        }

        var isUe5Preshader = set.UniformPreshaders.Length > 0;
        if (!isUe5Preshader)
        {
            // UE 4.25–4.27 path: UniformVectorPreshaders followed by UniformScalarPreshaders
            var useIndexParams = DetectUe427IndexedParameterEncoding(set, code);
            for (var i = 0; i < set.UniformVectorPreshaders.Length; i++)
            {
                var h = set.UniformVectorPreshaders[i];
                var v = EvaluateUe427(
                    code,
                    names,
                    (int) h.OpcodeOffset,
                    (int) h.OpcodeSize,
                    useIndexParams,
                    scalarParameter,
                    vectorParameter,
                    ResolveScalarByInfo,
                    ResolveVectorByInfo,
                    ResolveTextureSize,
                    out var trace,
                    out var rvt);
                if (rvt)
                    usesRvt = true;

                var dst = preshaderBaseOffset + i * 16;
                WriteVector4(constants, dst, v);
                evaluated.Add((dst, v, $"vector[{i}] (offset={h.OpcodeOffset}, size={h.OpcodeSize}): {trace}"));
            }

            var scalarBase = preshaderBaseOffset + set.UniformVectorPreshaders.Length * 16;
            for (var i = 0; i < set.UniformScalarPreshaders.Length; i++)
            {
                var h = set.UniformScalarPreshaders[i];
                var v = EvaluateUe427(
                    code,
                    names,
                    (int) h.OpcodeOffset,
                    (int) h.OpcodeSize,
                    useIndexParams,
                    scalarParameter,
                    vectorParameter,
                    ResolveScalarByInfo,
                    ResolveVectorByInfo,
                    ResolveTextureSize,
                    out var trace,
                    out var rvt);
                if (rvt)
                    usesRvt = true;

                var dst = scalarBase + i * 4;
                WriteFloat(constants, dst, v.X);
                evaluated.Add((dst, new Vector4(v.X, 0f, 0f, 0f), $"scalar[{i}] (offset={h.OpcodeOffset}, size={h.OpcodeSize}): {trace}"));
            }
        }
        else
        {
            // UE 5.0–5.2+ path: UniformPreshaders with typed FPreshaderValue stack and field descriptors
            var fields = set.UniformPreshaderFields ?? [];
            for (var i = 0; i < set.UniformPreshaders.Length; i++)
            {
                var h = set.UniformPreshaders[i];
                var resultVal = EvaluateUe52(
                    set,
                    code,
                    names,
                    (int) h.OpcodeOffset,
                    (int) h.OpcodeSize,
                    scalarParameter,
                    vectorParameter,
                    ResolveTextureSize,
                    out var trace,
                    out var rvt);
                if (rvt)
                    usesRvt = true;

                switch (h)
                {
                    case FMaterialUniformPreshaderHeader_5_0 h50:
                    {
                        var dst = preshaderBaseOffset + (int) h50.BufferOffset * 4;
                        var count = Math.Clamp((int) (h50.NumComponents ?? 4), 1, 4);
                        WriteComponentsUe5(constants, dst, resultVal, 0, count, EShaderValueType.Float4);
                        var v4 = resultVal.ToVector4(0);
                        evaluated.Add((dst, v4, $"preshader50[{i}] +{dst} ({count}c): {trace}"));
                        break;
                    }
                    case FMaterialUniformPreshaderHeader_5_1 h51:
                    {
                        for (var f = 0; f < h51.NumFields; f++)
                        {
                            var fieldIdx = (int) h51.FieldIndex + f;
                            if (fieldIdx < 0 || fieldIdx >= fields.Length)
                                break;
                            var field = fields[fieldIdx];
                            var dst = preshaderBaseOffset + (int) field.BufferOffset * 4;
                            var compIdx = (int) field.ComponentIndex;
                            var count = GetShaderValueTypeComponentCount(field.Type);
                            WriteComponentsUe5(constants, dst, resultVal, compIdx, count, field.Type);
                            var v4 = resultVal.ToVector4(compIdx);
                            evaluated.Add((dst, v4, $"preshader51[{i}].field[{f}] +{dst} ({field.Type}): {trace}"));
                        }
                        break;
                    }
                    case FMaterialUniformPreshaderHeader_5_8 h58:
                    {
                        var dst = preshaderBaseOffset + h58.BufferOffset * 4;
                        var count = GetShaderValueTypeComponentCount(h58.Type);
                        WriteComponentsUe5(constants, dst, resultVal, 0, count, h58.Type);
                        var v4 = resultVal.ToVector4(0);
                        evaluated.Add((dst, v4, $"preshader58[{i}] +{dst} ({h58.Type}): {trace}"));
                        break;
                    }
                }
            }

            // UE 5.6+ direct UniformParameterEvaluations
            foreach (var eval in set.UniformParameterEvaluations ?? [])
            {
                if (eval.ParameterIndex < set.UniformNumericParameters.Length)
                {
                    var param = set.UniformNumericParameters[eval.ParameterIndex];
                    var dst = preshaderBaseOffset + eval.BufferOffset * 4;
                    var v4 = vectorParameter(eval.ParameterIndex);
                    var count = param.ParameterType is EMaterialParameterType.Scalar or EMaterialParameterType.StaticSwitch ? 1 : 4;
                    for (var c = 0; c < count; c++)
                        WriteFloat(constants, dst + c * 4, v4[c]);
                }
            }
        }

        return new Result(constants, evaluated, usesRvt);
    }

    private static Vector4 GetDefaultRvtUniform(int vecIdx) => vecIdx switch
    {
        // URuntimeVirtualTexture::GetUniformParameter (RuntimeVirtualTexture.cpp:487-512):
        // vec 0 = WorldToUVTransformParameters[0] (VolumeToWorld.Translation = Origin)
        // vec 1 = WorldToUVTransformParameters[1] (UnitX / ScaleX)
        // vec 2 = WorldToUVTransformParameters[2] (UnitY / ScaleY)
        // vec 3 = WorldHeightUnpackParameter (HeightRange, MinZ, 0, 0)
        0 => new Vector4(-130048f, -130048f, -10000f, 0f),
        1 => new Vector4(1f / 262144f, 0f, 0f, 0f),
        2 => new Vector4(0f, 1f / 262144f, 0f, 0f),
        3 => new Vector4(20000f, -10000f, 0f, 0f),
        _ => Vector4.Zero
    };

    private static void WriteVirtualTextureHeaderDefaults(byte[] constants, int vtStackCount, int virtualTexCount)
    {
        // Exact FAllocatedVirtualTexture::GetPackedPageTableUniform / GetPackedUniform layout
        // (UE 4.27/5.2 AllocatedVirtualTexture.cpp:321-378) for a 1:1 unpacked fallback texture.
        var cursor = 0;
        for (var i = 0; i < vtStackCount; i++)
        {
            WriteFloat(constants, cursor + 0, 1.0f);  // 1.0f / WidthInBlocks
            WriteFloat(constants, cursor + 4, 1.0f);  // 1.0f / HeightInBlocks
            WriteFloat(constants, cursor + 8, 1.0f);  // WidthInPages
            WriteFloat(constants, cursor + 12, 1.0f); // HeightInPages
            WriteFloat(constants, cursor + 16, 0.0f); // MaxAnisotropyLog2
            WriteUInt32(constants, cursor + 20, 0u);  // vPageX | (vPageY << 12) | (MipBias << 24)
            WriteUInt32(constants, cursor + 24, 0u);  // MaxLevel | (AdaptiveLevelBias << 4)
            WriteUInt32(constants, cursor + 28, 0u);  // SpaceID << 28
            cursor += 32;
        }

        for (var i = 0; i < virtualTexCount; i++)
        {
            WriteUInt32(constants, cursor + 0, 0u);   // PageTableFormat == UInt32
            WriteFloat(constants, cursor + 4, 1.0f);  // vPageSize / PhysicalTextureSize
            WriteFloat(constants, cursor + 8, 0.0f);  // PageBorderSize / PhysicalTextureSize
            WriteFloat(constants, cursor + 12, 1.0f); // pPageSize / PhysicalTextureSize
            cursor += 16;
        }
    }

    /// <summary>
    /// In UE 4.25/4.26, <c>ScalarParameter</c> and <c>VectorParameter</c> encode a 7-byte <c>FHashedMaterialParameterInfo</c> in preshader bytecode.
    /// In UE 4.27 (<c>MaterialUniformExpressions.cpp:968,972</c>), they encode a 2-byte <c>uint16</c> parameter index.
    /// This checks which encoding matches the exact <c>OpcodeSize</c> of all preshader headers.
    /// </summary>
    private static bool DetectUe427IndexedParameterEncoding(FUniformExpressionSet set, ReadOnlySpan<byte> code)
    {
        var headers = set.UniformVectorPreshaders.Concat(set.UniformScalarPreshaders).ToList();
        if (headers.Count == 0)
            return true;

        var valid2Byte = true;
        var valid7Byte = true;

        foreach (var h in headers)
        {
            var start = (int) h.OpcodeOffset;
            var end = start + (int) h.OpcodeSize;
            if (start < 0 || end > code.Length)
                continue;

            if (valid2Byte && !CanWalkUe427Preshader(code, start, end, paramSize: 2, set.UniformScalarParameters.Length, set.UniformVectorParameters.Length))
                valid2Byte = false;
            if (valid7Byte && !CanWalkUe427Preshader(code, start, end, paramSize: 7, set.UniformScalarParameters.Length, set.UniformVectorParameters.Length))
                valid7Byte = false;
        }

        if (valid2Byte && !valid7Byte)
            return true;
        if (valid7Byte && !valid2Byte)
            return false;
        return false;
    }

    private static bool CanWalkUe427Preshader(ReadOnlySpan<byte> code, int start, int end, int paramSize, int scalarCount, int vectorCount)
    {
        var ptr = start;
        var stackDepth = 0;
        while (ptr < end)
        {
            var op = (EMaterialPreshaderOpcodeUe427) code[ptr++];
            switch (op)
            {
                case EMaterialPreshaderOpcodeUe427.Nop:
                    break;
                case EMaterialPreshaderOpcodeUe427.ConstantZero:
                    stackDepth++;
                    break;
                case EMaterialPreshaderOpcodeUe427.Constant:
                    ptr += 16;
                    stackDepth++;
                    break;
                case EMaterialPreshaderOpcodeUe427.ScalarParameter:
                    if (paramSize == 2 && ptr + 2 <= end)
                    {
                        var idx = BinaryPrimitives.ReadUInt16LittleEndian(code[ptr..]);
                        if (idx >= scalarCount)
                            return false;
                    }
                    ptr += paramSize;
                    stackDepth++;
                    break;
                case EMaterialPreshaderOpcodeUe427.VectorParameter:
                    if (paramSize == 2 && ptr + 2 <= end)
                    {
                        var idx = BinaryPrimitives.ReadUInt16LittleEndian(code[ptr..]);
                        if (idx >= vectorCount)
                            return false;
                    }
                    ptr += paramSize;
                    stackDepth++;
                    break;
                case EMaterialPreshaderOpcodeUe427.Add:
                case EMaterialPreshaderOpcodeUe427.Sub:
                case EMaterialPreshaderOpcodeUe427.Mul:
                case EMaterialPreshaderOpcodeUe427.Div:
                case EMaterialPreshaderOpcodeUe427.Fmod:
                case EMaterialPreshaderOpcodeUe427.Min:
                case EMaterialPreshaderOpcodeUe427.Max:
                case EMaterialPreshaderOpcodeUe427.Atan2:
                    if (stackDepth < 2) return false;
                    stackDepth--;
                    break;
                case EMaterialPreshaderOpcodeUe427.Dot:
                case EMaterialPreshaderOpcodeUe427.Cross:
                    ptr += 1; // uint8 ValueType
                    if (stackDepth < 2) return false;
                    stackDepth--;
                    break;
                case EMaterialPreshaderOpcodeUe427.Clamp:
                    if (stackDepth < 3) return false;
                    stackDepth -= 2;
                    break;
                case EMaterialPreshaderOpcodeUe427.Sin:
                case EMaterialPreshaderOpcodeUe427.Cos:
                case EMaterialPreshaderOpcodeUe427.Tan:
                case EMaterialPreshaderOpcodeUe427.Asin:
                case EMaterialPreshaderOpcodeUe427.Acos:
                case EMaterialPreshaderOpcodeUe427.Atan:
                case EMaterialPreshaderOpcodeUe427.Sqrt:
                case EMaterialPreshaderOpcodeUe427.Length:
                case EMaterialPreshaderOpcodeUe427.Saturate:
                case EMaterialPreshaderOpcodeUe427.Abs:
                case EMaterialPreshaderOpcodeUe427.Floor:
                case EMaterialPreshaderOpcodeUe427.Ceil:
                case EMaterialPreshaderOpcodeUe427.Round:
                case EMaterialPreshaderOpcodeUe427.Trunc:
                case EMaterialPreshaderOpcodeUe427.Sign:
                case EMaterialPreshaderOpcodeUe427.Frac:
                case EMaterialPreshaderOpcodeUe427.Fractional:
                case EMaterialPreshaderOpcodeUe427.Log2:
                case EMaterialPreshaderOpcodeUe427.Log10:
                    if (stackDepth < 1) return false;
                    break;
                case EMaterialPreshaderOpcodeUe427.ComponentSwizzle:
                    ptr += 5;
                    if (stackDepth < 1) return false;
                    break;
                case EMaterialPreshaderOpcodeUe427.AppendVector:
                    ptr += 1;
                    if (stackDepth < 2) return false;
                    stackDepth--;
                    break;
                case EMaterialPreshaderOpcodeUe427.TextureSize:
                case EMaterialPreshaderOpcodeUe427.TexelSize:
                    ptr += 11;
                    stackDepth++;
                    break;
                case EMaterialPreshaderOpcodeUe427.ExternalTextureCoordinateScaleRotation:
                case EMaterialPreshaderOpcodeUe427.ExternalTextureCoordinateOffset:
                    ptr += 22;
                    stackDepth++;
                    break;
                case EMaterialPreshaderOpcodeUe427.RuntimeVirtualTextureUniform:
                    ptr += 15;
                    stackDepth++;
                    break;
                default:
                    return false;
            }
            if (ptr > end)
                return false;
        }
        return ptr == end && stackDepth == 1;
    }

    private static float GetSafeDivisor(float number)
    {
        if (MathF.Abs(number) < Delta)
            return number < 0f ? -Delta : +Delta;
        return number;
    }

    private static float DivideComponent(float a, float b) => a / GetSafeDivisor(b);

    private static float FracUe(float x) => x - MathF.Floor(x);
    private static float FractionalUe(float x) => x - MathF.Truncate(x);
    private static float FmodUe(float a, float b) => MathF.Abs(b) > 1e-8f ? a - MathF.Truncate(a / b) * b : 0f;

    /// <summary>
    /// 1:1 implementation of <c>EvaluatePreshader</c> from UE 4.27 <c>MaterialUniformExpressions.cpp:949-1022</c>.
    /// Uses an <c>FLinearColor</c> (<c>Vector4</c>) stack with no component-count truncation on constants or arithmetic ops.
    /// </summary>
    private static Vector4 EvaluateUe427(
        ReadOnlySpan<byte> data,
        CUE4Parse.UE4.Objects.UObject.FName[] names,
        int offset,
        int size,
        bool useIndexParams,
        Func<int, float> scalarParamByIndex,
        Func<int, Vector4> vectorParamByIndex,
        Func<string?, EMaterialParameterAssociation, int, float> scalarParamByInfo,
        Func<string?, EMaterialParameterAssociation, int, Vector4> vectorParamByInfo,
        Func<string?, EMaterialParameterAssociation, int, int, Vector3> resolveTextureSize,
        out string trace,
        out bool usesRvt)
    {
        usesRvt = false;
        var end = Math.Min(offset + size, data.Length);
        if (offset < 0 || offset >= end)
        {
            trace = "empty";
            return Vector4.Zero;
        }

        var stack = new Stack<Vector4>(16);
        var ops = new List<string>();
        var ptr = offset;

        Vector4 Pop() => stack.Count > 0 ? stack.Pop() : Vector4.Zero;

        void Unary(EMaterialPreshaderOpcodeUe427 op, Func<float, float> fn)
        {
            ops.Add(op.ToString());
            var v = Pop();
            stack.Push(new Vector4(fn(v.X), fn(v.Y), fn(v.Z), fn(v.W)));
        }

        void Binary(EMaterialPreshaderOpcodeUe427 op, Func<float, float, float> fn)
        {
            ops.Add(op.ToString());
            var b = Pop();
            var a = Pop();
            stack.Push(new Vector4(fn(a.X, b.X), fn(a.Y, b.Y), fn(a.Z, b.Z), fn(a.W, b.W)));
        }

        while (ptr < end)
        {
            var op = (EMaterialPreshaderOpcodeUe427) data[ptr++];
            switch (op)
            {
                case EMaterialPreshaderOpcodeUe427.Nop:
                    ops.Add("Nop");
                    break;
                case EMaterialPreshaderOpcodeUe427.ConstantZero:
                    ops.Add("Zero");
                    stack.Push(Vector4.Zero);
                    break;
                case EMaterialPreshaderOpcodeUe427.Constant:
                {
                    if (ptr + 16 > end) { ptr = end; break; }
                    var v = MemoryMarshal.Read<Vector4>(data[ptr..]);
                    ptr += 16;
                    ops.Add($"Const({v.X:0.###},{v.Y:0.###},{v.Z:0.###},{v.W:0.###})");
                    stack.Push(v);
                    break;
                }
                case EMaterialPreshaderOpcodeUe427.ScalarParameter:
                {
                    if (useIndexParams)
                    {
                        if (ptr + 2 > end) { ptr = end; break; }
                        var paramIdx = BinaryPrimitives.ReadUInt16LittleEndian(data[ptr..]);
                        ptr += 2;
                        var s = scalarParamByIndex(paramIdx);
                        ops.Add($"Scalar[{paramIdx}]={s:0.###}");
                        stack.Push(new Vector4(s, s, s, s));
                    }
                    else
                    {
                        if (ptr + 7 > end) { ptr = end; break; }
                        var (pName, pAssoc, pIndex) = ReadHashedParameterInfo(data, ref ptr, names);
                        var s = scalarParamByInfo(pName, pAssoc, pIndex);
                        ops.Add($"Scalar({pName},{pAssoc},{pIndex})={s:0.###}");
                        stack.Push(new Vector4(s, s, s, s));
                    }
                    break;
                }
                case EMaterialPreshaderOpcodeUe427.VectorParameter:
                {
                    if (useIndexParams)
                    {
                        if (ptr + 2 > end) { ptr = end; break; }
                        var paramIdx = BinaryPrimitives.ReadUInt16LittleEndian(data[ptr..]);
                        ptr += 2;
                        var v = vectorParamByIndex(paramIdx);
                        ops.Add($"Vec[{paramIdx}]=({v.X:0.###},{v.Y:0.###},{v.Z:0.###},{v.W:0.###})");
                        stack.Push(v);
                    }
                    else
                    {
                        if (ptr + 7 > end) { ptr = end; break; }
                        var (pName, pAssoc, pIndex) = ReadHashedParameterInfo(data, ref ptr, names);
                        var v = vectorParamByInfo(pName, pAssoc, pIndex);
                        ops.Add($"Vec({pName},{pAssoc},{pIndex})=({v.X:0.###},{v.Y:0.###},{v.Z:0.###},{v.W:0.###})");
                        stack.Push(v);
                    }
                    break;
                }
                case EMaterialPreshaderOpcodeUe427.Add: Binary(op, (a, b) => a + b); break;
                case EMaterialPreshaderOpcodeUe427.Sub: Binary(op, (a, b) => a - b); break;
                case EMaterialPreshaderOpcodeUe427.Mul: Binary(op, (a, b) => a * b); break;
                case EMaterialPreshaderOpcodeUe427.Div: Binary(op, DivideComponent); break;
                case EMaterialPreshaderOpcodeUe427.Fmod: Binary(op, FmodUe); break;
                case EMaterialPreshaderOpcodeUe427.Min: Binary(op, MathF.Min); break;
                case EMaterialPreshaderOpcodeUe427.Max: Binary(op, MathF.Max); break;
                case EMaterialPreshaderOpcodeUe427.Clamp:
                {
                    ops.Add("Clamp");
                    var hi = Pop();
                    var lo = Pop();
                    var v = Pop();
                    stack.Push(Vector4.Clamp(v, lo, hi));
                    break;
                }
                case EMaterialPreshaderOpcodeUe427.Dot:
                {
                    if (ptr + 1 > end) { ptr = end; break; }
                    var valueType = data[ptr++];
                    ops.Add($"Dot({valueType})");
                    var b = Pop();
                    var a = Pop();
                    var dot = a.X * b.X;
                    if (valueType >= MCT_Float2) dot += a.Y * b.Y;
                    if (valueType >= MCT_Float3) dot += a.Z * b.Z;
                    if (valueType >= MCT_Float4) dot += a.W * b.W;
                    stack.Push(new Vector4(dot, dot, dot, dot));
                    break;
                }
                case EMaterialPreshaderOpcodeUe427.Cross:
                {
                    if (ptr + 1 > end) { ptr = end; break; }
                    var valueType = data[ptr++];
                    ops.Add($"Cross({valueType})");
                    var b = Pop();
                    var a = Pop();
                    switch (valueType)
                    {
                        case MCT_Float:
                            a.Z = a.Y = a.X;
                            b.Z = b.Y = b.X;
                            break;
                        case MCT_Float1:
                            a.Z = a.Y = 0f;
                            b.Z = b.Y = 0f;
                            break;
                        case MCT_Float2:
                            a.Z = 0f;
                            b.Z = 0f;
                            break;
                    }
                    var c = Vector3.Cross(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z));
                    stack.Push(new Vector4(c.X, c.Y, c.Z, 0f));
                    break;
                }
                case EMaterialPreshaderOpcodeUe427.Sqrt: Unary(op, x => x > 0f ? MathF.Sqrt(x) : 0f); break;
                case EMaterialPreshaderOpcodeUe427.Sin: Unary(op, MathF.Sin); break;
                case EMaterialPreshaderOpcodeUe427.Cos: Unary(op, MathF.Cos); break;
                case EMaterialPreshaderOpcodeUe427.Tan: Unary(op, MathF.Tan); break;
                case EMaterialPreshaderOpcodeUe427.Asin: Unary(op, x => MathF.Asin(Math.Clamp(x, -1f, 1f))); break;
                case EMaterialPreshaderOpcodeUe427.Acos: Unary(op, x => MathF.Acos(Math.Clamp(x, -1f, 1f))); break;
                case EMaterialPreshaderOpcodeUe427.Atan: Unary(op, MathF.Atan); break;
                case EMaterialPreshaderOpcodeUe427.Atan2: Binary(op, MathF.Atan2); break;
                case EMaterialPreshaderOpcodeUe427.Length:
                {
                    ops.Add("Length");
                    var v = Pop();
                    var len = v.Length();
                    stack.Push(new Vector4(len, len, len, len));
                    break;
                }
                case EMaterialPreshaderOpcodeUe427.Saturate: Unary(op, x => Math.Clamp(x, 0f, 1f)); break;
                case EMaterialPreshaderOpcodeUe427.Abs: Unary(op, MathF.Abs); break;
                case EMaterialPreshaderOpcodeUe427.Floor: Unary(op, MathF.Floor); break;
                case EMaterialPreshaderOpcodeUe427.Ceil: Unary(op, MathF.Ceiling); break;
                case EMaterialPreshaderOpcodeUe427.Round: Unary(op, MathF.Round); break;
                case EMaterialPreshaderOpcodeUe427.Trunc: Unary(op, MathF.Truncate); break;
                case EMaterialPreshaderOpcodeUe427.Sign: Unary(op, x => MathF.Sign(x)); break;
                case EMaterialPreshaderOpcodeUe427.Frac: Unary(op, FracUe); break;
                case EMaterialPreshaderOpcodeUe427.Fractional: Unary(op, FractionalUe); break;
                case EMaterialPreshaderOpcodeUe427.Log2: Unary(op, x => x > 0f ? MathF.Log2(x) : 0f); break;
                case EMaterialPreshaderOpcodeUe427.Log10: Unary(op, x => x > 0f ? MathF.Log10(x) : 0f); break;
                case EMaterialPreshaderOpcodeUe427.ComponentSwizzle:
                {
                    if (ptr + 5 > end) { ptr = end; break; }
                    var numElements = data[ptr];
                    int r = data[ptr + 1], g = data[ptr + 2], b = data[ptr + 3], a = data[ptr + 4];
                    ptr += 5;
                    ops.Add($"Swizzle(n={numElements},{r},{g},{b},{a})");
                    var v = Pop();
                    var result = Vector4.Zero;
                    switch (numElements)
                    {
                        case 1:
                        {
                            var c = GetComp(v, r);
                            result = new Vector4(c, c, c, c);
                            break;
                        }
                        case 4:
                            result.W = GetComp(v, a);
                            goto case 3;
                        case 3:
                            result.Z = GetComp(v, b);
                            goto case 2;
                        case 2:
                            result.Y = GetComp(v, g);
                            result.X = GetComp(v, r);
                            break;
                    }
                    stack.Push(result);
                    break;
                }
                case EMaterialPreshaderOpcodeUe427.AppendVector:
                {
                    if (ptr + 1 > end) { ptr = end; break; }
                    int numComponentsA = data[ptr++];
                    ops.Add($"Append({numComponentsA})");
                    var b = Pop();
                    var a = Pop();
                    var result = new Vector4(
                        numComponentsA >= 1 ? a.X : GetComp(b, 0 - numComponentsA),
                        numComponentsA >= 2 ? a.Y : GetComp(b, 1 - numComponentsA),
                        numComponentsA >= 3 ? a.Z : GetComp(b, 2 - numComponentsA),
                        numComponentsA >= 4 ? a.W : GetComp(b, 3 - numComponentsA));
                    stack.Push(result);
                    break;
                }
                case EMaterialPreshaderOpcodeUe427.TextureSize:
                case EMaterialPreshaderOpcodeUe427.TexelSize:
                {
                    if (ptr + 11 > end) { ptr = end; break; }
                    var (pName, pAssoc, pIndex) = ReadHashedParameterInfo(data, ref ptr, names);
                    var texIdx = BinaryPrimitives.ReadInt32LittleEndian(data[ptr..]);
                    ptr += 4;
                    var sz = resolveTextureSize(pName, pAssoc, pIndex, texIdx);
                    ops.Add($"{op}({pName ?? $"tex[{texIdx}]"})=({sz.X},{sz.Y},{sz.Z})");
                    stack.Push(op == EMaterialPreshaderOpcodeUe427.TexelSize
                        ? new Vector4(
                            sz.X > 0f ? 1f / sz.X : 0f,
                            sz.Y > 0f ? 1f / sz.Y : 0f,
                            sz.Z > 0f ? 1f / sz.Z : 0f,
                            0f)
                        : new Vector4(sz.X, sz.Y, sz.Z, 0f));
                    break;
                }
                case EMaterialPreshaderOpcodeUe427.ExternalTextureCoordinateScaleRotation:
                {
                    if (ptr + 22 > end) { ptr = end; break; }
                    ptr += 22; // FScriptName (2B) + FGuid (16B) + int32 TextureIndex (4B)
                    ops.Add("ExtTexScaleRot");
                    stack.Push(new Vector4(1f, 0f, 0f, 1f));
                    break;
                }
                case EMaterialPreshaderOpcodeUe427.ExternalTextureCoordinateOffset:
                {
                    if (ptr + 22 > end) { ptr = end; break; }
                    ptr += 22; // FScriptName (2B) + FGuid (16B) + int32 TextureIndex (4B)
                    ops.Add("ExtTexOffset");
                    stack.Push(Vector4.Zero);
                    break;
                }
                case EMaterialPreshaderOpcodeUe427.RuntimeVirtualTextureUniform:
                {
                    if (ptr + 15 > end) { ptr = end; break; }
                    _ = ReadHashedParameterInfo(data, ref ptr, names);
                    _ = BinaryPrimitives.ReadInt32LittleEndian(data[ptr..]); // TextureIndex
                    var vecIdx = BinaryPrimitives.ReadInt32LittleEndian(data[(ptr + 4)..]); // VectorIndex
                    ptr += 8;
                    usesRvt = true;
                    ops.Add($"RVTUniform(vec={vecIdx})");
                    stack.Push(GetDefaultRvtUniform(vecIdx));
                    break;
                }
                default:
                    ops.Add($"UnknownOp({(byte) op})");
                    ptr = end;
                    break;
            }
        }

        trace = string.Join(" -> ", ops);
        return stack.Count > 0 ? stack.Pop() : Vector4.Zero;
    }

    /// <summary>
    /// Typed preshader value for UE 5.0–5.2+ (<c>FPreshaderValue</c> in <c>Preshader.h</c>).
    /// </summary>
    private readonly struct Ue5PreshaderValue
    {
        public readonly EShaderValueType ValueType;
        public readonly double[] Components;

        public Ue5PreshaderValue(EShaderValueType valueType, params double[] components)
        {
            ValueType = valueType;
            Components = components;
        }

        public static Ue5PreshaderValue Zero(EShaderValueType type, int numComponents)
        {
            return new Ue5PreshaderValue(type, new double[Math.Max(numComponents, 1)]);
        }

        public static Ue5PreshaderValue FromFloat(float v) => new(EShaderValueType.Float1, v);
        public static Ue5PreshaderValue FromVector3(Vector3 v) => new(EShaderValueType.Float3, v.X, v.Y, v.Z);
        public static Ue5PreshaderValue FromVector4(Vector4 v, int numComponents = 4)
        {
            return numComponents switch
            {
                1 => new Ue5PreshaderValue(EShaderValueType.Float1, v.X),
                2 => new Ue5PreshaderValue(EShaderValueType.Float2, v.X, v.Y),
                3 => new Ue5PreshaderValue(EShaderValueType.Float3, v.X, v.Y, v.Z),
                _ => new Ue5PreshaderValue(EShaderValueType.Float4, v.X, v.Y, v.Z, v.W)
            };
        }

        public int Count => Components?.Length ?? 0;

        public double Get(int index)
        {
            if (Components is null || Components.Length == 0)
                return 0.0;
            if (index >= 0 && index < Components.Length)
                return Components[index];
            return 0.0;
        }

        public float GetFloat(int index) => (float) Get(index);

        public bool AsBoolScalar()
        {
            if (Components is null)
                return false;
            for (var i = 0; i < Components.Length; i++)
            {
                if (Math.Abs(Components[i]) > 1e-9)
                    return true;
            }
            return false;
        }

        public Vector4 ToVector4(int startIndex = 0)
        {
            return new Vector4(
                GetFloat(startIndex + 0),
                GetFloat(startIndex + 1),
                GetFloat(startIndex + 2),
                GetFloat(startIndex + 3));
        }
    }

    /// <summary>
    /// 1:1 implementation of <c>UE::Shader::EvaluatePreshader</c> from UE 5.2 <c>Preshader.cpp:853-948</c>.
    /// </summary>
    private static Ue5PreshaderValue EvaluateUe52(
        FUniformExpressionSet set,
        ReadOnlySpan<byte> data,
        CUE4Parse.UE4.Objects.UObject.FName[] names,
        int offset,
        int size,
        Func<int, float> scalarParamByIndex,
        Func<int, Vector4> vectorParamByIndex,
        Func<string?, EMaterialParameterAssociation, int, int, Vector3> resolveTextureSize,
        out string trace,
        out bool usesRvt)
    {
        usesRvt = false;
        var end = Math.Min(offset + size, data.Length);
        if (offset < 0 || offset >= end)
        {
            trace = "empty";
            return Ue5PreshaderValue.Zero(EShaderValueType.Float4, 4);
        }

        var stack = new List<Ue5PreshaderValue>(16);
        var ops = new List<string>();
        var ptr = offset;

        Ue5PreshaderValue Pop()
        {
            if (stack.Count == 0)
                return Ue5PreshaderValue.Zero(EShaderValueType.Float1, 1);
            var idx = stack.Count - 1;
            var val = stack[idx];
            stack.RemoveAt(idx);
            return val;
        }

        void Push(Ue5PreshaderValue val) => stack.Add(val);

        void Unary(EPreshaderOpcodeUe52 op, Func<double, double> fn)
        {
            ops.Add(op.ToString());
            var v = Pop();
            var n = Math.Clamp(v.Count, 1, 16);
            var res = new double[n];
            for (var i = 0; i < n; i++)
                res[i] = fn(v.Get(i));
            Push(new Ue5PreshaderValue(v.ValueType, res));
        }

        void Binary(EPreshaderOpcodeUe52 op, Func<double, double, double> fn, bool isComparison = false)
        {
            ops.Add(op.ToString());
            var b = Pop();
            var a = Pop();
            var n = Math.Max(Math.Clamp(a.Count, 1, 4), Math.Clamp(b.Count, 1, 4));
            var res = new double[n];
            for (var i = 0; i < n; i++)
            {
                var va = a.Count == 1 ? a.Get(0) : a.Get(i);
                var vb = b.Count == 1 ? b.Get(0) : b.Get(i);
                res[i] = fn(va, vb);
            }
            var outType = isComparison
                ? (EShaderValueType) ((byte) EShaderValueType.Bool1 + n - 1)
                : (a.Count >= b.Count ? a.ValueType : b.ValueType);
            Push(new Ue5PreshaderValue(outType, res));
        }

        while (ptr < end)
        {
            var op = (EPreshaderOpcodeUe52) data[ptr++];
            switch (op)
            {
                case EPreshaderOpcodeUe52.Nop:
                    ops.Add("Nop");
                    break;
                case EPreshaderOpcodeUe52.ConstantZero:
                {
                    var (valType, compCount, _) = ReadUe5PreshaderType(set, data, ref ptr, end);
                    ops.Add($"Zero({valType})");
                    Push(Ue5PreshaderValue.Zero(valType, compCount));
                    break;
                }
                case EPreshaderOpcodeUe52.Constant:
                {
                    var (valType, compCount, _) = ReadUe5PreshaderType(set, data, ref ptr, end);
                    var res = ReadUe5ConstantPayload(valType, compCount, data, ref ptr, end);
                    ops.Add($"Const({valType})");
                    Push(new Ue5PreshaderValue(valType, res));
                    break;
                }
                case EPreshaderOpcodeUe52.Parameter:
                {
                    if (ptr + 2 > end) { ptr = end; break; }
                    var paramIdx = BinaryPrimitives.ReadUInt16LittleEndian(data[ptr..]);
                    ptr += 2;
                    if (paramIdx < set.UniformNumericParameters.Length)
                    {
                        var p = set.UniformNumericParameters[paramIdx];
                        switch (p.ParameterType)
                        {
                            case EMaterialParameterType.Scalar:
                            {
                                var s = scalarParamByIndex(paramIdx);
                                ops.Add($"Param[{paramIdx}:{p.ParameterInfo?.Name.Text}]={s:0.###}");
                                Push(Ue5PreshaderValue.FromFloat(s));
                                break;
                            }
                            case EMaterialParameterType.StaticSwitch:
                            {
                                var s = scalarParamByIndex(paramIdx);
                                ops.Add($"Switch[{paramIdx}:{p.ParameterInfo?.Name.Text}]={s}");
                                Push(new Ue5PreshaderValue(EShaderValueType.Bool1, s != 0f ? 1.0 : 0.0));
                                break;
                            }
                            case EMaterialParameterType.DoubleVector:
                            {
                                var v = vectorParamByIndex(paramIdx);
                                ops.Add($"DVec[{paramIdx}:{p.ParameterInfo?.Name.Text}]");
                                Push(new Ue5PreshaderValue(EShaderValueType.Double4, v.X, v.Y, v.Z, v.W));
                                break;
                            }
                            default:
                            {
                                var v = vectorParamByIndex(paramIdx);
                                ops.Add($"Vec[{paramIdx}:{p.ParameterInfo?.Name.Text}]=({v.X:0.###},{v.Y:0.###},{v.Z:0.###},{v.W:0.###})");
                                Push(Ue5PreshaderValue.FromVector4(v, 4));
                                break;
                            }
                        }
                    }
                    else
                    {
                        Push(Ue5PreshaderValue.Zero(EShaderValueType.Float4, 4));
                    }
                    break;
                }
                case EPreshaderOpcodeUe52.Add: Binary(op, (a, b) => a + b); break;
                case EPreshaderOpcodeUe52.Sub: Binary(op, (a, b) => a - b); break;
                case EPreshaderOpcodeUe52.Mul: Binary(op, (a, b) => a * b); break;
                case EPreshaderOpcodeUe52.Div: Binary(op, (a, b) => DivideComponent((float) a, (float) b)); break;
                case EPreshaderOpcodeUe52.Fmod: Binary(op, (a, b) => FmodUe((float) a, (float) b)); break;
                case EPreshaderOpcodeUe52.Min: Binary(op, Math.Min); break;
                case EPreshaderOpcodeUe52.Max: Binary(op, Math.Max); break;
                case EPreshaderOpcodeUe52.Clamp:
                {
                    ops.Add("Clamp");
                    var hi = Pop();
                    var lo = Pop();
                    var v = Pop();
                    var n = Math.Max(v.Count, Math.Max(lo.Count, hi.Count));
                    n = Math.Clamp(n, 1, 4);
                    var res = new double[n];
                    for (var i = 0; i < n; i++)
                    {
                        var xv = v.Count == 1 ? v.Get(0) : v.Get(i);
                        var xl = lo.Count == 1 ? lo.Get(0) : lo.Get(i);
                        var xh = hi.Count == 1 ? hi.Get(0) : hi.Get(i);
                        res[i] = Math.Clamp(xv, xl, xh);
                    }
                    Push(new Ue5PreshaderValue(v.ValueType, res));
                    break;
                }
                case EPreshaderOpcodeUe52.Sin: Unary(op, Math.Sin); break;
                case EPreshaderOpcodeUe52.Cos: Unary(op, Math.Cos); break;
                case EPreshaderOpcodeUe52.Tan: Unary(op, Math.Tan); break;
                case EPreshaderOpcodeUe52.Asin: Unary(op, x => Math.Asin(Math.Clamp(x, -1.0, 1.0))); break;
                case EPreshaderOpcodeUe52.Acos: Unary(op, x => Math.Acos(Math.Clamp(x, -1.0, 1.0))); break;
                case EPreshaderOpcodeUe52.Atan: Unary(op, Math.Atan); break;
                case EPreshaderOpcodeUe52.Atan2: Binary(op, Math.Atan2); break;
                case EPreshaderOpcodeUe52.Dot:
                {
                    ops.Add("Dot");
                    var b = Pop();
                    var a = Pop();
                    var n = Math.Clamp(Math.Max(a.Count, b.Count), 1, 4);
                    var sum = 0.0;
                    for (var i = 0; i < n; i++)
                    {
                        var va = a.Count == 1 ? a.Get(0) : a.Get(i);
                        var vb = b.Count == 1 ? b.Get(0) : b.Get(i);
                        sum += va * vb;
                    }
                    Push(new Ue5PreshaderValue(EShaderValueType.Float1, sum));
                    break;
                }
                case EPreshaderOpcodeUe52.Cross:
                {
                    ops.Add("Cross");
                    var b = Pop();
                    var a = Pop();
                    var va = new Vector3(a.GetFloat(0), a.GetFloat(1), a.GetFloat(2));
                    var vb = new Vector3(b.GetFloat(0), b.GetFloat(1), b.GetFloat(2));
                    Push(Ue5PreshaderValue.FromVector3(Vector3.Cross(va, vb)));
                    break;
                }
                case EPreshaderOpcodeUe52.Sqrt: Unary(op, x => x > 0.0 ? Math.Sqrt(x) : 0.0); break;
                case EPreshaderOpcodeUe52.Rcp: Unary(op, x => DivideComponent(1f, (float) x)); break;
                case EPreshaderOpcodeUe52.Length:
                {
                    ops.Add("Length");
                    var v = Pop();
                    var n = Math.Clamp(v.Count, 1, 4);
                    var sumSq = 0.0;
                    for (var i = 0; i < n; i++)
                    {
                        var c = v.Get(i);
                        sumSq += c * c;
                    }
                    Push(new Ue5PreshaderValue(EShaderValueType.Float1, Math.Sqrt(sumSq)));
                    break;
                }
                case EPreshaderOpcodeUe52.Normalize:
                {
                    ops.Add("Normalize");
                    var v = Pop();
                    var n = Math.Clamp(v.Count, 1, 4);
                    var sumSq = 0.0;
                    for (var i = 0; i < n; i++)
                    {
                        var c = v.Get(i);
                        sumSq += c * c;
                    }
                    var invLen = DivideComponent(1f, MathF.Sqrt((float) sumSq));
                    var res = new double[n];
                    for (var i = 0; i < n; i++)
                        res[i] = v.Get(i) * invLen;
                    Push(new Ue5PreshaderValue((EShaderValueType) ((byte) EShaderValueType.Float1 + n - 1), res));
                    break;
                }
                case EPreshaderOpcodeUe52.Saturate: Unary(op, x => Math.Clamp(x, 0.0, 1.0)); break;
                case EPreshaderOpcodeUe52.Abs: Unary(op, Math.Abs); break;
                case EPreshaderOpcodeUe52.Floor: Unary(op, Math.Floor); break;
                case EPreshaderOpcodeUe52.Ceil: Unary(op, Math.Ceiling); break;
                case EPreshaderOpcodeUe52.Round: Unary(op, Math.Round); break;
                case EPreshaderOpcodeUe52.Trunc: Unary(op, Math.Truncate); break;
                case EPreshaderOpcodeUe52.Sign: Unary(op, x => Math.Sign(x)); break;
                case EPreshaderOpcodeUe52.Frac: Unary(op, x => x - Math.Floor(x)); break;
                case EPreshaderOpcodeUe52.Fractional: Unary(op, x => x - Math.Truncate(x)); break;
                case EPreshaderOpcodeUe52.Log2: Unary(op, x => x > 0.0 ? Math.Log2(x) : 0.0); break;
                case EPreshaderOpcodeUe52.Log10: Unary(op, x => x > 0.0 ? Math.Log10(x) : 0.0); break;
                case EPreshaderOpcodeUe52.Neg: Unary(op, x => -x); break;
                case EPreshaderOpcodeUe52.Less: Binary(op, (a, b) => a < b ? 1.0 : 0.0, isComparison: true); break;
                case EPreshaderOpcodeUe52.Greater: Binary(op, (a, b) => a > b ? 1.0 : 0.0, isComparison: true); break;
                case EPreshaderOpcodeUe52.LessEqual: Binary(op, (a, b) => a <= b ? 1.0 : 0.0, isComparison: true); break;
                case EPreshaderOpcodeUe52.GreaterEqual: Binary(op, (a, b) => a >= b ? 1.0 : 0.0, isComparison: true); break;
                case EPreshaderOpcodeUe52.ComponentSwizzle:
                {
                    if (ptr + 5 > end) { ptr = end; break; }
                    int numElements = Math.Clamp((int) data[ptr], 1, 4);
                    int r = data[ptr + 1], g = data[ptr + 2], b = data[ptr + 3], a = data[ptr + 4];
                    ptr += 5;
                    ops.Add($"Swizzle(n={numElements},{r},{g},{b},{a})");
                    var v = Pop();
                    int[] idx = [r, g, b, a];
                    var res = new double[numElements];
                    for (var i = 0; i < numElements; i++)
                        res[i] = v.Count == 1 ? v.Get(0) : v.Get(idx[i]);
                    var swizType = GetVectorTypeWithRank(v.ValueType, numElements);
                    Push(new Ue5PreshaderValue(swizType, res));
                    break;
                }
                case EPreshaderOpcodeUe52.AppendVector:
                {
                    ops.Add("Append");
                    var b = Pop();
                    var a = Pop();
                    var total = Math.Clamp(a.Count + b.Count, 1, 4);
                    var res = new double[total];
                    var k = 0;
                    for (var i = 0; i < a.Count && k < total; i++)
                        res[k++] = a.Get(i);
                    for (var i = 0; i < b.Count && k < total; i++)
                        res[k++] = b.Get(i);
                    var appType = GetVectorTypeWithRank(a.ValueType, total);
                    Push(new Ue5PreshaderValue(appType, res));
                    break;
                }
                case EPreshaderOpcodeUe52.TextureSize:
                case EPreshaderOpcodeUe52.TexelSize:
                {
                    if (ptr + 11 > end) { ptr = end; break; }
                    var (pName, pAssoc, pIndex) = ReadHashedParameterInfo(data, ref ptr, names);
                    var texIdx = BinaryPrimitives.ReadInt32LittleEndian(data[ptr..]);
                    ptr += 4;
                    var sz = resolveTextureSize(pName, pAssoc, pIndex, texIdx);
                    ops.Add($"{op}({pName ?? $"tex[{texIdx}]"})=({sz.X},{sz.Y},{sz.Z})");
                    if (op == EPreshaderOpcodeUe52.TexelSize)
                    {
                        Push(Ue5PreshaderValue.FromVector3(new Vector3(
                            sz.X > 0f ? 1f / sz.X : 0f,
                            sz.Y > 0f ? 1f / sz.Y : 0f,
                            sz.Z > 0f ? 1f / sz.Z : 0f)));
                    }
                    else
                    {
                        Push(Ue5PreshaderValue.FromVector3(sz));
                    }
                    break;
                }
                case EPreshaderOpcodeUe52.ExternalTextureCoordinateScaleRotation:
                {
                    if (ptr + 22 > end) { ptr = end; break; }
                    ptr += 22;
                    ops.Add("ExtTexScaleRot");
                    Push(Ue5PreshaderValue.FromVector4(new Vector4(1f, 0f, 0f, 1f), 4));
                    break;
                }
                case EPreshaderOpcodeUe52.ExternalTextureCoordinateOffset:
                {
                    if (ptr + 22 > end) { ptr = end; break; }
                    ptr += 22;
                    ops.Add("ExtTexOffset");
                    Push(Ue5PreshaderValue.FromVector4(Vector4.Zero, 4));
                    break;
                }
                case EPreshaderOpcodeUe52.RuntimeVirtualTextureUniform:
                case EPreshaderOpcodeUe52.SparseVolumeTextureUniform:
                {
                    if (ptr + 15 > end) { ptr = end; break; }
                    _ = ReadHashedParameterInfo(data, ref ptr, names);
                    _ = BinaryPrimitives.ReadInt32LittleEndian(data[ptr..]); // TextureIndex
                    var vecIdx = BinaryPrimitives.ReadInt32LittleEndian(data[(ptr + 4)..]); // VectorIndex
                    ptr += 8;
                    if (op == EPreshaderOpcodeUe52.RuntimeVirtualTextureUniform)
                        usesRvt = true;
                    ops.Add($"{op}(vec={vecIdx})");
                    var v = op == EPreshaderOpcodeUe52.RuntimeVirtualTextureUniform ? GetDefaultRvtUniform(vecIdx) : Vector4.Zero;
                    Push(Ue5PreshaderValue.FromVector4(v, 4));
                    break;
                }
                case EPreshaderOpcodeUe52.GetField:
                {
                    var (fieldType, compCount, _) = ReadUe5PreshaderType(set, data, ref ptr, end);
                    if (ptr + 2 > end) { ptr = end; break; }
                    int compIdx = BinaryPrimitives.ReadInt16LittleEndian(data[ptr..]);
                    ptr += 2;
                    var structVal = Pop();
                    var res = new double[compCount];
                    for (var i = 0; i < compCount; i++)
                        res[i] = structVal.Get(compIdx + i);
                    ops.Add($"GetField({fieldType},@{compIdx})");
                    Push(new Ue5PreshaderValue(fieldType, res));
                    break;
                }
                case EPreshaderOpcodeUe52.SetField:
                {
                    if (ptr + 4 > end) { ptr = end; break; }
                    int compIdx = BinaryPrimitives.ReadInt16LittleEndian(data[ptr..]);
                    int compNum = BinaryPrimitives.ReadInt16LittleEndian(data[(ptr + 2)..]);
                    ptr += 4;
                    var fieldVal = Pop();
                    var structVal = Pop();
                    var total = Math.Max(structVal.Count, compIdx + compNum);
                    var res = new double[total];
                    for (var i = 0; i < structVal.Count; i++)
                        res[i] = structVal.Get(i);
                    for (var i = 0; i < compNum && compIdx + i < total; i++)
                        res[compIdx + i] = fieldVal.Get(i);
                    ops.Add($"SetField(@{compIdx},n={compNum})");
                    Push(new Ue5PreshaderValue(structVal.ValueType, res));
                    break;
                }
                case EPreshaderOpcodeUe52.Jump:
                {
                    if (ptr + 4 > end) { ptr = end; break; }
                    var jumpOffset = BinaryPrimitives.ReadInt32LittleEndian(data[ptr..]);
                    ptr += 4;
                    ptr = Math.Clamp(ptr + jumpOffset, offset, end);
                    ops.Add($"Jump({jumpOffset})");
                    break;
                }
                case EPreshaderOpcodeUe52.JumpIfFalse:
                {
                    if (ptr + 4 > end) { ptr = end; break; }
                    var jumpOffset = BinaryPrimitives.ReadInt32LittleEndian(data[ptr..]);
                    ptr += 4;
                    var cond = Pop();
                    var truthy = cond.AsBoolScalar();
                    if (!truthy)
                        ptr = Math.Clamp(ptr + jumpOffset, offset, end);
                    ops.Add($"JumpIfFalse({truthy},{jumpOffset})");
                    break;
                }
                case EPreshaderOpcodeUe52.PushValue:
                {
                    if (ptr + 2 > end) { ptr = end; break; }
                    int stackOffset = BinaryPrimitives.ReadUInt16LittleEndian(data[ptr..]);
                    ptr += 2;
                    var srcIdx = stack.Count - 1 - stackOffset;
                    var val = srcIdx >= 0 && srcIdx < stack.Count ? stack[srcIdx] : Ue5PreshaderValue.Zero(EShaderValueType.Float1, 1);
                    ops.Add($"PushValue(-{stackOffset})");
                    Push(val);
                    break;
                }
                case EPreshaderOpcodeUe52.Assign:
                {
                    // Assign pops the top value and overwrites the value right below it on the stack (Preshader.cpp:259-265)
                    var rhs = Pop();
                    if (stack.Count > 0)
                        stack[^1] = rhs;
                    else
                        Push(rhs);
                    ops.Add("Assign");
                    break;
                }
                default:
                    ops.Add($"UnknownOp52({(byte) op})");
                    ptr = end;
                    break;
            }
        }

        trace = string.Join(" -> ", ops);
        return stack.Count > 0 ? stack[^1] : Ue5PreshaderValue.Zero(EShaderValueType.Float4, 4);
    }

    private static (string? Name, EMaterialParameterAssociation Association, int Index) ReadHashedParameterInfo(
        ReadOnlySpan<byte> data,
        ref int ptr,
        CUE4Parse.UE4.Objects.UObject.FName[] names)
    {
        var nameIdx = BinaryPrimitives.ReadUInt16LittleEndian(data[ptr..]);
        var paramIdx = BinaryPrimitives.ReadInt32LittleEndian(data[(ptr + 2)..]);
        var assoc = (EMaterialParameterAssociation) data[ptr + 6];
        ptr += 7;
        var name = nameIdx < names.Length ? names[nameIdx].Text : null;
        return (name, assoc, paramIdx);
    }

    private static (EShaderValueType ValueType, int NumComponents, ushort StructTypeIndex) ReadUe5PreshaderType(
        FUniformExpressionSet set,
        ReadOnlySpan<byte> data,
        ref int ptr,
        int end)
    {
        if (ptr >= end)
            return (EShaderValueType.Float1, 1, 0);

        var valueType = (EShaderValueType) data[ptr++];
        ushort structIdx = 0;
        if (valueType == EShaderValueType.Struct && ptr + 2 <= end)
        {
            structIdx = BinaryPrimitives.ReadUInt16LittleEndian(data[ptr..]);
            ptr += 2;
            var structTypes = set.UniformPreshaderData?.StructTypes ?? [];
            var compTypes = set.UniformPreshaderData?.StructComponentTypes ?? [];
            if (structIdx < structTypes.Length)
            {
                var st = structTypes[structIdx];
                return (valueType, Math.Max(st.NumComponents, 1), structIdx);
            }
            return (valueType, Math.Max(compTypes.Length, 1), structIdx);
        }

        return (valueType, GetShaderValueTypeComponentCount(valueType), 0);
    }

    private static double[] ReadUe5ConstantPayload(
        EShaderValueType valueType,
        int numComponents,
        ReadOnlySpan<byte> data,
        ref int ptr,
        int end)
    {
        var res = new double[Math.Max(numComponents, 1)];
        var elemBytes = GetShaderValueTypeElementSizeInBytes(valueType);
        for (var i = 0; i < numComponents; i++)
        {
            if (ptr + elemBytes > end)
            {
                ptr = end;
                break;
            }
            switch (elemBytes)
            {
                case 1:
                    res[i] = data[ptr] != 0 ? 1.0 : 0.0;
                    break;
                case 8:
                    res[i] = BinaryPrimitives.ReadDoubleLittleEndian(data[ptr..]);
                    break;
                default:
                    if (valueType is >= EShaderValueType.Int1 and <= EShaderValueType.Int4)
                        res[i] = BinaryPrimitives.ReadInt32LittleEndian(data[ptr..]);
                    else
                        res[i] = BinaryPrimitives.ReadSingleLittleEndian(data[ptr..]);
                    break;
            }
            ptr += elemBytes;
        }
        return res;
    }

    private static int GetShaderValueTypeComponentCount(EShaderValueType type) => type switch
    {
        EShaderValueType.Float1 or EShaderValueType.Double1 or EShaderValueType.Int1 or EShaderValueType.Bool1 or EShaderValueType.Numeric1 => 1,
        EShaderValueType.Float2 or EShaderValueType.Double2 or EShaderValueType.Int2 or EShaderValueType.Bool2 or EShaderValueType.Numeric2 => 2,
        EShaderValueType.Float3 or EShaderValueType.Double3 or EShaderValueType.Int3 or EShaderValueType.Bool3 or EShaderValueType.Numeric3 => 3,
        EShaderValueType.Float4 or EShaderValueType.Double4 or EShaderValueType.Int4 or EShaderValueType.Bool4 or EShaderValueType.Numeric4 => 4,
        EShaderValueType.Float4x4 or EShaderValueType.Double4x4 or EShaderValueType.DoubleInverse4x4 or EShaderValueType.Numeric4x4 => 16,
        _ => 4
    };

    private static int GetShaderValueTypeElementSizeInBytes(EShaderValueType type) => type switch
    {
        >= EShaderValueType.Bool1 and <= EShaderValueType.Bool4 => 1,
        >= EShaderValueType.Double1 and <= EShaderValueType.Double4 => 8,
        >= EShaderValueType.Numeric1 and <= EShaderValueType.Numeric4 => 8,
        EShaderValueType.Double4x4 or EShaderValueType.DoubleInverse4x4 or EShaderValueType.Numeric4x4 => 8,
        _ => 4
    };

    private static EShaderValueType GetVectorTypeWithRank(EShaderValueType baseType, int numElements)
    {
        var clamped = Math.Clamp(numElements, 1, 4) - 1;
        return baseType switch
        {
            >= EShaderValueType.Double1 and <= EShaderValueType.Double4 => (EShaderValueType) ((byte) EShaderValueType.Double1 + clamped),
            >= EShaderValueType.Int1 and <= EShaderValueType.Int4 => (EShaderValueType) ((byte) EShaderValueType.Int1 + clamped),
            >= EShaderValueType.Bool1 and <= EShaderValueType.Bool4 => (EShaderValueType) ((byte) EShaderValueType.Bool1 + clamped),
            _ => (EShaderValueType) ((byte) EShaderValueType.Float1 + clamped)
        };
    }

    /// <summary>
    /// Writes a preshader field into the uniform buffer matching UE 5.2 <c>FUniformExpressionSet::FillUniformBuffer</c>
    /// (<c>MaterialUniformExpressions.cpp:820-939</c>), including Float, Int, Bool bitfield, and Double LWC Tile/Offset packing.
    /// </summary>
    private static void WriteComponentsUe5(byte[] dst, int byteOffset, Ue5PreshaderValue val, int startComp, int numComponents, EShaderValueType fieldType)
    {
        if (fieldType is >= EShaderValueType.Double1 and <= EShaderValueType.Double4)
        {
            // UE 5.2 LWC Double1..4 writes Tile (float[0..N-1]) followed by Offset (float[N..2N-1])
            for (var i = 0; i < numComponents; i++)
            {
                var d = val.Get(startComp + i);
                var tile = Math.Floor(d / LwcTileSize);
                var offset = d - tile * LwcTileSize;
                WriteFloat(dst, byteOffset + i * 4, (float) tile);
                WriteFloat(dst, byteOffset + (numComponents + i) * 4, (float) offset);
            }
            return;
        }

        if (fieldType is >= EShaderValueType.Int1 and <= EShaderValueType.Int4)
        {
            for (var i = 0; i < numComponents; i++)
            {
                var iv = (int) Math.Round(val.Get(startComp + i));
                WriteUInt32(dst, byteOffset + i * 4, (uint) iv);
            }
            return;
        }

        if (fieldType is >= EShaderValueType.Bool1 and <= EShaderValueType.Bool4)
        {
            for (var i = 0; i < numComponents; i++)
            {
                var bv = Math.Abs(val.Get(startComp + i)) > 1e-9 ? 1u : 0u;
                WriteUInt32(dst, byteOffset + i * 4, bv);
            }
            return;
        }

        for (var i = 0; i < numComponents; i++)
        {
            WriteFloat(dst, byteOffset + i * 4, val.GetFloat(startComp + i));
        }
    }

    private static float GetComp(Vector4 v, int i) => i switch { 0 => v.X, 1 => v.Y, 2 => v.Z, 3 => v.W, _ => 0f };

    private static void WriteFloat(byte[] dst, int offset, float value)
    {
        if (offset >= 0 && offset + 4 <= dst.Length)
            MemoryMarshal.Write(dst.AsSpan(offset, 4), in value);
    }

    private static void WriteUInt32(byte[] dst, int offset, uint value)
    {
        if (offset >= 0 && offset + 4 <= dst.Length)
            MemoryMarshal.Write(dst.AsSpan(offset, 4), in value);
    }

    private static void WriteVector4(byte[] dst, int offset, Vector4 value)
    {
        if (offset >= 0 && offset + 16 <= dst.Length)
            MemoryMarshal.Write(dst.AsSpan(offset, 16), in value);
    }
}
