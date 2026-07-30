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

    /// <summary>Fires when the capture thread dies from an unhandled exception.</summary>
    public Action<Exception>? CaptureFailed;

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
        _thread = new Thread(() =>
        {
            try { CaptureLoop(_cts.Token); }
            catch (Exception ex) { CaptureFailed?.Invoke(ex); }
        }) { IsBackground = true, Name = "DxgiCapture" };
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

            // DXGI only delivers frames when desktop content changes.  On a static
            // desktop AcquireNextFrame times out forever, which would starve the
            // stream and trip the Mac's connection watchdog — so on timeout (or a
            // cursor-only update) the previous frame is re-emitted at the target fps.
            // lastFrame is always the CLEAN desktop (no cursor); the cursor is
            // composited per emit so it can move over a static desktop.
            byte[]? lastFrame   = null;
            byte[]? lastEmitted = null;
            long    lastEmitMs  = 0;

            // Cursor state, updated from DXGI pointer info on each acquired frame.
            var  cursorShape     = Array.Empty<byte>();
            DXGI_OUTDUPL_POINTER_SHAPE_INFO cursorShapeInfo = default;
            bool cursorVisible = false;
            int  cursorX = 0, cursorY = 0;
            bool cursorDirty = false;

            byte[] ComposeEmit()
            {
                if (!cursorVisible || cursorShape.Length == 0) return lastFrame!;
                byte[] buf = new byte[lastFrame!.Length];
                lastFrame.AsSpan().CopyTo(buf);
                DrawCursor(buf, width, height, cursorShape, in cursorShapeInfo, cursorX, cursorY);
                return buf;
            }

            while (!ct.IsCancellationRequested)
            {
                DXGI_OUTDUPL_FRAME_INFO frameInfo = default;
                IDXGIResource           resource  = default!;
                bool hasFrame      = false;
                bool gotNewContent = false;

                try
                {
                    try
                    {
                        duplication.AcquireNextFrame((uint)timeoutMs, out frameInfo, out resource);
                        hasFrame = true;
                    }
                    catch (COMException ex) when (ex.HResult == E_DXGI_WAIT_TIMEOUT)
                    {
                    }
                    catch (COMException ex) when (ex.HResult == E_DXGI_ACCESS_LOST)
                    {
                        throw new InvalidOperationException("Desktop duplication lost (display mode changed).", ex);
                    }

                    if (hasFrame)
                    {
                        // Position/visibility (only valid when the mouse actually updated)
                        if (frameInfo.LastMouseUpdateTime != 0)
                        {
                            bool visible = frameInfo.PointerPosition.Visible;
                            int  px = frameInfo.PointerPosition.Position.X;
                            int  py = frameInfo.PointerPosition.Position.Y;
                            if (visible != cursorVisible || px != cursorX || py != cursorY)
                            {
                                cursorVisible = visible;
                                cursorX = px;
                                cursorY = py;
                                cursorDirty = true;
                            }
                        }

                        // New shape (must be fetched while the frame is still acquired)
                        if (frameInfo.PointerShapeBufferSize > 0)
                        {
                            if (cursorShape.Length < frameInfo.PointerShapeBufferSize)
                                cursorShape = new byte[frameInfo.PointerShapeBufferSize];
                            duplication.GetFramePointerShape(
                                cursorShape.AsSpan(), out uint _, out cursorShapeInfo);
                            cursorDirty = true;
                        }
                    }

                    // LastPresentTime == 0 means no new desktop image (cursor-only
                    // update) — except the very first frame, which must seed the stream.
                    gotNewContent = hasFrame && (frameInfo.LastPresentTime != 0 || lastFrame is null);

                    if (gotNewContent)
                    {
                        var desktop = (ID3D11Texture2D)resource;
                        context.CopyResource((ID3D11Resource)staging, (ID3D11Resource)desktop);
                    }
                }
                finally
                {
                    if (hasFrame)
                        duplication.ReleaseFrame();
                }

                if (!gotNewContent)
                {
                    // Emitted buffers are never mutated after creation, so re-emitting
                    // one is safe under the receiver-owns-the-buffer contract; only a
                    // cursor change forces a fresh composite.
                    long nowMs = Environment.TickCount64;
                    if (lastFrame is not null && nowMs - lastEmitMs >= timeoutMs)
                    {
                        lastEmitMs = nowMs;
                        if (cursorDirty || lastEmitted is null)
                        {
                            lastEmitted = ComposeEmit();
                            cursorDirty = false;
                        }
                        FrameCaptured?.Invoke(new CapturedFrame { BgraData = lastEmitted, Width = width, Height = height });
                    }
                    continue;
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
                lastFrame   = frameCopy;
                lastEmitted = ComposeEmit();
                cursorDirty = false;
                lastEmitMs  = Environment.TickCount64;
                FrameCaptured?.Invoke(new CapturedFrame { BgraData = lastEmitted, Width = width, Height = height });
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

    // ── Cursor compositing ───────────────────────────────────────────────────

    private const uint PointerShapeMonochrome  = 1; // DXGI_OUTDUPL_POINTER_SHAPE_TYPE_*
    private const uint PointerShapeColor       = 2;
    private const uint PointerShapeMaskedColor = 4;

    /// <summary>
    /// Draws the DXGI pointer shape onto a BGRA frame at (px, py), clipped to the
    /// frame bounds.  Desktop duplication never composites the hardware cursor into
    /// captured frames, so it must be drawn manually before encoding.
    /// </summary>
    private static void DrawCursor(
        byte[] dst, int frameW, int frameH,
        byte[] shape, in DXGI_OUTDUPL_POINTER_SHAPE_INFO info, int px, int py)
    {
        uint type  = info.Type;
        int  w     = (int)info.Width;
        int  pitch = (int)info.Pitch;
        // Monochrome shapes stack the AND mask on top of the XOR mask, so the
        // reported height covers both.
        int h = (int)(type == PointerShapeMonochrome ? info.Height / 2 : info.Height);

        for (int y = 0; y < h; y++)
        {
            int fy = py + y;
            if ((uint)fy >= (uint)frameH) continue;

            for (int x = 0; x < w; x++)
            {
                int fx = px + x;
                if ((uint)fx >= (uint)frameW) continue;

                int di = (fy * frameW + fx) * 4;

                switch (type)
                {
                    case PointerShapeColor:
                    {
                        int si = y * pitch + x * 4;
                        int a  = shape[si + 3];
                        if (a == 0) break;
                        if (a == 255)
                        {
                            dst[di]     = shape[si];
                            dst[di + 1] = shape[si + 1];
                            dst[di + 2] = shape[si + 2];
                        }
                        else
                        {
                            dst[di]     = (byte)((shape[si]     * a + dst[di]     * (255 - a)) / 255);
                            dst[di + 1] = (byte)((shape[si + 1] * a + dst[di + 1] * (255 - a)) / 255);
                            dst[di + 2] = (byte)((shape[si + 2] * a + dst[di + 2] * (255 - a)) / 255);
                        }
                        break;
                    }
                    case PointerShapeMaskedColor:
                    {
                        // Alpha byte is a mask: 0 → replace with the shape colour,
                        // 0xFF → XOR the shape colour with the screen.
                        int si = y * pitch + x * 4;
                        if (shape[si + 3] == 0)
                        {
                            dst[di]     = shape[si];
                            dst[di + 1] = shape[si + 1];
                            dst[di + 2] = shape[si + 2];
                        }
                        else
                        {
                            dst[di]     ^= shape[si];
                            dst[di + 1] ^= shape[si + 1];
                            dst[di + 2] ^= shape[si + 2];
                        }
                        break;
                    }
                    case PointerShapeMonochrome:
                    {
                        // 1 bpp: AND mask rows first, then XOR mask rows.
                        // and=1,xor=0 → transparent; and=0 → solid black/white;
                        // and=1,xor=1 → invert the screen pixel.
                        int  bit = 0x80 >> (x & 7);
                        bool and = (shape[y * pitch + (x >> 3)]       & bit) != 0;
                        bool xor = (shape[(y + h) * pitch + (x >> 3)] & bit) != 0;
                        if (and && !xor) break;
                        if (!and)
                        {
                            byte v = xor ? (byte)255 : (byte)0;
                            dst[di] = v; dst[di + 1] = v; dst[di + 2] = v;
                        }
                        else
                        {
                            dst[di] ^= 255; dst[di + 1] ^= 255; dst[di + 2] ^= 255;
                        }
                        break;
                    }
                }
            }
        }
    }

    public void Dispose() => Stop();
}
