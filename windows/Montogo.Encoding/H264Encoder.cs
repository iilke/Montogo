using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace Montogo.Encoding;

[SupportedOSPlatform("windows6.0.6000")]
public sealed class H264Encoder : IDisposable
{
    private static readonly int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);

    // Hardware encoder CLSIDs tried in priority order
    private static readonly (Guid Clsid, string Name)[] HardwareCandidates =
    [
        (new Guid("8FD38F55-B9B6-4B14-AE64-7C4B8C4D6C7F"), "NVENC"),
        (new Guid("4BE8D3C0-0515-4A37-AD55-E4BAE19AF471"), "Intel Quick Sync"),
        (new Guid("ADC9BC80-0F41-46C6-AB75-D693D793597D"), "AMD AMF"),
    ];

    // Microsoft H.264 Video Encoder MFT (software, always available on Win 7+)
    private static readonly Guid SoftwareClsid = new("6CA50344-051A-4DED-9779-A43305165E35");

    private readonly EncoderOptions _options;
    private IMFTransform? _encoder;
    private byte[]? _nv12;
    private long _frameDurationHns;
    private bool _started;

    /// <summary>True if the active encoder is a hardware GPU encoder (NVENC / Quick Sync / AMF).</summary>
    public bool IsHardwareAccelerated { get; private set; }

    public Action<EncodedFrame>? FrameEncoded;

    public H264Encoder(EncoderOptions options) => _options = options;

    // ── Encoder probe ────────────────────────────────────────────────────────

    /// <summary>
    /// Probes for a hardware H.264 MFT via cheap COM instantiation.
    /// Call this before creating EncoderOptions so you can pick the right fps.
    /// Does its own MFStartup/MFShutdown pair — safe to call before Initialize().
    /// </summary>
    public static bool IsHardwareEncoderAvailable()
    {
        if (!PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_NOSOCKET).Succeeded)
            return false;
        try
        {
            foreach (var (clsid, _) in HardwareCandidates)
            {
                try
                {
                    var t = Type.GetTypeFromCLSID(clsid);
                    if (t is null) continue;
                    if (Activator.CreateInstance(t) is { } mft)
                    {
                        Marshal.ReleaseComObject(mft);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }
        finally { PInvoke.MFShutdown(); }
    }

    // ── Initialization ───────────────────────────────────────────────────────

    public void Initialize()
    {
        if (_started) throw new InvalidOperationException("Already initialized.");

        PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_NOSOCKET).ThrowOnFailure();

        (_encoder, IsHardwareAccelerated) = CreateEncoder();

        _encoder.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        _encoder.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

        _nv12 = new byte[_options.Width * _options.Height * 3 / 2];
        _frameDurationHns = 10_000_000L / _options.Fps;
        _started = true;
    }

    private (IMFTransform Mft, bool IsHardware) CreateEncoder()
    {
        // Try each hardware encoder. SetupTypesOn throws if the MFT rejects our config
        // (absent hardware, wrong driver, unsupported resolution, etc.).
        foreach (var (clsid, name) in HardwareCandidates)
        {
            try
            {
                var t = Type.GetTypeFromCLSID(clsid);
                if (t is null) continue;
                var mft = (IMFTransform)Activator.CreateInstance(t)!;
                SetupTypesOn(mft);
                Trace.WriteLine($"[Montogo.Encoding] Using hardware H.264 encoder: {name}");
                return (mft, IsHardware: true);
            }
            catch { }
        }

        // Software fallback — guaranteed to exist on all supported Windows versions
        Trace.WriteLine("[Montogo.Encoding] No hardware H.264 encoder found; falling back to Microsoft software MFT");
        var softType = Type.GetTypeFromCLSID(SoftwareClsid, throwOnError: true)!;
        var softMft  = (IMFTransform)Activator.CreateInstance(softType)!;
        SetupTypesOn(softMft);
        return (softMft, IsHardware: false);
    }

    private void SetupTypesOn(IMFTransform mft)
    {
        // Output (H.264) must be set before input (NV12) — MFT rejects the reverse order
        PInvoke.MFCreateMediaType(out IMFMediaType outputType).ThrowOnFailure();
        outputType.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        outputType.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
        outputType.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, PackRatio(_options.Width, _options.Height));
        outputType.SetUINT64(PInvoke.MF_MT_FRAME_RATE, PackRatio(_options.Fps, 1));
        outputType.SetUINT32(PInvoke.MF_MT_AVG_BITRATE, (uint)_options.BitrateBps);
        outputType.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, 2); // MFVideoInterlaceMode_Progressive
        outputType.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, PackRatio(1, 1));
        mft.SetOutputType(0, outputType, 0);

        PInvoke.MFCreateMediaType(out IMFMediaType inputType).ThrowOnFailure();
        inputType.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        inputType.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
        inputType.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, PackRatio(_options.Width, _options.Height));
        inputType.SetUINT64(PInvoke.MF_MT_FRAME_RATE, PackRatio(_options.Fps, 1));
        inputType.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, 2);
        inputType.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, PackRatio(1, 1));
        mft.SetInputType(0, inputType, 0);
    }

    private static ulong PackRatio(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    // ── Frame submission ─────────────────────────────────────────────────────

    public unsafe void SubmitFrame(ReadOnlySpan<byte> bgraData, long timestampUs)
    {
        if (!_started) throw new InvalidOperationException("Not initialized.");

        BgraToNv12(bgraData, _nv12!, _options.Width, _options.Height);

        uint nv12Size = (uint)_nv12!.Length;
        PInvoke.MFCreateMemoryBuffer(nv12Size, out IMFMediaBuffer buffer).ThrowOnFailure();
        buffer.Lock(out byte* ptr);
        try { _nv12.AsSpan().CopyTo(new Span<byte>(ptr, (int)nv12Size)); }
        finally { buffer.Unlock(); }
        buffer.SetCurrentLength(nv12Size);

        PInvoke.MFCreateSample(out IMFSample sample).ThrowOnFailure();
        sample.AddBuffer(buffer);
        sample.SetSampleTime(timestampUs * 10); // µs → 100-ns units
        sample.SetSampleDuration(_frameDurationHns);

        _encoder!.ProcessInput(0, sample, 0);
        DrainOutputs();
    }

    private unsafe void DrainOutputs()
    {
        var outputs = new MFT_OUTPUT_DATA_BUFFER[1];
        while (true)
        {
            outputs[0] = new MFT_OUTPUT_DATA_BUFFER { dwStreamID = 0 }; // pSample = null → MFT allocates
            try { _encoder!.ProcessOutput(0, 1, outputs, out _); }
            catch (COMException ex) when (ex.HResult == MF_E_TRANSFORM_NEED_MORE_INPUT) { break; }

            IMFSample outSample = outputs[0].pSample;
            if (outSample is null) continue;

            bool isKeyFrame;
            try { outSample.GetUINT32(PInvoke.MFSampleExtension_CleanPoint, out uint v); isKeyFrame = v != 0; }
            catch (COMException) { isKeyFrame = false; }

            outSample.GetSampleTime(out long sampleTimeHns);
            outSample.ConvertToContiguousBuffer(out IMFMediaBuffer outBuffer);

            outBuffer.Lock(out byte* ptr, out uint maxLen, out uint curLen);
            byte[] data = new byte[curLen];
            try { new ReadOnlySpan<byte>(ptr, (int)curLen).CopyTo(data); }
            finally { outBuffer.Unlock(); }

            FrameEncoded?.Invoke(new EncodedFrame
            {
                Data        = data,
                IsKeyFrame  = isKeyFrame,
                TimestampUs = sampleTimeHns / 10,
            });
        }
    }

    // ── Color conversion ─────────────────────────────────────────────────────

    // BT.601 limited range, integer approximation.
    // UV sampled from top-left pixel of each 2×2 block.
    private static void BgraToNv12(ReadOnlySpan<byte> bgra, byte[] nv12, int width, int height)
    {
        int yPlaneSize = width * height;

        for (int row = 0; row < height; row++)
        for (int col = 0; col < width;  col++)
        {
            int i = (row * width + col) * 4;
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            nv12[row * width + col] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
        }

        for (int row = 0; row < height; row += 2)
        for (int col = 0; col < width;  col += 2)
        {
            int i = (row * width + col) * 4;
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
            int v = ((112 * r - 94 * g -  18 * b + 128) >> 8) + 128;
            int idx = yPlaneSize + (row / 2) * width + col;
            nv12[idx]     = (byte)Math.Clamp(u, 0, 255);
            nv12[idx + 1] = (byte)Math.Clamp(v, 0, 255);
        }
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        try { _encoder?.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, 0); }
        catch { }
        _encoder = null;
        PInvoke.MFShutdown();
    }
}
