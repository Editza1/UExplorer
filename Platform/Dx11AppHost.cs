using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ImGuiNET;
using ImGuizmoNET;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using UExplorer.Rendering;

namespace UExplorer.Platform;

/// <summary>
/// Pure DirectX 11 application window, DXGI SwapChain, and ImGui D3D11 renderer backend (zero OpenGL).
/// </summary>
public sealed partial class Dx11AppHost : IDisposable
{
    private const string ImGuiHlsl = """
        cbuffer vertexBuffer : register(b0)
        {
            float4x4 ProjectionMatrix;
        };

        struct VS_INPUT
        {
            float2 pos : POSITION;
            float2 uv  : TEXCOORD0;
            float4 col : COLOR0;
        };

        struct PS_INPUT
        {
            float4 pos : SV_POSITION;
            float4 col : COLOR0;
            float2 uv  : TEXCOORD0;
        };

        PS_INPUT VSMain(VS_INPUT input)
        {
            PS_INPUT output;
            output.pos = mul(ProjectionMatrix, float4(input.pos.xy, 0.f, 1.f));
            output.col = input.col;
            output.uv  = input.uv;
            return output;
        }

        Texture2D texture0 : register(t0);
        SamplerState sampler0 : register(s0);

        float4 PSMain(PS_INPUT input) : SV_Target
        {
            return input.col * texture0.Sample(sampler0, input.uv);
        }
        """;

    public Form Window { get; }
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public ImFontPtr RegularFont { get; }
    public ImFontPtr BoldFont { get; }
    public ImFontPtr SmallFont { get; }
    public ImFontPtr SmallBoldFont { get; }
    public ImFontPtr AssetTitleFont { get; }
    public ImFontPtr AssetClassFont { get; }
    public ImFontPtr HeadingFont { get; }
    public int SyncInterval { get; set; } = 1;

    private readonly ConcurrentQueue<Action> _uiQueue = new();
    private readonly IDXGISwapChain1 _swapChain;
    private ID3D11RenderTargetView? _backBufferRtv;
    private int _clientWidth = 1600;
    private int _clientHeight = 960;

    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11InputLayout _layout;
    private readonly ID3D11Buffer _cb;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11BlendState _blend;
    private readonly ID3D11RasterizerState _raster;
    private readonly ID3D11DepthStencilState _depthDisabled;
    private readonly ID3D11ShaderResourceView _fontSrv;
    private ID3D11Buffer? _vb;
    private ID3D11Buffer? _ib;
    private int _vbSize = 5000;
    private int _ibSize = 10000;

    private DateTime _lastFrame = DateTime.UtcNow;
    private volatile bool _exitRequested;
    private volatile bool _disposed;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attr, in int val, int size);

    public Dx11AppHost(string title, int width = 1600, int height = 960)
    {
        Application.EnableVisualStyles();
        Window = new Form
        {
            Text = title,
            ClientSize = new System.Drawing.Size(width, height),
            StartPosition = FormStartPosition.CenterScreen,
            BackColor = System.Drawing.Color.FromArgb(26, 26, 26)
        };
        _clientWidth = Math.Max(1, Window.ClientSize.Width);
        _clientHeight = Math.Max(1, Window.ClientSize.Height);

        if (OperatingSystem.IsWindows())
        {
            var dark = 1;
            _ = DwmSetWindowAttribute(Window.Handle, 20, in dark, sizeof(int));
        }

        // Initialize D3D11 Device (or reuse shared device if already created)
        var shared = D3D11NativeShaderDevice.Shared ?? throw new InvalidOperationException(D3D11NativeShaderDevice.UnavailableReason ?? "DirectX 11 unavailable");
        Device = shared.Device;
        Context = shared.Context;

        using var dxgiDev = Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDev.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var scDesc = new SwapChainDescription1
        {
            Width = (uint) _clientWidth,
            Height = (uint) _clientHeight,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            SwapEffect = SwapEffect.FlipDiscard
        };
        _swapChain = factory.CreateSwapChainForHwnd(Device, Window.Handle, scDesc);
        RecreateBackBuffer();

        // Setup ImGui + ImGuizmo
        var ctx = ImGui.CreateContext();
        ImGui.SetCurrentContext(ctx);
        ImGuizmo.SetImGuiContext(ctx);
        ImGuizmo.AllowAxisFlip(false);

        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        io.ConfigDockingAlwaysTabBar = true;
        io.ConfigWindowsMoveFromTitleBarOnly = true;

        // UE 5.2 Starship uses Roboto: 10pt=13.333px, 8pt=10.667px, 9pt=12.0px, 7pt=9.333px, 11pt=14.667px at 96 DPI.
        var roboto = Path.Combine(AppContext.BaseDirectory, "Resources", "Fonts", "Roboto-Regular.ttf");
        var segoe = @"C:\Windows\Fonts\segoeui.ttf";
        var regularPath = File.Exists(roboto) ? roboto : (File.Exists(segoe) ? segoe : null);
        if (regularPath is not null)
        {
            var scale = File.Exists(roboto) ? 1.0f : (15f / 13.333f);
            RegularFont = io.Fonts.AddFontFromFileTTF(regularPath, 13.333f * scale);
            SmallFont = io.Fonts.AddFontFromFileTTF(regularPath, 10.667f * scale);
            AssetTitleFont = io.Fonts.AddFontFromFileTTF(regularPath, 12.0f * scale);
            AssetClassFont = io.Fonts.AddFontFromFileTTF(regularPath, 9.333f * scale);
            HeadingFont = io.Fonts.AddFontFromFileTTF(regularPath, 14.667f * scale);
        }
        else
        {
            RegularFont = io.Fonts.AddFontDefault();
            SmallFont = RegularFont;
            AssetTitleFont = RegularFont;
            AssetClassFont = RegularFont;
            HeadingFont = RegularFont;
        }

        var robotoBold = Path.Combine(AppContext.BaseDirectory, "Resources", "Fonts", "Roboto-Bold.ttf");
        var segoeBold = @"C:\Windows\Fonts\segoeuib.ttf";
        var boldPath = File.Exists(robotoBold) ? robotoBold : (File.Exists(segoeBold) ? segoeBold : null);
        if (boldPath is not null)
        {
            var scale = File.Exists(robotoBold) ? 1.0f : (15f / 13.333f);
            BoldFont = io.Fonts.AddFontFromFileTTF(boldPath, 13.333f * scale);
            SmallBoldFont = io.Fonts.AddFontFromFileTTF(boldPath, 10.667f * scale);
        }
        else
        {
            BoldFont = RegularFont;
            SmallBoldFont = SmallFont;
        }

        io.Fonts.GetTexDataAsRGBA32(out IntPtr fontPixels, out var fw, out var fh, out _);
        unsafe
        {
            var span = new ReadOnlySpan<byte>((void*) fontPixels, fw * fh * 4);
            using var fontTex = Device.CreateTexture2D(span, Format.R8G8B8A8_UNorm, (uint) fw, (uint) fh);
            _fontSrv = Device.CreateShaderResourceView(fontTex);
            io.Fonts.SetTexID(_fontSrv.NativePointer);
        }

        // Compile ImGui DX11 shaders
        var vsBytes = Compiler.Compile(ImGuiHlsl, "VSMain", "imgui_vs", "vs_4_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();
        var psBytes = Compiler.Compile(ImGuiHlsl, "PSMain", "imgui_ps", "ps_4_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();
        _vs = Device.CreateVertexShader(vsBytes);
        _ps = Device.CreatePixelShader(psBytes);
        _layout = Device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0, InputClassification.PerVertexData, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0, InputClassification.PerVertexData, 0),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 16, 0, InputClassification.PerVertexData, 0),
        ], vsBytes);

        _cb = Device.CreateBuffer(64, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        _sampler = Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Wrap, 0, 1, ComparisonFunction.Never, 0, float.MaxValue));
        _blend = Device.CreateBlendState(new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha));
        _raster = Device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { ScissorEnable = true, DepthClipEnable = true });
        _depthDisabled = Device.CreateDepthStencilState(new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Always));

        HookInputEvents();
    }

    public void InvokeOnUiThread(Action action) => TryInvokeOnUiThread(action);

    public bool TryInvokeOnUiThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_disposed || _exitRequested || Window.IsDisposed)
            return false;
        _uiQueue.Enqueue(action);
        return true;
    }

    public bool PumpUiQueueAndEvents()
    {
        if (_disposed || _exitRequested || Window.IsDisposed)
            return false;
        Application.DoEvents();
        if (_disposed || _exitRequested || Window.IsDisposed || !Window.IsHandleCreated)
            return false;

        while (_uiQueue.TryDequeue(out var action))
        {
            if (_disposed || _exitRequested || Window.IsDisposed || !Window.IsHandleCreated)
                return false;
            action();
        }
        return !_disposed && !_exitRequested && !Window.IsDisposed && Window.IsHandleCreated;
    }

    public bool StepProbeFrame(Action<float>? onFrame = null) => StepFrame(onFrame);

    public void RequestExit()
    {
        _exitRequested = true;
        if (!_disposed && !Window.IsDisposed && Window.IsHandleCreated)
        {
            try { Window.Close(); }
            catch (ObjectDisposedException) { }
        }
    }

    public void Run(Action<float> onFrame)
    {
        Window.Show();
        while (!_disposed && !_exitRequested && !Window.IsDisposed && Window.IsHandleCreated)
        {
            if (!StepFrame(onFrame))
                break;
        }
    }

    private bool StepFrame(Action<float>? onFrame)
    {
        if (!PumpUiQueueAndEvents())
            return false;

        var now = DateTime.UtcNow;
        var dt = Math.Clamp((float) (now - _lastFrame).TotalSeconds, 0.001f, 0.1f);
        _lastFrame = now;

        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(_clientWidth, _clientHeight);
        io.DeltaTime = dt;

        ImGui.NewFrame();
        ImGuizmo.BeginFrame();

        onFrame?.Invoke(dt);

        if (_exitRequested || _disposed || Window.IsDisposed || !Window.IsHandleCreated)
        {
            ImGui.EndFrame();
            return false;
        }

        ImGui.Render();
        RenderImGuiDrawData(ImGui.GetDrawData());
        _swapChain.Present((uint) Math.Clamp(SyncInterval, 0, 4), PresentFlags.None);
        return true;
    }

    public ID3D11ShaderResourceView CreateTextureSrv(int width, int height, ReadOnlySpan<byte> rgbaPixels)
    {
        using var tex = Device.CreateTexture2D(rgbaPixels, Format.R8G8B8A8_UNorm, (uint) width, (uint) height);
        return Device.CreateShaderResourceView(tex);
    }

    private void RecreateBackBuffer()
    {
        _backBufferRtv?.Dispose();
        _backBufferRtv = null;
        using var bb = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _backBufferRtv = Device.CreateRenderTargetView(bb);
    }

    private unsafe void RenderImGuiDrawData(ImDrawDataPtr drawData)
    {
        if (_backBufferRtv is null || drawData.DisplaySize.X <= 0 || drawData.DisplaySize.Y <= 0)
            return;

        if (_vb is null || _vbSize < drawData.TotalVtxCount)
        {
            _vb?.Dispose();
            _vbSize = Math.Max(drawData.TotalVtxCount + 5000, 5000);
            _vb = Device.CreateBuffer((uint) (_vbSize * sizeof(ImDrawVert)), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        }
        if (_ib is null || _ibSize < drawData.TotalIdxCount)
        {
            _ib?.Dispose();
            _ibSize = Math.Max(drawData.TotalIdxCount + 10000, 10000);
            _ib = Device.CreateBuffer((uint) (_ibSize * sizeof(ushort)), BindFlags.IndexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        }

        var vtxMap = Context.Map(_vb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        var idxMap = Context.Map(_ib, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        var vtxDst = (ImDrawVert*) vtxMap.DataPointer;
        var idxDst = (ushort*) idxMap.DataPointer;

        for (var n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            Buffer.MemoryCopy((void*) cmdList.VtxBuffer.Data, vtxDst, cmdList.VtxBuffer.Size * sizeof(ImDrawVert), cmdList.VtxBuffer.Size * sizeof(ImDrawVert));
            Buffer.MemoryCopy((void*) cmdList.IdxBuffer.Data, idxDst, cmdList.IdxBuffer.Size * sizeof(ushort), cmdList.IdxBuffer.Size * sizeof(ushort));
            vtxDst += cmdList.VtxBuffer.Size;
            idxDst += cmdList.IdxBuffer.Size;
        }
        Context.Unmap(_vb, 0);
        Context.Unmap(_ib, 0);

        var L = drawData.DisplayPos.X;
        var R = drawData.DisplayPos.X + drawData.DisplaySize.X;
        var T = drawData.DisplayPos.Y;
        var B = drawData.DisplayPos.Y + drawData.DisplaySize.Y;
        var mvp = new Matrix4x4(
            2f / (R - L), 0, 0, 0,
            0, 2f / (T - B), 0, 0,
            0, 0, 0.5f, 0,
            (R + L) / (L - R), (T + B) / (B - T), 0.5f, 1f);

        var cbMap = Context.Map(_cb, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        *(Matrix4x4*) cbMap.DataPointer = mvp;
        Context.Unmap(_cb, 0);

        Context.OMSetRenderTargets(_backBufferRtv);
        Context.ClearRenderTargetView(_backBufferRtv, new Color4(0.08f, 0.08f, 0.08f, 1f));
        Context.RSSetViewport(0, 0, _clientWidth, _clientHeight);

        var stride = (uint) sizeof(ImDrawVert);
        Context.IASetInputLayout(_layout);
        Context.IASetVertexBuffer(0, _vb, stride, 0);
        Context.IASetIndexBuffer(_ib, Format.R16_UInt, 0);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.VSSetShader(_vs);
        Context.VSSetConstantBuffer(0, _cb);
        Context.PSSetShader(_ps);
        Context.PSSetSampler(0, _sampler);
        Context.OMSetBlendState(_blend);
        Context.OMSetDepthStencilState(_depthDisabled);
        Context.RSSetState(_raster);

        var globalIdxOffset = 0;
        var globalVtxOffset = 0;
        for (var n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            for (var cmdI = 0; cmdI < cmdList.CmdBuffer.Size; cmdI++)
            {
                var pcmd = cmdList.CmdBuffer[cmdI];
                if (pcmd.UserCallback != IntPtr.Zero)
                    continue;

                var left = (int) Math.Max(0, pcmd.ClipRect.X - drawData.DisplayPos.X);
                var top = (int) Math.Max(0, pcmd.ClipRect.Y - drawData.DisplayPos.Y);
                var right = (int) Math.Min(_clientWidth, pcmd.ClipRect.Z - drawData.DisplayPos.X);
                var bottom = (int) Math.Min(_clientHeight, pcmd.ClipRect.W - drawData.DisplayPos.Y);
                if (right <= left || bottom <= top)
                    continue;

                Context.RSSetScissorRect(left, top, right, bottom);
                var texPtr = pcmd.GetTexID();
                if (texPtr != IntPtr.Zero)
                {
                    using var srv = new ID3D11ShaderResourceView(texPtr);
                    // Non-owning wrapper: AddRef before setting so Dispose doesn't drop refcount
                    Marshal.AddRef(texPtr);
                    Context.PSSetShaderResource(0, srv);
                }

                Context.DrawIndexed(pcmd.ElemCount, pcmd.IdxOffset + (uint) globalIdxOffset, (int) pcmd.VtxOffset + globalVtxOffset);
            }
            globalIdxOffset += cmdList.IdxBuffer.Size;
            globalVtxOffset += cmdList.VtxBuffer.Size;
        }
    }

    private void HookInputEvents()
    {
        Window.Resize += (_, _) =>
        {
            if (_disposed || _exitRequested || Window.IsDisposed || Window.WindowState == FormWindowState.Minimized) return;
            var w = Math.Max(1, Window.ClientSize.Width);
            var h = Math.Max(1, Window.ClientSize.Height);
            if (w == _clientWidth && h == _clientHeight) return;
            _clientWidth = w;
            _clientHeight = h;
            _backBufferRtv?.Dispose();
            _backBufferRtv = null;
            _swapChain.ResizeBuffers(0, (uint) w, (uint) h, Format.Unknown, SwapChainFlags.None);
            RecreateBackBuffer();
        };

        Window.MouseMove += (_, e) => { if (!_disposed && !_exitRequested) ImGui.GetIO().AddMousePosEvent(e.X, e.Y); };
        Window.MouseDown += (_, e) =>
        {
            if (_disposed || _exitRequested) return;
            var btn = MapMouseButton(e.Button);
            if (btn >= 0) ImGui.GetIO().AddMouseButtonEvent(btn, true);
        };
        Window.MouseUp += (_, e) =>
        {
            if (_disposed || _exitRequested) return;
            var btn = MapMouseButton(e.Button);
            if (btn >= 0) ImGui.GetIO().AddMouseButtonEvent(btn, false);
        };
        Window.MouseWheel += (_, e) => { if (!_disposed && !_exitRequested) ImGui.GetIO().AddMouseWheelEvent(0, e.Delta / 120f); };
        Window.KeyDown += (_, e) => { if (!_disposed && !_exitRequested) UpdateKey(e.KeyCode, true, e); };
        Window.KeyUp += (_, e) => { if (!_disposed && !_exitRequested) UpdateKey(e.KeyCode, false, e); };
        Window.KeyPress += (_, e) =>
        {
            if (!_disposed && !_exitRequested && !char.IsControl(e.KeyChar))
                ImGui.GetIO().AddInputCharacter(e.KeyChar);
        };
        Window.Deactivate += (_, _) => { if (!_disposed && !_exitRequested) ImGui.GetIO().AddFocusEvent(false); };
        Window.Activated += (_, _) => { if (!_disposed && !_exitRequested) ImGui.GetIO().AddFocusEvent(true); };
        Window.FormClosing += (_, _) => _exitRequested = true;
    }

    private static int MapMouseButton(MouseButtons b) => b switch
    {
        MouseButtons.Left => 0,
        MouseButtons.Right => 1,
        MouseButtons.Middle => 2,
        _ => -1
    };

    private static void UpdateKey(Keys key, bool down, KeyEventArgs e)
    {
        var io = ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.ModCtrl, e.Control);
        io.AddKeyEvent(ImGuiKey.ModShift, e.Shift);
        io.AddKeyEvent(ImGuiKey.ModAlt, e.Alt);

        var mapped = key switch
        {
            Keys.Tab => ImGuiKey.Tab,
            Keys.Left => ImGuiKey.LeftArrow,
            Keys.Right => ImGuiKey.RightArrow,
            Keys.Up => ImGuiKey.UpArrow,
            Keys.Down => ImGuiKey.DownArrow,
            Keys.PageUp => ImGuiKey.PageUp,
            Keys.PageDown => ImGuiKey.PageDown,
            Keys.Home => ImGuiKey.Home,
            Keys.End => ImGuiKey.End,
            Keys.Insert => ImGuiKey.Insert,
            Keys.Delete => ImGuiKey.Delete,
            Keys.Back => ImGuiKey.Backspace,
            Keys.Space => ImGuiKey.Space,
            Keys.Enter => ImGuiKey.Enter,
            Keys.Escape => ImGuiKey.Escape,
            >= Keys.A and <= Keys.Z => ImGuiKey.A + (key - Keys.A),
            >= Keys.D0 and <= Keys.D9 => ImGuiKey._0 + (key - Keys.D0),
            >= Keys.F1 and <= Keys.F12 => ImGuiKey.F1 + (key - Keys.F1),
            _ => ImGuiKey.None
        };
        if (mapped != ImGuiKey.None)
            io.AddKeyEvent(mapped, down);
    }

    public void Dispose()
    {
        _disposed = true;
        _exitRequested = true;
        while (_uiQueue.TryDequeue(out _)) { }
        _vb?.Dispose();
        _ib?.Dispose();
        _fontSrv.Dispose();
        _depthDisabled.Dispose();
        _raster.Dispose();
        _blend.Dispose();
        _sampler.Dispose();
        _cb.Dispose();
        _layout.Dispose();
        _ps.Dispose();
        _vs.Dispose();
        _backBufferRtv?.Dispose();
        _swapChain.Dispose();
        if (!Window.IsDisposed)
        {
            try { Window.Dispose(); }
            catch { }
        }
    }
}
