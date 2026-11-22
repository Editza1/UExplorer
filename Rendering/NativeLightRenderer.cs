using System.Numerics;
using System.Runtime.InteropServices;
using CUE4Parse.UE4.Assets.Exports.Texture;

using Vortice.Direct3D11;
using Vortice.DXGI;

using UExplorer.Scene;
namespace UExplorer.Rendering;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct NativeGpuLocalLight
{
    /// <summary>xyz = TranslatedWorldPosition (cm, relative to camera), w = InvRadius (1 / cm)</summary>
    public Vector4 PositionAndInvRadius;

    /// <summary>xyz = Color * Intensity (shader units), w = FalloffExponent (0 = inverse squared falloff)</summary>
    public Vector4 ColorAndFalloff;

    /// <summary>xyz = Direction (-Forward in Unreal world space), w = SourceRadius (cm, or Rect half-width cm)</summary>
    public Vector4 DirectionAndSourceRadius;

    /// <summary>xyz = Tangent (Unreal world space), w = SourceLength (cm, or Rect half-height cm)</summary>
    public Vector4 TangentAndSourceLength;

    /// <summary>
    /// x = SpotAngles.x (CosOuterCone) or RectBarnCosAngle,
    /// y = SpotAngles.y (1 / (CosInnerCone - CosOuterCone)) or RectBarnLength (cm),
    /// z = SoftSourceRadius (cm),
    /// w = LightType (1 = Point, 2 = Spot, 3 = Rect)
    /// </summary>
    public Vector4 SpotAnglesAndParams;
}

public struct NativeDirectionalLightState
{
    public Vector3 DirectionToLight;
    public Vector3 Color;
    public float Intensity;
    public float SinHalfSourceAngle;
    public float Daylight;
}

public struct NativeSkyLightState
{
    public Vector3 SkyColor;
    public Vector3 LowerHemisphereColor;
    public float Intensity;
    public UTextureCube? Cubemap;
}

public sealed class NativeLightRenderer : IDisposable
{
    public const int MaxGpuLocalLights = 1024;

    private readonly ID3D11Device _device;
    private readonly ID3D11Buffer _localLightBuffer;
    private readonly ID3D11ShaderResourceView _localLightSrv;
    private readonly NativeGpuLocalLight[] _stagingLights = new NativeGpuLocalLight[MaxGpuLocalLights];
    private readonly List<(NativeGpuLocalLight Light, float DistSq)> _localCandidates = new(128);

    public ID3D11ShaderResourceView LocalLightSrv => _localLightSrv;
    public int ActiveLocalLightCount { get; private set; }
    public NativeDirectionalLightState DirectionalLight { get; private set; }
    public NativeSkyLightState SkyLight { get; private set; }

    public NativeLightRenderer(ID3D11Device device)
    {
        _device = device;
        var stride = Marshal.SizeOf<NativeGpuLocalLight>();
        _localLightBuffer = _device.CreateBuffer(new BufferDescription(
            (uint) (MaxGpuLocalLights * stride),
            BindFlags.ShaderResource,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write,
            ResourceOptionFlags.BufferStructured,
            (uint) stride));

        _localLightSrv = _device.CreateShaderResourceView(
            _localLightBuffer,
            new ShaderResourceViewDescription(_localLightBuffer, Format.Unknown, 0, MaxGpuLocalLights));
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        var lenSq = v.LengthSquared();
        if (!float.IsFinite(lenSq) || lenSq < 1e-8f)
            return fallback;
        return v / MathF.Sqrt(lenSq);
    }

    private static bool IsFiniteVector(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static float Luminance(Vector3 c) =>
        0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    public unsafe void UpdateLights(
        ID3D11DeviceContext context,
        IReadOnlyList<SceneLight>? sceneLights,
        Vector3 cameraUnrealCm,
        Vector4[] frustumPlanes,
        Vector3 fallbackLightDir,
        WorldSettingsState? worldSettings = null,
        float maxLightDistanceMeters = 1200f)
    {
        var defaultDir = SafeNormalize(fallbackLightDir, new Vector3(0.4f, 0.3f, 0.85f));
        var defaultSinHalfAngle = MathF.Sin(0.5f * 0.5357f * MathF.PI / 180.0f);

        SceneDirectionalLight? selectedDir = null;
        SceneSkyLight? selectedSky = null;
        _localCandidates.Clear();
        var localCandidates = _localCandidates;

        if (sceneLights is { Count: > 0 })
        {
            for (var i = 0; i < sceneLights.Count; i++)
            {
                var light = sceneLights[i];
                if (!light.IsVisible || !(light.Actor?.IsVisibleRecursive ?? true))
                    continue;

                if (light is SceneDirectionalLight dirLight)
                {
                    if (selectedDir is null || dirLight.IsAtmosphereSun)
                    {
                        selectedDir = dirLight;
                    }
                    continue;
                }

                if (light is SceneSkyLight skyLight)
                {
                    selectedSky ??= skyLight;
                    continue;
                }

                if (light is LocalLightComponent localLight)
                {
                    if (!float.IsFinite(localLight.Intensity) || localLight.Intensity <= 1e-5f ||
                        !float.IsFinite(localLight.AttenuationRadius) || localLight.AttenuationRadius <= 1e-4f)
                        continue;

                    var wm = localLight.WorldMatrix;
                    var posSnooper = wm.Translation;
                    if (!IsFiniteVector(posSnooper))
                        continue;

                    var posUnrealCm = new Vector3(posSnooper.X, posSnooper.Z, posSnooper.Y) * 100f;
                    var radiusCm = Math.Clamp(localLight.AttenuationRadius, 1f, 100000f);
                    var invRadiusCm = 1.0f / radiusCm;

                    var distSqCm = Vector3.DistanceSquared(posUnrealCm, cameraUnrealCm);
                    var distMeters = MathF.Sqrt(distSqCm) * 0.01f;

                    var distFade = 1.0f;
                    if (localLight.MaxDrawDistance > 0.0f && float.IsFinite(localLight.MaxDrawDistance))
                    {
                        if (distMeters >= localLight.MaxDrawDistance)
                            continue;
                        if (localLight.MaxDistanceFadeRange > 0.0f && float.IsFinite(localLight.MaxDistanceFadeRange))
                        {
                            var fadeStart = MathF.Max(0.0f, localLight.MaxDrawDistance - localLight.MaxDistanceFadeRange);
                            distFade = 1.0f - Math.Clamp((distMeters - fadeStart) / localLight.MaxDistanceFadeRange, 0.0f, 1.0f);
                        }
                    }

                    // Tightened CPU culling: skip local lights outside view frustum or beyond max draw distance
                    if (distMeters > maxLightDistanceMeters + radiusCm * 0.01f ||
                        !InFrustum(frustumPlanes, posUnrealCm, radiusCm * 1.10f))
                        continue;

                    var dirUnreal = new Vector3(1f, 0f, 0f);
                    var tanUnreal = new Vector3(0f, 0f, 1f);
                    if (Matrix4x4.Decompose(wm, out _, out var rotSnooper, out _) &&
                        float.IsFinite(rotSnooper.X) && float.IsFinite(rotSnooper.Y) && float.IsFinite(rotSnooper.Z) && float.IsFinite(rotSnooper.W) &&
                        rotSnooper.LengthSquared() > 1e-6f)
                    {
                        rotSnooper = Quaternion.Normalize(rotSnooper);
                        var dirSnooper = Vector3.Transform(Vector3.UnitX, rotSnooper);
                        var tanSnooper = Vector3.Transform(Vector3.UnitY, rotSnooper);
                        dirUnreal = SafeNormalize(new Vector3(dirSnooper.X, dirSnooper.Z, dirSnooper.Y), new Vector3(1f, 0f, 0f));
                        tanUnreal = SafeNormalize(new Vector3(tanSnooper.X, tanSnooper.Z, tanSnooper.Y), new Vector3(0f, 0f, 1f));
                    }

                    // In UE 5.2 FLightSceneProxy::GetLightShaderParameters, LightData.Direction = -GetDirection()
                    var negDirUnreal = -dirUnreal;
                    var translatedPosCm = posUnrealCm - cameraUnrealCm;
                    var cleanColor = IsFiniteVector(localLight.Color) ? Vector3.Max(localLight.Color, Vector3.Zero) : Vector3.One;
                    var baseIntensity = Math.Clamp(localLight.Intensity * distFade, 0f, 1e6f);

                    var gpuLight = new NativeGpuLocalLight
                    {
                        PositionAndInvRadius = new Vector4(translatedPosCm, invRadiusCm),
                        DirectionAndSourceRadius = new Vector4(negDirUnreal, 0f),
                        TangentAndSourceLength = new Vector4(tanUnreal, 0f)
                    };

                    if (localLight is SceneSpotLight spot)
                    {
                        var innerCos = MathF.Cos(Math.Clamp(spot.InnerConeAngle, 0f, 89f) * MathF.PI / 180.0f);
                        var outerCos = MathF.Cos(Math.Clamp(spot.OuterConeAngle, 0.5f, 89.5f) * MathF.PI / 180.0f);
                        var invCosDiff = 1.0f / MathF.Max(innerCos - outerCos, 1e-4f);

                        var unitScale = ComputeLocalLightUnitScale(spot, outerCos);
                        var falloffExp = spot.UseInverseSquaredFalloff ? 0.0f : Math.Clamp(spot.LightFalloffExponent, 1.0f, 64.0f);

                        gpuLight.ColorAndFalloff = new Vector4(cleanColor * (baseIntensity * unitScale), falloffExp);
                        gpuLight.DirectionAndSourceRadius = new Vector4(negDirUnreal, MathF.Max(0f, spot.SourceRadius));
                        gpuLight.TangentAndSourceLength = new Vector4(tanUnreal, MathF.Max(0f, spot.SourceLength));
                        gpuLight.SpotAnglesAndParams = new Vector4(
                            outerCos,
                            invCosDiff,
                            MathF.Max(0f, spot.SoftSourceRadius),
                            2.0f);
                    }
                    else if (localLight is SceneRectLight rect)
                    {
                        var halfWidthCm = MathF.Max(0.5f, rect.Width * 0.5f);
                        var halfHeightCm = MathF.Max(0.5f, rect.Height * 0.5f);
                        var barnCos = MathF.Cos(Math.Clamp(rect.BarnDoorAngle, 0.0f, 88.0f) * MathF.PI / 180.0f);
                        var barnLenCm = MathF.Max(0.1f, rect.BarnDoorLength);

                        // UE 5.2 SceneRectLight.cpp: LightColor = Candelas * 10000 / (0.5 * Width_cm * Height_cm)
                        var areaFactor = MathF.Max(2.0f * halfWidthCm * halfHeightCm, 1.0f);
                        var unitScale = ComputeLocalLightUnitScale(rect, 0f) / areaFactor;

                        gpuLight.ColorAndFalloff = new Vector4(cleanColor * (baseIntensity * unitScale), 0.0f);
                        gpuLight.DirectionAndSourceRadius = new Vector4(negDirUnreal, halfWidthCm);
                        gpuLight.TangentAndSourceLength = new Vector4(tanUnreal, halfHeightCm);
                        gpuLight.SpotAnglesAndParams = new Vector4(
                            barnCos,
                            barnLenCm,
                            0.0f,
                            3.0f);
                    }
                    else if (localLight is ScenePointLight point)
                    {
                        var unitScale = ComputeLocalLightUnitScale(point, -1f);
                        var falloffExp = point.UseInverseSquaredFalloff ? 0.0f : Math.Clamp(point.LightFalloffExponent, 1.0f, 64.0f);

                        gpuLight.ColorAndFalloff = new Vector4(cleanColor * (baseIntensity * unitScale), falloffExp);
                        gpuLight.DirectionAndSourceRadius = new Vector4(negDirUnreal, MathF.Max(0f, point.SourceRadius));
                        gpuLight.TangentAndSourceLength = new Vector4(tanUnreal, MathF.Max(0f, point.SourceLength));
                        gpuLight.SpotAnglesAndParams = new Vector4(
                            -2.0f,
                            1.0f,
                            MathF.Max(0f, point.SoftSourceRadius),
                            1.0f);
                    }
                    else
                    {
                        continue;
                    }

                    localCandidates.Add((gpuLight, distSqCm));
                }
            }
        }

        // Compute UE 5.2 DirectionalLight & SkyLight states with physical PI illuminance scaling (matching SceneLighting.cs)
        const float budget = 1.25f;
        var dirState = new NativeDirectionalLightState
        {
            DirectionToLight = defaultDir,
            Color = new Vector3(1.0f, 0.98f, 0.94f),
            Intensity = MathF.PI * 0.95f,
            SinHalfSourceAngle = defaultSinHalfAngle,
            Daylight = 1.0f
        };

        var skyState = new NativeSkyLightState
        {
            SkyColor = new Vector3(0.28f, 0.33f, 0.40f),
            LowerHemisphereColor = new Vector3(0.10f, 0.11f, 0.13f),
            Intensity = 1.0f,
            Cubemap = null
        };

        if (selectedDir is not null || selectedSky is not null)
        {
            var daylight = selectedDir is not null && float.IsFinite(selectedDir.Daylight) && selectedDir.Daylight > 0.0f
                ? selectedDir.Daylight
                : (selectedDir is not null ? 1.0f : 1.0f);

            var sunCol = selectedDir is not null && IsFiniteVector(selectedDir.Color)
                ? Vector3.Max(selectedDir.Color, Vector3.Zero)
                : new Vector3(1.0f, 0.98f, 0.94f);
            var rawSunIntensity = selectedDir is not null && float.IsFinite(selectedDir.Intensity) && selectedDir.Intensity > 0.0f
                ? selectedDir.Intensity
                : 3.14159265f;

            var skyCol = selectedSky is not null && IsFiniteVector(selectedSky.Color)
                ? Vector3.Max(selectedSky.Color, Vector3.Zero)
                : new Vector3(0.28f, 0.33f, 0.40f);
            var lowerCol = selectedSky is not null && IsFiniteVector(selectedSky.LowerHemisphereColor)
                ? Vector3.Max(selectedSky.LowerHemisphereColor, Vector3.Zero)
                : skyCol * 0.32f;
            var rawSkyIntensity = selectedSky is not null && float.IsFinite(selectedSky.Intensity) && selectedSky.Intensity > 0.0f
                ? selectedSky.Intensity
                : 1.0f;

            var upper = selectedSky is not null ? skyCol * rawSkyIntensity : new Vector3(0.28f, 0.33f, 0.40f);
            var lower = selectedSky is not null ? lowerCol * rawSkyIntensity : new Vector3(0.10f, 0.11f, 0.13f);

            var fromSky = MathF.PI * Luminance(upper);
            var fromSun = selectedDir is not null && daylight > 0.0f ? (rawSunIntensity / daylight) * Luminance(sunCol) : 0.0f;
            var sunShare = (fromSun + fromSky) > 1e-5f
                ? Math.Clamp(fromSun / (fromSun + fromSky), selectedDir is not null ? 0.35f : 0.0f, selectedSky is not null ? 0.85f : 0.80f)
                : (selectedDir is not null ? 0.75f : 0.0f);

            if (selectedDir is not null)
            {
                var raySnooper = selectedDir.GetDirection();
                var toLightUnreal = SafeNormalize(new Vector3(-raySnooper.X, -raySnooper.Z, -raySnooper.Y), defaultDir);
                var sunBrightest = MathF.Max(sunCol.X, MathF.Max(sunCol.Y, sunCol.Z));
                var sunIntensity = sunBrightest > 1e-4f
                    ? MathF.PI * budget * sunShare * daylight / sunBrightest
                    : MathF.PI * 0.95f;
                var sourceAngleDeg = float.IsFinite(selectedDir.SourceAngle) && selectedDir.SourceAngle > 0.0f
                    ? Math.Clamp(selectedDir.SourceAngle, 0.05f, 45.0f)
                    : 0.5357f;

                dirState = new NativeDirectionalLightState
                {
                    DirectionToLight = toLightUnreal,
                    Color = sunCol,
                    Intensity = sunIntensity,
                    SinHalfSourceAngle = MathF.Sin(0.5f * sourceAngleDeg * MathF.PI / 180.0f),
                    Daylight = daylight
                };
            }

            var nightColor = new Vector3(0.080f, 0.100f, 0.145f);
            var nightLowerColor = new Vector3(0.026f, 0.030f, 0.038f);
            var brightestSky = MathF.Max(upper.X, MathF.Max(upper.Y, upper.Z));
            var skyScale = brightestSky > 1e-4f ? budget * (1.0f - sunShare) / brightestSky : 0.25f;
            var finalSkyUpper = Vector3.Lerp(nightColor, upper * skyScale, daylight);
            var finalSkyLower = Vector3.Lerp(nightLowerColor, lower * skyScale, daylight);

            skyState = new NativeSkyLightState
            {
                SkyColor = finalSkyUpper,
                LowerHemisphereColor = finalSkyLower,
                Intensity = 1.0f,
                Cubemap = selectedSky?.Cubemap
            };
        }

        if (localCandidates.Count > MaxGpuLocalLights)
        {
            localCandidates.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
        }

        // Apply WorldSettingsState Sky/TODM gating & intensity multipliers
        var skyEnabled = worldSettings?.SkyEnabled ?? true;
        var todmEnabled = worldSettings?.TodmEnabled ?? true;
        var sunMult = MathF.Max(0f, worldSettings?.SunIntensityMultiplier ?? 1f);
        var skyMult = MathF.Max(0f, worldSettings?.SkyIntensityMultiplier ?? 1f);

        if (!todmEnabled)
        {
            if (selectedDir is null || selectedDir.IsAtmosphereSun)
                dirState.Intensity = 0f;
            skyState = new NativeSkyLightState
            {
                SkyColor = Vector3.Zero,
                LowerHemisphereColor = Vector3.Zero,
                Intensity = 0f,
                Cubemap = null
            };
        }
        else if (!skyEnabled)
        {
            if (selectedDir is null || selectedDir.IsAtmosphereSun)
                dirState.Intensity = 0f;
            skyState = new NativeSkyLightState
            {
                SkyColor = Vector3.Zero,
                LowerHemisphereColor = Vector3.Zero,
                Intensity = 0f,
                Cubemap = null
            };
        }

        dirState.Intensity *= sunMult;
        skyState.Intensity *= skyMult;

        var count = Math.Min(localCandidates.Count, MaxGpuLocalLights);
        for (var i = 0; i < count; i++)
            _stagingLights[i] = localCandidates[i].Light;

        ActiveLocalLightCount = count;
        DirectionalLight = dirState;
        SkyLight = skyState;

        if (count > 0)
        {
            var mapped = context.Map(_localLightBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var span = _stagingLights.AsSpan(0, count);
                var bytes = MemoryMarshal.AsBytes(span);
                bytes.CopyTo(new Span<byte>((void*) mapped.DataPointer, bytes.Length));
            }
            finally
            {
                context.Unmap(_localLightBuffer, 0);
            }
        }
    }

    private static float ComputeLocalLightUnitScale(LocalLightComponent light, float cosHalfConeAngle)
    {
        if (!light.UseInverseSquaredFalloff)
            return 1.0f;
        return light.IntensityUnits switch
        {
            CUE4Parse.UE4.Objects.Engine.ELightUnits.Candelas when light.Intensity <= 250f => 10000.0f,
            CUE4Parse.UE4.Objects.Engine.ELightUnits.Lumens => 10000.0f / (2.0f * MathF.PI * MathF.Max(1.0f - cosHalfConeAngle, 1e-4f)),
            CUE4Parse.UE4.Objects.Engine.ELightUnits.EV => 10000.0f,
            CUE4Parse.UE4.Objects.Engine.ELightUnits.Nits => 1.0f,
            _ => 16.0f
        };
    }

    private static bool InFrustum(Vector4[] planes, Vector3 center, float radius)
    {
        for (var i = 0; i < planes.Length; i++)
        {
            var p = planes[i];
            if (p.X * center.X + p.Y * center.Y + p.Z * center.Z + p.W < -radius)
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        _localLightSrv.Dispose();
        _localLightBuffer.Dispose();
    }
}
