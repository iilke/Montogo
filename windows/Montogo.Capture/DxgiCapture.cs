using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;

namespace Montogo.Capture;

[SupportedOSPlatform("windows8.0")]
public sealed class DxgiCapture : IDisposable
{
    private static readonly int E_DXGI_NOT_FOUND    = unchecked((int)0x887A0002);
    private static readonly int E_DXGI_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private static readonly int E_DXGI_ACCESS_LOST  = unchecked((int)0x887A0026);

    private readonly CaptureOptions _options;
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    public Action<CapturedFrame>? FrameCaptured;

    public DxgiCapture(CaptureOptions options) => _options = options;

    public static IReadOnlyList<DisplayInfo> EnumerateDisplays()
    {
        PInvoke.CreateDXGIFactory1<IDXGIFactory1>(out IDXGIFactory1 factory).ThrowOnFailure();

        var displays = new List<DisplayInfo>();
        int globalIdx = 0;

        for (uint a = 0; ; a++)
        {
            HRESULT hr = factory.EnumAdapters1(a, out IDXGIAdapter1 adapter);
            if (hr.Value == E_DXGI_NOT_FOUND) break;
            hr.ThrowOnFailure();

            for (uint o = 0; ; o++)
            {
                HRESULT hr2 = adapter.EnumOutputs(o, out IDXGIOutput output);
                if (hr2.Value == E_DXGI_NOT_FOUND) break;
                hr2.ThrowOnFailure();

                DXGI_OUTPUT_DESC desc = output.GetDesc();
                int left = desc.DesktopCoordinates.left;
                int top  = desc.DesktopCoordinates.top;
                int w    = desc.DesktopCoordinates.right  - left;
                int h    = desc.DesktopCoordinates.bottom - top;
                displays.Add(new DisplayInfo(globalIdx, desc.DeviceName.ToString(), left, top, w, h, IsPrimary: globalIdx == 0));
                globalIdx++;
            }
        }

        return displays;
    }

    public void Start()
    {
        if (_cts != null) throw new InvalidOperationException("Already started.");
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => CaptureLoop(_cts.Token)) { IsBackground = true, Name = "DxgiCapture" };
        _thread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _thread?.Join();
        _cts?.Dispose();
        _cts = null;
        _thread = null;
    }

    private void CaptureLoop(CancellationToken ct)
    {
        // All COM objects declared up front so the single finally block can release them
        // in reverse acquisition order regardless of where an exception is thrown.
        PInvoke.CreateDXGIFactory1<IDXGIFactory1>(out IDXGIFactory1 factory).ThrowOnFailure();
        IDXGIAdapter1?          targetAdapter = null;
        IDXGIOutput?            targetOutput  = null;
        ID3D11Device?           device        = null;
        ID3D11DeviceContext?    context       = null;
        IDXGIOutputDuplication? duplication   = null;
        ID3D11Texture2D?        staging       = null;

        try
        {
            // Find target adapter+output by global index, matching EnumerateDisplays order
            int idx = 0;
            bool found = false;

            for (uint a = 0; !found; a++)
            {
                HRESULT hr = factory.EnumAdapters1(a, out IDXGIAdapter1 adapter);
                if (hr.Value == E_DXGI_NOT_FOUND) break;
                hr.ThrowOnFailure();

                for (uint o = 0; ; o++)
                {
                    HRESULT hr2 = adapter.EnumOutputs(o, out IDXGIOutput output);
                    if (hr2.Value == E_DXGI_NOT_FOUND) break;
                    hr2.ThrowOnFailure();

                    if (idx == _options.OutputIndex)
                    {
                        targetAdapter = adapter;
                        targetOutput  = output;
                        found = true;
                        break;
                    }
                    idx++;
                }
            }

            if (!found)
                throw new ArgumentOutOfRangeException(nameof(_options), $"Output index {_options.OutputIndex} does not exist.");

            DXGI_OUTPUT_DESC desc = targetOutput!.GetDesc();
            int width  = desc.DesktopCoordinates.right  - desc.DesktopCoordinates.left;
            int height = desc.DesktopCoordinates.bottom - desc.DesktopCoordinates.top;

            // Driver type must be UNKNOWN when passing a non-null adapter
            PInvoke.D3D11CreateDevice(
                (IDXGIAdapter)targetAdapter!,
                D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
                default,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                [D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0],
                PInvoke.D3D11_SDK_VERSION,
                out device,
                out context).ThrowOnFailure();

            var output1 = (IDXGIOutput1)targetOutput;
            output1.DuplicateOutput(device, out duplication);

            var stageDesc = new D3D11_TEXTURE2D_DESC
            {
                Width          = (uint)width,
                Height         = (uint)height,
                MipLevels      = 1,
                ArraySize      = 1,
                Format         = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc     = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage          = D3D11_USAGE.D3D11_USAGE_STAGING,
                BindFlags      = 0,
                CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                MiscFlags      = 0,
            };
            device.CreateTexture2D(in stageDesc, (D3D11_SUBRESOURCE_DATA?)null, out staging);

            // frameData is the reusable GPU-to-CPU staging buffer; it must not be passed
            // to the pipeline directly because the capture thread overwrites it each frame.
            var frameData = new byte[width * height * 4];
            int timeoutMs = 1000 / Math.Max(1, _options.TargetFps);

            while (!ct.IsCancellationRequested)
            {
                DXGI_OUTDUPL_FRAME_INFO frameInfo = default;
                IDXGIResource           resource  = default!;
                bool hasFrame = false;

                try
                {
                    try
                    {
                        duplication.AcquireNextFrame((uint)timeoutMs, out frameInfo, out resource);
                        hasFrame = true;
                    }
                    catch (COMException ex) when (ex.HResult == E_DXGI_WAIT_TIMEOUT)
                    {
                        continue;
                    }
                    catch (COMException ex) when (ex.HResult == E_DXGI_ACCESS_LOST)
                    {
                        throw new InvalidOperationException("Desktop duplication lost (display mode changed).", ex);
                    }

                    // Skip frames with no new desktop content (e.g. cursor-only updates)
                    if (frameInfo.LastPresentTime == 0)
                        continue;

                    var desktop = (ID3D11Texture2D)resource;
                    context.CopyResource((ID3D11Resource)staging, (ID3D11Resource)desktop);
                }
                finally
                {
                    if (hasFrame)
                        duplication.ReleaseFrame();
                }

                context.Map((ID3D11Resource)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, out D3D11_MAPPED_SUBRESOURCE mapped);
                try
                {
                    unsafe
                    {
                        byte* src    = (byte*)mapped.pData;
                        int rowPitch = (int)mapped.RowPitch;
                        int rowBytes = width * 4;

                        if (rowPitch == rowBytes)
                        {
                            new ReadOnlySpan<byte>(src, frameData.Length).CopyTo(frameData);
                        }
                        else
                        {
                            for (int y = 0; y < height; y++)
                                new ReadOnlySpan<byte>(src + (long)y * rowPitch, rowBytes)
                                    .CopyTo(frameData.AsSpan(y * rowBytes, rowBytes));
                        }
                    }
                }
                finally
                {
                    context.Unmap((ID3D11Resource)staging, 0);
                }

                // Fresh allocation per frame so CapturedFrame.BgraData is immutable and
                // safe to hold across threads, matching the contract in CapturedFrame.cs.
                byte[] frameCopy = new byte[frameData.Length];
                frameData.AsSpan().CopyTo(frameCopy);
                FrameCaptured?.Invoke(new CapturedFrame { BgraData = frameCopy, Width = width, Height = height });
            }
        }
        finally
        {
            // Release in reverse acquisition order so no object outlives its dependents.
            if (staging     is not null) Marshal.ReleaseComObject(staging);
            if (duplication is not null) Marshal.ReleaseComObject(duplication);
            if (context     is not null) Marshal.ReleaseComObject(context);
            if (device      is not null) Marshal.ReleaseComObject(device);
            if (targetOutput  is not null) Marshal.ReleaseComObject(targetOutput);
            if (targetAdapter is not null) Marshal.ReleaseComObject(targetAdapter);
            Marshal.ReleaseComObject(factory);
        }
    }

    public void Dispose() => Stop();
}
