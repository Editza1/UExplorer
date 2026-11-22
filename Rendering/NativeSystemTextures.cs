using System.IO.Compression;
using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace UExplorer.Rendering;

/// <summary>
/// System LUT textures ported from Unreal Engine 5.2 (Engine/Source/Runtime/Renderer/Private/SystemTextures.cpp and LTC.h):
/// - PreintegratedGF (128x32 R16G16_UNorm): Karis 2013 split-sum GGX + SmithJoint + Schlick environment BRDF LUT.
/// - LTCMat (64x64 R16G16B16A16_Float) &amp; LTCAmp (64x64 R16G16_Float): Heitz 2016 Linearly Transformed Cosines LUTs for RectLight GGX specular.
/// </summary>
public static class NativeSystemTextures
{
    public const int LtcSize = 64;

    private static uint ReverseBits32(uint bits)
    {
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x00ff00ffu) << 8) | ((bits & 0xff00ff00u) >> 8);
        bits = ((bits & 0x0f0f0f0fu) << 4) | ((bits & 0xf0f0f0f0u) >> 4);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xccccccccu) >> 2);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xaaaaaaaau) >> 1);
        return bits;
    }

    /// <summary>
    /// Exact port of FSystemTextures::InitializeFeatureLevelDependentTextures PreintegratedGF (SystemTextures.cpp:408-540).
    /// </summary>
    public static unsafe (ID3D11Texture2D Texture, ID3D11ShaderResourceView Srv) CreatePreintegratedGF(ID3D11Device device)
    {
        const int width = 128;
        const int height = 32;
        var texels = new ushort[width * height * 2];

        for (var y = 0; y < height; y++)
        {
            var roughness = (y + 0.5f) / height;
            var m = roughness * roughness;
            var m2 = m * m;

            for (var x = 0; x < width; x++)
            {
                var noV = (x + 0.5f) / width;
                var v = new Vector3(MathF.Sqrt(MathF.Max(0f, 1.0f - noV * noV)), 0.0f, noV);

                var a = 0.0f;
                var b = 0.0f;
                const uint numSamples = 128;

                for (uint i = 0; i < numSamples; i++)
                {
                    var e1 = (float) i / numSamples;
                    var e2 = (float) (ReverseBits32(i) / (double) 0x100000000UL);

                    var phi = 2.0f * MathF.PI * e1;
                    var cosPhi = MathF.Cos(phi);
                    var sinPhi = MathF.Sin(phi);
                    var cosTheta = MathF.Sqrt((1.0f - e2) / (1.0f + (m2 - 1.0f) * e2));
                    var sinTheta = MathF.Sqrt(MathF.Max(0f, 1.0f - cosTheta * cosTheta));

                    var h = new Vector3(sinTheta * cosPhi, sinTheta * sinPhi, cosTheta);
                    var vDotH = Vector3.Dot(v, h);
                    var l = 2.0f * vDotH * h - v;

                    var noL = MathF.Max(l.Z, 0.0f);
                    var noH = MathF.Max(h.Z, 0.0f);
                    var voH = MathF.Max(vDotH, 0.0f);

                    if (noL > 0.0f)
                    {
                        var visSmithV = noL * (noV * (1.0f - m) + m);
                        var visSmithL = noV * (noL * (1.0f - m) + m);
                        var vis = 0.5f / MathF.Max(visSmithV + visSmithL, 1e-6f);

                        var noLVisPdf = noL * vis * (4.0f * voH / MathF.Max(noH, 1e-6f));
                        var fc = 1.0f - voH;
                        var fc2 = fc * fc;
                        fc *= fc2 * fc2;
                        a += noLVisPdf * (1.0f - fc);
                        b += noLVisPdf * fc;
                    }
                }

                a /= numSamples;
                b /= numSamples;

                var idx = (y * width + x) * 2;
                texels[idx + 0] = (ushort) (Math.Clamp(a, 0.0f, 1.0f) * 65535.0f + 0.5f);
                texels[idx + 1] = (ushort) (Math.Clamp(b, 0.0f, 1.0f) * 65535.0f + 0.5f);
            }
        }

        fixed (ushort* p = texels)
        {
            var desc = new Texture2DDescription(Format.R16G16_UNorm, width, height, 1, 1, BindFlags.ShaderResource, ResourceUsage.Immutable);
            var sub = new SubresourceData((IntPtr) p, width * 4);
            var tex = device.CreateTexture2D(desc, [sub]);
            var srv = device.CreateShaderResourceView(tex);
            return (tex, srv);
        }
    }

    /// <summary>
    /// Exact port of FSystemTextures LTCMat and LTCAmp 64x64 LUTs from UE 5.2 (SystemTextures.cpp:660-717, LTC.h).
    /// </summary>
    public static unsafe (ID3D11Texture2D MatTex, ID3D11ShaderResourceView MatSrv, ID3D11Texture2D AmpTex, ID3D11ShaderResourceView AmpSrv) CreateLtcTextures(ID3D11Device device)
    {
        var compressed = Convert.FromBase64String(LtcCompressedBase64);
        using var ms = new MemoryStream(compressed);
        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
        using var br = new BinaryReader(ds);

        const int count = LtcSize * LtcSize;
        var matPixels = new ushort[count * 4];
        var ampPixels = new ushort[count * 2];

        for (var c = 0; c < 4; c++)
        {
            ushort prev = 0;
            for (var i = 0; i < count; i++)
            {
                var cur = (ushort) (prev + br.ReadUInt16());
                matPixels[i * 4 + c] = cur;
                prev = cur;
            }
        }

        for (var c = 0; c < 2; c++)
        {
            ushort prev = 0;
            for (var i = 0; i < count; i++)
            {
                var cur = (ushort) (prev + br.ReadUInt16());
                ampPixels[i * 2 + c] = cur;
                prev = cur;
            }
        }

        ID3D11Texture2D matTex, ampTex;
        ID3D11ShaderResourceView matSrv, ampSrv;

        fixed (ushort* pMat = matPixels)
        {
            var desc = new Texture2DDescription(Format.R16G16B16A16_Float, LtcSize, LtcSize, 1, 1, BindFlags.ShaderResource, ResourceUsage.Immutable);
            var sub = new SubresourceData((IntPtr) pMat, LtcSize * 8);
            matTex = device.CreateTexture2D(desc, [sub]);
            matSrv = device.CreateShaderResourceView(matTex);
        }

        fixed (ushort* pAmp = ampPixels)
        {
            var desc = new Texture2DDescription(Format.R16G16_Float, LtcSize, LtcSize, 1, 1, BindFlags.ShaderResource, ResourceUsage.Immutable);
            var sub = new SubresourceData((IntPtr) pAmp, LtcSize * 4);
            ampTex = device.CreateTexture2D(desc, [sub]);
            ampSrv = device.CreateShaderResourceView(ampTex);
        }

        return (matTex, matSrv, ampTex, ampSrv);
    }

    /// <summary>
    /// Creates tileable 64x64 2D high-frequency dither/noise and 32x32x32 3D Perlin-like gradient volume textures
    /// for Unreal View / Material noise slots (Good64x64TilingNoiseHighFreq, PerlinNoiseGradientTexture, PerlinNoise3DTexture).
    /// </summary>
    public static unsafe (ID3D11Texture2D Noise2DTex, ID3D11ShaderResourceView Noise2DSrv, ID3D11Texture3D Noise3DTex, ID3D11ShaderResourceView Noise3DSrv) CreateNoiseTextures(ID3D11Device device)
    {
        const int size2D = 64;
        var pixels2D = new uint[size2D * size2D];
        for (var y = 0; y < size2D; y++)
        {
            for (var x = 0; x < size2D; x++)
            {
                // Interleaved gradient noise + R2 low-discrepancy sequence (tileable mod 64, uniform [0..255] histogram)
                var ignR = Frac(52.9829189f * Frac(0.06711056f * x + 0.00583715f * y));
                var ignG = Frac(52.9829189f * Frac(0.06711056f * (x + 17) + 0.00583715f * (y + 31)));
                var ignB = Frac(52.9829189f * Frac(0.06711056f * (x + 43) + 0.00583715f * (y + 11)));
                var ignA = Frac(0.754877666f * x + 0.56984029f * y);
                var r = (uint) Math.Clamp((int) (ignR * 255f + 0.5f), 0, 255);
                var g = (uint) Math.Clamp((int) (ignG * 255f + 0.5f), 0, 255);
                var b = (uint) Math.Clamp((int) (ignB * 255f + 0.5f), 0, 255);
                var a = (uint) Math.Clamp((int) (ignA * 255f + 0.5f), 0, 255);
                pixels2D[y * size2D + x] = r | (g << 8) | (b << 16) | (a << 24);
            }
        }

        ID3D11Texture2D tex2D;
        ID3D11ShaderResourceView srv2D;
        fixed (uint* p2 = pixels2D)
        {
            var desc2 = new Texture2DDescription(Format.R8G8B8A8_UNorm, size2D, size2D, 1, 1, BindFlags.ShaderResource, ResourceUsage.Immutable);
            var sub2 = new SubresourceData((IntPtr) p2, size2D * 4);
            tex2D = device.CreateTexture2D(desc2, [sub2]);
            srv2D = device.CreateShaderResourceView(tex2D);
        }

        const int size3D = 32;
        var pixels3D = new uint[size3D * size3D * size3D];
        for (var z = 0; z < size3D; z++)
        {
            for (var y = 0; y < size3D; y++)
            {
                for (var x = 0; x < size3D; x++)
                {
                    var fx = x / (float) size3D * MathF.Tau;
                    var fy = y / (float) size3D * MathF.Tau;
                    var fz = z / (float) size3D * MathF.Tau;
                    var n0 = 0.5f + 0.5f * (MathF.Sin(fx * 2f + MathF.Cos(fy * 3f)) * MathF.Cos(fz * 2f + MathF.Sin(fx * 3f)));
                    var n1 = 0.5f + 0.5f * (MathF.Sin(fy * 3f + MathF.Cos(fz * 2f)) * MathF.Cos(fx * 4f + MathF.Sin(fy * 2f)));
                    var n2 = 0.5f + 0.5f * (MathF.Sin(fz * 4f + MathF.Cos(fx * 2f)) * MathF.Cos(fy * 3f + MathF.Sin(fz * 3f)));
                    var n3 = Frac(MathF.Sin(x * 12.9898f + y * 78.233f + z * 37.719f) * 43758.5453f);
                    var r = (uint) Math.Clamp((int) (n0 * 255f + 0.5f), 0, 255);
                    var g = (uint) Math.Clamp((int) (n1 * 255f + 0.5f), 0, 255);
                    var b = (uint) Math.Clamp((int) (n2 * 255f + 0.5f), 0, 255);
                    var a = (uint) Math.Clamp((int) (n3 * 255f + 0.5f), 0, 255);
                    pixels3D[(z * size3D + y) * size3D + x] = r | (g << 8) | (b << 16) | (a << 24);
                }
            }
        }

        ID3D11Texture3D tex3D;
        ID3D11ShaderResourceView srv3D;
        fixed (uint* p3 = pixels3D)
        {
            var desc3 = new Texture3DDescription(Format.R8G8B8A8_UNorm, size3D, size3D, size3D, 1, BindFlags.ShaderResource, ResourceUsage.Immutable);
            var sub3 = new SubresourceData((IntPtr) p3, size3D * 4, size3D * size3D * 4);
            tex3D = device.CreateTexture3D(desc3, [sub3]);
            srv3D = device.CreateShaderResourceView(tex3D);
        }

        return (tex2D, srv2D, tex3D, srv3D);

        static float Frac(float v) => v - MathF.Floor(v);
    }

    private const string LtcCompressedBase64 = "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";
}
