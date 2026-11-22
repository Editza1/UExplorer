using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UExplorer.Rendering;

/// <summary>
/// Whole scene cascaded shadow maps for the directional light, following UE 5.2's FProjectedShadowInfo / FDirectionalLightSceneProxy
/// conventions: split distribution exponent 3 (CascadeDistributionExponent), 10% cascade transition (CascadeTransitionFraction),
/// 10% distance fade (ShadowDistanceFadeoutFraction), stabilized (texel snapped, sphere bounded) cascades, and manual PCF filtering
/// in the deferred light pass (ShadowProjectionCommon.ush, ShadowQuality 3).
/// The depth pass renders with the cooked material shaders themselves, so World Position Offset and masked opacity cast correctly.
/// </summary>
public sealed class NativeShadowMaps : IDisposable
{
    public const int MaxCascades = 4;
    public const float CascadeDistributionExponent = 3.0f;
    public const float CascadeTransitionFraction = 0.1f;
    public const float ShadowDistanceFadeoutFraction = 0.1f;
    public const float CasterExtrusion = 50000f; // cm towards the light, for casters outside the cascade sphere

    private readonly ID3D11Device _device;
    private ID3D11Texture2D? _texture;
    private ID3D11DepthStencilView[] _dsvs = [];
    public ID3D11ShaderResourceView? Srv { get; private set; }
    public ID3D11SamplerState ComparisonSampler { get; }
    public ID3D11RasterizerState Raster { get; }
    public ID3D11DepthStencilState DepthState { get; }
    public ID3D11Buffer Constants { get; }
    public int Resolution { get; private set; }
    public int CascadeCount { get; private set; }

    public readonly Matrix4x4[] WorldToShadowClip = new Matrix4x4[MaxCascades];
    public readonly Vector4[] CascadeSpheres = new Vector4[MaxCascades]; // xyz = center (world), w = radius
    public readonly float[] SplitFar = new float[MaxCascades];
    public Vector3 DirectionToLight { get; private set; }

    private readonly Matrix4x4[] _cachedWorldToShadowClip = new Matrix4x4[MaxCascades];
    private readonly Vector4[] _cachedCascadeSpheres = new Vector4[MaxCascades];
    private readonly float[] _cachedSplitFar = new float[MaxCascades];
    private readonly Vector3[] _cachedCameraPos = new Vector3[MaxCascades];
    private readonly Vector3[] _cachedCameraForward = new Vector3[MaxCascades];
    private readonly bool[] _sliceValid = new bool[MaxCascades];
    private Vector3 _cachedLightDir;
    private int _cachedResolution;
    private int _cachedCascadeCount;
    private float _cachedShadowDistance;
    private long _cachedSceneRevision = -1;

    private readonly Vector4[] _constantsScratch = new Vector4[24];
    private readonly float[] _texelScratch = new float[MaxCascades];
    private readonly float[] _biasScratch = new float[MaxCascades];

    public NativeShadowMaps(ID3D11Device device)
    {
        _device = device;
        ComparisonSampler = device.CreateSamplerState(new SamplerDescription(Filter.ComparisonMinMagLinearMipPoint, TextureAddressMode.Clamp, 0, 1, ComparisonFunction.LessEqual, 0, float.MaxValue));
        Raster = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid)
        {
            FrontCounterClockwise = true,
            DepthClipEnable = false, // shadow pancaking: casters in front of the near plane are clamped onto it
            SlopeScaledDepthBias = 2.0f,
            DepthBias = 0,
            DepthBiasClamp = 0.0f
        });
        DepthState = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.LessEqual));
        Constants = device.CreateBuffer(16 * 24, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
    }

    public ID3D11DepthStencilView CascadeDsv(int cascade) => _dsvs[cascade];

    public void EnsureCreated(int resolution)
    {
        if (_texture is null)
            EnsureTexture(resolution);
    }

    private void EnsureTexture(int resolution)
    {
        if (_texture is not null && Resolution == resolution)
            return;
        DisposeTexture();
        Resolution = resolution;
        _texture = _device.CreateTexture2D(new Texture2DDescription(Format.R32_Typeless, (uint) resolution, (uint) resolution, MaxCascades, 1, BindFlags.DepthStencil | BindFlags.ShaderResource));
        _dsvs = new ID3D11DepthStencilView[MaxCascades];
        for (var i = 0; i < MaxCascades; i++)
            _dsvs[i] = _device.CreateDepthStencilView(_texture, new DepthStencilViewDescription(_texture, DepthStencilViewDimension.Texture2DArray, Format.D32_Float, 0, (uint) i, 1));
        Srv = _device.CreateShaderResourceView(_texture, new ShaderResourceViewDescription(_texture, ShaderResourceViewDimension.Texture2DArray, Format.R32_Float, 0, 1, 0, MaxCascades));
    }

    /// <summary>FDirectionalLightSceneProxy::GetSplitDistance (geometric distribution).</summary>
    public static float GetSplitDistance(int cascade, int numCascades, float near, float far)
    {
        if (cascade <= 0) return near;
        if (cascade >= numCascades) return far;
        const float e = CascadeDistributionExponent;
        var distanceWithoutExponent = (far - near) / ((1f - MathF.Pow(e, numCascades)) / (1f - e));
        return near + distanceWithoutExponent * (1f - MathF.Pow(e, cascade)) / (1f - e);
    }

    /// <summary>Computes stabilized cascades. <paramref name="cornerRays"/> are the 4 frustum corner directions (unnormalized) from the camera.</summary>
    public void Prepare(int resolution, int cascades, float shadowDistance, Vector3 cameraPosition, Vector3 cameraForward,
        ReadOnlySpan<Vector3> cornerRays, Vector3 directionToLight) =>
        Prepare(resolution, cascades, shadowDistance, cameraPosition, cameraForward, cornerRays, directionToLight, 0, 0, enableCaching: false, out _);

    public void Prepare(int resolution, int cascades, float shadowDistance, Vector3 cameraPosition, Vector3 cameraForward,
        ReadOnlySpan<Vector3> cornerRays, Vector3 directionToLight,
        long sceneRevision, int frameIndex, bool enableCaching, out uint dirtyCascadeMask)
    {
        EnsureTexture(resolution);
        CascadeCount = Math.Clamp(cascades, 1, MaxCascades);
        DirectionToLight = Vector3.Normalize(directionToLight);
        var camFwdNorm = cameraForward.LengthSquared() > 1e-8f ? Vector3.Normalize(cameraForward) : Vector3.UnitX;
        const float near = 10f;

        if (!enableCaching ||
            resolution != _cachedResolution ||
            CascadeCount != _cachedCascadeCount ||
            MathF.Abs(shadowDistance - _cachedShadowDistance) > 1e-2f ||
            Vector3.Dot(DirectionToLight, _cachedLightDir) < 0.99995f ||
            sceneRevision != _cachedSceneRevision)
        {
            Array.Fill(_sliceValid, false);
            _cachedResolution = resolution;
            _cachedCascadeCount = CascadeCount;
            _cachedShadowDistance = shadowDistance;
            _cachedLightDir = DirectionToLight;
            _cachedSceneRevision = sceneRevision;
        }

        dirtyCascadeMask = 0u;
        var lightUp = MathF.Abs(DirectionToLight.Z) > 0.95f ? Vector3.UnitX : Vector3.UnitZ;
        Span<Vector3> corners = stackalloc Vector3[8];
        for (var c = 0; c < CascadeCount; c++)
        {
            var splitNear = GetSplitDistance(c, CascadeCount, near, shadowDistance);
            var splitFar = GetSplitDistance(c + 1, CascadeCount, near, shadowDistance);
            SplitFar[c] = splitFar;
            // overlap with the previous cascade so the transition region is covered by both
            splitNear = MathF.Max(near, splitNear - (splitFar - splitNear) * CascadeTransitionFraction);

            for (var k = 0; k < 4; k++)
            {
                var ray = cornerRays[k];
                var along = MathF.Max(Vector3.Dot(ray, cameraForward), 1e-4f);
                corners[k] = cameraPosition + ray * (splitNear / along);
                corners[k + 4] = cameraPosition + ray * (splitFar / along);
            }

            // Bounding sphere (stable under rotation): center on the view axis, radius to the far corners
            var mid = (splitNear + splitFar) * 0.5f;
            var center = cameraPosition + cameraForward * mid;
            var radius = 0f;
            foreach (var p in corners)
                radius = MathF.Max(radius, Vector3.Distance(center, p));
            radius = MathF.Ceiling(radius / 16f) * 16f;

            // Distant cascade (2 & 3) caching/time-slicing: reuse exact cached matrix & sphere when skipped so shadows never swim
            var canSkip = enableCaching && c >= 2 && _sliceValid[c] &&
                          Vector3.Distance(cameraPosition, _cachedCameraPos[c]) < _cachedCascadeSpheres[c].W * 0.15f &&
                          Vector3.Dot(camFwdNorm, _cachedCameraForward[c]) > 0.995f &&
                          !((c == 2 && (frameIndex & 1) == 0) || (c == 3 && (frameIndex & 3) == 3));

            if (canSkip)
            {
                WorldToShadowClip[c] = _cachedWorldToShadowClip[c];
                CascadeSpheres[c] = _cachedCascadeSpheres[c];
                SplitFar[c] = _cachedSplitFar[c];
                continue;
            }

            var eye = center + DirectionToLight * (radius + CasterExtrusion);
            var view = Matrix4x4.CreateLookAt(eye, center, lightUp);
            var proj = Matrix4x4.CreateOrthographic(radius * 2f, radius * 2f, 0f, radius * 2f + CasterExtrusion);

            // Texel snapping (stabilization)
            var viewProj = view * proj;
            var origin = Vector4.Transform(new Vector4(0, 0, 0, 1), viewProj);
            var half = resolution * 0.5f;
            var ox = origin.X * half;
            var oy = origin.Y * half;
            proj.M41 += (MathF.Round(ox) - ox) / half;
            proj.M42 += (MathF.Round(oy) - oy) / half;

            var finalMat = view * proj;
            var sphere = new Vector4(center, radius);
            WorldToShadowClip[c] = finalMat;
            CascadeSpheres[c] = sphere;

            _cachedWorldToShadowClip[c] = finalMat;
            _cachedCascadeSpheres[c] = sphere;
            _cachedSplitFar[c] = SplitFar[c];
            _cachedCameraPos[c] = cameraPosition;
            _cachedCameraForward[c] = camFwdNorm;
            _sliceValid[c] = true;
            dirtyCascadeMask |= 1u << c;
        }
    }

    /// <summary>Upload the deferred light pass constants (cbuffer ShadowParams : register(b1)).</summary>
    public void UploadConstants(ID3D11DeviceContext ctx, Vector3 cameraForward, float shadowDistance, bool enabled)
    {
        var data = _constantsScratch;
        for (var c = 0; c < MaxCascades; c++)
        {
            var m = c < CascadeCount ? WorldToShadowClip[c] : Matrix4x4.Identity;
            data[c * 4 + 0] = new Vector4(m.M11, m.M12, m.M13, m.M14);
            data[c * 4 + 1] = new Vector4(m.M21, m.M22, m.M23, m.M24);
            data[c * 4 + 2] = new Vector4(m.M31, m.M32, m.M33, m.M34);
            data[c * 4 + 3] = new Vector4(m.M41, m.M42, m.M43, m.M44);
        }
        data[16] = new Vector4(SplitFar[0], SplitFar[1], SplitFar[2], SplitFar[3]);
        data[17] = new Vector4(enabled ? CascadeCount : 0, Resolution > 0 ? 1f / Resolution : 0f, CascadeTransitionFraction, ShadowDistanceFadeoutFraction);
        data[18] = new Vector4(cameraForward, shadowDistance);
        // world size of one shadow texel per cascade (normal offset) and depth range (constant bias in clip z)
        var texel = _texelScratch;
        var bias = _biasScratch;
        for (var c = 0; c < MaxCascades; c++)
        {
            var r = CascadeSpheres[c].W;
            texel[c] = Resolution > 0 ? 2f * r / Resolution : 0f;
            var range = 2f * r + CasterExtrusion;
            bias[c] = range > 0 ? texel[c] * 0.5f / range : 0f;
        }
        data[19] = new Vector4(texel[0], texel[1], texel[2], texel[3]);
        data[20] = new Vector4(bias[0], bias[1], bias[2], bias[3]);
        data[21] = new Vector4(DirectionToLight, 0);

        var mapped = ctx.Map(Constants, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe
        {
            fixed (Vector4* p = data)
                Buffer.MemoryCopy(p, (void*) mapped.DataPointer, 16 * 24, 16 * 24);
        }
        ctx.Unmap(Constants, 0);
    }

    /// <summary>True when a world-space sphere touches the cascade's light-space box.</summary>
    public bool CasterAffectsCascade(int cascade, Vector3 center, float radius)
    {
        var p = Vector4.Transform(new Vector4(center, 1), WorldToShadowClip[cascade]);
        var r = CascadeSpheres[cascade].W;
        var rClip = radius / MathF.Max(r, 1e-3f);
        if (p.X < -1 - rClip || p.X > 1 + rClip || p.Y < -1 - rClip || p.Y > 1 + rClip)
            return false;
        // behind the receiver range (further from the light than the cascade's far end)
        return p.Z - radius / (2f * r + CasterExtrusion) <= 1f;
    }

    private void DisposeTexture()
    {
        foreach (var d in _dsvs) d.Dispose();
        _dsvs = [];
        Srv?.Dispose();
        Srv = null;
        _texture?.Dispose();
        _texture = null;
    }

    public void Dispose()
    {
        DisposeTexture();
        ComparisonSampler.Dispose();
        Raster.Dispose();
        DepthState.Dispose();
        Constants.Dispose();
    }
}
