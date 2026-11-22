namespace UExplorer.Rendering;

/// <summary>
/// Direct HLSL port of Unreal Engine 5.2's Deferred Shading, Area Lighting (Capsule &amp; LTC RectLight),
/// GBuffer Decoding, Shading Models (DefaultLit, Subsurface, PreintegratedSkin, ClearCoat, SubsurfaceProfile,
/// TwoSidedFoliage, Hair, Cloth, Eye, SingleLayerWater, ThinTranslucent), Multi-Bounce AO &amp; GTAO/Contact Shadows,
/// SkyLight IBL (PreintegratedGF + Off-Specular Peak + Specular Occlusion), Exponential Height Fog, SkyAtmosphere,
/// and ACES Filmic Tonemapper (FilmToneMap + BlueCorrection + ExpandGamut + LinearToSrgbBranchless).
/// Source references: Read-only/UnrealEngine-5.2.0-release/Engine/Shaders/Private/*.ush / *.usf
/// </summary>
internal static class Unreal52DeferredShader
{
    public const string HlslSource = """
        #define PI 3.1415926535897932f

        // =============================================================================
        // ShadingCommon.ush: Shading Model IDs & SelectiveOutputMask flags
        // =============================================================================
        #define SHADINGMODELID_UNLIT                0
        #define SHADINGMODELID_DEFAULT_LIT          1
        #define SHADINGMODELID_SUBSURFACE           2
        #define SHADINGMODELID_PREINTEGRATED_SKIN   3
        #define SHADINGMODELID_CLEAR_COAT           4
        #define SHADINGMODELID_SUBSURFACE_PROFILE   5
        #define SHADINGMODELID_TWOSIDED_FOLIAGE     6
        #define SHADINGMODELID_HAIR                 7
        #define SHADINGMODELID_CLOTH                8
        #define SHADINGMODELID_EYE                  9
        #define SHADINGMODELID_SINGLELAYERWATER     10
        #define SHADINGMODELID_THIN_TRANSLUCENT     11
        #define SHADINGMODELID_STRATA               12
        #define SHADINGMODELID_MASK                 0xF

        #define HAS_ANISOTROPY_MASK                 (1 << 4)
        #define SKIP_PRECSHADOW_MASK                (1 << 5)
        #define ZERO_PRECSHADOW_MASK                (1 << 6)
        #define SKIP_VELOCITY_MASK                  (1 << 7)

        #define LIGHT_TYPE_DIRECTIONAL              0
        #define LIGHT_TYPE_POINT                    1
        #define LIGHT_TYPE_SPOT                     2
        #define LIGHT_TYPE_RECT                     3

        struct GpuLocalLight
        {
            float4 PositionAndInvRadius;      // xyz = TranslatedWorldPosition (cm), w = InvRadius (1/cm)
            float4 ColorAndFalloff;           // xyz = Color * Brightness, w = FalloffExponent (0 = InverseSquared)
            float4 DirectionAndSourceRadius;  // xyz = LightData.Direction (-GetDirection()), w = SourceRadius (cm, or HalfWidth for Rect)
            float4 TangentAndSourceLength;    // xyz = LightData.Tangent, w = SourceLength (cm, or HalfHeight for Rect)
            float4 SpotAnglesAndParams;       // x = CosOuterCone (or BarnCosAngle), y = InvCosConeDiff (or BarnLength cm), z = SoftSourceRadius (cm), w = LightType (1=Point,2=Spot,3=Rect)
        };

        Texture2D<float4> SceneColor      : register(t0);
        Texture2D<float4> GBufferA        : register(t1);
        Texture2D<float4> GBufferB        : register(t2);
        Texture2D<float4> GBufferC        : register(t3);
        Texture2D<float>  DepthTex        : register(t4);
        StructuredBuffer<GpuLocalLight> LocalLights : register(t5);
        Texture2D<float2> SelectionMask   : register(t6);
        Texture2D<float4> GBufferD        : register(t7);
        Texture2D<float4> GBufferE        : register(t8);
        Texture2D<float2> PreIntegratedGF : register(t9);
        Texture2D<float4> LTCMatTexture   : register(t10);
        Texture2D<float2> LTCAmpTexture   : register(t11);
        TextureCube<float4> SkyCubemap    : register(t12);

        SamplerState LinearClampSampler   : register(s0);

        cbuffer Params : register(b0)
        {
            int Mode;
            float Exposure;
            int NumLocalLights;
            float SkyIntensity;
            float4 LightDirAndIntensity;     // xyz = DirectionToLight, w = SunIntensity (pre-scaled by PI)
            float4 LightColorAndSourceRadius;// xyz = DirectionalLightColor, w = SinHalfSourceAngle
            float4 SkyUpperColor;            // xyz = SkyUpperHemisphere, w = HasSkyCubemap (>0.5 if valid, else 0)
            float4 SkyLowerColor;            // xyz = SkyLowerHemisphere, w = SkyCubemapMaxMip
            float4 Background;               // rgb = FallbackBackground, w = HasSelection (1.0 or 0.0)
            float4 CameraPos;                // xyz = CameraWorldPos (cm), w = IsMaterialPreview (1.0 if sphere preview)
            float4 SvPosRow0;
            float4 SvPosRow1;
            float4 SvPosRow2;
            float4 SvPosRow3;
            float4 FogParams0;               // x = Density, y = HeightFalloff, z = FogHeightCm, w = StartDistanceCm
            float4 FogParams1;               // xyz = InscatteringColor, w = MaxOpacity (negative if fog disabled)
            float4 FogParams2;               // xyz = DirectionalInscatteringColor, w = DirectionalExponent
            float4 ViewportSizeAndInv;       // xy = Width, Height, zw = 1/Width, 1/Height
            float4 WorldRenderFlags;         // x = SkyDome, y = Fog, z = TiledLight, w = SsaoHalfRes
            uint4  TileParams;               // x = TileCountX, y = 16, z = UseTiled, w = 0
        };

        // Directional light cascaded shadow maps (NativeShadowMaps.cs)
        cbuffer ShadowParams : register(b1)
        {
            float4 ShadowMatrix[16];          // 4 cascades x 4 rows, world (cm) -> light clip
            float4 CascadeSplitFar;           // view depth of each cascade's far end
            float4 ShadowInfo;                // x = NumCascades (0 = shadows off), y = 1/Resolution, z = transition fraction, w = fade fraction
            float4 ShadowCameraForward;       // xyz = camera forward, w = shadow distance
            float4 CascadeTexelWorld;         // world size of one shadow texel per cascade
            float4 CascadeDepthBias;          // constant clip-space bias per cascade
            float4 ShadowLightDirection;      // xyz = direction to light
        };
        Texture2DArray<float> ShadowDepths : register(t13);
        SamplerComparisonState ShadowCmpSampler : register(s1);

        // Post-process inputs for the tonemapper (NativePostProcess.cs)
        cbuffer PostParams : register(b2)
        {
            float4 PostSettings;              // x = auto exposure, y = bloom intensity, z = vignette intensity, w = manual exposure
        };
        Texture2D<float4> PostBloom       : register(t14);
        StructuredBuffer<float4> EyeAdapt : register(t15);
        StructuredBuffer<uint> TileLightList : register(t16);
        Texture2D<float>  SsaoTex         : register(t17);

        struct V
        {
            float4 Pos : SV_Position;
        };

        V VSMain(uint id : SV_VertexID)
        {
            V o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.Pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        // Volumetric Fog Volume (BP_FogVolume)
        cbuffer FogVolumeParams : register(b3)
        {
            float4 FogVolColor;      // rgb = color, a = density
            float4 FogVolParams;     // xyz = position, w = radius
        };
        float4 PSVolumetricFog(V i) : SV_Target
        {
            float3 worldPos = i.Pos.xyz; // Need proper world pos calculation
            float dist = length(worldPos - FogVolParams.xyz);
            float fog = saturate(1.0 - dist / FogVolParams.w) * FogVolColor.a;
            return float4(FogVolColor.rgb * fog, fog);
        }

        // =============================================================================
        // Common.ush & FastMath.ush helpers
        // =============================================================================
        float Square(float x) { return x * x; }
        float2 Square(float2 x) { return x * x; }
        float3 Square(float3 x) { return x * x; }
        float Pow2(float x) { return x * x; }
        float Pow4(float x) { float x2 = x * x; return x2 * x2; }
        float Pow5(float x) { float x2 = x * x; return x2 * x2 * x; }
        float3 Pow5(float3 x) { float3 x2 = x * x; return x2 * x2 * x; }
        float Luminance(float3 LinearColor) { return dot(LinearColor, float3(0.3, 0.59, 0.11)); }
        float max3(float a, float b, float c) { return max(a, max(b, c)); }

        float3 SafeNormalize(float3 v, float3 fallback)
        {
            float lenSq = dot(v, v);
            return lenSq > 1e-8f ? v * rsqrt(lenSq) : fallback;
        }

        float DielectricSpecularToF0(float Specular)
        {
            return 0.08f * Specular;
        }

        float3 ComputeF0(float Specular, float3 BaseColor, float Metallic)
        {
            return lerp(DielectricSpecularToF0(Specular).xxx, BaseColor, Metallic.xxx);
        }

        // =============================================================================
        // OctahedralCommon.ush
        // =============================================================================
        float2 UnitVectorToOctahedron(float3 N)
        {
            N.xy /= dot(1.0f.xxx, abs(N));
            if (N.z <= 0.0f)
            {
                N.xy = (1.0f - abs(N.yx)) * (N.xy >= 0.0f ? float2(1.0f, 1.0f) : float2(-1.0f, -1.0f));
            }
            return N.xy;
        }

        float3 OctahedronToUnitVector(float2 Oct)
        {
            float3 N = float3(Oct, 1.0f - dot(1.0f.xx, abs(Oct)));
            float t = max(-N.z, 0.0f);
            N.xy += N.xy >= 0.0f ? float2(-t, -t) : float2(t, t);
            return SafeNormalize(N, float3(0, 0, 1));
        }

        // =============================================================================
        // DeferredShadingCommon.ush: GBuffer Data & Decoding
        // =============================================================================
        struct FGBufferData
        {
            float3 WorldNormal;
            float3 WorldTangent;
            float3 DiffuseColor;
            float3 SpecularColor;
            float3 BaseColor;
            float  Metallic;
            float  Specular;
            float4 CustomData;
            float  Roughness;
            float  Anisotropy;
            float  GBufferAO;
            uint   ShadingModelID;
            uint   SelectiveOutputMask;
            float  PerObjectGBufferData;
            float3 StoredBaseColor;
            float  StoredSpecular;
            float  StoredMetallic;
        };

        uint DecodeShadingModelId(float InPackedChannel)
        {
            return ((uint) round(InPackedChannel * 255.0f)) & SHADINGMODELID_MASK;
        }

        uint DecodeSelectiveOutputMask(float InPackedChannel)
        {
            return ((uint) round(InPackedChannel * 255.0f)) & ~SHADINGMODELID_MASK;
        }

        bool HasCustomGBufferData(uint ShadingModelID)
        {
            return ShadingModelID == SHADINGMODELID_SUBSURFACE
                || ShadingModelID == SHADINGMODELID_PREINTEGRATED_SKIN
                || ShadingModelID == SHADINGMODELID_CLEAR_COAT
                || ShadingModelID == SHADINGMODELID_SUBSURFACE_PROFILE
                || ShadingModelID == SHADINGMODELID_TWOSIDED_FOLIAGE
                || ShadingModelID == SHADINGMODELID_HAIR
                || ShadingModelID == SHADINGMODELID_CLOTH
                || ShadingModelID == SHADINGMODELID_EYE;
        }

        bool HasAnisotropy(uint SelectiveOutputMask)
        {
            return (SelectiveOutputMask & HAS_ANISOTROPY_MASK) != 0;
        }

        float3 ExtractSubsurfaceColor(FGBufferData BufferData)
        {
            return Square(BufferData.CustomData.rgb);
        }

        float3 UnpackYCoCg(float4 ycocg)
        {
            float scale = 1.0f / (ycocg.z * 31.875f + 1.0f);
            float Co = (ycocg.x - 0.5019608f) * scale;
            float Cg = (ycocg.y - 0.5019608f) * scale;
            float Y  = ycocg.w;
            float R  = Y + Co - Cg;
            float G  = Y + Cg;
            float B  = Y - Co - Cg;
            return saturate(float3(R, G, B));
        }

        // [ Jimenez et al. 2016, "Practical Realtime Strategies for Accurate Indirect Occlusion" ]
        float3 AOMultiBounce(float3 BaseColor, float AO)
        {
            float3 a =  2.0404f * BaseColor - 0.3324f;
            float3 b = -4.7951f * BaseColor + 0.6417f;
            float3 c =  2.7552f * BaseColor + 0.6903f;
            return max(AO.xxx, ((AO * a + b) * AO + c) * AO);
        }

        FGBufferData DecodeGBuffer(float4 a, float4 b, float4 c, float4 d, float4 e)
        {
            FGBufferData GBuffer = (FGBufferData) 0;

            bool isYCoCgRvt = (b.a > 0.45f && b.a < 0.55f);
            uint rawByteA = (uint) round(saturate(b.a) * 255.0f);
            bool isStandardRvt = ((rawByteA & SHADINGMODELID_MASK) == 15);

            if (isYCoCgRvt)
            {
                float2 nXY = a.xy * 2.0f - 1.0f;
                float nZ = sqrt(saturate(1.0f - dot(nXY, nXY)));
                GBuffer.WorldNormal = SafeNormalize(float3(nXY, nZ), float3(0, 0, 1));
                GBuffer.BaseColor = UnpackYCoCg(c);
                GBuffer.Metallic = 0.0f;
                GBuffer.Specular = saturate(b.r);
                GBuffer.Roughness = clamp(b.g, 0.04f, 1.0f);
                GBuffer.GBufferAO = 1.0f;
                GBuffer.ShadingModelID = SHADINGMODELID_DEFAULT_LIT;
                GBuffer.SelectiveOutputMask = 0;
            }
            else if (isStandardRvt)
            {
                float3 packedN = float3(a.xy, b.b);
                GBuffer.WorldNormal = SafeNormalize(packedN * (255.0f / 127.0f) - 1.0f, float3(0, 0, 1));
                GBuffer.BaseColor = saturate(c.rgb);
                GBuffer.Metallic = 0.0f;
                GBuffer.Specular = saturate(b.r);
                GBuffer.Roughness = clamp(b.g, 0.04f, 1.0f);
                GBuffer.GBufferAO = 1.0f;
                GBuffer.ShadingModelID = SHADINGMODELID_DEFAULT_LIT;
                GBuffer.SelectiveOutputMask = 0;
            }
            else
            {
                GBuffer.WorldNormal = SafeNormalize(a.xyz * 2.0f - 1.0f, float3(0, 0, 1));
                GBuffer.PerObjectGBufferData = a.a;
                GBuffer.Metallic = saturate(b.r);
                GBuffer.Specular = saturate(b.g);
                GBuffer.Roughness = clamp(b.b, 0.02f, 1.0f);
                GBuffer.ShadingModelID = DecodeShadingModelId(b.a);
                GBuffer.SelectiveOutputMask = DecodeSelectiveOutputMask(b.a);

                if (GBuffer.ShadingModelID == 0 && dot(c.rgb, c.rgb) > 1e-4f)
                {
                    // Fallback if a custom shader wrote 0 to b.a but wrote valid normal and basecolor
                    GBuffer.ShadingModelID = SHADINGMODELID_DEFAULT_LIT;
                }

                GBuffer.BaseColor = saturate(c.rgb);
                GBuffer.GBufferAO = c.a > 1e-4f ? saturate(c.a) : 1.0f;

                // Determine where CustomData and WorldTangent live across MRT4 (d) and MRT5 (e)
                bool bAniso = HasAnisotropy(GBuffer.SelectiveOutputMask);
                float4 customRaw = bAniso ? e : (dot(e, e) > 1e-5f ? e : d);
                GBuffer.CustomData = HasCustomGBufferData(GBuffer.ShadingModelID) ? saturate(customRaw) : 0.0f.xxxx;

                if (bAniso && dot(d.xyz, d.xyz) > 1e-4f)
                {
                    GBuffer.WorldTangent = SafeNormalize(d.xyz * 2.0f - 1.0f, float3(1, 0, 0));
                    GBuffer.Anisotropy = clamp(d.a * 2.0f - 1.0f, -1.0f, 1.0f);
                }
            }

            GBuffer.StoredBaseColor = GBuffer.BaseColor;
            GBuffer.StoredMetallic  = GBuffer.Metallic;
            GBuffer.StoredSpecular  = GBuffer.Specular;

            if (GBuffer.ShadingModelID == SHADINGMODELID_EYE)
            {
                GBuffer.Metallic = 0.0f;
            }

            GBuffer.SpecularColor = ComputeF0(GBuffer.Specular, GBuffer.BaseColor, GBuffer.Metallic);
            GBuffer.DiffuseColor  = GBuffer.BaseColor - GBuffer.BaseColor * GBuffer.Metallic;

            return GBuffer;
        }

        // =============================================================================
        // BRDF.ush & ShadingEnergyConservation.ush
        // =============================================================================
        struct BxDFContext
        {
            float NoV;
            float NoL;
            float VoL;
            float NoH;
            float VoH;
            float XoV;
            float XoL;
            float XoH;
            float YoV;
            float YoL;
            float YoH;
        };

        void Init(inout BxDFContext Context, float3 N, float3 V, float3 L)
        {
            Context.NoL = dot(N, L);
            Context.NoV = dot(N, V);
            Context.VoL = dot(V, L);
            float InvLenH = rsqrt(max(2.0f + 2.0f * Context.VoL, 1e-6f));
            Context.NoH = saturate((Context.NoL + Context.NoV) * InvLenH);
            Context.VoH = saturate(InvLenH + InvLenH * Context.VoL);
            Context.XoV = 0.0f;
            Context.XoL = 0.0f;
            Context.XoH = 0.0f;
            Context.YoV = 0.0f;
            Context.YoL = 0.0f;
            Context.YoH = 0.0f;
        }

        void InitAniso(inout BxDFContext Context, float3 N, float3 X, float3 Y, float3 V, float3 L)
        {
            Context.NoL = dot(N, L);
            Context.NoV = dot(N, V);
            Context.VoL = dot(V, L);
            float InvLenH = rsqrt(max(2.0f + 2.0f * Context.VoL, 1e-6f));
            Context.NoH = saturate((Context.NoL + Context.NoV) * InvLenH);
            Context.VoH = saturate(InvLenH + InvLenH * Context.VoL);
            Context.XoV = dot(X, V);
            Context.XoL = dot(X, L);
            Context.XoH = (Context.XoL + Context.XoV) * InvLenH;
            Context.YoV = dot(Y, V);
            Context.YoL = dot(Y, L);
            Context.YoH = (Context.YoL + Context.YoV) * InvLenH;
        }

        // [ de Carpentier 2017, "Decima Engine: Advances in Lighting and AA" ]
        void SphereMaxNoH(inout BxDFContext Context, float SinAlpha, bool bNewtonIteration)
        {
            if (SinAlpha > 0.0f)
            {
                float CosAlpha = sqrt(saturate(1.0f - Pow2(SinAlpha)));
                float RoL = 2.0f * Context.NoL * Context.NoV - Context.VoL;
                if (RoL >= CosAlpha)
                {
                    Context.NoH = 1.0f;
                    Context.XoH = 0.0f;
                    Context.YoH = 0.0f;
                    Context.VoH = abs(Context.NoV);
                }
                else
                {
                    float rInvLengthT = SinAlpha * rsqrt(max(1.0f - RoL * RoL, 1e-6f));
                    float NoTr = rInvLengthT * (Context.NoV - RoL * Context.NoL);
                    float VoTr = rInvLengthT * (2.0f * Context.NoV * Context.NoV - 1.0f - RoL * Context.VoL);

                    if (bNewtonIteration)
                    {
                        float NxLoV = sqrt(saturate(1.0f - Pow2(Context.NoL) - Pow2(Context.NoV) - Pow2(Context.VoL) + 2.0f * Context.NoL * Context.NoV * Context.VoL));
                        float NoBr = rInvLengthT * NxLoV;
                        float VoBr = rInvLengthT * NxLoV * 2.0f * Context.NoV;
                        float NoLVTr = Context.NoL * CosAlpha + Context.NoV + NoTr;
                        float VoLVTr = Context.VoL * CosAlpha + 1.0f + VoTr;
                        float p = NoBr * VoLVTr;
                        float q = NoLVTr * VoLVTr;
                        float s = VoBr * NoLVTr;
                        float xNum = q * (-0.5f * p + 0.25f * VoBr * NoLVTr);
                        float xDenom = p * p + s * (s - 2.0f * p) + NoLVTr * ((Context.NoL * CosAlpha + Context.NoV) * Pow2(VoLVTr) + q * (-0.5f * (VoLVTr + Context.VoL * CosAlpha) - 0.5f));
                        float denomSq = Pow2(xDenom) + Pow2(xNum);
                        if (denomSq > 1e-8f)
                        {
                            float TwoX1 = 2.0f * xNum / denomSq;
                            float SinTheta = TwoX1 * xDenom;
                            float CosTheta = 1.0f - TwoX1 * xNum;
                            NoTr = CosTheta * NoTr + SinTheta * NoBr;
                            VoTr = CosTheta * VoTr + SinTheta * VoBr;
                        }
                    }

                    Context.NoL = Context.NoL * CosAlpha + NoTr;
                    Context.VoL = Context.VoL * CosAlpha + VoTr;
                    float InvLenH = rsqrt(max(2.0f + 2.0f * Context.VoL, 1e-6f));
                    Context.NoH = saturate((Context.NoL + Context.NoV) * InvLenH);
                    Context.VoH = saturate(InvLenH + InvLenH * Context.VoL);
                }
            }
        }

        float3 Diffuse_Lambert(float3 DiffuseColor)
        {
            return DiffuseColor * (1.0f / PI);
        }

        float3 Diffuse_Burley(float3 DiffuseColor, float Roughness, float NoV, float NoL, float VoH)
        {
            float FD90 = 0.5f + 2.0f * VoH * VoH * Roughness;
            float FdV = 1.0f + (FD90 - 1.0f) * Pow5(1.0f - NoV);
            float FdL = 1.0f + (FD90 - 1.0f) * Pow5(1.0f - NoL);
            return DiffuseColor * ((1.0f / PI) * FdV * FdL);
        }

        float D_GGX(float a2, float NoH)
        {
            float d = (NoH * a2 - NoH) * NoH + 1.0f;
            return a2 / max(PI * d * d, 1e-7f);
        }

        float D_GGXaniso(float ax, float ay, float NoH, float XoH, float YoH)
        {
            float a2 = ax * ay;
            float3 V = float3(ay * XoH, ax * YoH, a2 * NoH);
            float S = max(dot(V, V), 1e-7f);
            return (1.0f / PI) * a2 * Square(a2 / S);
        }

        float Vis_SmithJointApprox(float a2, float NoV, float NoL)
        {
            float a = sqrt(a2);
            float Vis_SmithV = NoL * (NoV * (1.0f - a) + a);
            float Vis_SmithL = NoV * (NoL * (1.0f - a) + a);
            return 0.5f * rcp(max(Vis_SmithV + Vis_SmithL, 1e-6f));
        }

        float Vis_SmithJointAniso(float ax, float ay, float NoV, float NoL, float XoV, float XoL, float YoV, float YoL)
        {
            float Vis_SmithV = NoL * length(float3(ax * XoV, ay * YoV, NoV));
            float Vis_SmithL = NoV * length(float3(ax * XoL, ay * YoL, NoL));
            return 0.5f * rcp(max(Vis_SmithV + Vis_SmithL, 1e-6f));
        }

        float3 F_Schlick(float3 SpecularColor, float VoH)
        {
            float Fc = Pow5(1.0f - VoH);
            return saturate(50.0f * SpecularColor.g) * Fc + (1.0f - Fc) * SpecularColor;
        }

        float D_InvGGX(float a2, float NoH)
        {
            float A = 4.0f;
            float d = (NoH - a2 * NoH) * NoH + a2;
            return rcp(PI * (1.0f + A * a2)) * (1.0f + 4.0f * a2 * a2 / max(d * d, 1e-6f));
        }

        float Vis_Cloth(float NoV, float NoL)
        {
            return rcp(max(4.0f * (NoL + NoV - NoL * NoV), 1e-5f));
        }

        void GetAnisotropicRoughness(float Alpha, float Anisotropy, out float ax, out float ay)
        {
            ax = max(Alpha * (1.0f + Anisotropy), 0.001f);
            ay = max(Alpha * (1.0f - Anisotropy), 0.001f);
        }

        float3 EnvBRDF(float3 SpecularColor, float Roughness, float NoV)
        {
            float2 AB = PreIntegratedGF.SampleLevel(LinearClampSampler, float2(NoV, Roughness), 0).rg;
            float3 GF = SpecularColor * AB.x + saturate(50.0f * SpecularColor.g) * AB.y;
            return GF;
        }

        // ShadingEnergyConservation.ush: Multiple scattering energy compensation & preservation
        struct FBxDFEnergyTerms
        {
            float3 W;
            float3 E;
        };

        FBxDFEnergyTerms ComputeGGXSpecEnergyTerms(float Roughness, float NoV, float3 F0)
        {
            float2 AB = PreIntegratedGF.SampleLevel(LinearClampSampler, float2(NoV, Roughness), 0).rg;
            float F90 = saturate(50.0f * F0.g);
            float dirAlbedo = max(AB.x + AB.y, 1e-4f);
            FBxDFEnergyTerms Out;
            Out.W = 1.0f + F0 * ((1.0f - dirAlbedo) / dirAlbedo);
            Out.E = Out.W * (AB.x * F0 + AB.y * F90.xxx);
            return Out;
        }

        float ComputeEnergyPreservation(FBxDFEnergyTerms EnergyTerms)
        {
            return saturate(1.0f - Luminance(EnergyTerms.E));
        }

        float3 ComputeEnergyConservation(FBxDFEnergyTerms EnergyTerms)
        {
            return EnergyTerms.W;
        }

        // =============================================================================
        // CapsuleLight.ush, RectLight.ush, AreaLightCommon.ush
        // =============================================================================
        struct FRect
        {
            float3   Origin;
            float3x3 Axis;
            float2   Extent;
            float2   FullExtent;
            float2   Offset;
        };

        struct FAreaLight
        {
            float  SphereSinAlpha;
            float  SphereSinAlphaSoft;
            float  LineCosSubtended;
            float3 FalloffColor;
            FRect  Rect;
            bool   bIsRect;
        };

        struct FCapsuleLight
        {
            float3 LightPos[2];
            float  Length;
            float  Radius;
            float  SoftRadius;
            float  DistBiasSqr;
        };

        float3 ClosestPointLineToRay(float3 Line0, float3 Line1, float Length, float3 R)
        {
            float3 Line01 = Line1 - Line0;
            float A = Square(Length);
            float B = dot(R, Line01);
            float denom = A - B * B;
            float t = abs(denom) > 1e-6f ? saturate(dot(Line0, B * R - Line01) / denom) : 0.0f;
            return Line0 + t * Line01;
        }

        float3 LineIrradiance(float3 N, float3 Line0, float3 Line1, float DistanceBiasSqr, out float CosSubtended, out float BaseIrradiance, out float NoL)
        {
            float LengthSqr0 = max(dot(Line0, Line0), 1e-4f);
            float LengthSqr1 = max(dot(Line1, Line1), 1e-4f);
            float InvLength0 = rsqrt(LengthSqr0);
            float InvLength1 = rsqrt(LengthSqr1);
            float InvLength01 = InvLength0 * InvLength1;

            CosSubtended = dot(Line0, Line1) * InvLength01;
            BaseIrradiance = InvLength01 / max(CosSubtended * 0.5f + 0.5f + DistanceBiasSqr * InvLength01, 1e-5f);
            NoL = 0.5f * (dot(N, Line0) * InvLength0 + dot(N, Line1) * InvLength1);

            return (BaseIrradiance * 0.5f) * (Line0 * InvLength0 + Line1 * InvLength1);
        }

        float SphereHorizonCosWrap(float NoL, float SinAlphaSqr)
        {
            float SinAlpha = sqrt(saturate(SinAlphaSqr));
            if (NoL < SinAlpha)
            {
                NoL = max(NoL, -SinAlpha);
                NoL = Pow2(SinAlpha + NoL) / max(4.0f * SinAlpha, 1e-5f);
            }
            return NoL;
        }

        FRect GetRect(
            float3 ToLight,
            float3 LightDataDirection,
            float3 LightDataTangent,
            float LightDataSourceRadius,
            float LightDataSourceLength,
            float LightDataRectLightBarnCosAngle,
            float LightDataRectLightBarnLength,
            bool bComputeVisibleRect)
        {
            FRect Rect;
            Rect.Origin = ToLight;
            Rect.Axis[1] = LightDataTangent;
            Rect.Axis[2] = LightDataDirection;
            Rect.Axis[0] = cross(Rect.Axis[1], Rect.Axis[2]);
            Rect.Extent = float2(LightDataSourceRadius, LightDataSourceLength);
            Rect.FullExtent = Rect.Extent;
            Rect.Offset = 0.0f;

            if (bComputeVisibleRect && LightDataRectLightBarnCosAngle > 0.035f)
            {
                const float3 LightdPdv = -Rect.Axis[1];
                const float3 LightdPdu = -Rect.Axis[0];
                const float2 LightExtent = float2(LightDataSourceRadius, LightDataSourceLength);
                const float BarnLength = LightDataRectLightBarnLength;

                float3 S_Light = mul(Rect.Axis, ToLight);

                const float CosTheta = LightDataRectLightBarnCosAngle;
                const float SinTheta = sqrt(saturate(1.0f - CosTheta * CosTheta));
                const float BarnDepth = min(S_Light.z, CosTheta * BarnLength);
                const float S_ratio = BarnDepth / max(0.0001f, CosTheta * BarnLength);
                const float D_B = SinTheta * BarnLength * S_ratio;

                const float2 SignS = sign(S_Light.xy);
                S_Light.xy = SignS * max(abs(S_Light.xy), LightExtent + D_B.xx);

                const float3 C = float3(SignS * (LightExtent + D_B.xx), BarnDepth);
                const float3 SProj = S_Light - C;
                const float CosEta = max(SProj.z, 0.001f);
                const float2 TanEta = abs(SProj.xy) / CosEta;
                const float2 D_S = BarnDepth * TanEta;

                const float2 MinXY = clamp(-LightExtent + (D_S - D_B.xx) * max(0.0f, -SignS), -LightExtent, LightExtent);
                const float2 MaxXY = clamp( LightExtent - (D_S - D_B.xx) * max(0.0f,  SignS), -LightExtent, LightExtent);
                const float2 RectOffset = 0.5f * (MinXY + MaxXY);

                Rect.Extent = 0.5f * (MaxXY - MinXY);
                Rect.Origin = Rect.Origin + LightdPdu * RectOffset.x + LightdPdv * RectOffset.y;
                Rect.Offset = -RectOffset;
                Rect.FullExtent = LightExtent;
            }

            return Rect;
        }

        // RectLight.ush: Optimized Lambert
        float3 RectIrradianceLambert(float3 N, FRect Rect, out float BaseIrradiance, out float NoL)
        {
            float3 LocalPosition;
            LocalPosition.x = dot(Rect.Axis[0], Rect.Origin);
            LocalPosition.y = dot(Rect.Axis[1], Rect.Origin);
            LocalPosition.z = dot(Rect.Axis[2], Rect.Origin);

            float x0 = LocalPosition.x - Rect.Extent.x;
            float x1 = LocalPosition.x + Rect.Extent.x;
            float y0 = LocalPosition.y - Rect.Extent.y;
            float y1 = LocalPosition.y + Rect.Extent.y;
            float z0 = LocalPosition.z;
            float z0Sqr = z0 * z0;

            float3 v0 = float3(x0, y0, z0);
            float3 v1 = float3(x1, y0, z0);
            float3 v2 = float3(x1, y1, z0);
            float3 v3 = float3(x0, y1, z0);

            float3 L0 = v0 * rsqrt(max(dot(v0.xy, v0.xy) + z0Sqr, 1e-6f));
            float3 L1 = v1 * rsqrt(max(dot(v1.xy, v1.xy) + z0Sqr, 1e-6f));
            float3 L2 = v2 * rsqrt(max(dot(v2.xy, v2.xy) + z0Sqr, 1e-6f));
            float3 L3 = v3 * rsqrt(max(dot(v3.xy, v3.xy) + z0Sqr, 1e-6f));

            float c01 = dot(L0, L1);
            float c12 = dot(L1, L2);
            float c23 = dot(L2, L3);
            float c30 = dot(L3, L0);

            float w01 = (1.5708f - 0.175f * c01) * rsqrt(max(c01 + 1.0f, 1e-5f));
            float w12 = (1.5708f - 0.175f * c12) * rsqrt(max(c12 + 1.0f, 1e-5f));
            float w23 = (1.5708f - 0.175f * c23) * rsqrt(max(c23 + 1.0f, 1e-5f));
            float w30 = (1.5708f - 0.175f * c30) * rsqrt(max(c30 + 1.0f, 1e-5f));

            float3 L = cross(L1, -w01 * L0 + w12 * L2);
            L += cross(L3, w30 * L0 - w23 * L2);

            L = L.x * Rect.Axis[0] + L.y * Rect.Axis[1] + L.z * Rect.Axis[2];

            float LengthSqr = dot(L, L);
            float InvLength = rsqrt(max(LengthSqr, 1e-8f));
            float Length = LengthSqr * InvLength;

            L *= InvLength;
            BaseIrradiance = 0.5f * Length;

            float SinAlphaSqr = BaseIrradiance * (1.0f / PI);
            NoL = SphereHorizonCosWrap(dot(N, L), SinAlphaSqr);

            return L;
        }

        float IntegrateEdge(float3 L0, float3 L1)
        {
            float c01 = dot(L0, L1);
            float absC = abs(c01);
            float w01 = (0.8543985f + (0.4965155f + 0.0145206f * absC) * absC) /
                        (3.4175940f + (4.1616724f + absC) * absC);
            w01 = c01 > 0.0f ? w01 : 0.5f * rsqrt(max(1.0f - c01 * c01, 1e-6f)) - w01;
            return w01;
        }

        float3 PolygonIrradiance(float3 Poly[4])
        {
            float3 L0 = SafeNormalize(Poly[0], float3(0, 0, 1));
            float3 L1 = SafeNormalize(Poly[1], float3(0, 0, 1));
            float3 L2 = SafeNormalize(Poly[2], float3(0, 0, 1));
            float3 L3 = SafeNormalize(Poly[3], float3(0, 0, 1));

            float w01 = IntegrateEdge(L0, L1);
            float w12 = IntegrateEdge(L1, L2);
            float w23 = IntegrateEdge(L2, L3);
            float w30 = IntegrateEdge(L3, L0);

            float3 L = cross(L1, -w01 * L0 + w12 * L2);
            L += cross(L3, w30 * L0 - w23 * L2);
            return L;
        }

        // [ Heitz et al. 2016, "Real-Time Polygonal-Light Shading with Linearly Transformed Cosines" ]
        float3 RectGGXApproxLTC(float Roughness, float3 SpecularColor, float3 N, float3 V, FRect Rect)
        {
            if (Rect.Extent.x <= 0.0f || Rect.Extent.y <= 0.0f)
                return 0.0f;

            const float3 F0  = SpecularColor;
            const float3 F90 = saturate(50.0f * SpecularColor);

            float NoV = saturate(abs(dot(N, V)) + 1e-5f);
            float2 UV = float2(Roughness, sqrt(1.0f - NoV));
            UV = UV * (63.0f / 64.0f) + (0.5f / 64.0f);

            float4 LTCMat = LTCMatTexture.SampleLevel(LinearClampSampler, UV, 0);
            float2 LTCAmp = LTCAmpTexture.SampleLevel(LinearClampSampler, UV, 0).rg;

            float3x3 LTC = {
                float3(LTCMat.x, 0.0f, LTCMat.z),
                float3(    0.0f, 1.0f,     0.0f),
                float3(LTCMat.y, 0.0f, LTCMat.w)
            };

            float3 T1 = SafeNormalize(V - N * dot(N, V), float3(1, 0, 0));
            float3 T2 = cross(N, T1);
            float3x3 TangentBasis = float3x3(T1, T2, N);

            LTC = mul(LTC, TangentBasis);

            float3 Poly[4];
            Poly[0] = mul(LTC, Rect.Origin - Rect.Axis[0] * Rect.Extent.x - Rect.Axis[1] * Rect.Extent.y);
            Poly[1] = mul(LTC, Rect.Origin + Rect.Axis[0] * Rect.Extent.x - Rect.Axis[1] * Rect.Extent.y);
            Poly[2] = mul(LTC, Rect.Origin + Rect.Axis[0] * Rect.Extent.x + Rect.Axis[1] * Rect.Extent.y);
            Poly[3] = mul(LTC, Rect.Origin - Rect.Axis[0] * Rect.Extent.x + Rect.Axis[1] * Rect.Extent.y);

            float3 L = PolygonIrradiance(Poly);

            float LengthSqr = dot(L, L);
            float InvLength = rsqrt(max(LengthSqr, 1e-8f));
            float Length = LengthSqr * InvLength;
            L *= InvLength;

            float SinAlphaSqr = Length;
            float NoL = SphereHorizonCosWrap(L.z, SinAlphaSqr);
            float Irradiance = max(SinAlphaSqr * NoL, 0.0f);

            float3 IrradianceScale = F90 * LTCAmp.y + (LTCAmp.x - LTCAmp.y) * F0;
            return Irradiance * IrradianceScale;
        }

        // =============================================================================
        // ShadingModels.ush: EnergyNormalization, SpecularGGX, & BxDF Shading Models
        // =============================================================================
        float New_a2(float a2, float SinAlpha, float VoH)
        {
            return a2 + 0.25f * SinAlpha * (3.0f * sqrt(a2) + SinAlpha) / (VoH + 0.001f);
        }

        float EnergyNormalization(inout float a2, float VoH, FAreaLight AreaLight)
        {
            if (AreaLight.SphereSinAlphaSoft > 0.0f)
            {
                a2 = saturate(a2 + Pow2(AreaLight.SphereSinAlphaSoft) / (VoH * 3.6f + 0.4f));
            }

            float Sphere_a2 = a2;
            float Energy = 1.0f;
            if (AreaLight.SphereSinAlpha > 0.0f)
            {
                Sphere_a2 = New_a2(a2, AreaLight.SphereSinAlpha, VoH);
                Energy = a2 / max(Sphere_a2, 1e-6f);
            }

            if (AreaLight.LineCosSubtended < 1.0f)
            {
                float LineCosTwoAlpha = AreaLight.LineCosSubtended;
                float LineTanAlpha = sqrt(max(1.0001f - LineCosTwoAlpha, 0.0f) / max(1.0f + LineCosTwoAlpha, 1e-4f));
                float Line_a2 = New_a2(Sphere_a2, LineTanAlpha, VoH);
                Energy *= sqrt(Sphere_a2 / max(Line_a2, 1e-6f));
            }

            return Energy;
        }

        float3 SpecularGGXAniso(float Roughness, float Anisotropy, float3 SpecularColor, BxDFContext Context, float NoL, FAreaLight AreaLight)
        {
            float Alpha = Roughness * Roughness;
            float a2 = Alpha * Alpha;

            FAreaLight Punctual = AreaLight;
            Punctual.SphereSinAlpha = 0.0f;
            Punctual.SphereSinAlphaSoft = 0.0f;
            Punctual.LineCosSubtended = 1.0f;
            Punctual.bIsRect = false;

            float Energy = EnergyNormalization(a2, Context.VoH, Punctual);

            float ax = 0.0f;
            float ay = 0.0f;
            GetAnisotropicRoughness(Alpha, Anisotropy, ax, ay);

            float D = D_GGXaniso(ax, ay, Context.NoH, Context.XoH, Context.YoH) * Energy;
            float Vis = Vis_SmithJointAniso(ax, ay, Context.NoV, NoL, Context.XoV, Context.XoL, Context.YoV, Context.YoL);
            float3 F = F_Schlick(SpecularColor, Context.VoH);

            return (D * Vis) * F;
        }

        float3 SpecularGGX(float Roughness, float3 SpecularColor, BxDFContext Context, float NoL, FAreaLight AreaLight)
        {
            float a2 = Pow4(Roughness);
            float Energy = EnergyNormalization(a2, Context.VoH, AreaLight);

            float D = D_GGX(a2, Context.NoH) * Energy;
            float Vis = Vis_SmithJointApprox(a2, Context.NoV, NoL);
            float3 F = F_Schlick(SpecularColor, Context.VoH);

            return (D * Vis) * F;
        }

        struct FDirectLighting
        {
            float3 Diffuse;
            float3 Specular;
            float3 Transmission;
        };

        FDirectLighting DefaultLitBxDF(FGBufferData GBuffer, float3 N, float3 V, float3 L, float Falloff, float NoL, FAreaLight AreaLight)
        {
            BxDFContext Context;
            FDirectLighting Lighting = (FDirectLighting) 0;

            bool bHasAnisotropy = HasAnisotropy(GBuffer.SelectiveOutputMask);
            if (bHasAnisotropy)
            {
                float3 X = GBuffer.WorldTangent;
                float3 Y = SafeNormalize(cross(N, X), float3(0, 1, 0));
                InitAniso(Context, N, X, Y, V, L);
            }
            else
            {
                Init(Context, N, V, L);
                SphereMaxNoH(Context, AreaLight.SphereSinAlpha, true);
            }

            Context.NoV = saturate(abs(Context.NoV) + 1e-5f);

            Lighting.Diffuse = AreaLight.FalloffColor * (Falloff * NoL) * Diffuse_Lambert(GBuffer.DiffuseColor);

            if (bHasAnisotropy)
            {
                Lighting.Specular = AreaLight.FalloffColor * (Falloff * NoL) * SpecularGGXAniso(GBuffer.Roughness, GBuffer.Anisotropy, GBuffer.SpecularColor, Context, NoL, AreaLight);
            }
            else if (AreaLight.bIsRect)
            {
                Lighting.Specular = AreaLight.FalloffColor * RectGGXApproxLTC(GBuffer.Roughness, GBuffer.SpecularColor, N, V, AreaLight.Rect);
            }
            else
            {
                Lighting.Specular = AreaLight.FalloffColor * (Falloff * NoL) * SpecularGGX(GBuffer.Roughness, GBuffer.SpecularColor, Context, NoL, AreaLight);
            }

            FBxDFEnergyTerms EnergyTerms = ComputeGGXSpecEnergyTerms(GBuffer.Roughness, Context.NoV, GBuffer.SpecularColor);
            Lighting.Diffuse  *= ComputeEnergyPreservation(EnergyTerms);
            Lighting.Specular *= ComputeEnergyConservation(EnergyTerms);
            Lighting.Transmission = 0.0f;
            return Lighting;
        }

        float RefractBlendClearCoatApprox(float VoH)
        {
            return (0.63f - 0.22f * VoH) * VoH - 0.745f;
        }

        BxDFContext RefractClearCoatContext(BxDFContext Context)
        {
            BxDFContext RefractedContext = Context;
            float Eta = 1.0f / 1.5f;
            float RefractionBlendFactor = RefractBlendClearCoatApprox(Context.VoH);
            float RefractionProjectionTerm = RefractionBlendFactor * Context.NoH;
            RefractedContext.NoV = clamp(Eta * Context.NoV - RefractionProjectionTerm, 0.001f, 1.0f);
            RefractedContext.NoL = clamp(Eta * Context.NoL - RefractionProjectionTerm, 0.001f, 1.0f);
            RefractedContext.VoH = saturate(Eta * Context.VoH - RefractionBlendFactor);
            RefractedContext.VoL = 2.0f * RefractedContext.VoH * RefractedContext.VoH - 1.0f;
            RefractedContext.NoH = Context.NoH;
            return RefractedContext;
        }

        float3 SimpleClearCoatTransmittance(float NoL, float NoV, float Metallic, float3 BaseColor)
        {
            float3 Transmittance = 1.0f;
            float ClearCoatCoverage = Metallic;
            if (ClearCoatCoverage > 0.0f)
            {
                float LayerThickness = 1.0f;
                float ThinDistance = LayerThickness * (rcp(max(NoV, 1e-4f)) + rcp(max(NoL, 1e-4f)));
                float3 TransmittanceColor = Diffuse_Lambert(BaseColor);
                float3 ExtinctionCoefficient = -log(max(TransmittanceColor, 0.0001f)) / (2.0f * LayerThickness);
                float3 OpticalDepth = ExtinctionCoefficient * max(ThinDistance - 2.0f * LayerThickness, 0.0f);
                Transmittance = exp(-OpticalDepth);
                Transmittance = lerp(1.0f.xxx, Transmittance, ClearCoatCoverage);
            }
            return Transmittance;
        }

        FDirectLighting ClearCoatBxDF(FGBufferData GBuffer, float3 N, float3 V, float3 L, float Falloff, float NoL, FAreaLight AreaLight)
        {
            const float ClearCoat          = GBuffer.CustomData.x;
            const float ClearCoatRoughness = max(GBuffer.CustomData.y, 0.02f);

            FDirectLighting Lighting = (FDirectLighting) 0;
            BxDFContext Context;
            Init(Context, N, V, L);

            float SphereSinAlpha = AreaLight.SphereSinAlpha;
            float RoughnessCompensation = 1.0f - Pow2(GBuffer.Roughness);
            float Alpha = ClearCoatRoughness * ClearCoatRoughness;
            RoughnessCompensation = RoughnessCompensation > 0.0f ? (1.0f - Alpha) / RoughnessCompensation : 0.0f;
            AreaLight.SphereSinAlpha = saturate(AreaLight.SphereSinAlpha * RoughnessCompensation);

            SphereMaxNoH(Context, AreaLight.SphereSinAlpha, true);
            Context.NoV = saturate(abs(Context.NoV) + 1e-5f);
            Context.VoH = AreaLight.bIsRect ? Context.NoV : Context.VoH;

            float F0 = 0.04f;
            float Fc = Pow5(1.0f - Context.VoH);
            float F = Fc + (1.0f - Fc) * F0;

            FBxDFEnergyTerms EnergyTermsCoat = ComputeGGXSpecEnergyTerms(ClearCoatRoughness, Context.NoV, F0.xxx);

            if (AreaLight.bIsRect)
            {
                Lighting.Specular = ClearCoat * AreaLight.FalloffColor * RectGGXApproxLTC(ClearCoatRoughness, F0.xxx, N, V, AreaLight.Rect);
            }
            else
            {
                float a2 = Pow2(Alpha);
                float ClearCoatEnergy = EnergyNormalization(a2, Context.VoH, AreaLight);
                float Vis = Vis_SmithJointApprox(a2, Context.NoV, NoL);
                float D = D_GGX(a2, Context.NoH) * ClearCoatEnergy;
                float Fr1 = (D * Vis) * F;
                Lighting.Specular = ClearCoat * AreaLight.FalloffColor * (Falloff * NoL * Fr1);
            }
            Lighting.Specular *= ComputeEnergyConservation(EnergyTermsCoat);

            AreaLight.SphereSinAlpha = SphereSinAlpha;
            float FresnelCoeff = ComputeEnergyPreservation(EnergyTermsCoat);
            FresnelCoeff *= FresnelCoeff;

            // Bottom Layer
            BxDFContext BottomContext = RefractClearCoatContext(Context);
            BottomContext.VoH = AreaLight.bIsRect ? BottomContext.NoV : BottomContext.VoH;

            FBxDFEnergyTerms EnergyTermsBottom = ComputeGGXSpecEnergyTerms(GBuffer.Roughness, BottomContext.NoV, GBuffer.SpecularColor);
            float3 Transmission = SimpleClearCoatTransmittance(BottomContext.NoL, BottomContext.NoV, GBuffer.Metallic, GBuffer.BaseColor);

            float3 DefaultDiffuse = (Falloff * NoL) * AreaLight.FalloffColor * Diffuse_Lambert(GBuffer.DiffuseColor) * ComputeEnergyPreservation(EnergyTermsBottom);
            float3 RefractedDiffuse = FresnelCoeff * Transmission * DefaultDiffuse;
            Lighting.Diffuse = lerp(DefaultDiffuse, RefractedDiffuse, ClearCoat);

            if (AreaLight.bIsRect)
            {
                float3 DefaultSpecular = AreaLight.FalloffColor * RectGGXApproxLTC(GBuffer.Roughness, GBuffer.SpecularColor, N, V, AreaLight.Rect);
                float3 RefractedSpecular = FresnelCoeff * Transmission * DefaultSpecular;
                Lighting.Specular += lerp(DefaultSpecular, RefractedSpecular, ClearCoat);
            }
            else
            {
                float a2 = Pow4(GBuffer.Roughness);
                float Vis2 = Vis_SmithJointApprox(a2, BottomContext.NoV, NoL);
                float D2 = D_GGX(a2, BottomContext.NoH);
                float3 F_Bot = F_Schlick(GBuffer.SpecularColor, BottomContext.VoH);
                float3 F_DefaultLit = F_Schlick(GBuffer.SpecularColor, Context.VoH);
                float Energy = EnergyNormalization(a2, Context.VoH, AreaLight);
                float3 CommonSpecular = (Energy * Falloff * NoL * D2 * Vis2) * AreaLight.FalloffColor;
                Lighting.Specular += CommonSpecular * lerp(F_DefaultLit, FresnelCoeff * Transmission * F_Bot, ClearCoat);
            }

            return Lighting;
        }

        FDirectLighting SubsurfaceProfileBxDF(FGBufferData GBuffer, float3 N, float3 V, float3 L, float Falloff, float NoL, FAreaLight AreaLight)
        {
            BxDFContext Context;
            Init(Context, N, V, L);
            SphereMaxNoH(Context, AreaLight.SphereSinAlpha, true);
            Context.NoV = saturate(abs(Context.NoV) + 1e-5f);

            float Opacity = GBuffer.CustomData.a > 0.0f ? GBuffer.CustomData.a : 0.85f;
            float Roughness = GBuffer.Roughness;
            float Lobe0Roughness = max(Roughness * 0.65f, 0.02f);
            float Lobe1Roughness = min(Roughness * 1.35f, 1.0f);
            float LobeMix = 0.15f * Opacity;
            float AverageRoughness = lerp(Lobe0Roughness, Lobe1Roughness, LobeMix);

            FBxDFEnergyTerms EnergyTerms = ComputeGGXSpecEnergyTerms(AverageRoughness, Context.NoV, GBuffer.SpecularColor);

            FDirectLighting Lighting = (FDirectLighting) 0;
            float3 DiffuseReflection = Diffuse_Burley(GBuffer.DiffuseColor, GBuffer.Roughness, Context.NoV, NoL, Context.VoH);
            Lighting.Diffuse = AreaLight.FalloffColor * (Falloff * NoL) * DiffuseReflection;

            if (AreaLight.bIsRect)
            {
                float3 Lobe0Spec = RectGGXApproxLTC(Lobe0Roughness, GBuffer.SpecularColor, N, V, AreaLight.Rect);
                float3 Lobe1Spec = RectGGXApproxLTC(Lobe1Roughness, GBuffer.SpecularColor, N, V, AreaLight.Rect);
                Lighting.Specular = AreaLight.FalloffColor * lerp(Lobe0Spec, Lobe1Spec, LobeMix);
            }
            else
            {
                float a2_0 = Pow4(Lobe0Roughness);
                float a2_1 = Pow4(Lobe1Roughness);
                float avg_a2 = Pow4(AverageRoughness);
                float E0 = EnergyNormalization(a2_0, Context.VoH, AreaLight);
                float E1 = EnergyNormalization(a2_1, Context.VoH, AreaLight);
                float D = lerp(D_GGX(a2_0, Context.NoH) * E0, D_GGX(a2_1, Context.NoH) * E1, LobeMix);
                float Vis = Vis_SmithJointApprox(avg_a2, Context.NoV, NoL);
                float3 F = F_Schlick(GBuffer.SpecularColor, Context.VoH);
                Lighting.Specular = AreaLight.FalloffColor * (Falloff * NoL) * (D * Vis) * F;
            }

            Lighting.Diffuse  *= ComputeEnergyPreservation(EnergyTerms);
            Lighting.Specular *= ComputeEnergyConservation(EnergyTerms);

            // Skin wrapped subsurface scatter approximation
            float WrapNoL = saturate((dot(N, L) + 0.35f) / (1.35f * 1.35f));
            float Scatter = saturate(WrapNoL - NoL);
            Lighting.Transmission = AreaLight.FalloffColor * Falloff * Scatter * GBuffer.DiffuseColor * float3(0.85f, 0.32f, 0.20f) * (1.0f / PI);
            return Lighting;
        }

        FDirectLighting ClothBxDF(FGBufferData GBuffer, float3 N, float3 V, float3 L, float Falloff, float NoL, FAreaLight AreaLight)
        {
            const float3 FuzzColor = ExtractSubsurfaceColor(GBuffer);
            const float  Cloth     = saturate(GBuffer.CustomData.a);

            BxDFContext Context;
            Init(Context, N, V, L);
            SphereMaxNoH(Context, AreaLight.SphereSinAlpha, true);
            Context.NoV = saturate(abs(Context.NoV) + 1e-5f);

            float3 Spec1;
            if (AreaLight.bIsRect)
                Spec1 = AreaLight.FalloffColor * RectGGXApproxLTC(GBuffer.Roughness, GBuffer.SpecularColor, N, V, AreaLight.Rect);
            else
                Spec1 = AreaLight.FalloffColor * (Falloff * NoL) * SpecularGGX(GBuffer.Roughness, GBuffer.SpecularColor, Context, NoL, AreaLight);

            FBxDFEnergyTerms EnergyTerms1 = ComputeGGXSpecEnergyTerms(GBuffer.Roughness, Context.NoV, GBuffer.SpecularColor);
            Spec1 *= ComputeEnergyConservation(EnergyTerms1);

            float D2 = D_InvGGX(Pow4(GBuffer.Roughness), Context.NoH);
            float Vis2 = Vis_Cloth(Context.NoV, NoL);
            float3 F2 = F_Schlick(FuzzColor, Context.VoH);
            float3 Spec2 = AreaLight.FalloffColor * (Falloff * NoL) * (D2 * Vis2) * F2;

            FDirectLighting Lighting;
            Lighting.Diffuse = AreaLight.FalloffColor * (Falloff * NoL) * Diffuse_Lambert(GBuffer.DiffuseColor) * ComputeEnergyPreservation(EnergyTerms1);
            Lighting.Specular = lerp(Spec1, Spec2, Cloth);
            Lighting.Transmission = 0.0f;
            return Lighting;
        }

        FDirectLighting SubsurfaceBxDF(FGBufferData GBuffer, float3 N, float3 V, float3 L, float Falloff, float NoL, FAreaLight AreaLight)
        {
            FDirectLighting Lighting = DefaultLitBxDF(GBuffer, N, V, L, Falloff, NoL, AreaLight);

            float3 SubsurfaceColor = ExtractSubsurfaceColor(GBuffer);
            float Opacity = GBuffer.CustomData.a;

            float InScatter = pow(saturate(dot(L, -V)), 12.0f) * lerp(3.0f, 0.1f, Opacity);
            float WrappedDiffuse = pow(saturate(dot(N, L) * (1.0f / 1.5f) + (0.5f / 1.5f)), 1.5f) * (2.5f / 1.5f);
            float NormalContribution = lerp(1.0f, WrappedDiffuse, Opacity);
            float BackScatter = GBuffer.GBufferAO * NormalContribution / (PI * 2.0f);

            Lighting.Transmission = AreaLight.FalloffColor * (Falloff * lerp(BackScatter, 1.0f, InScatter)) * SubsurfaceColor;
            return Lighting;
        }

        FDirectLighting TwoSidedBxDF(FGBufferData GBuffer, float3 N, float3 V, float3 L, float Falloff, float NoL, FAreaLight AreaLight)
        {
            FDirectLighting Lighting = DefaultLitBxDF(GBuffer, N, V, L, Falloff, NoL, AreaLight);

            float3 SubsurfaceColor = ExtractSubsurfaceColor(GBuffer);
            float Wrap = 0.5f;
            float WrapNoL = saturate((-dot(N, L) + Wrap) / Square(1.0f + Wrap));
            float VoL = dot(V, L);
            float Scatter = D_GGX(0.6f * 0.6f, saturate(-VoL));

            Lighting.Transmission = AreaLight.FalloffColor * (Falloff * WrapNoL * Scatter) * SubsurfaceColor;
            return Lighting;
        }

        FDirectLighting EyeBxDF(FGBufferData GBuffer, float3 N, float3 V, float3 L, float Falloff, float NoL, FAreaLight AreaLight)
        {
            const float3 IrisNormal   = dot(GBuffer.CustomData.yz, GBuffer.CustomData.yz) > 1e-4f
                ? OctahedronToUnitVector(GBuffer.CustomData.yz * 2.0f - 1.0f)
                : N;
            const float  IrisDistance = GBuffer.StoredMetallic;
            const float  IrisMask     = 1.0f - GBuffer.CustomData.w;
            const float3 CausticNormal = SafeNormalize(lerp(IrisNormal, -N, IrisMask * IrisDistance), N);

            BxDFContext Context;
            Init(Context, N, V, L);
            SphereMaxNoH(Context, AreaLight.SphereSinAlpha, false);
            Context.NoV = saturate(abs(Context.NoV) + 1e-5f);
            Context.VoH = AreaLight.bIsRect ? Context.NoV : Context.VoH;

            float F0 = GBuffer.Specular * 0.08f;
            float Fc = Pow5(1.0f - Context.VoH);
            float F = Fc + (1.0f - Fc) * F0;

            FBxDFEnergyTerms EnergyTerms = ComputeGGXSpecEnergyTerms(GBuffer.Roughness, Context.NoV, F0.xxx);

            FDirectLighting Lighting = (FDirectLighting) 0;
            if (AreaLight.bIsRect)
            {
                Lighting.Specular = AreaLight.FalloffColor * RectGGXApproxLTC(GBuffer.Roughness, F0.xxx, N, V, AreaLight.Rect);
            }
            else
            {
                float a2 = Pow4(GBuffer.Roughness);
                float Energy = EnergyNormalization(a2, Context.VoH, AreaLight);
                float Vis = Vis_SmithJointApprox(a2, Context.NoV, NoL);
                float D = D_GGX(a2, Context.NoH) * Energy;
                Lighting.Specular = AreaLight.FalloffColor * (Falloff * NoL) * D * Vis * F;
            }

            float IrisNoL = saturate(dot(IrisNormal, L));
            float Power = lerp(12.0f, 1.0f, IrisNoL);
            float Caustic = 0.8f + 0.2f * (Power + 1.0f) * pow(saturate(dot(CausticNormal, L)), Power);
            float Iris = IrisNoL * Caustic;
            float Sclera = NoL;

            Lighting.Specular *= ComputeEnergyConservation(EnergyTerms);
            float EnergyPreservation = ComputeEnergyPreservation(EnergyTerms);
            Lighting.Diffuse = 0.0f;
            Lighting.Transmission = AreaLight.FalloffColor * (Falloff * lerp(Sclera, Iris, IrisMask) * EnergyPreservation) * Diffuse_Lambert(GBuffer.DiffuseColor);
            return Lighting;
        }

        FDirectLighting HairBxDF(FGBufferData GBuffer, float3 N, float3 V, float3 L, float Falloff, float NoL, FAreaLight AreaLight)
        {
            // UE5 Marschner / Kajiya-Kay hair shading approximation for deferred GBuffer hair
            FDirectLighting Lighting = DefaultLitBxDF(GBuffer, N, V, L, Falloff, NoL, AreaLight);
            float3 Ex = pow(max(GBuffer.BaseColor, 0.01f), 0.5f);
            float VoL = dot(V, L);
            float ForwardScatter = pow(saturate(-VoL * 0.5f + 0.5f), 4.0f) * 0.35f;
            Lighting.Transmission = AreaLight.FalloffColor * Falloff * Ex * ForwardScatter * (1.0f / PI);
            return Lighting;
        }

        FDirectLighting IntegrateBxDF(FGBufferData GBuffer, float3 N, float3 V, float3 L, float Falloff, float NoL, FAreaLight AreaLight)
        {
            switch (GBuffer.ShadingModelID)
            {
                case SHADINGMODELID_DEFAULT_LIT:
                case SHADINGMODELID_SINGLELAYERWATER:
                case SHADINGMODELID_THIN_TRANSLUCENT:
                    return DefaultLitBxDF(GBuffer, N, V, L, Falloff, NoL, AreaLight);
                case SHADINGMODELID_SUBSURFACE:
                case SHADINGMODELID_PREINTEGRATED_SKIN:
                    return SubsurfaceBxDF(GBuffer, N, V, L, Falloff, NoL, AreaLight);
                case SHADINGMODELID_CLEAR_COAT:
                    return ClearCoatBxDF(GBuffer, N, V, L, Falloff, NoL, AreaLight);
                case SHADINGMODELID_SUBSURFACE_PROFILE:
                    return SubsurfaceProfileBxDF(GBuffer, N, V, L, Falloff, NoL, AreaLight);
                case SHADINGMODELID_TWOSIDED_FOLIAGE:
                    return TwoSidedBxDF(GBuffer, N, V, L, Falloff, NoL, AreaLight);
                case SHADINGMODELID_HAIR:
                    return HairBxDF(GBuffer, N, V, L, Falloff, NoL, AreaLight);
                case SHADINGMODELID_CLOTH:
                    return ClothBxDF(GBuffer, N, V, L, Falloff, NoL, AreaLight);
                case SHADINGMODELID_EYE:
                    return EyeBxDF(GBuffer, N, V, L, Falloff, NoL, AreaLight);
                default:
                    return DefaultLitBxDF(GBuffer, N, V, L, Falloff, NoL, AreaLight);
            }
        }

        // =============================================================================
        // CapsuleLightIntegrate.ush & RectLightIntegrate.ush
        // =============================================================================
        FDirectLighting IntegrateCapsuleBxDF(FGBufferData GBuffer, float3 N, float3 V, FCapsuleLight Capsule, bool bInverseSquared)
        {
            float NoL;
            float Falloff;
            float LineCosSubtended = 1.0f;

            if (Capsule.Length > 0.0f)
            {
                LineIrradiance(N, Capsule.LightPos[0], Capsule.LightPos[1], Capsule.DistBiasSqr, LineCosSubtended, Falloff, NoL);
            }
            else
            {
                float DistSqr = dot(Capsule.LightPos[0], Capsule.LightPos[0]);
                Falloff = rcp(DistSqr + Capsule.DistBiasSqr);
                float3 L_dir = Capsule.LightPos[0] * rsqrt(max(DistSqr, 1e-8f));
                NoL = dot(N, L_dir);
            }

            if (Capsule.Radius > 0.0f)
            {
                float SinAlphaSqr = saturate(Pow2(Capsule.Radius) * Falloff);
                NoL = SphereHorizonCosWrap(NoL, SinAlphaSqr);
            }

            NoL = saturate(NoL);
            Falloff = bInverseSquared ? Falloff : 1.0f;

            float3 ToLight = Capsule.LightPos[0];
            if (Capsule.Length > 0.0f)
            {
                float3 R = reflect(-V, N);
                ToLight = ClosestPointLineToRay(Capsule.LightPos[0], Capsule.LightPos[1], Capsule.Length, R);
            }

            float DistSqrSpec = max(dot(ToLight, ToLight), 1e-6f);
            float InvDist = rsqrt(DistSqrSpec);
            float3 L = ToLight * InvDist;

            float Roughness = max(GBuffer.Roughness, 0.02f);
            float a = Pow2(Roughness);

            FAreaLight AreaLight = (FAreaLight) 0;
            AreaLight.SphereSinAlpha = saturate(Capsule.Radius * InvDist * (1.0f - a));
            AreaLight.SphereSinAlphaSoft = saturate(Capsule.SoftRadius * InvDist);
            AreaLight.LineCosSubtended = LineCosSubtended;
            AreaLight.FalloffColor = 1.0f.xxx;
            AreaLight.bIsRect = false;

            return IntegrateBxDF(GBuffer, N, V, L, Falloff, NoL, AreaLight);
        }

        FDirectLighting IntegrateRectBxDF(FGBufferData GBuffer, float3 N, float3 V, FRect Rect)
        {
            FDirectLighting Out = (FDirectLighting) 0;
            if (Rect.Extent.x <= 0.0f || Rect.Extent.y <= 0.0f)
                return Out;

            float NoL = 0.0f;
            float Falloff = 0.0f;
            float3 L = RectIrradianceLambert(N, Rect, Falloff, NoL);

            FAreaLight AreaLight = (FAreaLight) 0;
            AreaLight.SphereSinAlpha = 0.0f;
            AreaLight.SphereSinAlphaSoft = 0.0f;
            AreaLight.LineCosSubtended = 1.0f;
            AreaLight.FalloffColor = 1.0f.xxx;
            AreaLight.Rect = Rect;
            AreaLight.bIsRect = true;

            GBuffer.Roughness = max(GBuffer.Roughness, 0.02f);
            return IntegrateBxDF(GBuffer, N, V, L, Falloff, NoL, AreaLight);
        }

        // =============================================================================
        // DeferredLightingCommon.ush & DynamicLightingCommon.ush: Local Light Evaluation
        // =============================================================================
        float RadialAttenuation(float3 WorldLightVector, float FalloffExponent)
        {
            float NormalizeDistanceSquared = dot(WorldLightVector, WorldLightVector);
            return pow(1.0f - saturate(NormalizeDistanceSquared), FalloffExponent);
        }

        float SpotAttenuation(float3 L, float3 SpotDirection, float2 SpotAngles)
        {
            float mask = saturate((dot(L, -SpotDirection) - SpotAngles.x) * SpotAngles.y);
            return mask * mask;
        }

        float3 EvaluateUE52LocalLight(GpuLocalLight light, float3 TranslatedWorldPosition, float3 N, float3 V, FGBufferData GBuffer, float AmbientOcclusion)
        {
            float3 ToLight = light.PositionAndInvRadius.xyz - TranslatedWorldPosition;
            float InvRadius = light.PositionAndInvRadius.w;
            float DistanceSqr = dot(ToLight, ToLight);
            if (DistanceSqr * Square(InvRadius) >= 1.0f)
                return 0.0f;

            float3 L = ToLight * rsqrt(max(DistanceSqr, 1e-6f));
            int lightType = (int) round(light.SpotAnglesAndParams.w);
            float falloffExponent = light.ColorAndFalloff.w;
            bool bInverseSquared = (falloffExponent <= 0.0f);

            float LightMask;
            if (bInverseSquared)
            {
                LightMask = Square(saturate(1.0f - Square(DistanceSqr * Square(InvRadius))));
            }
            else
            {
                LightMask = RadialAttenuation(ToLight * InvRadius, falloffExponent);
            }

            float3 lightDir = light.DirectionAndSourceRadius.xyz; // equals -GetDirection()
            if (lightType == LIGHT_TYPE_SPOT)
            {
                LightMask *= SpotAttenuation(L, -lightDir, light.SpotAnglesAndParams.xy);
            }
            else if (lightType == LIGHT_TYPE_RECT)
            {
                if (dot(lightDir, L) < 0.0f)
                    return 0.0f;
            }

            if (LightMask <= 1e-6f)
                return 0.0f;

            float3 MaskedLightColor = light.ColorAndFalloff.rgb * LightMask;
            float NoL = 0.0f;
            float Falloff = 0.0f;
            FAreaLight AreaLight = (FAreaLight) 0;
            GBuffer.Roughness = max(GBuffer.Roughness, 0.02f);

            if (lightType == LIGHT_TYPE_RECT)
            {
                FRect Rect = GetRect(
                    ToLight,
                    lightDir,
                    light.TangentAndSourceLength.xyz,
                    light.DirectionAndSourceRadius.w,
                    light.TangentAndSourceLength.w,
                    light.SpotAnglesAndParams.x,
                    light.SpotAnglesAndParams.y,
                    true);
                if (Rect.Extent.x <= 0.0f || Rect.Extent.y <= 0.0f)
                    return 0.0f;

                L = RectIrradianceLambert(N, Rect, Falloff, NoL);
                AreaLight.SphereSinAlpha = 0.0f;
                AreaLight.SphereSinAlphaSoft = 0.0f;
                AreaLight.LineCosSubtended = 1.0f;
                AreaLight.FalloffColor = 1.0f.xxx;
                AreaLight.Rect = Rect;
                AreaLight.bIsRect = true;
            }
            else
            {
                FCapsuleLight Capsule;
                Capsule.Length = light.TangentAndSourceLength.w;
                Capsule.Radius = light.DirectionAndSourceRadius.w;
                Capsule.SoftRadius = light.SpotAnglesAndParams.z;
                Capsule.DistBiasSqr = 1.0f;
                Capsule.LightPos[0] = ToLight - 0.5f * Capsule.Length * light.TangentAndSourceLength.xyz;
                Capsule.LightPos[1] = ToLight + 0.5f * Capsule.Length * light.TangentAndSourceLength.xyz;

                float LineCosSubtended = 1.0f;
                if (Capsule.Length > 0.0f)
                {
                    LineIrradiance(N, Capsule.LightPos[0], Capsule.LightPos[1], Capsule.DistBiasSqr, LineCosSubtended, Falloff, NoL);
                }
                else
                {
                    float DistSqrCap = dot(Capsule.LightPos[0], Capsule.LightPos[0]);
                    Falloff = rcp(DistSqrCap + Capsule.DistBiasSqr);
                    float3 L_dir = Capsule.LightPos[0] * rsqrt(max(DistSqrCap, 1e-8f));
                    NoL = dot(N, L_dir);
                }

                if (Capsule.Radius > 0.0f)
                {
                    float SinAlphaSqr = saturate(Pow2(Capsule.Radius) * Falloff);
                    NoL = SphereHorizonCosWrap(NoL, SinAlphaSqr);
                }

                NoL = saturate(NoL);
                Falloff = bInverseSquared ? Falloff : 1.0f;

                float3 ToLightSpec = Capsule.LightPos[0];
                if (Capsule.Length > 0.0f)
                {
                    float3 R = reflect(-V, N);
                    ToLightSpec = ClosestPointLineToRay(Capsule.LightPos[0], Capsule.LightPos[1], Capsule.Length, R);
                }

                float DistSqrSpec = max(dot(ToLightSpec, ToLightSpec), 1e-6f);
                float InvDist = rsqrt(DistSqrSpec);
                L = ToLightSpec * InvDist;

                float a = Pow2(GBuffer.Roughness);
                AreaLight.SphereSinAlpha = saturate(Capsule.Radius * InvDist * (1.0f - a));
                AreaLight.SphereSinAlphaSoft = saturate(Capsule.SoftRadius * InvDist);
                AreaLight.LineCosSubtended = LineCosSubtended;
                AreaLight.FalloffColor = 1.0f.xxx;
                AreaLight.bIsRect = false;
            }

            FDirectLighting Lighting = IntegrateBxDF(GBuffer, N, V, L, Falloff, NoL, AreaLight);

            float3 total = (Lighting.Diffuse + Lighting.Specular) * (MaskedLightColor * AmbientOcclusion)
                         + Lighting.Transmission * MaskedLightColor;
            if (any(isnan(total)) || any(isinf(total)))
                return 0.0f;
            return total;
        }

        // =============================================================================
        // Screen-Space Ambient Occlusion (GTAO Horizon Search) & Contact Shadows
        // =============================================================================
        float3 ReconstructTranslatedWorld(float2 pixelPos, float deviceZ)
        {
            float4 h = pixelPos.xxxx * SvPosRow0 + pixelPos.yyyy * SvPosRow1 + deviceZ.xxxx * SvPosRow2 + SvPosRow3;
            return h.xyz / max(h.w, 1e-6f);
        }

        float ComputeScreenSpaceAO(int2 pix, float centerDepth, float3 P_cm, float3 N, float3 V)
        {
            if (CameraPos.w > 0.5f) return 1.0f; // Material sphere preview

            int2 dims = int2(ViewportSizeAndInv.xy);
            float viewDistCm = length(P_cm);
            if (viewDistCm > 35000.0f || viewDistCm < 5.0f) return 1.0f;

            // Project a 45cm world radius to screen pixels (clamped to 2..14 px)
            float pixelRadius = clamp(45.0f * ViewportSizeAndInv.x / max(viewDistCm, 50.0f), 2.0f, 14.0f);

            // Interleaved gradient noise per pixel for smooth spatial distribution
            float ign = frac(52.9829189f * frac(dot(float2(pix), float2(0.06711056f, 0.00583715f))));

            static const float2 dirs[4] = {
                float2( 1.0f,  0.0f),
                float2( 0.0f,  1.0f),
                float2( 0.7071f,  0.7071f),
                float2(-0.7071f,  0.7071f)
            };

            float occlusion = 0.0f;
            [unroll]
            for (int d = 0; d < 4; d++)
            {
                float2 stepPx = dirs[d] * (pixelRadius * (0.45f + 0.55f * ign));
                int2 samplePixPos = clamp(pix + int2(round(stepPx)), int2(0, 0), dims - 1);
                int2 samplePixNeg = clamp(pix - int2(round(stepPx)), int2(0, 0), dims - 1);

                float zPos = DepthTex.Load(int3(samplePixPos, 0));
                float zNeg = DepthTex.Load(int3(samplePixNeg, 0));

                if (zPos > 1e-6f)
                {
                    float3 diff = ReconstructTranslatedWorld(float2(samplePixPos) + 0.5f, zPos) - P_cm;
                    float distSq = dot(diff, diff);
                    if (distSq > 1.0f && distSq < 120.0f * 120.0f)
                    {
                        float dist = sqrt(distSq);
                        float cosH = dot(N, diff / dist);
                        float falloff = saturate(1.0f - dist / 120.0f);
                        occlusion += saturate(cosH - 0.12f) * falloff;
                    }
                }

                if (zNeg > 1e-6f)
                {
                    float3 diff = ReconstructTranslatedWorld(float2(samplePixNeg) + 0.5f, zNeg) - P_cm;
                    float distSq = dot(diff, diff);
                    if (distSq > 1.0f && distSq < 120.0f * 120.0f)
                    {
                        float dist = sqrt(distSq);
                        float cosH = dot(N, diff / dist);
                        float falloff = saturate(1.0f - dist / 120.0f);
                        occlusion += saturate(cosH - 0.12f) * falloff;
                    }
                }
            }

            return saturate(1.0f - (occlusion / 8.0f) * 1.15f);
        }

        float4 PSSsaoHalf(V i) : SV_Target
        {
            if (CameraPos.w > 0.5f || WorldRenderFlags.w < 0.5f)
                return 1.0f.xxxx;

            int2 dims = int2(ViewportSizeAndInv.xy);
            int2 fullPix = min(int2(i.Pos.xy) * 2, dims - 1);
            float4 a = GBufferA.Load(int3(fullPix, 0));
            if (dot(a.xyz, a.xyz) < 1e-4f)
                return 1.0f.xxxx;

            float depth = DepthTex.Load(int3(fullPix, 0));
            if (depth <= 1e-6f)
                return 1.0f.xxxx;

            float3 P_cm = ReconstructTranslatedWorld(float2(fullPix) + 0.5f, depth);
            float3 N = SafeNormalize(a.xyz * 2.0f - 1.0f, float3(0, 0, 1));
            float3 V = SafeNormalize(-P_cm, float3(0, 0, 1));
            float ao = ComputeScreenSpaceAO(fullPix, depth, P_cm, N, V);
            return float4(ao, ao, ao, 1.0f);
        }

        float4 PSSsaoBlur(V i) : SV_Target
        {
            int2 fullDims = int2(ViewportSizeAndInv.xy);
            int2 halfDims = max(int2(1, 1), fullDims / 2);
            int2 hpix = clamp(int2(i.Pos.xy), int2(0, 0), halfDims - 1);
            int2 fp0 = min(hpix * 2, fullDims - 1);
            float centerZ = DepthTex.Load(int3(fp0, 0));
            float centerAo = SsaoTex.Load(int3(hpix, 0));
            if (centerZ <= 1e-6f)
                return float4(centerAo, centerAo, centerAo, 1.0f);

            float sum = 0.0f;
            float wsum = 0.0f;
            [unroll] for (int dy = -1; dy <= 1; dy++)
            [unroll] for (int dx = -1; dx <= 1; dx++)
            {
                int2 sp = clamp(hpix + int2(dx, dy), int2(0, 0), halfDims - 1);
                int2 fsp = min(sp * 2, fullDims - 1);
                float sz = DepthTex.Load(int3(fsp, 0));
                float sao = SsaoTex.Load(int3(sp, 0));
                float spatial = (dx == 0 && dy == 0) ? 2.0f : ((dx == 0 || dy == 0) ? 1.5f : 1.0f);
                float w = spatial * exp(-abs(sz - centerZ) * 400.0f);
                sum += sao * w;
                wsum += w;
            }
            float ao = sum / max(wsum, 1e-5f);
            return float4(ao, ao, ao, 1.0f);
        }

        float SampleSsaoBilateral(int2 pix, float centerDepth, float3 N)
        {
            int2 fullDims = int2(ViewportSizeAndInv.xy);
            int2 halfDims = max(int2(1, 1), fullDims / 2);
            float2 halfCoord = (float2(pix) + 0.5f) * 0.5f - 0.5f;
            int2 baseH = clamp(int2(floor(halfCoord)), int2(0, 0), halfDims - 1);
            float2 fracH = saturate(halfCoord - float2(baseH));

            float sum = 0.0f;
            float wsum = 0.0f;
            [unroll] for (int oy = 0; oy <= 1; oy++)
            [unroll] for (int ox = 0; ox <= 1; ox++)
            {
                int2 hp = clamp(baseH + int2(ox, oy), int2(0, 0), halfDims - 1);
                int2 fp = min(hp * 2, fullDims - 1);
                float zTap = DepthTex.Load(int3(fp, 0));
                float3 aTap = GBufferA.Load(int3(fp, 0)).xyz;
                float aoTap = SsaoTex.Load(int3(hp, 0));
                float bx = ox == 0 ? (1.0f - fracH.x) : fracH.x;
                float by = oy == 0 ? (1.0f - fracH.y) : fracH.y;
                float bil = bx * by;
                float nW = dot(aTap, aTap) > 1e-4f
                    ? pow(saturate(dot(SafeNormalize(aTap * 2.0f - 1.0f, N), N)), 4.0f)
                    : 0.0f;
                float zW = exp(-abs(zTap - centerDepth) * 400.0f);
                float w = bil * zW * max(nW, 0.02f);
                sum += aoTap * w;
                wsum += w;
            }
            return wsum > 1e-5f ? saturate(sum / wsum) : 1.0f;
        }

        // =============================================================================
        // ReflectionEnvironmentShared.ush: SkyLight IBL & SH Diffuse
        // =============================================================================
        float3 GetOffSpecularPeakReflectionDir(float3 Normal, float3 ReflectionVector, float Roughness)
        {
            float a = Roughness * Roughness;
            return SafeNormalize(lerp(Normal, ReflectionVector, (1.0f - a) * (sqrt(1.0f - a) + a)), ReflectionVector);
        }

        float GetSpecularOcclusion(float NoV, float RoughnessSq, float AO)
        {
            return saturate(pow(max(NoV + AO, 0.0f), RoughnessSq) - 1.0f + AO);
        }

        float ComputeReflectionCaptureMipFromRoughness(float Roughness, float CubemapMaxMip)
        {
            float LevelFrom1x1 = 1.0f - 1.2f * log2(max(Roughness, 0.001f));
            return clamp(CubemapMaxMip - 1.0f - LevelFrom1x1, 0.0f, max(CubemapMaxMip - 1.0f, 0.0f));
        }

        float3 EvaluateProceduralSky(float3 dir, float3 sunDir, float3 sunColor, float sunIntensity)
        {
            // Smooth physical sky gradient + Rayleigh/Mie horizon scattering
            float up = dir.z;
            float horizon = pow(saturate(1.0f - abs(up)), 3.5f);
            float3 zenithCol = SkyUpperColor.rgb * SkyIntensity * 1.15f;
            float3 horizCol  = lerp(SkyUpperColor.rgb, SkyLowerColor.rgb, 0.35f) * SkyIntensity * 1.35f
                             + sunColor * saturate(sunIntensity * 0.06f) * horizon;
            float3 groundCol = SkyLowerColor.rgb * SkyIntensity * 0.65f;

            float3 sky = up >= 0.0f
                ? lerp(horizCol, zenithCol, pow(saturate(up), 0.55f))
                : lerp(horizCol, groundCol, pow(saturate(-up), 0.45f));

            // Solar aureole & Mie forward scattering
            float cosSun = dot(dir, sunDir);
            float mie = pow(saturate(cosSun), 16.0f) * 0.25f + pow(saturate(cosSun), 256.0f) * 1.5f;
            if (up >= -0.05f)
            {
                sky += sunColor * (sunIntensity * 0.15f) * mie * saturate((up + 0.05f) * 10.0f);
            }
            return max(sky, 0.0f);
        }

        float3 GetSkySHDiffuse(float3 Normal)
        {
            // 2-band hemispherical cosine-convolved irradiance between SkyUpperColor and SkyLowerColor
            float h = Normal.z * 0.5f + 0.5f;
            float smoothH = h * h * (3.0f - 2.0f * h);
            return lerp(SkyLowerColor.rgb, SkyUpperColor.rgb, smoothH) * SkyIntensity;
        }

        float3 GetSkyLightReflection(float3 ReflectionVector, float Roughness, float3 sunDir, float3 sunColor, float sunIntensity)
        {
            if (SkyUpperColor.w > 0.5f)
            {
                // Convert Unreal Z-up ReflectionVector to D3D11 Y-up cubemap lookup or sample directly
                float mip = ComputeReflectionCaptureMipFromRoughness(Roughness, SkyLowerColor.w);
                float3 samp = SkyCubemap.SampleLevel(LinearClampSampler, ReflectionVector, mip).rgb;
                return samp * SkyIntensity;
            }
            else
            {
                float3 sharpSky = EvaluateProceduralSky(ReflectionVector, sunDir, sunColor, sunIntensity);
                float3 diffuseSky = GetSkySHDiffuse(ReflectionVector);
                float roughBlend = saturate(Roughness * Roughness);
                return lerp(sharpSky, diffuseSky, roughBlend);
            }
        }

        // =============================================================================
        // HeightFogCommon.ush: UE 5.2 Exponential Height Fog
        // =============================================================================
        float CalculateLineIntegralShared(float FogHeightFalloff, float RayDirectionZ, float RayOriginTerms)
        {
            float Falloff = max(-127.0f, FogHeightFalloff * RayDirectionZ);
            float LineIntegral = (1.0f - exp2(-Falloff)) / Falloff;
            float LineIntegralTaylor = log(2.0f) - (0.5f * Pow2(log(2.0f))) * Falloff;
            return RayOriginTerms * (abs(Falloff) > 0.01f ? LineIntegral : LineIntegralTaylor);
        }

        float4 EvaluateUE52HeightFog(float3 WorldPositionRelativeToCameraCm, float3 sunDir)
        {
            float maxOpacity = FogParams1.w;
            if (maxOpacity <= 0.0f || CameraPos.w > 0.5f)
                return float4(0, 0, 0, 1);

            float fogDensity = FogParams0.x;
            float fogHeightFalloff = FogParams0.y;
            float fogHeightCm = FogParams0.z;
            float startDistanceCm = FogParams0.w;

            float3 camToReceiver = WorldPositionRelativeToCameraCm;
            float distSq = dot(camToReceiver, camToReceiver);
            float distInv = rsqrt(max(distSq, 1e-4f));
            float distCm = distSq * distInv;
            float3 dir = camToReceiver * distInv;

            if (distCm <= startDistanceCm)
                return float4(0, 0, 0, 1);

            float excludeFraction = startDistanceCm * distInv;
            float startZ = CameraPos.z + excludeFraction * camToReceiver.z;
            float rayDirZ = (1.0f - excludeFraction) * camToReceiver.z;
            float rayLenM = (distCm - startDistanceCm) * 0.01f;

            float heightAboveFogM = (startZ - fogHeightCm) * 0.01f;
            float exponent = clamp(fogHeightFalloff * heightAboveFogM, -64.0f, 64.0f);
            float rayOriginTerms = fogDensity * exp2(-exponent);

            float lineIntegralShared = CalculateLineIntegralShared(fogHeightFalloff, rayDirZ * 0.01f, rayOriginTerms);
            float lineIntegral = lineIntegralShared * rayLenM;

            float minFogOpacity = 1.0f - saturate(maxOpacity);
            float expFogFactor = max(saturate(exp2(-lineIntegral)), minFogOpacity);

            float3 inscattering = FogParams1.rgb * (1.0f - expFogFactor);
            float dirExp = FogParams2.w;
            if (dirExp > 0.0f && dot(FogParams2.rgb, FogParams2.rgb) > 1e-6f)
            {
                float3 dirInscatter = FogParams2.rgb * pow(saturate(dot(dir, sunDir)), dirExp);
                float dirFactor = saturate(exp2(-lineIntegral));
                inscattering += dirInscatter * (1.0f - dirFactor);
            }

            return float4(inscattering, expFogFactor);
        }

        // =============================================================================
        // Whole scene directional shadow projection (ShadowProjectionCommon.ush, ShadowQuality 3: 5x5 PCF)
        // =============================================================================
        float PickComponent(float4 v, int i)
        {
            return dot(v, float4(i == 0, i == 1, i == 2, i == 3));
        }

        float SampleShadowCascade(int cascade, float3 worldPos, float3 N)
        {
            float texelWorld = PickComponent(CascadeTexelWorld, cascade);
            float NoL = saturate(dot(N, ShadowLightDirection.xyz));
            // Normal offset + receiver bias (ShadowDepthPixelShader / ShadowProjection "NormalOffset" r.Shadow.CSMDepthBias)
            float3 p = worldPos + N * texelWorld * (1.0f + 1.5f * (1.0f - NoL)) + ShadowLightDirection.xyz * texelWorld * 0.5f;
            float4 r = p.x * ShadowMatrix[cascade * 4 + 0] + p.y * ShadowMatrix[cascade * 4 + 1]
                     + p.z * ShadowMatrix[cascade * 4 + 2] + ShadowMatrix[cascade * 4 + 3];
            float3 clip = r.xyz / r.w;
            float2 uv = float2(clip.x * 0.5f + 0.5f, 0.5f - clip.y * 0.5f);
            if (any(uv < 0.0f) || any(uv > 1.0f) || clip.z > 1.0f)
                return 1.0f;
            float z = clip.z - PickComponent(CascadeDepthBias, cascade);
            float t = ShadowInfo.y;
            // 5x5 PCF footprint via 9 bilinear comparison taps (tent filter weights)
            float s = 0.0f;
            [unroll] for (int y = -1; y <= 1; y++)
            [unroll] for (int x = -1; x <= 1; x++)
            {
                float w = (x == 0 ? 2.0f : 1.0f) * (y == 0 ? 2.0f : 1.0f);
                s += w * ShadowDepths.SampleCmpLevelZero(ShadowCmpSampler, float3(uv + float2(x, y) * t * 1.5f, cascade), z);
            }
            return s / 16.0f;
        }

        float ComputeDirectionalShadow(float3 translatedWorldPos, float3 N)
        {
            int numCascades = (int) ShadowInfo.x;
            if (numCascades <= 0)
                return 1.0f;
            float3 worldPos = translatedWorldPos + CameraPos.xyz;
            float viewDepth = dot(translatedWorldPos, ShadowCameraForward.xyz);
            float shadowDistance = ShadowCameraForward.w;
            if (viewDepth > shadowDistance)
                return 1.0f;

            float shadow = 1.0f;
            [loop] for (int c = 0; c < numCascades; c++)
            {
                float splitFar = PickComponent(CascadeSplitFar, c);
                if (viewDepth <= splitFar)
                {
                    shadow = SampleShadowCascade(c, worldPos, N);
                    float splitNear = c > 0 ? PickComponent(CascadeSplitFar, c - 1) : 0.0f;
                    float transition = (splitFar - splitNear) * ShadowInfo.z;
                    float blend = saturate((viewDepth - (splitFar - transition)) / max(transition, 1e-3f));
                    if (blend > 0.0f && c + 1 < numCascades)
                        shadow = lerp(shadow, SampleShadowCascade(c + 1, worldPos, N), blend);
                    break;
                }
            }
            // ShadowDistanceFadeoutFraction
            float fadeStart = shadowDistance * (1.0f - ShadowInfo.w);
            float fade = saturate((viewDepth - fadeStart) / max(shadowDistance - fadeStart, 1e-3f));
            return lerp(shadow, 1.0f, fade);
        }

        // =============================================================================
        // Deferred Lighting Pass (PSDeferredLit)
        // =============================================================================
        float3 AccumulateLocalLight(uint lightIndex, float3 translatedWorldPos, float3 N, float3 V, FGBufferData GBuffer, float ssao)
        {
            return EvaluateUE52LocalLight(LocalLights[lightIndex], translatedWorldPos, N, V, GBuffer, ssao);
        }

        float4 PSDeferredLit(V i) : SV_Target
        {
            int3 p = int3(i.Pos.xy, 0);
            float4 emissiveSample = SceneColor.Load(p);
            float4 a = GBufferA.Load(p);
            float4 b = GBufferB.Load(p);
            float4 c = GBufferC.Load(p);
            float4 d = GBufferD.Load(p);
            float4 e = GBufferE.Load(p);
            float depth = DepthTex.Load(p);

            if (any(isnan(emissiveSample.rgb)) || any(isinf(emissiveSample.rgb)))
                emissiveSample.rgb = 0.0f;

            float4 worldH = i.Pos.xxxx * SvPosRow0 + i.Pos.yyyy * SvPosRow1 + depth.xxxx * SvPosRow2 + SvPosRow3;
            float3 translatedWorldPos = worldH.xyz / max(worldH.w, 1e-6f);
            float3 viewRayDir = SafeNormalize(translatedWorldPos, float3(0, 0, 1));
            float3 sunDir = SafeNormalize(LightDirAndIntensity.xyz, float3(0.4f, 0.3f, 0.85f));
            float sunIntensity = LightDirAndIntensity.w;
            float3 sunColor = LightColorAndSourceRadius.rgb;

            // Sky / Background pixel
            if (dot(a.xyz, a.xyz) < 1e-4f)
            {
                if (CameraPos.w > 0.5f || WorldRenderFlags.x < 0.5f)
                {
                    return float4(Background.rgb + emissiveSample.rgb, 1.0f);
                }
                float3 skyBg = Background.r > 0.2f
                    ? Background.rgb
                    : EvaluateProceduralSky(viewRayDir, sunDir, sunColor, sunIntensity / PI);
                float4 skyFog = EvaluateUE52HeightFog(viewRayDir * 500000.0f, sunDir);
                skyBg = skyBg * skyFog.a + skyFog.rgb;
                return float4(skyBg + emissiveSample.rgb, 1.0f);
            }

            FGBufferData GBuffer = DecodeGBuffer(a, b, c, d, e);

            // SHADINGMODELID_UNLIT: return emissive + fog only
            if (GBuffer.ShadingModelID == SHADINGMODELID_UNLIT)
            {
                float4 unlitFog = EvaluateUE52HeightFog(translatedWorldPos, sunDir);
                float3 unlitCol = emissiveSample.rgb * unlitFog.a + unlitFog.rgb;
                return float4(unlitCol, 1.0f);
            }

            float3 V = -viewRayDir;
            float3 N = GBuffer.WorldNormal;

            // Clear Coat bottom normal octahedral decoding (DeferredLightingCommon.ush:316)
            if (GBuffer.ShadingModelID == SHADINGMODELID_CLEAR_COAT && dot(GBuffer.CustomData.zw, GBuffer.CustomData.zw) > 1e-4f)
            {
                const float2 oct1 = ((float2(GBuffer.CustomData.a, GBuffer.CustomData.z) * 4.0f) - (512.0f / 255.0f)) + UnitVectorToOctahedron(GBuffer.WorldNormal);
                N = OctahedronToUnitVector(oct1);
            }

            float NoV = saturate(abs(dot(N, V)) + 1e-5f);

            // Screen-Space Ambient Occlusion combined with Material AO (GBuffer.GBufferAO)
            float ssao = (WorldRenderFlags.w > 0.5f && CameraPos.w <= 0.5f)
                ? SampleSsaoBilateral(p.xy, depth, N)
                : 1.0f;
            float combinedAO = GBuffer.GBufferAO * ssao;
            float3 multiBounceDiffuseAO = AOMultiBounce(GBuffer.BaseColor, combinedAO);
            float specOcclusion = GetSpecularOcclusion(NoV, Square(GBuffer.Roughness), combinedAO);
            float3 multiBounceSpecAO = AOMultiBounce(GBuffer.SpecularColor, specOcclusion);

            // 1. Directional Light (via UE 5.2 FCapsuleLight with sun disk angular radius)
            FCapsuleLight SunCapsule;
            SunCapsule.Length = 0.0f;
            SunCapsule.Radius = max(LightColorAndSourceRadius.w, 0.004675f);
            SunCapsule.SoftRadius = 0.0f;
            SunCapsule.DistBiasSqr = 1.0f;
            SunCapsule.LightPos[0] = sunDir;
            SunCapsule.LightPos[1] = sunDir;

            FDirectLighting SunLighting = IntegrateCapsuleBxDF(GBuffer, N, V, SunCapsule, false);
            float sunShadow = ComputeDirectionalShadow(translatedWorldPos, GBuffer.WorldNormal);
            float3 sunIlluminance = sunColor * sunIntensity * sunShadow;
            float3 directSun = (SunLighting.Diffuse + SunLighting.Specular) * sunIlluminance
                             + SunLighting.Transmission * sunIlluminance;

            // 2. Local Lights (Point, Spot, Rect with LTC & Capsule area light integration)
            float3 localLighting = 0.0f;
            uint tileBase = 0u;
            uint tileCount = 0u;
            bool useTileList = false;
            if (TileParams.z > 0u)
            {
                uint2 tileCoord = uint2(p.xy) / 16u;
                tileBase = (tileCoord.y * TileParams.x + tileCoord.x) * 128u;
                tileCount = TileLightList[tileBase];
                useTileList = (tileCount != 0xFFFFFFFFu);
            }
            uint iterCount = useTileList ? min(tileCount, 127u) : (uint) max(NumLocalLights, 0);
            [loop]
            for (uint k = 0u; k < iterCount; k++)
            {
                uint li = useTileList ? TileLightList[tileBase + 1u + k] : k;
                localLighting += AccumulateLocalLight(li, translatedWorldPos, N, V, GBuffer, ssao);
            }

            // 3. SkyLight Diffuse SH + Subsurface/Foliage/Cloth Indirect Diffuse (BasePassPixelShader.usf:1153-1203)
            float3 DiffuseColorForIndirect = GBuffer.DiffuseColor;
            float3 SubsurfaceColor = ExtractSubsurfaceColor(GBuffer);
            if (GBuffer.ShadingModelID == SHADINGMODELID_SUBSURFACE || GBuffer.ShadingModelID == SHADINGMODELID_PREINTEGRATED_SKIN)
            {
                DiffuseColorForIndirect += SubsurfaceColor;
            }
            else if (GBuffer.ShadingModelID == SHADINGMODELID_CLOTH)
            {
                DiffuseColorForIndirect += SubsurfaceColor * saturate(GBuffer.CustomData.a);
            }

            FBxDFEnergyTerms IndirectEnergyTerms = ComputeGGXSpecEnergyTerms(GBuffer.Roughness, NoV, GBuffer.SpecularColor);
            float indirectEnergyPreservation = ComputeEnergyPreservation(IndirectEnergyTerms);

            float3 skyDiffuseIrradiance = GetSkySHDiffuse(N);
            float3 skySubsurfaceIrradiance = (GBuffer.ShadingModelID == SHADINGMODELID_TWOSIDED_FOLIAGE)
                ? GetSkySHDiffuse(-N)
                : 0.0f.xxx;

            float3 indirectDiffuse = (skyDiffuseIrradiance * DiffuseColorForIndirect * indirectEnergyPreservation
                                    + skySubsurfaceIrradiance * SubsurfaceColor) * multiBounceDiffuseAO;

            // 4. SkyLight Specular IBL (ReflectionEnvironmentShared.ush: OffSpecularPeak + PreintegratedGF + SpecularOcclusion)
            float3 R = reflect(-V, N);
            float3 offSpecR = GetOffSpecularPeakReflectionDir(N, R, GBuffer.Roughness);
            float3 skyReflection = GetSkyLightReflection(offSpecR, GBuffer.Roughness, sunDir, sunColor, sunIntensity / PI);
            float3 envBrdf = EnvBRDF(GBuffer.SpecularColor, GBuffer.Roughness, NoV);
            float3 indirectSpecular = skyReflection * envBrdf * multiBounceSpecAO;

            // ClearCoat top-layer environment reflection
            if (GBuffer.ShadingModelID == SHADINGMODELID_CLEAR_COAT)
            {
                float clearCoat = GBuffer.CustomData.x;
                float clearCoatRoughness = max(GBuffer.CustomData.y, 0.02f);
                float3 R_coat = reflect(-V, GBuffer.WorldNormal);
                float3 offSpecCoat = GetOffSpecularPeakReflectionDir(GBuffer.WorldNormal, R_coat, clearCoatRoughness);
                float3 coatSkyRefl = GetSkyLightReflection(offSpecCoat, clearCoatRoughness, sunDir, sunColor, sunIntensity / PI);
                float3 coatEnvBrdf = EnvBRDF(0.04f.xxx, clearCoatRoughness, saturate(abs(dot(GBuffer.WorldNormal, V)) + 1e-5f));
                float coatAtten = saturate(1.0f - Luminance(coatEnvBrdf) * clearCoat);
                indirectSpecular = indirectSpecular * coatAtten + coatSkyRefl * coatEnvBrdf * clearCoat * specOcclusion;
            }

            float3 totalLit = emissiveSample.rgb + directSun + localLighting + indirectDiffuse + indirectSpecular;

            // 5. Exponential Height Fog (HeightFogCommon.ush)
            float4 heightFog = EvaluateUE52HeightFog(translatedWorldPos, sunDir);
            totalLit = totalLit * heightFog.a + heightFog.rgb;

            if (any(isnan(totalLit)) || any(isinf(totalLit)))
                totalLit = GBuffer.DiffuseColor * 0.5f + emissiveSample.rgb;

            return float4(max(totalLit, 0.0f), 1.0f);
        }

        // =============================================================================
        // ACES.ush, TonemapCommon.ush, PostProcessCombineLUTs.usf, GammaCorrectionCommon.ush
        // =============================================================================
        static const float3x3 sRGB_2_XYZ_MAT = {
            0.4124564f, 0.3575761f, 0.1804375f,
            0.2126729f, 0.7151522f, 0.0721750f,
            0.0193339f, 0.1191920f, 0.9503041f
        };

        static const float3x3 XYZ_2_sRGB_MAT = {
             3.2409699419f, -1.5373831776f, -0.4986107603f,
            -0.9692436363f,  1.8759675015f,  0.0415550574f,
             0.0556300797f, -0.2039769589f,  1.0569715142f
        };

        static const float3x3 D65_2_D60_CAT = {
             1.01303f,    0.00610531f, -0.014971f,
             0.00769823f, 0.998165f,   -0.00503203f,
            -0.00284131f, 0.00468516f,  0.924507f
        };

        static const float3x3 D60_2_D65_CAT = {
             0.987224f,   -0.00611327f, 0.0159533f,
            -0.00759836f,  1.00186f,    0.00533002f,
             0.00307257f, -0.00509595f, 1.08168f
        };

        static const float3x3 XYZ_2_AP1_MAT = {
             1.6410233797f, -0.3248032942f, -0.2364246952f,
            -0.6636628587f,  1.6153315917f,  0.0167563477f,
             0.0117218943f, -0.0082844420f,  0.9883948585f
        };

        static const float3x3 AP1_2_XYZ_MAT = {
             0.6624541811f, 0.1340042065f, 0.1561876870f,
             0.2722287168f, 0.6740817658f, 0.0536895174f,
            -0.0055746495f, 0.0040607335f, 1.0103391003f
        };

        static const float3x3 AP0_2_AP1_MAT = {
             1.4514393161f, -0.2365107469f, -0.2149285693f,
            -0.0765537734f,  1.1762296998f, -0.0996759264f,
             0.0083161484f, -0.0060324498f,  0.9977163014f
        };

        static const float3x3 AP1_2_AP0_MAT = {
             0.6954522414f, 0.1406786965f, 0.1638690622f,
             0.0447945634f, 0.8596711185f, 0.0955343182f,
            -0.0055258826f, 0.0040252103f, 1.0015006723f
        };

        static const float3 AP1_RGB2Y = float3(0.2722287168f, 0.6740817658f, 0.0536895174f);

        float rgb_2_saturation(float3 rgb)
        {
            float minrgb = min(min(rgb.r, rgb.g), rgb.b);
            float maxrgb = max(max(rgb.r, rgb.g), rgb.b);
            return (max(maxrgb, 1e-10f) - max(minrgb, 1e-10f)) / max(maxrgb, 1e-2f);
        }

        float rgb_2_yc(float3 rgb)
        {
            const float ycRadiusWeight = 1.75f;
            float r = rgb.r;
            float g = rgb.g;
            float b = rgb.b;
            float chroma = sqrt(max(0.0f, b * (b - g) + g * (g - r) + r * (r - b)));
            return (b + g + r + ycRadiusWeight * chroma) / 3.0f;
        }

        float sigmoid_shaper(float x)
        {
            float t = max(1.0f - abs(0.5f * x), 0.0f);
            float y = 1.0f + sign(x) * (1.0f - t * t);
            return 0.5f * y;
        }

        float glow_fwd(float ycIn, float glowGainIn, float glowMid)
        {
            if (ycIn <= (2.0f / 3.0f) * glowMid)
                return glowGainIn;
            else if (ycIn >= 2.0f * glowMid)
                return 0.0f;
            else
                return glowGainIn * (glowMid / max(ycIn, 1e-6f) - 0.5f);
        }

        float rgb_2_hue(float3 rgb)
        {
            if (rgb.r == rgb.g && rgb.g == rgb.b)
                return 0.0f;
            float hue = (180.0f / PI) * atan2(sqrt(3.0f) * (rgb.g - rgb.b), 2.0f * rgb.r - rgb.g - rgb.b);
            if (hue < 0.0f)
                hue += 360.0f;
            return clamp(hue, 0.0f, 360.0f);
        }

        float center_hue(float hue, float centerH)
        {
            float hueCentered = hue - centerH;
            if (hueCentered < -180.0f) hueCentered += 360.0f;
            else if (hueCentered > 180.0f) hueCentered -= 360.0f;
            return hueCentered;
        }

        // TonemapCommon.ush: FilmToneMap (UE 5.2 defaults: Slope=0.88, Toe=0.55, Shoulder=0.26, BlackClip=0.0, WhiteClip=0.04)
        float3 FilmToneMapUE52(float3 ColorAP1)
        {
            const float FilmSlope     = 0.88f;
            const float FilmToe       = 0.55f;
            const float FilmShoulder  = 0.26f;
            const float FilmBlackClip = 0.00f;
            const float FilmWhiteClip = 0.04f;

            float3 ColorAP0 = mul(AP1_2_AP0_MAT, ColorAP1);

            // RRT Glow module
            const float RRT_GLOW_GAIN = 0.05f;
            const float RRT_GLOW_MID  = 0.08f;
            float saturation = rgb_2_saturation(ColorAP0);
            float ycIn = rgb_2_yc(ColorAP0);
            float s = sigmoid_shaper((saturation - 0.4f) / 0.2f);
            float addedGlow = 1.0f + glow_fwd(ycIn, RRT_GLOW_GAIN * s, RRT_GLOW_MID);
            ColorAP0 *= addedGlow;

            // RRT Red modifier
            const float RRT_RED_SCALE = 0.82f;
            const float RRT_RED_PIVOT = 0.03f;
            const float RRT_RED_HUE   = 0.0f;
            const float RRT_RED_WIDTH = 135.0f;
            float hue = rgb_2_hue(ColorAP0);
            float centeredHue = center_hue(hue, RRT_RED_HUE);
            float hueWeight = Square(smoothstep(0.0f, 1.0f, 1.0f - abs(2.0f * centeredHue / RRT_RED_WIDTH)));
            ColorAP0.r += hueWeight * saturation * (RRT_RED_PIVOT - ColorAP0.r) * (1.0f - RRT_RED_SCALE);

            // ACEScg working space
            float3 WorkingColor = max(0.0f, mul(AP0_2_AP1_MAT, ColorAP0));

            // Pre desaturate
            WorkingColor = lerp(dot(WorkingColor, AP1_RGB2Y).xxx, WorkingColor, 0.96f);

            const float ToeScale      = 1.0f + FilmBlackClip - FilmToe;
            const float ShoulderScale = 1.0f + FilmWhiteClip - FilmShoulder;
            const float InMatch  = 0.18f;
            const float OutMatch = 0.18f;

            const float bt = (OutMatch + FilmBlackClip) / ToeScale - 1.0f;
            const float ToeMatch = log10(InMatch) - 0.5f * log((1.0f + bt) / (1.0f - bt)) * (ToeScale / FilmSlope);
            const float StraightMatch = (1.0f - FilmToe) / FilmSlope - ToeMatch;
            const float ShoulderMatch = FilmShoulder / FilmSlope - StraightMatch;

            float3 LogColor = log10(max(WorkingColor, 1e-10f));
            float3 StraightColor = FilmSlope * (LogColor + StraightMatch);

            float3 ToeColor      = (-FilmBlackClip) + (2.0f * ToeScale) / (1.0f + exp((-2.0f * FilmSlope / ToeScale) * (LogColor - ToeMatch)));
            float3 ShoulderColor = (1.0f + FilmWhiteClip) - (2.0f * ShoulderScale) / (1.0f + exp((2.0f * FilmSlope / ShoulderScale) * (LogColor - ShoulderMatch)));

            ToeColor      = LogColor < ToeMatch      ? ToeColor      : StraightColor;
            ShoulderColor = LogColor > ShoulderMatch ? ShoulderColor : StraightColor;

            float3 t = saturate((LogColor - ToeMatch) / (ShoulderMatch - ToeMatch));
            t = ShoulderMatch < ToeMatch ? 1.0f - t : t;
            t = (3.0f - 2.0f * t) * t * t;
            float3 ToneColor = lerp(ToeColor, ShoulderColor, t);

            // Post desaturate
            ToneColor = lerp(dot(ToneColor, AP1_RGB2Y).xxx, ToneColor, 0.93f);
            return max(0.0f, ToneColor);
        }

        // GammaCorrectionCommon.ush: LinearToSrgbBranchless
        float3 LinearToSrgbBranchless(float3 lin)
        {
            lin = max(6.10352e-5f, lin);
            return min(lin * 12.92f, pow(max(lin, 0.00313067f), 1.0f / 2.4f) * 1.055f - 0.055f);
        }

        float GetSceneExposure()
        {
            // Auto exposure (EyeAdaptation buffer, PostProcessEyeAdaptation.usf) multiplied by the manual exposure / preview exposure
            return PostSettings.x > 0.5f ? EyeAdapt[0].x * Exposure : Exposure;
        }

        // UE 5.2 TonemapCommon.ush: ComputeVignetteMask
        float ComputeVignetteMask(float2 pix)
        {
            float2 ndc = (pix * ViewportSizeAndInv.zw) * 2.0f - 1.0f;
            float2 circle = ndc * ViewportSizeAndInv.xy / length(ViewportSizeAndInv.xy) * 1.41421356f;
            circle *= PostSettings.z;
            float tan2Angle = dot(circle, circle);
            float cos4Angle = Square(rcp(tan2Angle + 1.0f));
            return cos4Angle;
        }

        // PostProcessCombineLUTs.usf: Full UE 5.2 Color Grading + ExpandGamut + BlueCorrection + FilmToneMap + sRGB
        float3 ToDisplayUE52(float3 LinearColor)
        {
            LinearColor = max(0.0f, LinearColor * GetSceneExposure());

            const float3x3 sRGB_2_AP1 = mul(XYZ_2_AP1_MAT, mul(D65_2_D60_CAT, sRGB_2_XYZ_MAT));
            const float3x3 AP1_2_sRGB = mul(XYZ_2_sRGB_MAT, mul(D60_2_D65_CAT, AP1_2_XYZ_MAT));

            float3 ColorAP1 = mul(sRGB_2_AP1, LinearColor);

            // ExpandGamut = 1.0 (PostProcessCombineLUTs.usf:214-237)
            const float ExpandGamut = 1.0f;
            float LumaAP1 = dot(ColorAP1, AP1_RGB2Y);
            if (LumaAP1 > 1e-6f)
            {
                float3 ChromaAP1 = ColorAP1 / LumaAP1;
                float ChromaDistSqr = dot(ChromaAP1 - 1.0f, ChromaAP1 - 1.0f);
                float ExpandAmount = (1.0f - exp2(-4.0f * ChromaDistSqr)) * (1.0f - exp2(-4.0f * ExpandGamut * LumaAP1 * LumaAP1));

                const float3x3 Wide_2_XYZ_MAT = {
                     0.5441691f, 0.2395926f, 0.1666943f,
                     0.2394656f, 0.7021530f, 0.0583814f,
                    -0.0023439f, 0.0361834f, 1.0552183f
                };
                const float3x3 Wide_2_AP1 = mul(XYZ_2_AP1_MAT, Wide_2_XYZ_MAT);
                const float3x3 ExpandMat = mul(Wide_2_AP1, AP1_2_sRGB);
                float3 ColorExpand = mul(ExpandMat, ColorAP1);
                ColorAP1 = lerp(ColorAP1, ColorExpand, ExpandAmount);
            }

            // BlueCorrection = 0.6 (PostProcessCombineLUTs.usf:244-267)
            const float BlueCorrection = 0.6f;
            const float3x3 BlueCorrect = {
                0.9404372683f, -0.0183068787f, 0.0778696104f,
                0.0083786969f,  0.8286599939f, 0.1629613092f,
                0.0005471261f, -0.0008833746f, 1.0003362486f
            };
            const float3x3 BlueCorrectInv = {
                 1.06318f,     0.0233956f, -0.0865726f,
                -0.0106337f,   1.20632f,   -0.19569f,
                -0.000590887f, 0.00105248f, 0.999538f
            };
            const float3x3 BlueCorrectAP1    = mul(AP0_2_AP1_MAT, mul(BlueCorrect,    AP1_2_AP0_MAT));
            const float3x3 BlueCorrectInvAP1 = mul(AP0_2_AP1_MAT, mul(BlueCorrectInv, AP1_2_AP0_MAT));

            ColorAP1 = lerp(ColorAP1, mul(BlueCorrectAP1, ColorAP1), BlueCorrection);
            ColorAP1 = FilmToneMapUE52(ColorAP1);
            ColorAP1 = lerp(ColorAP1, mul(BlueCorrectInvAP1, ColorAP1), BlueCorrection);

            float3 FilmColor = max(0.0f, mul(AP1_2_sRGB, ColorAP1));
            return saturate(LinearToSrgbBranchless(saturate(FilmColor)));
        }

        // Classic Unreal Engine Editor Selection Highlight (2px anti-aliased #FF6B00 outline + subtle interior fill)
        float3 ApplyUnrealSelectionHighlight(float3 displayColor, int2 pix, float sceneDepth)
        {
            if (Background.w < 0.5f) return displayColor;

            float2 centerSel = SelectionMask.Load(int3(pix, 0));
            float m0 = centerSel.x;

            float d1 = max(
                max(SelectionMask.Load(int3(pix + int2( 1,  0), 0)).x, SelectionMask.Load(int3(pix + int2(-1,  0), 0)).x),
                max(SelectionMask.Load(int3(pix + int2( 0,  1), 0)).x, SelectionMask.Load(int3(pix + int2( 0, -1), 0)).x));

            float dDiag = max(
                max(SelectionMask.Load(int3(pix + int2( 1,  1), 0)).x, SelectionMask.Load(int3(pix + int2(-1,  1), 0)).x),
                max(SelectionMask.Load(int3(pix + int2( 1, -1), 0)).x, SelectionMask.Load(int3(pix + int2(-1, -1), 0)).x));

            float d2 = max(
                max(SelectionMask.Load(int3(pix + int2( 2,  0), 0)).x, SelectionMask.Load(int3(pix + int2(-2,  0), 0)).x),
                max(SelectionMask.Load(int3(pix + int2( 0,  2), 0)).x, SelectionMask.Load(int3(pix + int2( 0, -2), 0)).x));

            // UE 5.2 Starship SelectionOutline (#F5A623)
            float3 ueOrange = float3(0.961f, 0.651f, 0.137f);

            if (m0 < 0.5f)
            {
                float edge = max(d1, max(dDiag * 0.90f, d2 * 0.72f));
                if (edge > 0.01f)
                {
                    return lerp(displayColor, ueOrange, saturate(edge));
                }
            }
            else
            {
                bool isOccluded = (centerSel.y + 0.0005f < sceneDepth);
                if (isOccluded)
                {
                    float stripe = (((pix.x + pix.y) & 7) < 3) ? 0.14f : 0.04f;
                    return lerp(displayColor, ueOrange, stripe);
                }
            }

            return displayColor;
        }

        // =============================================================================
        // Tonemap & Buffer Visualization Pass (PSToneMap)
        // =============================================================================
        float4 PSToneMap(V i) : SV_Target
        {
            int3 p = int3(i.Pos.xy, 0);
            float4 scene = SceneColor.Load(p);
            float depth = DepthTex.Load(p);
            if (any(isnan(scene.rgb)) || any(isinf(scene.rgb)))
                scene.rgb = 0.0f;

            float3 outCol;
            if (Mode == 0 || Mode == 1)
            {
                float3 linearColor = scene.rgb;
                if (PostSettings.y > 0.0f)
                    linearColor += PostBloom.SampleLevel(LinearClampSampler, (i.Pos.xy) * ViewportSizeAndInv.zw, 0).rgb * PostSettings.y;
                if (PostSettings.z > 0.0f)
                    linearColor *= ComputeVignetteMask(i.Pos.xy);
                outCol = ToDisplayUE52(linearColor);
                return float4(ApplyUnrealSelectionHighlight(outCol, p.xy, depth), 1.0f);
            }

            float4 a = GBufferA.Load(p);
            float4 b = GBufferB.Load(p);
            float4 c = GBufferC.Load(p);
            float4 d = GBufferD.Load(p);
            float4 e = GBufferE.Load(p);

            if (Mode == 5)
            {
                if (dot(a.xyz, a.xyz) < 1e-4f)
                    return float4(ApplyUnrealSelectionHighlight(float3(0.055f, 0.058f, 0.064f), p.xy, depth), 1.0f);
                FGBufferData wireGb = DecodeGBuffer(a, b, c, d, e);
                float3 wireCol = lerp(float3(0.22f, 0.66f, 0.98f), LinearToSrgbBranchless(wireGb.BaseColor), 0.35f);
                return float4(ApplyUnrealSelectionHighlight(wireCol, p.xy, depth), 1.0f);
            }

            if (dot(a.xyz, a.xyz) < 1e-4f)
            {
                outCol = (Mode == 2 && depth > 0.0f) ? ToDisplayUE52(scene.rgb) : float3(0, 0, 0);
                return float4(ApplyUnrealSelectionHighlight(outCol, p.xy, depth), 1.0f);
            }

            FGBufferData GBuffer = DecodeGBuffer(a, b, c, d, e);

            if (Mode == 2)      outCol = LinearToSrgbBranchless(GBuffer.BaseColor);
            else if (Mode == 3) outCol = GBuffer.WorldNormal * 0.5f + 0.5f;
            else                outCol = float3(GBuffer.Metallic, GBuffer.Specular, GBuffer.Roughness);

            return float4(ApplyUnrealSelectionHighlight(outCol, p.xy, depth), 1.0f);
        }
        """;

    public const string TileCullHlsl = """
        struct GpuLocalLight
        {
            float4 PositionAndInvRadius;
            float4 ColorAndFalloff;
            float4 DirectionAndSourceRadius;
            float4 TangentAndSourceLength;
            float4 SpotAnglesAndParams;
        };

        cbuffer TileCullCb : register(b0)
        {
            float4 ClipToViewRow0;
            float4 ClipToViewRow1;
            float4 ClipToViewRow2;
            float4 ClipToViewRow3;
            float4 TWToViewRow0;
            float4 TWToViewRow1;
            float4 TWToViewRow2;
            float4 ScreenSizeAndInv;
            uint4  TileGridInfo; // x = tileCountX, y = tileCountY, z = activeLocalLightCount, w = 0
        };

        Texture2D<float> DepthTex : register(t0);
        StructuredBuffer<GpuLocalLight> LocalLights : register(t1);
        RWStructuredBuffer<uint> TileLightList : register(u0);

        groupshared uint gsMinZBits;
        groupshared uint gsMaxZBits;
        groupshared uint gsTileLightCount;
        groupshared uint gsTileLights[127];

        float3 ClipToViewPoint(float nx, float ny, float nz)
        {
            float4 v = nx * ClipToViewRow0 + ny * ClipToViewRow1 + nz * ClipToViewRow2 + ClipToViewRow3;
            return v.xyz / (abs(v.w) > 1e-6f ? v.w : 1e-6f);
        }

        [numthreads(16, 16, 1)]
        void CSTileLightCull(
            uint3 gid  : SV_GroupID,
            uint3 dtid : SV_DispatchThreadID,
            uint  gi   : SV_GroupIndex)
        {
            if (gi == 0u)
            {
                gsMinZBits = 0x7F7FFFFFu; // large positive float bits
                gsMaxZBits = 0u;          // +0.0f bits
                gsTileLightCount = 0u;
            }
            GroupMemoryBarrierWithGroupSync();

            uint width  = (uint) ScreenSizeAndInv.x;
            uint height = (uint) ScreenSizeAndInv.y;
            if (dtid.x < width && dtid.y < height)
            {
                float d = DepthTex.Load(int3(dtid.xy, 0));
                if (d > 1e-6f)
                {
                    float nx = (dtid.x + 0.5f) * ScreenSizeAndInv.z * 2.0f - 1.0f;
                    float ny = 1.0f - (dtid.y + 0.5f) * ScreenSizeAndInv.w * 2.0f;
                    float vz = max(ClipToViewPoint(nx, ny, d).z, 0.0f);
                    uint zBits = asuint(vz);
                    InterlockedMin(gsMinZBits, zBits);
                    InterlockedMax(gsMaxZBits, zBits);
                }
            }
            GroupMemoryBarrierWithGroupSync();

            float minZ = asfloat(gsMinZBits);
            float maxZ = asfloat(gsMaxZBits);

            if (maxZ > 0.0f && minZ <= maxZ)
            {
                float x0 = (gid.x * 16.0f) * ScreenSizeAndInv.z * 2.0f - 1.0f;
                float x1 = min((gid.x + 1u) * 16.0f, ScreenSizeAndInv.x) * ScreenSizeAndInv.z * 2.0f - 1.0f;
                float y0 = 1.0f - min((gid.y + 1u) * 16.0f, ScreenSizeAndInv.y) * ScreenSizeAndInv.w * 2.0f;
                float y1 = 1.0f - (gid.y * 16.0f) * ScreenSizeAndInv.w * 2.0f;

                float3 c00 = ClipToViewPoint(x0, y0, 0.5f);
                float3 c10 = ClipToViewPoint(x1, y0, 0.5f);
                float3 c01 = ClipToViewPoint(x0, y1, 0.5f);
                float3 c11 = ClipToViewPoint(x1, y1, 0.5f);
                float3 centerRay = normalize(c00 + c10 + c01 + c11);

                float3 nL = normalize(cross(c01, c00)); if (dot(nL, centerRay) < 0.0f) nL = -nL;
                float3 nR = normalize(cross(c10, c11)); if (dot(nR, centerRay) < 0.0f) nR = -nR;
                float3 nB = normalize(cross(c00, c10)); if (dot(nB, centerRay) < 0.0f) nB = -nB;
                float3 nT = normalize(cross(c11, c01)); if (dot(nT, centerRay) < 0.0f) nT = -nT;

                uint lightCount = TileGridInfo.z;
                for (uint li = gi; li < lightCount; li += 256u)
                {
                    float4 posAndInvR = LocalLights[li].PositionAndInvRadius;
                    float3 posTW = posAndInvR.xyz;
                    float r = 1.0f / max(posAndInvR.w, 1e-7f);
                    float3 posV = posTW.x * TWToViewRow0.xyz + posTW.y * TWToViewRow1.xyz + posTW.z * TWToViewRow2.xyz;

                    if (posV.z + r >= minZ && posV.z - r <= maxZ &&
                        dot(posV, nL) >= -r && dot(posV, nR) >= -r &&
                        dot(posV, nB) >= -r && dot(posV, nT) >= -r)
                    {
                        uint slot;
                        InterlockedAdd(gsTileLightCount, 1u, slot);
                        if (slot < 127u)
                            gsTileLights[slot] = li;
                    }
                }
            }
            GroupMemoryBarrierWithGroupSync();

            if (gid.x < TileGridInfo.x && gid.y < TileGridInfo.y)
            {
                uint tileBase = (gid.y * TileGridInfo.x + gid.x) * 128u;
                if (gsTileLightCount > 127u)
                {
                    if (gi == 0u)
                        TileLightList[tileBase] = 0xFFFFFFFFu;
                }
                else
                {
                    if (gi == 0u)
                        TileLightList[tileBase] = gsTileLightCount;
                    if (gi < gsTileLightCount)
                        TileLightList[tileBase + 1u + gi] = gsTileLights[gi];
                }
            }
        }
        """;
}
