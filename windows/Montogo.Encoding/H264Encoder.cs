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
    private static readonly int MF_E_NO_EVENTS_AVAILABLE       = unchecked((int)0xC00D3E80);

    // MediaEventType values for async MFTs (mfobjects.h)
    private const uint METransformNeedInput  = 601;
    private const uint METransformHaveOutput = 602;
    private const uint MF_EVENT_FLAG_NO_WAIT = 1;

    private readonly EncoderOptions _options;
    private IMFTransform? _encoder;
    private long _frameDurationHns;
    private bool _started;

    // Set for an MFT that does not advertise MFT_OUTPUT_STREAM_PROVIDES_SAMPLES, so we must
    // supply a reusable output sample. Hardware HEVC MFTs provide their own, so this stays null
    // for NVENC/Quick Sync/AMF; kept as a defensive fallback for any that don't.
    private IMFSample? _reusableOutputSample;

    // Non-null when the active encoder is an async MFT (all hardware encoders are).
    // Async MFTs must be driven by their event queue: ProcessInput is only legal
    // after a METransformNeedInput event, ProcessOutput after METransformHaveOutput.
    private IMFMediaEventGenerator? _eventGen;
    private int _pendingNeedInput;

    // Retained ICodecAPI + a cross-thread flag so RequestKeyFrame can force an IDR
    // on the next frame (set from the handshake thread, applied on the pipeline thread).
    private ICodecAPI? _codecApi;
    private int _forceKeyframe;
    private int _pendingBitrate;   // 0 = none; set cross-thread, applied on the pump thread

    /// <summary>True if the active encoder is a hardware GPU encoder (NVENC / Quick Sync / AMF).</summary>
    public bool IsHardwareAccelerated { get; private set; }

    public Action<EncodedFrame>? FrameEncoded;

    public H264Encoder(EncoderOptions options) => _options = options;

    // ── Encoder probe ────────────────────────────────────────────────────────

    /// <summary>
    /// Probes for a hardware H.264 MFT via MFTEnumEx.
    /// Call this before creating EncoderOptions so you can pick the right fps.
    /// Does its own MFStartup/MFShutdown pair — safe to call before Initialize().
    /// </summary>
    public static bool IsHardwareEncoderAvailable()
    {
        if (!PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_NOSOCKET).Succeeded)
            return false;
        try
        {
            EnumHardwareEncoders(activateFirst: false, out uint count);
            return count > 0;
        }
        finally { PInvoke.MFShutdown(); }
    }

    /// <summary>
    /// Enumerates hardware H.264 encoder MFTs.  With activateFirst the first one
    /// (MFT_ENUM_FLAG_SORTANDFILTER orders by merit) is activated and returned.
    /// </summary>
    private static unsafe IMFTransform? EnumHardwareEncoders(bool activateFirst, out uint count)
    {
        var outType = new MFT_REGISTER_TYPE_INFO
        {
            guidMajorType = PInvoke.MFMediaType_Video,
            guidSubtype   = PInvoke.MFVideoFormat_HEVC,
        };

        PInvoke.MFTEnumEx(
            PInvoke.MFT_CATEGORY_VIDEO_ENCODER,
            MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER,
            null, outType,
            out IMFActivate_unmanaged** activates, out count).ThrowOnFailure();

        if (count == 0) return null;

        try
        {
            if (!activateFirst) return null;

            var activate = (IMFActivate)Marshal.GetObjectForIUnknown((IntPtr)activates[0]);
            try
            {
                Guid iid = typeof(IMFTransform).GUID;
                return (IMFTransform)activate.ActivateObject(&iid);
            }
            finally { Marshal.ReleaseComObject(activate); }
        }
        finally
        {
            for (uint i = 0; i < count; i++) Marshal.Release((IntPtr)activates[i]);
            Marshal.FreeCoTaskMem((IntPtr)activates);
        }
    }

    // ── Initialization ───────────────────────────────────────────────────────

    public void Initialize()
    {
        if (_started) throw new InvalidOperationException("Already initialized.");

        PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_NOSOCKET).ThrowOnFailure();

        (_encoder, IsHardwareAccelerated) = CreateEncoder();

        // Hardware MFTs advertise MFT_OUTPUT_STREAM_PROVIDES_SAMPLES and allocate their own
        // output samples (pSample = null). Supply one only for an MFT that doesn't.
        _encoder.GetOutputStreamInfo(0, out MFT_OUTPUT_STREAM_INFO streamInfo);
        const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;
        if ((streamInfo.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) == 0)
        {
            uint size = streamInfo.cbSize > 0
                ? streamInfo.cbSize
                : (uint)(_options.Width * _options.Height * 2);
            PInvoke.MFCreateMemoryBuffer(size, out IMFMediaBuffer outBuf).ThrowOnFailure();
            PInvoke.MFCreateSample(out IMFSample outSample).ThrowOnFailure();
            outSample.AddBuffer(outBuf);
            _reusableOutputSample = outSample;
        }

        _encoder.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        _encoder.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

        _frameDurationHns = 10_000_000L / _options.Fps;
        _started = true;
    }

    private (IMFTransform Mft, bool IsHardware) CreateEncoder()
    {
        // Hardware first: MFTEnumEx finds whatever the GPU driver registered
        // (NVENC / Quick Sync / AMF) without hard-coded vendor CLSIDs.
        try
        {
            if (EnumHardwareEncoders(activateFirst: true, out _) is { } hwMft)
            {
                try
                {
                    PrepareMft(hwMft);
                    Trace.WriteLine("[Montogo.Encoding] Using hardware HEVC encoder MFT");
                    return (hwMft, IsHardware: true);
                }
                catch
                {
                    // Config rejected (unsupported resolution, driver quirk…) — release
                    // and fall through to the unsupported path below.
                    _eventGen = null;
                    _pendingNeedInput = 0;
                    Marshal.ReleaseComObject(hwMft);
                }
            }
        }
        catch { }

        // No software HEVC encoder ships with Windows, so HEVC requires a GPU encoder — the
        // trade for HEVC's ~2x better compression. A machine without one can't run this build.
        throw new NotSupportedException(
            "No hardware HEVC encoder found. HEVC requires a GPU encoder (NVENC / Quick Sync / AMF).");
    }

    /// <summary>
    /// Unlocks async MFTs, applies low-latency codec settings, and negotiates types.
    /// Order matters: an async MFT rejects most IMFTransform calls until unlocked.
    /// </summary>
    private void PrepareMft(IMFTransform mft)
    {
        mft.GetAttributes(out IMFAttributes attrs);
        uint isAsync = 0;
        try { attrs.GetUINT32(PInvoke.MF_TRANSFORM_ASYNC, out isAsync); }
        catch (COMException) { }
        if (isAsync != 0)
        {
            attrs.SetUINT32(PInvoke.MF_TRANSFORM_ASYNC_UNLOCK, 1);
            _eventGen = (IMFMediaEventGenerator)mft;
        }

        // Rate-control settings must be applied AFTER the output type is set —
        // SetOutputType otherwise resets them, so an earlier CBR request is lost.
        SetupTypesOn(mft);
        TryConfigureCodec(mft);
    }

    // Best-effort ICodecAPI configuration. An encoder that rejects any of these
    // still works, just with more latency or a looser bitrate.
    private void TryConfigureCodec(IMFTransform mft)
    {
        if (mft is not ICodecAPI api)
        {
            Trace.WriteLine("[Montogo.Encoding] MFT does not expose ICodecAPI — no rate control applied");
            return;
        }
        _codecApi = api;   // retained so RequestKeyFrame can force an IDR later

        void Set(string name, Guid key, object value)
        {
            int hr = api.SetValue(key, ref value);
            if (hr != 0) Trace.WriteLine($"[Montogo.Encoding] ICodecAPI {name} rejected (0x{hr:X8})");
        }

        // Constant bitrate: without this the NVENC MFT treats MF_MT_AVG_BITRATE as a
        // soft hint and lets high-entropy frames balloon to ~170 Mbps.  CBR + a tight
        // VBV buffer keeps every frame near budget.
        Set("RateControlMode=CBR", CodecApiGuids.AVEncCommonRateControlMode, 0u);
        Set("MeanBitRate",         CodecApiGuids.AVEncCommonMeanBitRate, (uint)_options.BitrateBps);
        Set("MaxBitRate",          CodecApiGuids.AVEncCommonMaxBitRate, (uint)_options.BitrateBps);
        Set("BufferSize",          CodecApiGuids.AVEncCommonBufferSize, (uint)(_options.BitrateBps / 4));
        Set("LowLatencyMode",      CodecApiGuids.AVLowLatencyMode, true);
        // No B-frames. The Mac decoder assumes a plain IPPP stream and its renderer presents
        // frames in decode order, so B-frame reordering (decode order != display order) is
        // what shredded the picture when QualityVsSpeed was raised before. Pinning B-pictures
        // to 0 keeps the stream reorder-free, so we can now raise QualityVsSpeed for a cleaner
        // encode (more motion search / better mode decisions) without breaking the decoder.
        Set("BPictureCount=0",     CodecApiGuids.AVEncMPVDefaultBPictureCount, 0u);
        Set("QualityVsSpeed",      CodecApiGuids.AVEncCommonQualityVsSpeed, 60u);
        // (Keyframe interval is set on the output media type — see SetupTypesOn.
        //  The NVENC MFT ignores both that and the ICodecAPI GOP-size property and
        //  always emits ~1 keyframe/sec, so a shorter GOP can't be forced here.)
    }

    /// <summary>
    /// Requests that the next submitted frame be encoded as a keyframe (IDR).
    /// Thread-safe — call from any thread (e.g. when a Mac connects) so a new
    /// client can start decoding immediately without waiting for the long GOP.
    /// </summary>
    public void RequestKeyFrame() => Interlocked.Exchange(ref _forceKeyframe, 1);

    /// <summary>
    /// Requests a new target bitrate (bits/sec), applied to CBR mean/max/VBV on the next
    /// submitted frame. Thread-safe — the adaptive controller calls this from the feedback
    /// path; the actual ICodecAPI writes happen on the pump thread inside SubmitFrame.
    /// </summary>
    public void SetBitrate(int bitsPerSecond) => Interlocked.Exchange(ref _pendingBitrate, bitsPerSecond);

    private void SetupTypesOn(IMFTransform mft)
    {
        // Output (HEVC) must be set before the input type — the MFT rejects the reverse order.
        PInvoke.MFCreateMediaType(out IMFMediaType outputType).ThrowOnFailure();
        outputType.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        outputType.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_HEVC);
        outputType.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, PackRatio(_options.Width, _options.Height));
        outputType.SetUINT64(PInvoke.MF_MT_FRAME_RATE, PackRatio(_options.Fps, 1));
        outputType.SetUINT32(PInvoke.MF_MT_AVG_BITRATE, (uint)_options.BitrateBps);
        outputType.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, 2); // MFVideoInterlaceMode_Progressive
        outputType.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, PackRatio(1, 1));
        // Long keyframe interval. The NVENC MFT ignores it (it always emits ~1 keyframe/sec)
        // and a keyframe is forced on connect (RequestKeyFrame) for fast startup regardless.
        outputType.SetUINT32(PInvoke.MF_MT_MAX_KEYFRAME_SPACING, (uint)(_options.Fps * 4));
        mft.SetOutputType(0, outputType, 0);

        // Hardware NVENC accepts ARGB32 (our DXGI BGRA) directly and converts on the GPU, so
        // SubmitFrame hands it the raw DXGI bytes with no CPU colour conversion.
        var argb32 = new Guid("00000015-0000-0010-8000-00aa00389b71"); // MFVideoFormat_ARGB32
        PInvoke.MFCreateMediaType(out IMFMediaType inputType).ThrowOnFailure();
        inputType.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        inputType.SetGUID(PInvoke.MF_MT_SUBTYPE, argb32);
        inputType.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, PackRatio(_options.Width, _options.Height));
        inputType.SetUINT64(PInvoke.MF_MT_FRAME_RATE, PackRatio(_options.Fps, 1));
        inputType.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, 2);
        inputType.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, PackRatio(1, 1));
        // ARGB32 needs an explicit stride; positive = top-down, matching DXGI's row order.
        inputType.SetUINT32(PInvoke.MF_MT_DEFAULT_STRIDE, (uint)(_options.Width * 4));
        mft.SetInputType(0, inputType, 0);
    }

    private static ulong PackRatio(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    // ── Frame submission ─────────────────────────────────────────────────────

    public unsafe void SubmitFrame(ReadOnlySpan<byte> bgraData, long timestampUs)
    {
        if (!_started) throw new InvalidOperationException("Not initialized.");

        // Honour a pending keyframe request before the frame is fed to the encoder.
        if (Interlocked.Exchange(ref _forceKeyframe, 0) == 1 && _codecApi is not null)
        {
            try { object one = 1u; _codecApi.SetValue(CodecApiGuids.AVEncVideoForceKeyFrame, ref one); }
            catch { }
        }

        // Apply a pending adaptive-bitrate change (CBR mean/max + tight VBV) on this thread.
        int newBitrate = Interlocked.Exchange(ref _pendingBitrate, 0);
        if (newBitrate > 0 && _codecApi is not null)
        {
            try
            {
                object mean = (uint)newBitrate;       _codecApi.SetValue(CodecApiGuids.AVEncCommonMeanBitRate, ref mean);
                object max  = (uint)newBitrate;       _codecApi.SetValue(CodecApiGuids.AVEncCommonMaxBitRate,  ref max);
                object buf  = (uint)(newBitrate / 4); _codecApi.SetValue(CodecApiGuids.AVEncCommonBufferSize,  ref buf);
            }
            catch { }
        }

        // Hand the encoder the raw DXGI BGRA (ARGB32 input); the GPU does the colour convert.
        ReadOnlySpan<byte> src = bgraData;

        uint size = (uint)src.Length;
        PInvoke.MFCreateMemoryBuffer(size, out IMFMediaBuffer buffer).ThrowOnFailure();
        buffer.Lock(out byte* ptr);
        try { src.CopyTo(new Span<byte>(ptr, (int)size)); }
        finally { buffer.Unlock(); }
        buffer.SetCurrentLength(size);

        PInvoke.MFCreateSample(out IMFSample sample).ThrowOnFailure();
        sample.AddBuffer(buffer);
        sample.SetSampleTime(timestampUs * 10); // µs → 100-ns units
        sample.SetSampleDuration(_frameDurationHns);

        if (_eventGen is not null)
        {
            // Async MFT: ProcessInput is only legal after a NeedInput event.
            // Service output events while waiting for input credit.
            while (_pendingNeedInput == 0)
                PumpOneEvent(blocking: true);

            _encoder!.ProcessInput(0, sample, 0);
            _pendingNeedInput--;

            // Drain whatever the encoder has already queued, without blocking.
            while (PumpOneEvent(blocking: false)) { }
        }
        else
        {
            _encoder!.ProcessInput(0, sample, 0);
            while (DrainOne()) { }
        }
    }

    /// <returns>false when the event queue was empty (non-blocking mode only).</returns>
    private bool PumpOneEvent(bool blocking)
    {
        IMFMediaEvent evt;
        var flags = (MEDIA_EVENT_GENERATOR_GET_EVENT_FLAGS)(blocking ? 0 : MF_EVENT_FLAG_NO_WAIT);
        try { _eventGen!.GetEvent(flags, out evt); }
        catch (COMException ex) when (ex.HResult == MF_E_NO_EVENTS_AVAILABLE) { return false; }

        evt.GetType(out uint eventType);
        if      (eventType == METransformNeedInput)  _pendingNeedInput++;
        else if (eventType == METransformHaveOutput) DrainOne();
        return true;
    }

    /// <returns>false when the MFT needs more input before it can produce output.</returns>
    private unsafe bool DrainOne()
    {
        var outputs = new MFT_OUTPUT_DATA_BUFFER[1];
        // pSample is null when the MFT provides its own output samples (hardware).
        outputs[0] = new MFT_OUTPUT_DATA_BUFFER { dwStreamID = 0, pSample = _reusableOutputSample! };
        try { _encoder!.ProcessOutput(0, 1, outputs, out _); }
        catch (COMException ex) when (ex.HResult == MF_E_TRANSFORM_NEED_MORE_INPUT) { return false; }

        IMFSample outSample = outputs[0].pSample;
        if (outSample is null) return true;

        bool isKeyFrame;
        try { outSample.GetUINT32(PInvoke.MFSampleExtension_CleanPoint, out uint v); isKeyFrame = v != 0; }
        catch (COMException) { isKeyFrame = false; }

        outSample.GetSampleTime(out long sampleTimeHns);
        outSample.ConvertToContiguousBuffer(out IMFMediaBuffer outBuffer);

        outBuffer.Lock(out byte* ptr, out uint maxLen, out uint curLen);
        byte[] data = new byte[curLen];
        try { new ReadOnlySpan<byte>(ptr, (int)curLen).CopyTo(data); }
        finally { outBuffer.Unlock(); }

        // Hardware-provided samples must be released back to the MFT's pool.
        if (_reusableOutputSample is null)
        {
            Marshal.ReleaseComObject(outBuffer);
            Marshal.ReleaseComObject(outSample);
        }

        FrameEncoded?.Invoke(new EncodedFrame
        {
            Data        = data,
            IsKeyFrame  = isKeyFrame,
            TimestampUs = sampleTimeHns / 10,
        });
        return true;
    }

    // ── Color conversion ─────────────────────────────────────────────────────

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
