using System.Numerics;
using System.Runtime.InteropServices;

using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

using UExplorer.Scene;
namespace UExplorer.Rendering;

public readonly record struct NativeNiagaraSpriteBatch(
    NiagaraSpriteEmitterDesc Desc,
    Matrix4x4 WorldMatrix,
    ID3D11ShaderResourceView? TextureSrv,
    NiagaraEmitterVmSimulation? VmSimOverride = null,
    NativeNiagaraMaterialPipeline? MaterialPipeline = null);

public readonly record struct NativeNiagaraMeshBatch(
    NiagaraMeshEmitterDesc Desc,
    NativeMeshLods Mesh,
    Matrix4x4 WorldMatrix,
    ID3D11ShaderResourceView? TextureSrv,
    NiagaraEmitterVmSimulation? VmSimOverride = null,
    NativeNiagaraMaterialPipeline? MaterialPipeline = null);

public sealed class NativeNiagaraRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeNiagaraMeshInstance
    {
        public Vector4 Row0;
        public Vector4 Row1;
        public Vector4 Row2;
        public Vector4 Row3;
        public Vector4 Color;
        public Vector4 DynParam0;
        public Vector4 Meta;
    }

    private const string SpriteHlsl = """
        cbuffer SpriteParams : register(b0)
        {
            float4 WorldToClipRow0;
            float4 WorldToClipRow1;
            float4 WorldToClipRow2;
            float4 WorldToClipRow3;
            float4 CameraRight;     // xyz = CameraRight in Unreal world space, w = ScaledTime
            float4 CameraUp;        // xyz = CameraUp in Unreal world space, w = HasTexture
            float4 EmitterOrigin;   // xyz = WorldOrigin + OriginOffset, w = SpawnRadius (or SubImageSize.Y in VM mode)
            float4 SpriteConfig;    // x = SizeStart, y = SizeMid, z = SizeEnd, w = SubUV_X
            float4 SpriteColorStart;// rgba = Color at t=0
            float4 SpriteColorEnd;  // rgba = Color at t=0.8
            float4 MotionParams;    // x = Speed, y = SeedOffset, z = IsSoftBubble, w = IsMeshPass
            float4 LocalToWorldRow0;
            float4 LocalToWorldRow1;
            float4 LocalToWorldRow2;
            float4 LocalToWorldRow3;
            float4 VolumeParams;    // x = VerticalOffsetZ, y = VPosStart, z = VPosEnd, w = Lifetime
            float4 ShapeParams;     // x = CylRadMin, y = CylRadMax, z = UseSphericalDome, w = VerticalLineLength
            float4 ExtraPhysics;    // x = GravityZ, y = TurbulenceStrength, z = AlphaMultiplier, w = ColorBrightness
        };

        Texture2D<float4> SpriteTex : register(t0);
        Buffer<float2>    MeshUVs   : register(t1);
        StructuredBuffer<float4> VmParticles : register(t2);
        Texture2D<float>  DepthTex  : register(t3);
        SamplerState LinearWrap     : register(s0);

        struct PSIn
        {
            float4 Pos   : SV_Position;
            float2 UV    : TEXCOORD0;
            float2 QuadUV: TEXCOORD1;
            float4 Color : TEXCOORD2;
        };

        float3 Hash31(float p)
        {
            float3 p3 = frac(float3(p, p, p) * float3(0.1031, 0.1030, 0.0973));
            p3 += dot(p3, p3.yzx + 33.33);
            return frac((p3.xxy + p3.yzz) * p3.zyx);
        }

        float4 TransformClip(float3 worldPos)
        {
            return worldPos.xxxx * WorldToClipRow0 + worldPos.yyyy * WorldToClipRow1 + worldPos.zzzz * WorldToClipRow2 + WorldToClipRow3;
        }

        PSIn VSMainSprite(uint vid : SV_VertexID, uint iid : SV_InstanceID)
        {
            PSIn o;
            float2 corner = float2((vid == 1 || vid == 2 || vid == 4) ? 1.0 : -1.0,
                                   (vid == 2 || vid == 4 || vid == 5) ? 1.0 : -1.0);
            float2 rawUV = corner * float2(0.5, -0.5) + 0.5;

            if (MotionParams.w > 2.5)
            {
                // Connected camera-facing ribbon segment from VmParticles[iid] to VmParticles[iid + 1]
                float4 a0 = VmParticles[iid * 4 + 0];
                float4 a1 = VmParticles[iid * 4 + 1];
                float4 b0 = VmParticles[(iid + 1) * 4 + 0];
                float4 b1 = VmParticles[(iid + 1) * 4 + 1];

                float3 worldA = (a0.xxxx * LocalToWorldRow0 + a0.yyyy * LocalToWorldRow1 + a0.zzzz * LocalToWorldRow2 + LocalToWorldRow3).xyz;
                float3 worldB = (b0.xxxx * LocalToWorldRow0 + b0.yyyy * LocalToWorldRow1 + b0.zzzz * LocalToWorldRow2 + LocalToWorldRow3).xyz;

                float tEnd = corner.x > 0.0 ? 1.0 : 0.0;
                float side = corner.y;
                float3 pt = lerp(worldA, worldB, tEnd);
                float width = max(lerp(a0.w, b0.w, tEnd), 1.0) * 0.5;

                float3 camForward = normalize(cross(CameraRight.xyz, CameraUp.xyz));
                float3 tangent = worldB - worldA;
                float tLen = length(tangent);
                float3 sideDir = tLen > 1e-3 ? normalize(cross(tangent / tLen, camForward)) : CameraUp.xyz;
                if (length(sideDir) < 1e-3) sideDir = CameraUp.xyz;

                float3 worldPos = pt + sideDir * (side * width);
                o.Pos = TransformClip(worldPos);
                float u0 = (float) iid / max(MotionParams.x, 1.0);
                float u1 = (float) (iid + 1) / max(MotionParams.x, 1.0);
                o.UV = float2(lerp(u0, u1, tEnd), side * -0.5 + 0.5);
                o.QuadUV = float2(0.0, side * 0.85);
                float4 col = lerp(a1, b1, tEnd);
                o.Color = float4(col.rgb * ExtraPhysics.w, saturate(col.a * ExtraPhysics.z));
                return o;
            }

            if (MotionParams.w > 1.5)
            {
                float4 p0 = VmParticles[iid * 4 + 0];
                float4 p1 = VmParticles[iid * 4 + 1];
                float4 p2 = VmParticles[iid * 4 + 2];

                float3 worldPosCenter = (p0.xxxx * LocalToWorldRow0
                                       + p0.yyyy * LocalToWorldRow1
                                       + p0.zzzz * LocalToWorldRow2
                                       + LocalToWorldRow3).xyz;

                float s = sin(p2.y);
                float c = cos(p2.y);
                float2 scaled = corner * float2(p0.w * 0.5, p2.x * 0.5);
                float2 rotOff = float2(scaled.x * c - scaled.y * s, scaled.x * s + scaled.y * c);
                float3 worldPos = worldPosCenter
                                + CameraRight.xyz * rotOff.x
                                + CameraUp.xyz * rotOff.y;
                o.Pos = TransformClip(worldPos);

                float subU = max(SpriteConfig.w, 1.0);
                float subV = max(EmitterOrigin.w, 1.0);
                float idx = floor(max(p2.z, 0.0));
                float cellX = fmod(idx, subU);
                float cellY = floor(idx / subU);
                o.UV = (rawUV + float2(cellX, cellY)) / float2(subU, subV);
                o.QuadUV = corner;
                o.Color = float4(p1.rgb * ExtraPhysics.w, saturate(p1.a * ExtraPhysics.z));
                return o;
            }

            float seed = (float) iid + MotionParams.y;
            float3 rnd = Hash31(seed * 17.31);
            float baseLife = max(VolumeParams.w, 0.1);
            float lifetime = lerp(baseLife * 0.85, baseLife * 1.15, rnd.x);
            float age = frac((CameraRight.w + rnd.y * lifetime) / lifetime);
            float elapsed = age * lifetime;

            float angle = rnd.z * 6.2831853 + sin(age * 4.0 + rnd.x * 6.28) * ExtraPhysics.y;
            float accelAge = age * age * 0.65 + age * 0.35;
            float zMotion = accelAge * MotionParams.x * lifetime + 0.5 * ExtraPhysics.x * elapsed * elapsed;

            float3 offset = float3(0.0, 0.0, 0.0);
            if (EmitterOrigin.w > 0.01 || ShapeParams.w > 0.01)
            {
                if (ShapeParams.z > 0.5)
                {
                    float vPos = lerp(VolumeParams.y, VolumeParams.z, rnd.y);
                    float zUnit = clamp(vPos * 2.0 - 1.0, -0.98, 0.98);
                    float rUnit = sqrt(saturate(1.0 - zUnit * zUnit));
                    float domeR = EmitterOrigin.w;
                    float domeH = ShapeParams.w > 0.01 ? ShapeParams.w : domeR;
                    offset = float3(
                        cos(angle) * rUnit * domeR,
                        sin(angle) * rUnit * domeR,
                        zUnit * domeH + zMotion + VolumeParams.x);
                }
                else
                {
                    float radFrac = lerp(ShapeParams.x, ShapeParams.y, rnd.x);
                    float radius = EmitterOrigin.w * radFrac;
                    float lineLen = max(ShapeParams.w, 10.0);
                    float zSpawn = (rnd.y - 0.5) * (lineLen * 0.05) + VolumeParams.x;
                    offset = float3(
                        cos(angle) * radius,
                        sin(angle) * radius,
                        zSpawn + zMotion);
                }
            }

            float3 center = EmitterOrigin.xyz + offset;

            float curSize = age < 0.5
                ? lerp(SpriteConfig.x, SpriteConfig.y, age * 2.0)
                : lerp(SpriteConfig.y, SpriteConfig.z, (age - 0.5) * 2.0);
            float halfRadius = curSize * 0.5;

            float3 worldPos = center + CameraRight.xyz * (corner.x * halfRadius) + CameraUp.xyz * (corner.y * halfRadius);
            o.Pos = TransformClip(worldPos);

            float subU = max(SpriteConfig.w, 1.0);
            o.UV = rawUV / subU;
            o.QuadUV = corner;

            float alphaCurve = sin(age * 3.14159265) * ExtraPhysics.z;
            float4 curCol = lerp(SpriteColorStart, SpriteColorEnd, smoothstep(0.0, 0.85, age));
            o.Color = float4(curCol.rgb * ExtraPhysics.w, saturate(curCol.a * alphaCurve));
            return o;
        }

        struct MeshVSIn
        {
            float3 Pos : ATTRIBUTE0;
            uint   Vid : SV_VertexID;
        };

        PSIn VSMainMesh(MeshVSIn input)
        {
            PSIn o;
            float3 worldPos = (input.Pos.xxxx * LocalToWorldRow0
                             + input.Pos.yyyy * LocalToWorldRow1
                             + input.Pos.zzzz * LocalToWorldRow2
                             + LocalToWorldRow3).xyz;
            o.Pos = TransformClip(worldPos);
            uint stride = max((uint) SpriteConfig.w, 1u);
            float2 uv = MeshUVs.Load((int) (input.Vid * stride));
            o.UV = uv;
            o.QuadUV = float2(0.0, 0.0);
            o.Color = SpriteColorStart;
            return o;
        }

        float4 PSMain(PSIn i) : SV_Target0
        {
            if (MotionParams.w > 0.5 && MotionParams.w < 1.5)
            {
                float4 texCol = CameraUp.w > 0.5 ? SpriteTex.Sample(LinearWrap, i.UV) : float4(1, 1, 1, 1);
                float mask = max(texCol.r, max(texCol.g, max(texCol.b, texCol.a)));
                float alpha = saturate(i.Color.a * lerp(0.75, 1.0, mask));
                float3 rgb = lerp(float3(1, 1, 1), texCol.rgb, 0.65) * i.Color.rgb * 1.8;
                return float4(rgb * alpha, alpha);
            }

            if (CameraUp.w > 0.5 && MotionParams.z < 0.5)
            {
                float4 texCol = SpriteTex.Sample(LinearWrap, i.UV);
                float texAlpha = texCol.a > 0.01 ? texCol.a : dot(texCol.rgb, float3(0.333, 0.333, 0.333));
                float edgeSoft = saturate(1.0 - max(abs(i.QuadUV.x), abs(i.QuadUV.y)) * 0.08);
                float finalA = saturate(i.Color.a * texAlpha * edgeSoft);
                if (finalA < 0.002) discard;
                float3 finalC = texCol.rgb * i.Color.rgb * 1.4;
                return float4(finalC * finalA, finalA);
            }

            float distSq = dot(i.QuadUV, i.QuadUV);
            if (distSq > 1.0) discard;

            if (MotionParams.z > 0.5)
            {
                float r = sqrt(distSq);
                float nz = sqrt(saturate(1.0 - distSq));
                float3 n = float3(i.QuadUV.x, -i.QuadUV.y, nz);
                float rim = pow(1.0 - nz, 2.2);
                float spec = pow(saturate(dot(n, normalize(float3(-0.45, 0.55, 0.70)))), 24.0);
                float edgeFade = smoothstep(1.0, 0.88, r);
                float bubbleAlpha = saturate((rim * 0.85 + spec * 0.9 + 0.08) * edgeFade * i.Color.a);
                float3 bubbleRgb = i.Color.rgb * (rim * 1.3 + 0.25) + spec.xxx * 1.6;
                return float4(bubbleRgb * bubbleAlpha, bubbleAlpha);
            }

            float radialMask = pow(saturate(1.0 - distSq), 1.8);
            float4 texCol = CameraUp.w > 0.5 ? SpriteTex.Sample(LinearWrap, i.UV) : float4(1, 1, 1, 1);
            float texAlpha = max(texCol.a, dot(texCol.rgb, float3(0.333, 0.333, 0.333)));
            float finalAlpha = saturate(i.Color.a * texAlpha * radialMask);
            float3 finalRgb = texCol.rgb * i.Color.rgb * (1.8 * radialMask);

            return float4(finalRgb * finalAlpha, finalAlpha);
        }
        """;

    private readonly ID3D11Device _device;
    private readonly List<IDisposable> _owned = [];
    private readonly ID3D11VertexShader _vsSprite;
    private readonly ID3D11VertexShader _vsMesh;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11InputLayout _meshInputLayout;
    private readonly ID3D11Buffer _cb;
    private readonly ID3D11Buffer _spriteAdapterCb;
    private readonly ID3D11Buffer _meshAdapterCb;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11BlendState _additiveBlend;
    private readonly ID3D11BlendState _translucentBlend;
    private readonly ID3D11ShaderResourceView _white2D;
    private readonly ID3D11Buffer _vmParticleBuf;
    private readonly ID3D11ShaderResourceView _vmParticleSrv;
    private readonly ID3D11Buffer _meshInstanceBuf;
    private readonly ID3D11ShaderResourceView _meshInstanceSrv;
    private readonly ID3D11Buffer _meshFloatDataBuf;
    private readonly ID3D11ShaderResourceView _meshFloatDataSrv;
    private readonly Dictionary<MaterialAsset, NativeNiagaraMaterialPipeline?> _localSpritePipelines = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MaterialAsset, NativeNiagaraMaterialPipeline?> _localMeshPipelines = new(ReferenceEqualityComparer.Instance);
    private const int MaxVmParticles = 2048;
    private float _frozenTime;

    private T Own<T>(T r) where T : IDisposable { _owned.Add(r); return r; }

    public NativeNiagaraRenderer(ID3D11Device device, ID3D11ShaderResourceView white2D)
    {
        _device = device;
        _white2D = white2D;

        var vsSpriteBytes = Compiler.Compile(SpriteHlsl, "VSMainSprite", "niagara_sprite_vs", "vs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();
        var vsMeshBytes = Compiler.Compile(SpriteHlsl, "VSMainMesh", "niagara_mesh_vs", "vs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();
        var psBytes = Compiler.Compile(SpriteHlsl, "PSMain", "niagara_ps", "ps_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();

        _vsSprite = Own(device.CreateVertexShader(vsSpriteBytes));
        _vsMesh = Own(device.CreateVertexShader(vsMeshBytes));
        _ps = Own(device.CreatePixelShader(psBytes));
        _meshInputLayout = Own(device.CreateInputLayout(
        [
            new InputElementDescription("ATTRIBUTE", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerVertexData, 0)
        ], vsMeshBytes));

        _cb = Own(device.CreateBuffer(18 * 16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _spriteAdapterCb = Own(device.CreateBuffer(11 * 16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _meshAdapterCb = Own(device.CreateBuffer(5 * 16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        _vmParticleBuf = Own(device.CreateBuffer(new BufferDescription(
            MaxVmParticles * 4 * 16,
            BindFlags.ShaderResource,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write,
            ResourceOptionFlags.BufferStructured,
            16)));
        _vmParticleSrv = Own(device.CreateShaderResourceView(_vmParticleBuf, new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = MaxVmParticles * 4 }
        }));

        var instStride = (uint) Marshal.SizeOf<NativeNiagaraMeshInstance>();
        _meshInstanceBuf = Own(device.CreateBuffer(new BufferDescription(
            (uint) MaxVmParticles * instStride,
            BindFlags.ShaderResource,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write,
            ResourceOptionFlags.BufferStructured,
            instStride)));
        _meshInstanceSrv = Own(device.CreateShaderResourceView(_meshInstanceBuf, new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = MaxVmParticles }
        }));

        const int maxFloatElements = MaxVmParticles * 24;
        _meshFloatDataBuf = Own(device.CreateBuffer(new BufferDescription(
            (uint) (maxFloatElements * sizeof(float)),
            BindFlags.ShaderResource,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write)));
        _meshFloatDataSrv = Own(device.CreateShaderResourceView(_meshFloatDataBuf, new ShaderResourceViewDescription(
            _meshFloatDataBuf, Format.R32_Float, 0, maxFloatElements)));

        _sampler = Own(device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Wrap, 0, 1, ComparisonFunction.Never, 0, float.MaxValue)));

        _additiveBlend = Own(device.CreateBlendState(new BlendDescription(Vortice.Direct3D11.Blend.One, Vortice.Direct3D11.Blend.One, Vortice.Direct3D11.Blend.One, Vortice.Direct3D11.Blend.InverseSourceAlpha)));
        _translucentBlend = Own(device.CreateBlendState(new BlendDescription(Vortice.Direct3D11.Blend.SourceAlpha, Vortice.Direct3D11.Blend.InverseSourceAlpha, Vortice.Direct3D11.Blend.One, Vortice.Direct3D11.Blend.InverseSourceAlpha)));
    }

    private NativeNiagaraMaterialPipeline? ResolvePipeline(MaterialAsset? mat, NativeNiagaraMaterialPipeline? overridePipe, bool isMesh)
    {
        if (overridePipe is not null)
            return overridePipe;
        if (mat is null)
            return null;

        var cache = isMesh ? _localMeshPipelines : _localSpritePipelines;
        if (!cache.TryGetValue(mat, out var pipe))
        {
            pipe = NativeNiagaraMaterialPipeline.Create(_device, mat, isMesh, out _);
            if (pipe is not null)
                Own(pipe);
            cache[mat] = pipe;
        }
        return pipe;
    }

    /// <summary>Rejects VM output that is clearly broken (all zero scale / size, non finite positions).</summary>
    private static bool VmLooksValid(NiagaraSimulatedParticle[] particles)
    {
        var usable = 0;
        foreach (var p in particles)
        {
            if (!float.IsFinite(p.Position.X + p.Position.Y + p.Position.Z) || p.Position.Length() > 1e7f)
                continue;
            if (p.Scale.LengthSquared() < 1e-10f && p.SpriteSize.LengthSquared() < 1e-10f)
                continue;
            usable++;
        }
        return usable * 2 >= particles.Length && usable > 0;
    }

    private float GetEffectiveTime(float rawTime)
    {
        if (!NiagaraAsset.GlobalPaused)
            _frozenTime = rawTime * NiagaraAsset.GlobalTimeScale;
        return _frozenTime;
    }

    public unsafe void RenderMeshParticles(
        ID3D11DeviceContext ctx,
        Matrix4x4 worldToClip,
        float time,
        IReadOnlyList<NativeNiagaraMeshBatch> batches,
        NativeSharedResources shared,
        ID3D11ShaderResourceView depthSrv,
        ID3D11RasterizerState cullNone,
        ID3D11RasterizerState cullBack,
        ref int drawCalls)
    {
        if (batches.Count == 0)
            return;

        var effectiveTime = GetEffectiveTime(time);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        foreach (var batch in batches)
        {
            var desc = batch.Desc;
            if (!desc.Enabled || desc.MaxParticles <= 0)
                continue;

            var lod = batch.Mesh.Lods[0];
            var particleCount = Math.Clamp(desc.MaxParticles, 0, MaxVmParticles);
            NiagaraSimulatedParticle[] vmParticles = [];
            var vmSim = batch.VmSimOverride ?? desc.VmSimulation;
            if (vmSim is not null)
            {
                if (vmSim.TrySimulate(effectiveTime * desc.TimeScale, out vmParticles) && vmParticles.Length > 0 && VmLooksValid(vmParticles))
                    particleCount = Math.Min(vmParticles.Length, MaxVmParticles);
                else
                    vmParticles = [];
            }
            if (particleCount <= 0)
                continue;

            var isLocalSpace = vmSim?.RuntimeLocalSpace ?? desc.LocalSpace;
            var applyBatchWorld = vmParticles.Length == 0 || isLocalSpace || !(vmSim?.SimulatedInWorldSpace ?? false);
            var hasDynParam = vmParticles.Length > 0
                ? (vmSim?.HasDynamicMaterialParameterAttr ?? false)
                : (desc.DynamicParam1Curve is not null || desc.DynamicParam2Curve is not null);

            var cookedPipe = ResolvePipeline(desc.Material, batch.MaterialPipeline, isMesh: true);
            if (cookedPipe is not null && cookedPipe.UsesCookedVertexShader)
            {
                var n = particleCount;
                var floatData = new float[23 * n];
                for (var p = 0; p < n; p++)
                {
                    Vector3 worldPos;
                    Quaternion worldRot;
                    Vector3 worldScale;
                    Vector3 worldVel;
                    Vector4 particleColor;
                    Vector4 dynParam;
                    float rnd;
                    float age;
                    if (vmParticles.Length > 0)
                    {
                        var vp = vmParticles[p];
                        if (applyBatchWorld && !batch.WorldMatrix.IsIdentity)
                        {
                            var localMat = Matrix4x4.CreateScale(vp.Scale) * Matrix4x4.CreateFromQuaternion(vp.MeshOrientation) * Matrix4x4.CreateTranslation(vp.Position);
                            var combined = localMat * batch.WorldMatrix;
                            if (!Matrix4x4.Decompose(combined, out worldScale, out worldRot, out worldPos))
                            {
                                worldPos = Vector3.Transform(vp.Position, batch.WorldMatrix);
                                worldRot = vp.MeshOrientation;
                                worldScale = vp.Scale;
                            }
                            worldVel = Vector3.TransformNormal(vp.Velocity, batch.WorldMatrix);
                        }
                        else
                        {
                            worldPos = vp.Position;
                            worldRot = vp.MeshOrientation;
                            worldScale = vp.Scale;
                            worldVel = vp.Velocity;
                        }
                        particleColor = vp.Color;
                        dynParam = vp.DynamicMaterialParameter;
                        rnd = vp.MaterialRandom;
                        age = vp.NormalizedAge;
                    }
                    else
                    {
                        var (localAnim, animColor) = desc.EvaluateInstance(p, effectiveTime);
                        var instanceWorld = localAnim * batch.WorldMatrix;
                        if (!Matrix4x4.Decompose(instanceWorld, out worldScale, out worldRot, out worldPos))
                        {
                            worldPos = instanceWorld.Translation;
                            worldRot = Quaternion.Identity;
                            worldScale = Vector3.One;
                        }
                        worldVel = Vector3.Zero;
                        particleColor = animColor;
                        age = ((effectiveTime * desc.TimeScale / MathF.Max(0.05f, desc.Lifetime)) + (float) p / Math.Max(1, desc.MaxParticles) * desc.PhaseSpread) % 1f;
                        if (age < 0f) age += 1f;
                        var dp1 = desc.DynamicParam1Curve?.Sample(age) ?? 1f;
                        var dp2 = desc.DynamicParam2Curve?.Sample(age) ?? 1f;
                        dynParam = new Vector4(dp1, dp2, dp2, 1f);
                        rnd = (p * 0.61803398875f) % 1f;
                    }

                    if (worldRot.LengthSquared() < 1e-8f)
                        worldRot = Quaternion.Identity;
                    else
                        worldRot = Quaternion.Normalize(worldRot);

                    floatData[0 * n + p] = worldPos.X;
                    floatData[1 * n + p] = worldPos.Y;
                    floatData[2 * n + p] = worldPos.Z;

                    floatData[3 * n + p] = particleColor.X;
                    floatData[4 * n + p] = particleColor.Y;
                    floatData[5 * n + p] = particleColor.Z;
                    floatData[6 * n + p] = particleColor.W;

                    floatData[7 * n + p] = worldRot.X;
                    floatData[8 * n + p] = worldRot.Y;
                    floatData[9 * n + p] = worldRot.Z;
                    floatData[10 * n + p] = worldRot.W;

                    floatData[11 * n + p] = worldScale.X;
                    floatData[12 * n + p] = worldScale.Y;
                    floatData[13 * n + p] = worldScale.Z;

                    floatData[14 * n + p] = dynParam.X;
                    floatData[15 * n + p] = dynParam.Y;
                    floatData[16 * n + p] = dynParam.Z;
                    floatData[17 * n + p] = dynParam.W;

                    floatData[18 * n + p] = age;
                    floatData[19 * n + p] = rnd;

                    floatData[20 * n + p] = worldVel.X;
                    floatData[21 * n + p] = worldVel.Y;
                    floatData[22 * n + p] = worldVel.Z;
                }

                var mappedFloat = ctx.Map(_meshFloatDataBuf, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var bytes = MemoryMarshal.AsBytes(floatData.AsSpan());
                    bytes.CopyTo(new Span<byte>((void*) mappedFloat.DataPointer, bytes.Length));
                }
                finally { ctx.Unmap(_meshFloatDataBuf, 0); }

                cookedPipe.BindMesh(ctx, shared, lod, _meshFloatDataSrv, n, hasDynParam, desc.FacingMode, desc.LockedAxisEnable, desc.LockedAxis, desc.LockedAxisSpace);
                ctx.RSSetState(cookedPipe.TwoSided ? cullNone : cullBack);
                ctx.IASetVertexBuffer(0, lod.Positions, 12u, 0u);
                ctx.IASetVertexBuffer(1, lod.TangentBuffer, 8u, 0u);
                ctx.IASetVertexBuffer(2, lod.TexCoordBuffer, (uint) (lod.UvChannelCount * 8), 0u);
                ctx.IASetVertexBuffer(3, lod.ColorBuffer, lod.HasVertexColors ? 4u : 0u, 0u);
                ctx.IASetIndexBuffer(lod.Indices, Format.R32_UInt, 0);

                foreach (var section in lod.Sections)
                {
                    ctx.DrawIndexedInstanced((uint) section.IndexCount, (uint) n, (uint) section.FirstIndex, 0, 0);
                    drawCalls++;
                }
                continue;
            }

            if (cookedPipe is not null)
            {
                var instData = new NativeNiagaraMeshInstance[particleCount];
                for (var p = 0; p < particleCount; p++)
                {
                    Matrix4x4 instanceWorld;
                    Vector4 particleColor;
                    Vector4 dynParam;
                    float rnd;
                    if (vmParticles.Length > 0)
                    {
                        var vp = vmParticles[p];
                        var local = Matrix4x4.CreateScale(vp.Scale) * Matrix4x4.CreateFromQuaternion(vp.MeshOrientation) * Matrix4x4.CreateTranslation(vp.Position);
                        instanceWorld = applyBatchWorld ? local * batch.WorldMatrix : local;
                        particleColor = vp.Color;
                        dynParam = vp.DynamicMaterialParameter;
                        rnd = vp.MaterialRandom;
                    }
                    else
                    {
                        var (localAnim, animColor) = desc.EvaluateInstance(p, effectiveTime);
                        instanceWorld = localAnim * batch.WorldMatrix;
                        particleColor = animColor;
                        var age = ((effectiveTime * desc.TimeScale / MathF.Max(0.05f, desc.Lifetime)) + (float) p / Math.Max(1, desc.MaxParticles) * desc.PhaseSpread) % 1f;
                        if (age < 0f) age += 1f;
                        var dp1 = desc.DynamicParam1Curve?.Sample(age) ?? 1f;
                        var dp2 = desc.DynamicParam2Curve?.Sample(age) ?? 1f;
                        dynParam = new Vector4(dp1, dp2, dp2, 1f);
                        rnd = (p * 0.61803398875f) % 1f;
                    }

                    instData[p] = new NativeNiagaraMeshInstance
                    {
                        Row0 = new Vector4(instanceWorld.M11, instanceWorld.M12, instanceWorld.M13, instanceWorld.M14),
                        Row1 = new Vector4(instanceWorld.M21, instanceWorld.M22, instanceWorld.M23, instanceWorld.M24),
                        Row2 = new Vector4(instanceWorld.M31, instanceWorld.M32, instanceWorld.M33, instanceWorld.M34),
                        Row3 = new Vector4(instanceWorld.M41, instanceWorld.M42, instanceWorld.M43, instanceWorld.M44),
                        Color = particleColor,
                        DynParam0 = dynParam,
                        Meta = new Vector4(rnd, 0f, 0f, 0f)
                    };
                }

                var mappedInst = ctx.Map(_meshInstanceBuf, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var bytes = MemoryMarshal.AsBytes(instData.AsSpan());
                    bytes.CopyTo(new Span<byte>((void*) mappedInst.DataPointer, bytes.Length));
                }
                finally { ctx.Unmap(_meshInstanceBuf, 0); }

                var cbVecs = new Vector4[5];
                WriteMatrix(cbVecs, 0, worldToClip);
                cbVecs[4] = new Vector4(BitConverter.Int32BitsToSingle(lod.UvChannelCount), 0f, 0f, 0f);
                var mappedCb = ctx.Map(_meshAdapterCb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var bytes = MemoryMarshal.AsBytes(cbVecs.AsSpan());
                    bytes.CopyTo(new Span<byte>((void*) mappedCb.DataPointer, bytes.Length));
                }
                finally { ctx.Unmap(_meshAdapterCb, 0); }

                cookedPipe.Bind(ctx, shared, hasDynParam);
                ctx.RSSetState(cookedPipe.TwoSided ? cullNone : cullBack);
                ctx.VSSetConstantBuffer(0, _meshAdapterCb);
                ctx.VSSetShaderResources(0, [_meshInstanceSrv, lod.TexCoords]);
                ctx.IASetVertexBuffer(0, lod.Positions, 12u, 0u);
                ctx.IASetIndexBuffer(lod.Indices, Format.R32_UInt, 0);

                foreach (var section in lod.Sections)
                {
                    ctx.DrawIndexedInstanced((uint) section.IndexCount, (uint) particleCount, (uint) section.FirstIndex, 0, 0);
                    drawCalls++;
                }
                continue;
            }

            // Fallback path when no cooked material shader exists
            ctx.IASetInputLayout(_meshInputLayout);
            ctx.VSSetShader(_vsMesh);
            ctx.PSSetShader(_ps);
            ctx.VSSetConstantBuffer(0, _cb);
            ctx.PSSetConstantBuffer(0, _cb);
            ctx.PSSetSampler(0, _sampler);
            ctx.RSSetState(cullNone);
            ctx.OMSetBlendState(_additiveBlend);

            var cbData = new Vector4[18];
            WriteMatrix(cbData, 0, worldToClip);
            ctx.IASetVertexBuffer(0, lod.Positions, 12u, 0u);
            ctx.IASetIndexBuffer(lod.Indices, Format.R32_UInt, 0);
            ctx.VSSetShaderResource(1, lod.TexCoords);
            ctx.PSSetShaderResource(0, batch.TextureSrv ?? _white2D);

            for (var p = 0; p < particleCount; p++)
            {
                Matrix4x4 instanceWorld;
                Vector4 particleColor;
                if (vmParticles.Length > 0)
                {
                    var vp = vmParticles[p];
                    var local = Matrix4x4.CreateScale(vp.Scale) * Matrix4x4.CreateFromQuaternion(vp.MeshOrientation) * Matrix4x4.CreateTranslation(vp.Position);
                    instanceWorld = applyBatchWorld ? local * batch.WorldMatrix : local;
                    particleColor = vp.Color;
                }
                else
                {
                    var (localAnim, animColor) = desc.EvaluateInstance(p, effectiveTime);
                    instanceWorld = localAnim * batch.WorldMatrix;
                    particleColor = animColor;
                }

                cbData[4] = new Vector4(0, 0, 0, effectiveTime);
                cbData[5] = new Vector4(0, 0, 0, batch.TextureSrv is not null ? 1f : 0f);
                cbData[7] = new Vector4(1f, 1f, 1f, lod.UvChannelCount);
                cbData[8] = particleColor;
                cbData[10] = new Vector4(0, 0, 0, 1f);
                WriteMatrix(cbData, 11, instanceWorld);

                var mapped = ctx.Map(_cb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var bytes = MemoryMarshal.AsBytes(cbData.AsSpan());
                    bytes.CopyTo(new Span<byte>((void*) mapped.DataPointer, bytes.Length));
                }
                finally { ctx.Unmap(_cb, 0); }

                foreach (var section in lod.Sections)
                {
                    ctx.DrawIndexed((uint) section.IndexCount, (uint) section.FirstIndex, 0);
                    drawCalls++;
                }
            }
        }

        ctx.OMSetBlendState(null);
    }

    public unsafe void RenderSprites(
        ID3D11DeviceContext ctx,
        Matrix4x4 worldToClip,
        Vector3 camRight,
        Vector3 camUp,
        float time,
        IReadOnlyList<NativeNiagaraSpriteBatch> batches,
        NativeSharedResources shared,
        ID3D11ShaderResourceView depthSrv,
        ID3D11RasterizerState cullNone,
        ID3D11RasterizerState cullBack,
        ref int drawCalls)
    {
        if (batches.Count == 0)
            return;

        _ = cullBack;
        var effectiveTime = GetEffectiveTime(time);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        for (var i = 0; i < batches.Count; i++)
        {
            var batch = batches[i];
            var desc = batch.Desc;
            if (!desc.Enabled || desc.MaxParticles <= 0)
                continue;

            var scale = MathF.Max(new Vector3(batch.WorldMatrix.M11, batch.WorldMatrix.M12, batch.WorldMatrix.M13).Length(), 1f);
            var origin = batch.WorldMatrix.Translation + desc.OriginOffset * scale;

            NiagaraSimulatedParticle[] vmParticles = [];
            var useVm = false;
            var vmSim = batch.VmSimOverride ?? desc.VmSimulation;
            if (vmSim is not null)
            {
                useVm = vmSim.TrySimulate(effectiveTime * desc.TimeScale, out vmParticles) &&
                        vmParticles.Length > 0;
                if (useVm && !VmLooksValid(vmParticles))
                    useVm = false;
            }

            var isLocalSpace = vmSim?.RuntimeLocalSpace ?? desc.LocalSpace;
            var applyBatchWorld = !useVm || isLocalSpace || !(vmSim?.SimulatedInWorldSpace ?? false);
            var drawCount = useVm
                ? Math.Min(vmParticles!.Length, MaxVmParticles)
                : Math.Min(desc.MaxParticles, MaxVmParticles);
            if (drawCount <= 0)
                continue;

            var cookedPipe = !desc.IsRibbon
                ? ResolvePipeline(desc.Material, batch.MaterialPipeline, isMesh: false)
                : null;

            if (cookedPipe is not null)
            {
                var vmData = new Vector4[drawCount * 4];
                if (useVm)
                {
                    for (var p = 0; p < drawCount; p++)
                    {
                        var vp = vmParticles![p];
                        var szX = MathF.Max(0.5f, vp.SpriteSize.X * desc.SizeMultiplier * scale);
                        var szY = MathF.Max(0.5f, vp.SpriteSize.Y * desc.SizeMultiplier * scale);
                        vmData[p * 4 + 0] = new Vector4(vp.Position + desc.OriginOffset, szX);
                        vmData[p * 4 + 1] = vp.Color;
                        vmData[p * 4 + 2] = new Vector4(szY, vp.SpriteRotation * (MathF.PI / 180f), vp.SubImageIndex, vp.MaterialRandom);
                        vmData[p * 4 + 3] = vp.DynamicMaterialParameter;
                    }
                }
                else
                {
                    var colorStart = desc.ColorCurve is not null ? desc.ColorCurve.Sample(0.05f) : desc.Color;
                    var colorEnd = desc.ColorCurve is not null ? desc.ColorCurve.Sample(0.65f) : desc.Color;
                    for (var p = 0; p < drawCount; p++)
                    {
                        var t = drawCount > 1 ? p / (float) (drawCount - 1) : 0.5f;
                        var sz = desc.SpriteSize * desc.SizeMultiplier * scale;
                        var col = Vector4.Lerp(colorStart, colorEnd, t);
                        vmData[p * 4 + 0] = new Vector4(desc.OriginOffset, MathF.Max(0.5f, sz.X));
                        vmData[p * 4 + 1] = col;
                        vmData[p * 4 + 2] = new Vector4(MathF.Max(0.5f, sz.Y), 0f, 0f, (p * 0.61803398875f) % 1f);
                        vmData[p * 4 + 3] = Vector4.One;
                    }
                }

                var mappedVm = ctx.Map(_vmParticleBuf, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var bytes = MemoryMarshal.AsBytes(vmData.AsSpan());
                    bytes.CopyTo(new Span<byte>((void*) mappedVm.DataPointer, bytes.Length));
                }
                finally { ctx.Unmap(_vmParticleBuf, 0); }

                var cbAdapter = new Vector4[11];
                WriteMatrix(cbAdapter, 0, worldToClip);
                cbAdapter[4] = new Vector4(camRight, effectiveTime * desc.TimeScale);
                cbAdapter[5] = new Vector4(camUp, 0f);
                WriteMatrix(cbAdapter, 6, applyBatchWorld ? batch.WorldMatrix : Matrix4x4.Identity);
                cbAdapter[10] = new Vector4(desc.SubImageSize.X, desc.SubImageSize.Y, desc.PivotInUVSpace.X, desc.PivotInUVSpace.Y);

                var mappedCb = ctx.Map(_spriteAdapterCb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var bytes = MemoryMarshal.AsBytes(cbAdapter.AsSpan());
                    bytes.CopyTo(new Span<byte>((void*) mappedCb.DataPointer, bytes.Length));
                }
                finally { ctx.Unmap(_spriteAdapterCb, 0); }

                cookedPipe.Bind(ctx, shared, vmSim?.HasDynamicMaterialParameterAttr ?? false);
                ctx.RSSetState(cullNone);
                ctx.VSSetConstantBuffer(0, _spriteAdapterCb);
                ctx.VSSetShaderResource(0, _vmParticleSrv);
                ctx.DrawInstanced(6u, (uint) drawCount, 0u, 0u);
                drawCalls++;
                continue;
            }

            // Fallback / Ribbon path via SpriteHlsl
            var cbData = new Vector4[18];
            WriteMatrix(cbData, 0, worldToClip);

            var baseSz = desc.SpriteSize.X * desc.SizeMultiplier;
            var sizeStart = baseSz;
            var sizeMid = baseSz;
            var sizeEnd = baseSz;
            if (desc.UniformSizeCurve is not null)
            {
                var ratio = baseSz / MathF.Max(0.1f, desc.UniformSizeCurve.Sample(0.5f));
                sizeStart = desc.UniformSizeCurve.Sample(0f) * ratio;
                sizeMid = desc.UniformSizeCurve.Sample(0.5f) * ratio;
                sizeEnd = desc.UniformSizeCurve.Sample(1f) * ratio;
            }
            if (desc.ScaleFactorCurve is not null)
            {
                sizeStart *= desc.ScaleFactorCurve.Sample(0f);
                sizeMid *= desc.ScaleFactorCurve.Sample(0.5f);
                sizeEnd *= desc.ScaleFactorCurve.Sample(1f);
            }

            var fbColorStart = desc.ColorCurve is not null ? desc.ColorCurve.Sample(0.05f) : desc.Color;
            var fbColorEnd = desc.ColorCurve is not null ? desc.ColorCurve.Sample(0.65f) : desc.Color * new Vector4(0.8f, 0.9f, 1.0f, 0.7f);

            if (useVm)
            {
                var vmData = new Vector4[drawCount * 4];
                for (var p = 0; p < drawCount; p++)
                {
                    var vp = vmParticles![p];
                    var baseW = desc.IsRibbon && vp.SpriteSize.X <= 0.01f ? desc.RibbonWidth : vp.SpriteSize.X;
                    var szX = MathF.Max(0.5f, baseW * desc.SizeMultiplier * scale);
                    var szY = MathF.Max(0.5f, vp.SpriteSize.Y * desc.SizeMultiplier * scale);
                    vmData[p * 4 + 0] = new Vector4(vp.Position + desc.OriginOffset, szX);
                    vmData[p * 4 + 1] = vp.Color;
                    vmData[p * 4 + 2] = new Vector4(szY, vp.SpriteRotation * (MathF.PI / 180f), vp.SubImageIndex, vp.MaterialRandom);
                    vmData[p * 4 + 3] = vp.DynamicMaterialParameter;
                }

                var mappedVm = ctx.Map(_vmParticleBuf, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var bytes = MemoryMarshal.AsBytes(vmData.AsSpan());
                    bytes.CopyTo(new Span<byte>((void*) mappedVm.DataPointer, bytes.Length));
                }
                finally { ctx.Unmap(_vmParticleBuf, 0); }

                ctx.VSSetShaderResource(2, _vmParticleSrv);
                WriteMatrix(cbData, 11, applyBatchWorld ? batch.WorldMatrix : Matrix4x4.Identity);
            }
            else if (desc.IsRibbon && drawCount >= 2)
            {
                var segCount = Math.Clamp(drawCount, 4, 64);
                drawCount = segCount;
                var vmData = new Vector4[segCount * 4];
                var len = desc.VerticalLineLength > 1f ? desc.VerticalLineLength : 240f;
                for (var p = 0; p < segCount; p++)
                {
                    var t = p / (float) (segCount - 1);
                    var wave = MathF.Sin(t * 9f - effectiveTime * desc.TimeScale * 6f) * (desc.TurbulenceStrength * 25f);
                    var pos = desc.OriginOffset + new Vector3(wave, MathF.Cos(t * 7f - effectiveTime * 4f) * (desc.TurbulenceStrength * 15f), t * len);
                    var col = desc.ColorCurve is not null ? desc.ColorCurve.Sample(t) : Vector4.Lerp(fbColorStart, fbColorEnd, t);
                    var w = MathF.Max(1f, desc.RibbonWidth * desc.SizeMultiplier * scale * MathF.Sin(Math.Clamp(t, 0.05f, 0.95f) * MathF.PI));
                    vmData[p * 4 + 0] = new Vector4(pos, w);
                    vmData[p * 4 + 1] = col;
                    vmData[p * 4 + 2] = new Vector4(w, 0f, 0f, 1f);
                    vmData[p * 4 + 3] = Vector4.One;
                }

                var mappedVm = ctx.Map(_vmParticleBuf, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var bytes = MemoryMarshal.AsBytes(vmData.AsSpan());
                    bytes.CopyTo(new Span<byte>((void*) mappedVm.DataPointer, bytes.Length));
                }
                finally { ctx.Unmap(_vmParticleBuf, 0); }

                ctx.VSSetShaderResource(2, _vmParticleSrv);
                WriteMatrix(cbData, 11, batch.WorldMatrix);
                useVm = true;
            }

            var isRibbonMode = desc.IsRibbon && useVm && drawCount >= 2;
            var instanceDraws = isRibbonMode ? drawCount - 1 : drawCount;

            cbData[4] = new Vector4(camRight, effectiveTime * desc.TimeScale);
            cbData[5] = new Vector4(camUp, batch.TextureSrv is not null ? 1f : 0f);
            cbData[6] = new Vector4(origin, useVm ? desc.SubImageSize.Y : desc.SpawnRadius * scale);
            cbData[7] = new Vector4(sizeStart * scale, sizeMid * scale, sizeEnd * scale, desc.SubImageSize.X);
            cbData[8] = fbColorStart;
            cbData[9] = fbColorEnd;
            cbData[10] = new Vector4(isRibbonMode ? instanceDraws : desc.VelocitySpeed * scale, i * 13.37f, desc.IsSoftBubble ? 1f : 0f, isRibbonMode ? 3f : (useVm ? 2f : 0f));
            cbData[15] = new Vector4(desc.VerticalOffsetZ * scale, desc.VPosStart, desc.VPosEnd, desc.Lifetime);
            cbData[16] = new Vector4(desc.CylinderRadiusMin, desc.CylinderRadiusMax, desc.UseSphericalDome ? 1f : 0f, desc.VerticalLineLength * scale);
            cbData[17] = new Vector4(desc.GravityZ * scale, desc.TurbulenceStrength, desc.AlphaMultiplier, desc.ColorBrightness);

            var mapped = ctx.Map(_cb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var bytes = MemoryMarshal.AsBytes(cbData.AsSpan());
                bytes.CopyTo(new Span<byte>((void*) mapped.DataPointer, bytes.Length));
            }
            finally { ctx.Unmap(_cb, 0); }

            ctx.IASetInputLayout(null);
            ctx.VSSetShader(_vsSprite);
            ctx.PSSetShader(_ps);
            ctx.VSSetConstantBuffer(0, _cb);
            ctx.PSSetConstantBuffer(0, _cb);
            ctx.PSSetSampler(0, _sampler);
            ctx.RSSetState(cullNone);
            ctx.OMSetBlendState(desc.IsAdditive && !desc.IsSoftBubble ? _additiveBlend : _translucentBlend);
            ctx.PSSetShaderResource(0, batch.TextureSrv ?? _white2D);
            ctx.DrawInstanced(6u, (uint) instanceDraws, 0u, 0u);
            drawCalls++;
        }

        ctx.OMSetBlendState(null);
    }

    private static void WriteMatrix(Vector4[] dst, int offset, Matrix4x4 m)
    {
        dst[offset + 0] = new Vector4(m.M11, m.M12, m.M13, m.M14);
        dst[offset + 1] = new Vector4(m.M21, m.M22, m.M23, m.M24);
        dst[offset + 2] = new Vector4(m.M31, m.M32, m.M33, m.M34);
        dst[offset + 3] = new Vector4(m.M41, m.M42, m.M43, m.M44);
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
            _owned[i].Dispose();
    }
}
