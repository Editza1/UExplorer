using System.Runtime.Versioning;
using Serilog;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D11.Shader;

namespace UExplorer.Rendering;

/// <summary>
/// A headless D3D11 device that lives next to the OpenGL renderer. For now it only validates native material bytecode
/// (creates the shader objects) and reflects the bindings each shader expects, which is what the next step needs
/// to rebuild Unreal's uniform buffers (View, Primitive, Material...) and render the material to a shared texture.
/// </summary>
public sealed class D3D11NativeShaderDevice : IDisposable
{
    private static readonly Lazy<D3D11NativeShaderDevice?> _shared = new(Create);
    public static D3D11NativeShaderDevice? Shared => _shared.Value;
    public static string? UnavailableReason { get; private set; }

    private readonly ID3D11Device _device;
    public FeatureLevel FeatureLevel { get; }
    internal ID3D11Device Device => _device;
    internal ID3D11DeviceContext Context => _device.ImmediateContext;

    private D3D11NativeShaderDevice(ID3D11Device device)
    {
        _device = device;
        FeatureLevel = device.FeatureLevel;
    }

    private static D3D11NativeShaderDevice? Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            UnavailableReason = "Direct3D 11 is only available on Windows.";
            return null;
        }

        try
        {
            var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device? device);
            if (result.Failure || device is null)
            {
                UnavailableReason = $"D3D11CreateDevice failed ({result}).";
                return null;
            }

            Log.Information("Created D3D11 device for native materials (feature level {Level})", device.FeatureLevel);
            return new D3D11NativeShaderDevice(device);
        }
        catch (Exception e)
        {
            UnavailableReason = e.Message;
            Log.Warning(e, "Could not create a D3D11 device for native materials");
            return null;
        }
    }

    public void Validate(NativeShaderEntry entry)
    {
        if (entry.Status is not ENativeShaderStatus.Extracted) return;

        var bytecode = entry.Bytecode.ToArray();
        try
        {
            switch (entry.Stage)
            {
                case ENativeShaderStage.Vertex: _device.CreateVertexShader(bytecode).Dispose(); break;
                case ENativeShaderStage.Pixel: _device.CreatePixelShader(bytecode).Dispose(); break;
                case ENativeShaderStage.Geometry: _device.CreateGeometryShader(bytecode).Dispose(); break;
                case ENativeShaderStage.Hull: _device.CreateHullShader(bytecode).Dispose(); break;
                case ENativeShaderStage.Domain: _device.CreateDomainShader(bytecode).Dispose(); break;
                case ENativeShaderStage.Compute: _device.CreateComputeShader(bytecode).Dispose(); break;
                default:
                    entry.Status = ENativeShaderStatus.Rejected;
                    entry.Error = "unknown shader stage";
                    return;
            }

            entry.Status = ENativeShaderStatus.Created;
            Reflect(entry, bytecode);
        }
        catch (Exception e)
        {
            entry.Status = ENativeShaderStatus.Rejected;
            entry.Error = e.Message;
        }
    }

    /// <summary>D3D assembly listing; works on stripped shaders too (registers show as cbN[i] / tN / sN).</summary>
    public static unsafe string? Disassemble(NativeShaderEntry entry)
    {
        if (!OperatingSystem.IsWindows() || entry.Container is not { Kind: ENativeBytecodeKind.Dxbc }) return null;
        try
        {
            var bytecode = entry.Bytecode.ToArray();
            fixed (byte* ptr = bytecode)
            {
                using var blob = Compiler.Disassemble((IntPtr) ptr, (nuint) bytecode.Length, DisasmFlags.None, null!);
                return blob.AsString();
            }
        }
        catch (Exception e)
        {
            return $"disassembly failed: {e.Message}";
        }
    }

    private static void Reflect(NativeShaderEntry entry, byte[] bytecode)
    {
        try
        {
            using var reflection = Compiler.Reflect<ID3D11ShaderReflection>(bytecode);

            var bindings = new List<NativeShaderBinding>();
            foreach (var resource in reflection.BoundResources)
                bindings.Add(new NativeShaderBinding(resource.Name, resource.Type.ToString(), (int) resource.BindPoint, (int) resource.BindCount));

            var buffers = new List<NativeConstantBuffer>();
            foreach (var buffer in reflection.ConstantBuffers)
            {
                var variables = buffer.Variables
                    .Select(v => (v.Description.Name, (int) v.Description.StartOffset, (int) v.Description.Size))
                    .ToList();
                buffers.Add(new NativeConstantBuffer(buffer.Description.Name, (int) buffer.Description.Size, variables));
            }

            entry.Bindings = bindings;
            entry.ConstantBuffers = buffers;
        }
        catch (Exception e)
        {
            // UE strips reflection data (RDEF) from shipping shaders on some titles, creation still succeeded
            entry.Error = $"no reflection data: {e.Message}";
        }
    }

    public void Dispose() => _device.Dispose();
}
