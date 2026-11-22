using System.Numerics;
using System.Runtime.InteropServices;
using CUE4Parse.UE4.Assets.Exports.Component.Landscape;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;



using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

using UExplorer.Scene;
namespace UExplorer.Rendering;

public readonly record struct NativeLandscapeDrawItem(NativeLandscapeTile Tile, Matrix4x4 LocalToWorld, float Distance);

public sealed class NativeLandscapeTile : IDisposable
{
    public ID3D11ShaderResourceView Heightmap { get; init; } = null!;
    public ID3D11ShaderResourceView[] Weightmaps { get; init; } = [];
    // 8 material texture slots:
    // [0] = Grass / Base Diffuse (or ColorMask)
    // [1] = Cliff / Rock Diffuse
    // [2] = Dirt / Mud Diffuse
    // [3] = Sand / Beach Diffuse
    // [4] = Gravel / Road / Asphalt Diffuse
    // [5] = Forest Floor / Secondary Diffuse
    // [6] = World Biome Map (e.g. T_ApolloBiome)
    // [7] = Emissive / Noise Mask
    public ID3D11ShaderResourceView[] LayerDiffuse { get; init; } = [];
    public ID3D11ShaderResourceView[] LayerNormal { get; init; } = [];
    public ID3D11ShaderResourceView[] LayerMasks { get; init; } = [];
    public Vector4 HeightmapScaleBias { get; init; }
    public Vector4 WeightmapScaleBias { get; init; }
    public Vector4 SubsectionParams { get; init; } // x = SizeQuads, y = SubsectionSizeQuads, z = NumSubsections, w = VisPacked
    public Vector4 HeightmapTexelInfo { get; init; } // x = Width, y = Height, z = TilingScale, w = EmissiveStrength
    public Vector4 WorldBoundsParams { get; init; } // x = OffsetX (130048), y = OffsetY (130048), z = InvWorldSize (1/260096), w = HasBiomeMap
    public Vector4 GrassColorA { get; init; }
    public Vector4 GrassColorB { get; init; }
    public Vector4 GrassColorC { get; init; }
    public Vector4[] LayerColors { get; init; } = new Vector4[16]; // xyz = diffuse tint, w = slot (1..6 = textured slot, 0.5 = flat color, 0 = unused)
    public Vector4[] LayerEmissiveColors { get; init; } = new Vector4[16]; // xyz = emissive tint, w = roughness default
    public Vector3 BoundsCenter { get; init; }
    public float BoundsRadius { get; init; }
    public uint SizeQuads { get; init; }

    public void Dispose()
    {
    }
}

public sealed class NativeLandscapeRenderer : IDisposable
{
    private const string LandscapeHlsl = """
        cbuffer LandscapeParams : register(b0)
        {
            float4 LocalToWorldRow0;
            float4 LocalToWorldRow1;
            float4 LocalToWorldRow2;
            float4 LocalToWorldRow3;
            float4 WorldToClipRow0;
            float4 WorldToClipRow1;
            float4 WorldToClipRow2;
            float4 WorldToClipRow3;
            float4 HeightmapScaleBias;   // x=ScaleX, y=ScaleY, z=BiasX, w=BiasY
            float4 WeightmapScaleBias;   // x=ScaleX, y=ScaleY, z=BiasX, w=BiasY
            float4 SubsectionParams;     // x=ComponentSizeQuads, y=SubsectionSizeQuads, z=NumSubsections, w=VisPacked (-1 if none)
            float4 HeightmapTexelInfo;   // x=Width, y=Height, z=UVTilingScale, w=GlobalEmissiveMult
            float4 WorldBoundsParams;    // x=WorldOffsetX, y=WorldOffsetY, z=InvWorldExtent, w=HasBiomeMap
            float4 GrassColorA;          // FarmGrassColorA (or primary grass tint)
            float4 GrassColorB;          // GrasslandsGrassColorB (or secondary grass tint)
            float4 GrassColorC;          // FarmGrassColorC (or forest grass tint)
            float4 LayerColors[16];      // [texIdx * 4 + chanIdx]: rgb = layer tint, a = textured layer id (1..6) or 0.5
            float4 LayerEmissiveColors[16]; // [texIdx * 4 + chanIdx]: rgb = emissive color, a = default roughness
        };

        Texture2D<float4> HeightmapTex : register(t0);
        Texture2D<float4> Weightmap0   : register(t1);
        Texture2D<float4> Weightmap1   : register(t2);
        Texture2D<float4> Weightmap2   : register(t3);
        Texture2D<float4> Weightmap3   : register(t4);

        Texture2D<float4> LayerDiff0   : register(t5);  // Grass / Base
        Texture2D<float4> LayerDiff1   : register(t6);  // Cliff / Rock
        Texture2D<float4> LayerDiff2   : register(t7);  // Dirt / Mud
        Texture2D<float4> LayerDiff3   : register(t8);  // Sand
        Texture2D<float4> LayerDiff4   : register(t9);  // Gravel / Asphalt / Road
        Texture2D<float4> LayerDiff5   : register(t10); // Forest Floor
        Texture2D<float4> BiomeMapTex  : register(t11); // T_ApolloBiome / World Color Map
        Texture2D<float4> EmissiveTex  : register(t12); // Emissive / Noise

        Texture2D<float4> LayerNorm0   : register(t13); // Grass Normal
        Texture2D<float4> LayerNorm1   : register(t14); // Cliff Normal
        Texture2D<float4> LayerNorm2   : register(t15); // Dirt Normal
        Texture2D<float4> LayerNorm3   : register(t16); // Sand Normal
        Texture2D<float4> LayerNorm4   : register(t17); // Gravel / Road Normal
        Texture2D<float4> LayerNorm5   : register(t18); // Forest Floor Normal

        Texture2D<float4> LayerMask0   : register(t19); // Grass SpecularMasks
        Texture2D<float4> LayerMask1   : register(t20); // Cliff SpecularMasks
        Texture2D<float4> LayerMask2   : register(t21); // Dirt SpecularMasks
        Texture2D<float4> LayerMask3   : register(t22); // Sand SpecularMasks

        SamplerState LinearClamp       : register(s0);
        SamplerState AnisoWrap         : register(s1);

        struct VSIn
        {
            float2 GridPos : ATTRIBUTE0;
        };

        struct PSIn
        {
            float4 Pos       : SV_Position;
            float3 WorldPos  : TEXCOORD0;
            float3 WorldNorm : TEXCOORD1;
            float3 WorldTang : TEXCOORD2;
            float2 WeightUV  : TEXCOORD3;
            float2 HeightUV  : TEXCOORD4;
        };

        float2 GridToHeightUV(float2 xy)
        {
            float maxQ = SubsectionParams.x;
            xy = clamp(xy, 0.0.xx, maxQ.xx);
            float subSize = max(SubsectionParams.y, 1.0);
            float2 subIdx = min(floor(xy / subSize), max(SubsectionParams.z - 1.0, 0.0));
            return (xy + subIdx) * HeightmapScaleBias.xy + HeightmapScaleBias.zw;
        }

        float DecodeTexelHeight(int2 coord)
        {
            int2 maxC = int2(HeightmapTexelInfo.xy) - 1;
            coord = clamp(coord, int2(0, 0), maxC);
            float4 h = HeightmapTex.Load(int3(coord, 0));
            // Exact UE LandscapeVertexFactory 16-bit height decode (from VS_TBasePassVSFNoLightMapPolicy_5.asm line 93):
            // dp2 r0.w, r1.zwzz, l(65280.0, 255.0, 0, 0); add -32768.0; mul 0.0078125
            float rawH = dot(round(h.rg * 255.0), float2(256.0, 1.0));
            return (rawH - 32768.0) * 0.0078125;
        }

        float SampleSmoothHeight(float2 xy)
        {
            float2 uv = GridToHeightUV(xy);
            float2 texelPos = uv * HeightmapTexelInfo.xy - 0.5;
            int2 c00 = (int2) floor(texelPos);
            float2 f = frac(texelPos);

            float h00 = DecodeTexelHeight(c00);
            float h10 = DecodeTexelHeight(c00 + int2(1, 0));
            float h01 = DecodeTexelHeight(c00 + int2(0, 1));
            float h11 = DecodeTexelHeight(c00 + int2(1, 1));

            return lerp(lerp(h00, h10, f.x), lerp(h01, h11, f.x), f.y);
        }

        float3 TransformPoint(float3 p)
        {
            return (p.xxxx * LocalToWorldRow0 + p.yyyy * LocalToWorldRow1 + p.zzzz * LocalToWorldRow2 + LocalToWorldRow3).xyz;
        }

        float4 TransformClip(float3 worldPos)
        {
            return worldPos.xxxx * WorldToClipRow0 + worldPos.yyyy * WorldToClipRow1 + worldPos.zzzz * WorldToClipRow2 + WorldToClipRow3;
        }

        PSIn VSMain(VSIn input)
        {
            PSIn o;
            float2 xy = input.GridPos;

            float subSize = max(SubsectionParams.y, 1.0);
            float2 subIdx = min(floor(xy / subSize), max(SubsectionParams.z - 1.0, 0.0));
            float2 weightUv = (xy + subIdx) * WeightmapScaleBias.xy + WeightmapScaleBias.zw;
            float2 heightUv = (xy + subIdx) * HeightmapScaleBias.xy + HeightmapScaleBias.zw + HeightmapScaleBias.xy * 0.5;

            float zC = SampleSmoothHeight(xy);
            float zR = SampleSmoothHeight(xy + float2(1.0, 0.0));
            float zL = SampleSmoothHeight(xy - float2(1.0, 0.0));
            float zU = SampleSmoothHeight(xy + float2(0.0, 1.0));
            float zD = SampleSmoothHeight(xy - float2(0.0, 1.0));

            float3 worldC = TransformPoint(float3(xy.x, xy.y, zC));
            float3 dX = TransformPoint(float3(xy.x + 1.0, xy.y, zR)) - TransformPoint(float3(xy.x - 1.0, xy.y, zL));
            float3 dY = TransformPoint(float3(xy.x, xy.y + 1.0, zU)) - TransformPoint(float3(xy.x, xy.y - 1.0, zD));

            float3 worldN = normalize(cross(dX, dY));
            if (worldN.z < 0.0) worldN = -worldN;
            float3 worldT = normalize(dX - worldN * dot(dX, worldN));

            o.WorldPos  = worldC;
            o.WorldNorm = worldN;
            o.WorldTang = worldT;
            o.Pos       = TransformClip(worldC);
            o.WeightUV  = weightUv;
            o.HeightUV  = heightUv;
            return o;
        }

        struct PSOut
        {
            float4 SceneColor : SV_Target0;
            float4 GBufferA   : SV_Target1;
            float4 GBufferB   : SV_Target2;
            float4 GBufferC   : SV_Target3;
            float4 Velocity   : SV_Target4;
        };

        float3 UnpackNormalSample(float4 nSample)
        {
            float2 xy = nSample.rg * 2.0 - 1.0;
            float z = sqrt(saturate(1.0 - dot(xy, xy)));
            return normalize(float3(xy, z));
        }

        PSOut PSMain(PSIn i)
        {
            float4 w0 = Weightmap0.Sample(LinearClamp, i.WeightUV);
            float4 w1 = Weightmap1.Sample(LinearClamp, i.WeightUV);
            float4 w2 = Weightmap2.Sample(LinearClamp, i.WeightUV);
            float4 w3 = Weightmap3.Sample(LinearClamp, i.WeightUV);
            float4 weights[4] = { w0, w1, w2, w3 };

            int visPacked = (int) round(SubsectionParams.w);
            if (visPacked >= 0)
            {
                int vTex = visPacked >> 2;
                int vChan = visPacked & 3;
                float hole = weights[clamp(vTex, 0, 3)][vChan];
                if (hole > 0.5) discard;
            }

            // 1. Decode exact heightmap normal from t0.ba (matches PS_TBasePassPSFNoLightMapPolicy_5.asm line 43):
            // sample r0.xy, v1.zwzz, t0.zwxy; mad r0.xy, r0.xy, 2.0, -1.0; sqrt r0.z, 1 - dot(r0.xy, r0.xy)
            float4 hSample = HeightmapTex.Sample(LinearClamp, i.HeightUV);
            float3 N = normalize(i.WorldNorm);
            float3 T = normalize(i.WorldTang - N * dot(i.WorldTang, N));
            float3 B = cross(N, T);
            if (dot(hSample.ba, hSample.ba) > 0.01 && dot(hSample.ba - 0.5.xx, hSample.ba - 0.5.xx) < 0.24)
            {
                float2 hmXY = hSample.ba * 2.0 - 1.0;
                float hmZ = sqrt(saturate(1.0 - dot(hmXY, hmXY)));
                float3 hmWorldN = normalize(T * hmXY.x + B * hmXY.y + N * hmZ);
                N = normalize(lerp(N, hmWorldN, 0.75));
            }

            // 2. World-space Biome UV (matches PS_5.asm line 153: (WorldXY + 130048) * (1 / 260096))
            float2 biomeUV = saturate((i.WorldPos.xy - WorldBoundsParams.xy) * WorldBoundsParams.z);
            float4 biomeSample = BiomeMapTex.Sample(LinearClamp, biomeUV);

            // Compute biome grass color from material vector parameters (FarmGrassColorA, GrasslandsGrassColorB, FarmGrassColorC)
            float3 grassTint = lerp(GrassColorA.rgb, GrassColorB.rgb, saturate(biomeSample.r));
            grassTint = lerp(grassTint, GrassColorC.rgb, saturate(biomeSample.g) * 0.65);

            float uvScale = max(HeightmapTexelInfo.z, 0.0001);
            float2 detailUV = i.WorldPos.xy * uvScale;
            float2 macroUV  = i.WorldPos.xy * (uvScale * 0.14);

            // Triplanar UV for steep cliffs so vertical rock faces never stretch!
            float3 absN = abs(N);
            float2 cliffUV_X = i.WorldPos.yz * (uvScale * 0.45);
            float2 cliffUV_Y = i.WorldPos.xz * (uvScale * 0.45);
            float2 cliffUV_Z = i.WorldPos.xy * (uvScale * 0.45);
            float3 triW = pow(absN, 4.0);
            triW /= max(triW.x + triW.y + triW.z, 1e-4);

            // Sample Grass Diffuse (Slot 0) — if it's a grayscale ColorMask (like T_ApolloGrass_ColorMask), modulate with grassTint!
            float3 rawGrass = lerp(LayerDiff0.Sample(AnisoWrap, detailUV).rgb, LayerDiff0.Sample(AnisoWrap, macroUV).rgb, 0.35);
            float grassMax = max(rawGrass.r, max(rawGrass.g, rawGrass.b));
            float grassMin = min(rawGrass.r, min(rawGrass.g, rawGrass.b));
            float grassSat = (grassMax - grassMin) / max(grassMax, 1e-4);
            float grassVar = clamp(dot(rawGrass, float3(0.3, 0.59, 0.11)) * 1.45 + 0.35, 0.45, 1.35);
            float3 d0 = grassSat < 0.12 ? (grassTint * grassVar) : (rawGrass * LayerColors[0].rgb);

            // Sample Triplanar Cliff Diffuse (Slot 1)
            float3 d1 = LayerDiff1.Sample(AnisoWrap, cliffUV_X).rgb * triW.x
                      + LayerDiff1.Sample(AnisoWrap, cliffUV_Y).rgb * triW.y
                      + LayerDiff1.Sample(AnisoWrap, cliffUV_Z).rgb * triW.z;

            // Sample Dirt (Slot 2), Sand (Slot 3), Gravel/Asphalt (Slot 4), Forest Floor (Slot 5)
            float3 d2 = lerp(LayerDiff2.Sample(AnisoWrap, detailUV).rgb, LayerDiff2.Sample(AnisoWrap, macroUV).rgb, 0.35);
            float3 d3 = lerp(LayerDiff3.Sample(AnisoWrap, detailUV).rgb, LayerDiff3.Sample(AnisoWrap, macroUV).rgb, 0.35);
            float3 d4 = lerp(LayerDiff4.Sample(AnisoWrap, detailUV).rgb, LayerDiff4.Sample(AnisoWrap, macroUV).rgb, 0.35);
            float3 d5 = lerp(LayerDiff5.Sample(AnisoWrap, detailUV).rgb, LayerDiff5.Sample(AnisoWrap, macroUV).rgb, 0.35);
            float3 layerDiffs[6] = { d0, d1, d2, d3, d4, d5 };

            // Sample Normals
            float3 n0 = UnpackNormalSample(LayerNorm0.Sample(AnisoWrap, detailUV));
            float3 n1 = UnpackNormalSample(
                LayerNorm1.Sample(AnisoWrap, cliffUV_X) * triW.x +
                LayerNorm1.Sample(AnisoWrap, cliffUV_Y) * triW.y +
                LayerNorm1.Sample(AnisoWrap, cliffUV_Z) * triW.z);
            float3 n2 = UnpackNormalSample(LayerNorm2.Sample(AnisoWrap, detailUV));
            float3 n3 = UnpackNormalSample(LayerNorm3.Sample(AnisoWrap, detailUV));
            float3 n4 = UnpackNormalSample(LayerNorm4.Sample(AnisoWrap, detailUV));
            float3 n5 = UnpackNormalSample(LayerNorm5.Sample(AnisoWrap, detailUV));
            float3 layerNorms[6] = { n0, n1, n2, n3, n4, n5 };

            // Sample Specular/Roughness Masks (_S)
            float3 m0 = LayerMask0.Sample(AnisoWrap, detailUV).rgb;
            float3 m1 = LayerMask1.Sample(AnisoWrap, cliffUV_Z).rgb;
            float3 m2 = LayerMask2.Sample(AnisoWrap, detailUV).rgb;
            float3 m3 = LayerMask3.Sample(AnisoWrap, detailUV).rgb;
            float3 layerMasks[6] = { m0, m1, m2, m3, m2, m0 };

            float3 blendedColor = 0;
            float3 blendedEmissive = 0;
            float3 blendedMasks = 0;
            float3 blendedTangentN = 0;
            float totalWeight = 0;

            [unroll]
            for (int t = 0; t < 4; t++)
            {
                [unroll]
                for (int c = 0; c < 4; c++)
                {
                    int idx = t * 4 + c;
                    if (idx == visPacked) continue;
                    float4 lc = LayerColors[idx];
                    if (lc.a > 0.0)
                    {
                        float w = weights[t][c];
                        if (w > 0.001)
                        {
                            int texSlot = clamp((int) round(lc.a) - 1, 0, 5);
                            float4 ec = LayerEmissiveColors[idx];
                            float3 sampleCol = layerDiffs[texSlot];
                            // If slot > 0 and texture is grayscale, tint by lc.rgb; otherwise modulate gently
                            float sMax = max(sampleCol.r, max(sampleCol.g, sampleCol.b));
                            float sMin = min(sampleCol.r, min(sampleCol.g, sampleCol.b));
                            float sSat = (sMax - sMin) / max(sMax, 1e-4);
                            float3 texCol = (texSlot == 0)
                                ? d0
                                : (sSat < 0.08 ? lc.rgb * clamp(sMax * 1.4, 0.35, 1.3) : sampleCol * lc.rgb);

                            blendedColor += texCol * w;
                            blendedEmissive += ec.rgb * w;

                            float3 mSample = layerMasks[texSlot];
                            // In Fortnite _S textures: R = Specular, G = Cavity/Metallic, B = Roughness
                            float rough = mSample.b > 0.05 ? clamp(mSample.b, 0.45, 0.98) : ec.a;
                            float spec  = mSample.r > 0.01 ? clamp(mSample.r * 0.35, 0.10, 0.45) : 0.20;
                            blendedMasks += float3(0.0, spec, rough) * w;

                            blendedTangentN += layerNorms[texSlot] * w;
                            totalWeight += w;
                        }
                    }
                }
            }

            // Automatic slope-based cliff rock blending (just like M_CommonLandscape's WorldAlignedBlend!)
            float slope = saturate(1.0 - N.z);
            float cliffAlpha = smoothstep(0.18, 0.42, slope);

            float invW = 1.0 / max(totalWeight, 1e-4);
            float3 paintColor = totalWeight > 0.05 ? blendedColor * invW : d0;
            float3 paintTanN  = totalWeight > 0.05 ? normalize(blendedTangentN) : n0;
            float3 paintMSR   = totalWeight > 0.05 ? blendedMasks * invW : float3(0.0, 0.18, 0.88);

            // Blend painted ground with triplanar cliff texture on steep slopes
            float3 finalColor = lerp(paintColor, d1 * LayerColors[1].rgb, cliffAlpha);
            float3 finalTanN  = normalize(lerp(paintTanN, n1, cliffAlpha));
            float3 finalMSR   = lerp(paintMSR, float3(0.0, 0.25, 0.78), cliffAlpha);
            float3 finalEmissive = (totalWeight > 0.05 ? blendedEmissive * invW : 0.0.xxx) * HeightmapTexelInfo.w;

            float3 worldN = normalize(T * finalTanN.x * 0.55 + B * finalTanN.y * 0.55 + N * finalTanN.z);

            PSOut o;
            o.SceneColor = float4(max(finalEmissive, 0.0), 0.0);
            o.GBufferA   = float4(worldN * 0.5 + 0.5, 1.0);
            o.GBufferB   = float4(saturate(finalMSR.x), saturate(finalMSR.y), clamp(finalMSR.z, 0.04, 1.0), 161.0 / 255.0);
            o.GBufferC   = float4(saturate(finalColor), 1.0);
            o.Velocity   = float4(0, 0, 0, 0);
            return o;
        }
        """;

    private readonly ID3D11Device _device;
    private readonly List<IDisposable> _owned = [];
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11SamplerState _clampSampler;
    private readonly ID3D11SamplerState _wrapSampler;
    private readonly ID3D11ShaderResourceView _black2D;
    private readonly ID3D11ShaderResourceView _white2D;
    private readonly ID3D11ShaderResourceView _flatNormal2D;
    private readonly Dictionary<(uint SizeQuads, uint Step), (ID3D11Buffer Vb, ID3D11Buffer Ib, int IndexCount)> _grids = [];

    private T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }

    public NativeLandscapeRenderer(ID3D11Device device, ID3D11ShaderResourceView black2D, ID3D11ShaderResourceView white2D, ID3D11ShaderResourceView flatNormal2D)
    {
        _device = device;
        _black2D = black2D;
        _white2D = white2D;
        _flatNormal2D = flatNormal2D;

        var vsBytes = Compiler.Compile(LandscapeHlsl, "VSMain", "landscape_vs", "vs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();
        var psBytes = Compiler.Compile(LandscapeHlsl, "PSMain", "landscape_ps", "ps_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();

        _vs = Own(device.CreateVertexShader(vsBytes));
        _ps = Own(device.CreatePixelShader(psBytes));
        _inputLayout = Own(device.CreateInputLayout(
        [
            new InputElementDescription("ATTRIBUTE", 0, Format.R32G32_Float, 0, 0, InputClassification.PerVertexData, 0)
        ], vsBytes));

        _constantBuffer = Own(device.CreateBuffer(48 * 16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _clampSampler = Own(device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue)));
        _wrapSampler = Own(device.CreateSamplerState(new SamplerDescription(Filter.Anisotropic, TextureAddressMode.Wrap, 0, 8, ComparisonFunction.Never, 0, float.MaxValue)));
    }

    private (ID3D11Buffer Vb, ID3D11Buffer Ib, int IndexCount) GetOrCreateGrid(uint sizeQuads, uint step)
    {
        sizeQuads = Math.Clamp(sizeQuads, 1u, 255u);
        step = Math.Clamp(step, 1u, Math.Max(1u, sizeQuads));
        while (sizeQuads % step != 0 && step > 1)
            step--;

        var key = (sizeQuads, step);
        if (_grids.TryGetValue(key, out var existing))
            return existing;

        var stepsPerSide = sizeQuads / step;
        var vertsPerSide = stepsPerSide + 1;
        var vertices = new Vector2[vertsPerSide * vertsPerSide];
        for (uint y = 0; y < vertsPerSide; y++)
            for (uint x = 0; x < vertsPerSide; x++)
                vertices[y * vertsPerSide + x] = new Vector2(x * step, y * step);

        var indices = new uint[stepsPerSide * stepsPerSide * 6];
        var idx = 0;
        for (uint y = 0; y < stepsPerSide; y++)
            for (uint x = 0; x < stepsPerSide; x++)
            {
                var v00 = y * vertsPerSide + x;
                var v10 = v00 + 1;
                var v01 = (y + 1) * vertsPerSide + x;
                var v11 = v01 + 1;

                indices[idx++] = v00;
                indices[idx++] = v01;
                indices[idx++] = v10;

                indices[idx++] = v10;
                indices[idx++] = v01;
                indices[idx++] = v11;
            }

        var vb = Own(_device.CreateBuffer(vertices.AsSpan(), BindFlags.VertexBuffer));
        var ib = Own(_device.CreateBuffer(indices.AsSpan(), BindFlags.IndexBuffer));
        var built = (vb, ib, indices.Length);
        _grids[key] = built;
        return built;
    }

    public unsafe void Render(ID3D11DeviceContext ctx, Matrix4x4 worldToClip, IReadOnlyList<NativeLandscapeDrawItem> items, ID3D11RasterizerState rasterState, ref int drawCalls)
    {
        if (items.Count == 0)
            return;

        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.VSSetConstantBuffer(0, _constantBuffer);
        ctx.PSSetConstantBuffer(0, _constantBuffer);
        ctx.VSSetSampler(0, _clampSampler);
        ctx.PSSetSamplers(0, [_clampSampler, _wrapSampler]);
        ctx.RSSetState(rasterState);

        var cbData = new Vector4[48];
        (uint Size, uint Step) currentGrid = (0, 0);
        var currentIndexCount = 0;

        foreach (var (tile, localToWorld, distance) in items)
        {
            uint step = distance switch
            {
                > 150000f => 8u,
                > 75000f => 4u,
                > 30000f => 2u,
                _ => 1u
            };

            if ((tile.SizeQuads, step) != currentGrid)
            {
                currentGrid = (tile.SizeQuads, step);
                var (vb, ib, idxCount) = GetOrCreateGrid(tile.SizeQuads, step);
                currentIndexCount = idxCount;
                ctx.IASetVertexBuffer(0, vb, 8u, 0u);
                ctx.IASetIndexBuffer(ib, Format.R32_UInt, 0);
            }

            WriteMatrix(cbData, 0, localToWorld);
            WriteMatrix(cbData, 4, worldToClip);
            cbData[8] = tile.HeightmapScaleBias;
            cbData[9] = tile.WeightmapScaleBias;
            cbData[10] = tile.SubsectionParams;
            cbData[11] = tile.HeightmapTexelInfo;
            cbData[12] = tile.WorldBoundsParams;
            cbData[13] = tile.GrassColorA;
            cbData[14] = tile.GrassColorB;
            cbData[15] = tile.GrassColorC;
            for (var l = 0; l < 16; l++)
            {
                cbData[16 + l] = tile.LayerColors[l];
                cbData[32 + l] = tile.LayerEmissiveColors[l];
            }

            var mapped = ctx.Map(_constantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var bytes = MemoryMarshal.AsBytes(cbData.AsSpan());
                bytes.CopyTo(new Span<byte>((void*) mapped.DataPointer, bytes.Length));
            }
            finally { ctx.Unmap(_constantBuffer, 0); }

            var srvs = new ID3D11ShaderResourceView[23];
            srvs[0] = tile.Heightmap;
            for (var w = 0; w < 4; w++)
                srvs[1 + w] = w < tile.Weightmaps.Length && tile.Weightmaps[w] is not null ? tile.Weightmaps[w] : _black2D;
            for (var d = 0; d < 8; d++)
                srvs[5 + d] = d < tile.LayerDiffuse.Length && tile.LayerDiffuse[d] is not null ? tile.LayerDiffuse[d] : _white2D;
            for (var n = 0; n < 6; n++)
                srvs[13 + n] = n < tile.LayerNormal.Length && tile.LayerNormal[n] is not null ? tile.LayerNormal[n] : _flatNormal2D;
            for (var m = 0; m < 4; m++)
                srvs[19 + m] = m < tile.LayerMasks.Length && tile.LayerMasks[m] is not null ? tile.LayerMasks[m] : _black2D;

            ctx.VSSetShaderResource(0, tile.Heightmap);
            ctx.PSSetShaderResources(0, srvs);

            ctx.DrawIndexed((uint) currentIndexCount, 0, 0);
            drawCalls++;
        }
    }

    private static void WriteMatrix(Vector4[] dst, int offset, Matrix4x4 m)
    {
        dst[offset + 0] = new Vector4(m.M11, m.M12, m.M13, m.M14);
        dst[offset + 1] = new Vector4(m.M21, m.M22, m.M23, m.M24);
        dst[offset + 2] = new Vector4(m.M31, m.M32, m.M33, m.M34);
        dst[offset + 3] = new Vector4(m.M41, m.M42, m.M43, m.M44);
    }

    public static NativeLandscapeTile? BuildTile(ID3D11Device device, SceneLandscape comp)
    {
        try
        {
            var uComp = comp.SourceComponent;
            if (uComp.GetHeightmap() is not { } heightmapTex)
                return null;
            if (NativeTextureCache.Get(device, heightmapTex, asCube: false, forceLinearUncompressed: true) is not { } heightSrv)
                return null;

            var hmWidth = Math.Max(1, heightmapTex.PlatformData?.SizeX ?? 512);
            var hmHeight = Math.Max(1, heightmapTex.PlatformData?.SizeY ?? 512);

            var weightTexs = uComp.GetWeightmapTextures();
            var weightSrvs = new ID3D11ShaderResourceView[Math.Min(4, weightTexs.Length)];
            for (var i = 0; i < weightSrvs.Length; i++)
                weightSrvs[i] = NativeTextureCache.Get(device, weightTexs[i], asCube: false, forceLinearUncompressed: true)!;

            var matNode = ResolveLandscapeMaterialNode(comp, uComp);
            if (matNode is not null)
                NativeAssetCache.DumpLandscapeMaterialOnce(matNode);

            var extracted = ExtractAllMaterialLayers(device, matNode);

            var sizeQuads = (uint) Math.Max(1, uComp.ComponentSizeQuads);
            var subQuads = uComp.NumSubsections > 1 ? (uint) uComp.SubsectionSizeQuads : sizeQuads;
            var numSub = Math.Max(1, uComp.NumSubsections);

            var visPacked = -1f;
            var layerColors = new Vector4[16];
            var layerEmissive = new Vector4[16];

            // Slot 0 default = Grass (1), Slot 1 default = Cliff (2)
            layerColors[0] = new Vector4(1f, 1f, 1f, 1f);
            layerColors[1] = new Vector4(1f, 1f, 1f, 2f);
            for (var i = 0; i < 16; i++)
                layerEmissive[i] = new Vector4(0f, 0f, 0f, 0.85f);

            foreach (var (name, mapping) in comp.Layers)
            {
                if (mapping.TextureIndex >= 4 || mapping.ChannelIndex >= 4)
                    continue;
                var slot = (int) (mapping.TextureIndex * 4 + mapping.ChannelIndex);
                if (name == LandscapeSystem.VisibilityLayer)
                {
                    visPacked = slot;
                    continue;
                }

                // Map each painted weightmap layer to its exact semantic texture slot (1=Grass, 2=Cliff/Rock, 3=Dirt/Mud, 4=Sand, 5=Gravel/Road, 6=Forest)
                var texSlot = MapLayerNameToTextureSlot(name);
                var (tint, emissive, roughness) = ResolveLayerParametersFromMaterial(matNode, name, mapping.DebugColor, texSlot);
                layerColors[slot] = new Vector4(tint, texSlot);
                layerEmissive[slot] = new Vector4(emissive, roughness);
            }

            var minBox = new Vector3(uComp.CachedLocalBox.Min.X, uComp.CachedLocalBox.Min.Y, uComp.CachedLocalBox.Min.Z);
            var maxBox = new Vector3(uComp.CachedLocalBox.Max.X, uComp.CachedLocalBox.Max.Y, uComp.CachedLocalBox.Max.Z);
            var center = (minBox + maxBox) * 0.5f;
            var radius = MathF.Max((maxBox - minBox).Length() * 0.5f, sizeQuads * 150f);

            return new NativeLandscapeTile
            {
                Heightmap = heightSrv,
                Weightmaps = weightSrvs,
                LayerDiffuse = extracted.Diff,
                LayerNormal = extracted.Norm,
                LayerMasks = extracted.Masks,
                HeightmapScaleBias = new Vector4(uComp.HeightmapScaleBias.X, uComp.HeightmapScaleBias.Y, uComp.HeightmapScaleBias.Z, uComp.HeightmapScaleBias.W),
                WeightmapScaleBias = new Vector4(uComp.WeightmapScaleBias.X, uComp.WeightmapScaleBias.Y, uComp.WeightmapScaleBias.Z, uComp.WeightmapScaleBias.W),
                SubsectionParams = new Vector4(sizeQuads, subQuads, numSub, visPacked),
                HeightmapTexelInfo = new Vector4(hmWidth, hmHeight, extracted.UvScale, extracted.EmisMult),
                WorldBoundsParams = extracted.WorldBounds,
                GrassColorA = extracted.GrassA,
                GrassColorB = extracted.GrassB,
                GrassColorC = extracted.GrassC,
                LayerColors = layerColors,
                LayerEmissiveColors = layerEmissive,
                BoundsCenter = center,
                BoundsRadius = radius,
                SizeQuads = sizeQuads
            };
        }
        catch (Exception e)
        {
            Log.Warning(e, "Native landscape tile failed for {Name}", comp.Name);
            return null;
        }
    }

    private static int MapLayerNameToTextureSlot(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("cliff") || lower.Contains("rock") || lower.Contains("stone") || lower.Contains("mountain"))
            return 2; // LayerDiff1 (Cliff)
        if (lower.Contains("dirt") || lower.Contains("mud") || lower.Contains("soil") || lower.Contains("path"))
            return 3; // LayerDiff2 (Dirt)
        if (lower.Contains("sand") || lower.Contains("beach") || lower.Contains("desert"))
            return 4; // LayerDiff3 (Sand)
        if (lower.Contains("gravel") || lower.Contains("asphalt") || lower.Contains("road") || lower.Contains("concrete") || lower.Contains("pavement"))
            return 5; // LayerDiff4 (Gravel / Asphalt)
        if (lower.Contains("forest") || lower.Contains("leaves") || lower.Contains("mulch") || lower.Contains("wood"))
            return 6; // LayerDiff5 (Forest Floor)
        return 1;     // LayerDiff0 (Grass / Base)
    }

    private static MaterialAsset? ResolveLandscapeMaterialNode(SceneLandscape comp, ULandscapeComponent uComp)
    {
        if (comp.Materials.Length > 0 && comp.Materials[0]?.Node is { } existingNode)
            return existingNode;

        var matIndices = uComp.GetOrDefault<FPackageIndex[]>("MaterialInstances", []);
        foreach (var idx in matIndices)
        {
            if (idx is { IsNull: false } && idx.TryLoad<UUnrealMaterial>(out var loadedMat))
                return new MaterialAsset(loadedMat);
        }

        if (uComp.GetOrDefault<FPackageIndex?>("MaterialInstance") is { IsNull: false } singleIdx && singleIdx.TryLoad<UUnrealMaterial>(out var singleMat))
            return new MaterialAsset(singleMat);

        if (uComp.GetOrDefault<FPackageIndex?>("OverrideMaterial") is { IsNull: false } overIdx && overIdx.TryLoad<UUnrealMaterial>(out var overMat))
            return new MaterialAsset(overMat);

        if (uComp.Outer is { } outerResolved && outerResolved.TryLoad(out var outerObj))
        {
            if (outerObj.GetOrDefault<FPackageIndex?>("LandscapeMaterial") is { IsNull: false } actorMatIdx && actorMatIdx.TryLoad<UUnrealMaterial>(out var actorMat))
                return new MaterialAsset(actorMat);
        }

        return null;
    }

    private readonly record struct ExtractedLandscapeMaterial(
        ID3D11ShaderResourceView?[] Diff,
        ID3D11ShaderResourceView?[] Norm,
        ID3D11ShaderResourceView?[] Masks,
        float UvScale,
        float EmisMult,
        Vector4 WorldBounds,
        Vector4 GrassA,
        Vector4 GrassB,
        Vector4 GrassC);

    private static ExtractedLandscapeMaterial ExtractAllMaterialLayers(ID3D11Device device, MaterialAsset? matNode)
    {
        var diff = new ID3D11ShaderResourceView?[8];
        var norm = new ID3D11ShaderResourceView?[6];
        var mask = new ID3D11ShaderResourceView?[4];
        var uvScale = 0.0025f;
        var emisMult = 1.0f;

        // Defaults verified from summary_5.txt (LandscapeMaterialInstanceConstant_0 / M_CommonLandscape):
        // FarmGrassColorA = (0.0824, 0.14, 0), GrasslandsGrassColorB = (0.35, 0.3467, 0.035), FarmGrassColorC = (0.0945, 0.15, 0.015)
        // Note: In shader PS_5.asm, grass albedo is multiplied by ~2.2x before GBufferC output
        var grassA = new Vector4(0.18f, 0.34f, 0.06f, 1f);
        var grassB = new Vector4(0.36f, 0.42f, 0.10f, 1f);
        var grassC = new Vector4(0.16f, 0.30f, 0.05f, 1f);
        var worldBounds = new Vector4(-130048f, -130048f, 1f / 260096f, 0f);

        if (matNode is null)
            return new ExtractedLandscapeMaterial(diff, norm, mask, uvScale, emisMult, worldBounds, grassA, grassB, grassC);

        if (matNode.TryGetVector(out var cA, "FarmGrassColorA", "GrassColorA", "GrassColor", "GrassTint"))
            grassA = new Vector4(Math.Clamp(cA.R * 2.2f, 0.08f, 0.85f), Math.Clamp(cA.G * 2.2f, 0.15f, 0.9f), Math.Clamp(cA.B * 2.2f, 0.02f, 0.6f), 1f);
        if (matNode.TryGetVector(out var cB, "GrasslandsGrassColorB", "GrassColorB", "DryGrassColor"))
            grassB = new Vector4(Math.Clamp(cB.R * 1.3f, 0.12f, 0.85f), Math.Clamp(cB.G * 1.3f, 0.18f, 0.9f), Math.Clamp(cB.B * 1.3f, 0.04f, 0.6f), 1f);
        if (matNode.TryGetVector(out var cC, "FarmGrassColorC", "GrassColorC", "ForestGrassColor"))
            grassC = new Vector4(Math.Clamp(cC.R * 2.2f, 0.08f, 0.85f), Math.Clamp(cC.G * 2.2f, 0.15f, 0.9f), Math.Clamp(cC.B * 2.2f, 0.02f, 0.6f), 1f);

        if (matNode.TryGetVector(out var wUv, "LandscapeWorldUV") && MathF.Abs(wUv.A) > 100f)
            worldBounds = new Vector4(wUv.B, wUv.B, 1f / wUv.A, 1f);

        if (matNode.TryGetScalar(out var gScale, "EPIC_GrassA-Scale", "Tiling", "UVScale", "GlobalTiling") && gScale > 0f)
            uvScale = gScale > 10f ? 1f / gScale : gScale * 0.004f;

        var shaderSets = matNode.GetNativeShaders(validateOnDevice: false);
        var allTextures = new List<UTexture>();
        foreach (var set in shaderSets)
        {
            foreach (var tex in matNode.ResolveNativeMaterialResources(set))
            {
                if (tex is not null && !allTextures.Contains(tex))
                    allTextures.Add(tex);
            }
        }

        // Categorize all textures from the Landscape Material into their exact semantic slots!
        foreach (var tex in allTextures)
        {
            var n = tex.Name;
            if (n.Contains("Biome", StringComparison.OrdinalIgnoreCase))
            {
                diff[6] ??= NativeTextureCache.Get(device, tex, forceLinearUncompressed: true);
                worldBounds.W = 1f;
                continue;
            }

            var isNorm = tex.CompressionSettings == TextureCompressionSettings.TC_Normalmap || n.EndsWith("_N", StringComparison.OrdinalIgnoreCase) || n.Contains("_N_", StringComparison.OrdinalIgnoreCase);
            var isMask = tex.CompressionSettings == TextureCompressionSettings.TC_Masks || n.EndsWith("_S", StringComparison.OrdinalIgnoreCase) || n.Contains("_S_", StringComparison.OrdinalIgnoreCase);
            var isDiff = tex.SRGB || n.EndsWith("_D", StringComparison.OrdinalIgnoreCase) || n.Contains("_D_", StringComparison.OrdinalIgnoreCase) || n.Contains("ColorMask", StringComparison.OrdinalIgnoreCase);

            if (n.Contains("Grass", StringComparison.OrdinalIgnoreCase))
            {
                if (isNorm)
                    norm[0] ??= NativeTextureCache.Get(device, tex);
                else if (isMask && !n.Contains("ColorMask", StringComparison.OrdinalIgnoreCase))
                    mask[0] ??= NativeTextureCache.Get(device, tex);
                else if (isDiff)
                    diff[0] ??= NativeTextureCache.Get(device, tex);
            }
            else if (n.Contains("Cliff", StringComparison.OrdinalIgnoreCase) || n.Contains("Rock", StringComparison.OrdinalIgnoreCase))
            {
                if (isNorm)
                    norm[1] ??= NativeTextureCache.Get(device, tex);
                else if (isMask)
                    mask[1] ??= NativeTextureCache.Get(device, tex);
                else if (isDiff)
                    diff[1] ??= NativeTextureCache.Get(device, tex);
            }
            else if (n.Contains("Dirt", StringComparison.OrdinalIgnoreCase) || n.Contains("Mud", StringComparison.OrdinalIgnoreCase))
            {
                if (isNorm)
                    norm[2] ??= NativeTextureCache.Get(device, tex);
                else if (isMask)
                    mask[2] ??= NativeTextureCache.Get(device, tex);
                else if (isDiff)
                    diff[2] ??= NativeTextureCache.Get(device, tex);
            }
            else if (n.Contains("Sand", StringComparison.OrdinalIgnoreCase) || n.Contains("Beach", StringComparison.OrdinalIgnoreCase))
            {
                if (isNorm)
                    norm[3] ??= NativeTextureCache.Get(device, tex);
                else if (isMask)
                    mask[3] ??= NativeTextureCache.Get(device, tex);
                else if (isDiff)
                    diff[3] ??= NativeTextureCache.Get(device, tex);
            }
            else if (n.Contains("Gravel", StringComparison.OrdinalIgnoreCase) || n.Contains("Asphalt", StringComparison.OrdinalIgnoreCase) || n.Contains("Road", StringComparison.OrdinalIgnoreCase))
            {
                if (isNorm)
                    norm[4] ??= NativeTextureCache.Get(device, tex);
                else if (isDiff)
                    diff[4] ??= NativeTextureCache.Get(device, tex);
            }
            else if (n.Contains("Forest", StringComparison.OrdinalIgnoreCase) || n.Contains("Floor", StringComparison.OrdinalIgnoreCase))
            {
                if (isNorm)
                    norm[5] ??= NativeTextureCache.Get(device, tex);
                else if (isDiff)
                    diff[5] ??= NativeTextureCache.Get(device, tex);
            }
        }

        // Fill any missing slots with the closest available terrain texture so no slot is ever null/white
        var fallbackDiff = diff[2] ?? diff[5] ?? diff[1] ?? diff[0];
        var fallbackNorm = norm[0] ?? norm[2] ?? norm[1];
        var fallbackMask = mask[2] ?? mask[1] ?? mask[0];

        for (var i = 0; i < 6; i++)
        {
            diff[i] ??= fallbackDiff;
            norm[i] ??= fallbackNorm;
        }
        for (var i = 0; i < 4; i++)
            mask[i] ??= fallbackMask;

        return new ExtractedLandscapeMaterial(diff, norm, mask, uvScale, emisMult, worldBounds, grassA, grassB, grassC);
    }

    private static (Vector3 Tint, Vector3 Emissive, float Roughness) ResolveLayerParametersFromMaterial(
        MaterialAsset? matNode, string layerName, Vector4 debugColor, int texSlot)
    {
        // When using real sRGB textures (Cliff, Dirt, Sand, Gravel, Forest), use a neutral (1,1,1) tint so the texture's true colors shine!
        var tint = texSlot switch
        {
            1 => new Vector3(0.24f, 0.44f, 0.14f), // Grass
            _ => Vector3.One                       // Cliff, Dirt, Sand, Gravel, Forest use their actual sRGB texture!
        };
        var emissive = Vector3.Zero;
        var roughness = texSlot switch
        {
            2 => 0.78f, // Cliff rock
            4 => 0.82f, // Sand
            5 => 0.75f, // Gravel/Asphalt
            _ => 0.88f  // Grass/Dirt/Forest
        };

        if (matNode is not null)
        {
            if (matNode.TryGetVector(out var vEmis, $"{layerName}_Emissive", $"{layerName}Emissive", "EmissiveColor"))
                emissive = new Vector3(vEmis.R, vEmis.G, vEmis.B);
        }

        return (tint, emissive, roughness);
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
            _owned[i].Dispose();
    }
}
