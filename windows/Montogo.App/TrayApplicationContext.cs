using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading.Channels;
using Montogo.Capture;
using Montogo.Driver;
using Montogo.Encoding;
using Montogo.Protocol;
using Montogo.Transport;

namespace Montogo.App;

[SupportedOSPlatform("windows8.0")]
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const int VirtualWidth = 1920;
    private const int VirtualHeight = 1080;

    // Hardware path: 60 fps @ 11 Mbps; software fallback: 30 fps @ 5 Mbps.
    // Proven zero-loss point: the once-per-second keyframe (~340 KB, ~250 packets) fits
    // under the link's burst buffer, so it sends cleanly with no packet pacing — pacing
    // was tried at higher bitrates but its spin-wait stalled the encoder pump and
    // corrupted the stream, so it was removed. Softer than 15-18 Mbps but stable.
    private const int HardwareFps     = 60;
    private const int HardwareBitrate = 11_000_000;

    private readonly NotifyIcon       _trayIcon;
    private readonly CancellationTokenSource _cts = new();
    private readonly AppSettings      _settings;
    private SecurityContext           _security;   // replaced by the tray "Reset connection code" action
    private ToolStripMenuItem _codeItem   = null!;
    private ToolStripMenuItem _ipItem     = null!;
    private ToolStripMenuItem _statusItem = null!;
    private IPAddress? _boundIp;   // LAN address the listener is bound to (what the Mac must enter)

    // Rejected-handshake log: last 10 wrong-token attempts, shown via the tray.
    // Written from the handshake loop (thread pool), read from the tray click (UI thread).
    private readonly object      _securityLogLock = new();
    private readonly List<string> _securityLog    = new();
    private int _rejectedAttempts;

    private VirtualDisplayManager? _driver;

    // Pipeline components — null until the Mac connects
    private UdpClient?   _listener;    // bound to LAN:47921; kept alive for the pipeline so video shares the same source port
    private DxgiCapture? _capture;
    private H264Encoder? _encoder;
    private UdpSender?   _sender;
    private Channel<(CapturedFrame Frame, long TimestampUs)>? _channel;
    private Task?        _pipelineTask;
    private Channel<(EncodedFrame Frame, uint FrameId)>? _sendChannel;
    private Task?        _sendTask;
    private Task?        _keyframeTask;   // forces a periodic IDR so P-frame error can't accumulate
    private Task?        _livenessTask;   // stops the stream if the Mac stops sending valid feedback
    private CancellationTokenSource? _sessionCts;   // per-session; cancelled on teardown so a re-handshake can restart cleanly
    private readonly object _pipelineLock = new();  // serializes StartPipeline/TearDownPipeline across the handshake loop, watchdog, and reset

    // Liveness: Windows keeps streaming only while it receives authenticated feedback from the
    // paired Mac. Without this, a spoofed handshake (source IP forged to a victim on the LAN)
    // could aim a continuous 60 fps stream at that victim — a reflection/amplification vector —
    // and a force-quit Mac would leave Windows streaming into the void. If no valid feedback
    // arrives for this long, the pipeline is torn down and Windows waits for a fresh handshake.
    private const int LivenessTimeoutSeconds = 5;
    // While the session is receiving authenticated feedback this recently, the real Mac is
    // present, so incoming handshakes can only be replays — ignore them rather than tear the
    // live session down (anti-replay). A genuine reconnect arrives after feedback goes stale.
    private const int HandshakeReplayGraceSeconds = 3;
    private DateTime _sessionStart = DateTime.MinValue;      // watchdog grace; set at StartPipeline
    private DateTime _lastValidFeedback = DateTime.MinValue; // set ONLY on real feedback (not at start)

    // Feedback authentication (v8): a per-session HMAC key derived from the ECDH shared secret,
    // plus the highest counter seen — so feedback can't be forged or replayed by a LAN sniffer.
    private byte[] _feedbackKey = Array.Empty<byte>();
    private uint   _lastFeedbackCounter;

    // Adaptive bitrate — AIMD congestion control driven by the Mac's loss feedback AND the
    // local send-queue depth (see OnSendQueueDepth). The Mac's feedback is loss-based, but
    // when the Wi-Fi link saturates the frames pile up in _sendChannel and get dropped HERE,
    // before the network — so the Mac sees 0% loss and the loss path never reacts. The
    // send-queue signal covers that blind spot.
    private int      _adaptiveMax;                    // hard ceiling = configured target bitrate
    private int      _adaptiveBitrate;                // current adaptive target
    private int      _softCeiling;                    // learned link-capacity estimate ≤ _adaptiveMax
    private DateTime _lastAdapt = DateTime.MinValue;
    private DateTime _lastCongestion = DateTime.MinValue; // last time the link showed it was overdriven
    private const int AdaptiveMinBitrate = 2_000_000; // don't starve below ~2 Mbps

    // _sendChannel holds 16 frames. When it backs up past this, the send loop can't keep the
    // link fed at the current bitrate: DropOldest is (about to be) discarding a frame, which
    // punches a frameId gap that breaks the Mac's P-frame reference chain (a -12909 cascade).
    private const int SendQueueHighWater = 8;
    private DateTime _lastSendPressureKeyframe = DateTime.MinValue;

    public TrayApplicationContext()
    {
        // Load settings and create security context before building the tray menu
        // so the connection code is available immediately on startup.
        _settings = AppSettings.Load();
        _security = new SecurityContext(_settings.GetOrCreateConnectionCode());

        _trayIcon = new NotifyIcon
        {
            Icon             = LoadTrayIcon(),
            Text             = "Montogo – Starting…",
            Visible          = true,
            ContextMenuStrip = BuildMenu(),
        };

        _ = Task.Run(() => InitAsync(_cts.Token));
    }

    // Load the app icon's small (tray-sized) frame from montogo.ico shipped next to
    // the exe, so the tray shows the Montogo logo at the current DPI. Falls back to
    // the generic application icon if the file is missing or unreadable.
    private static Icon LoadTrayIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "montogo.ico");
            if (File.Exists(path))
                return new Icon(path, SystemInformation.SmallIconSize);
        }
        catch
        {
            // fall through to the system default
        }
        return SystemIcons.Application;
    }

    private ContextMenuStrip BuildMenu()
    {
        _codeItem   = new ToolStripMenuItem($"Code: {_security.ConnectionCode}") { Enabled = false };
        _ipItem     = new ToolStripMenuItem("PC address: (resolving…)") { Enabled = false };
        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_codeItem);
        menu.Items.Add(_ipItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Security log", null, (_, _) => ShowSecurityLog());
        menu.Items.Add("Reset connection code", null, (_, _) => ResetConnectionCode());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        return menu;
    }

    // ── Startup ──────────────────────────────────────────────────────────────

    private async Task InitAsync(CancellationToken ct)
    {
        try
        {
            // Set up the virtual display via the elevated helper (one UAC prompt); the app
            // itself stays non-elevated. The helper adds the monitor now and removes it when
            // this process exits.
            _driver = new VirtualDisplayManager();
            await _driver.StartAsync(VirtualWidth, VirtualHeight, HardwareFps, ct);

            // DXGI may take up to ~3 s to expose the new output
            DisplayInfo? display = await WaitForVirtualDisplayAsync(ct);
            if (display is null)
            {
                UpdateStatus("Error: virtual display did not appear — is the virtual-display-rs driver installed?");
                return;
            }

            // HEVC requires a hardware encoder; fail fast with a clear message rather than
            // letting the Mac connect and then hit the encoder's NotSupportedException.
            if (!H264Encoder.IsHardwareEncoderAvailable())
            {
                UpdateStatus("No hardware HEVC encoder found — Montogo needs an NVENC / Quick Sync / AMF GPU.");
                return;
            }

            await HandshakeLoopAsync(display, HardwareFps, HardwareBitrate, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            UpdateStatus($"Error: {ex.Message}");
        }
    }

    private static async Task<DisplayInfo?> WaitForVirtualDisplayAsync(CancellationToken ct)
    {
        // Poll up to 3 seconds in 150 ms increments
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(150, ct);
            var displays = DxgiCapture.EnumerateDisplays();
            var nonPrimary = displays.FirstOrDefault(d => !d.IsPrimary);
            if (nonPrimary is not null) return nonPrimary;
        }
        return null;
    }

    // ── Handshake ────────────────────────────────────────────────────────────

    private async Task HandshakeLoopAsync(
        DisplayInfo display, int targetFps, int targetBitrate, CancellationToken ct)
    {
        var (lanIp, warning) = LanIpResolver.GetLanAddress();
        _boundIp = lanIp;
        SetIpText(lanIp.ToString());
        UpdateStatus(warning is null
            ? $"Waiting for Mac… (LAN: {lanIp})"
            : $"Waiting for Mac… (warning: {warning})");

        // Do not use 'using' here — listener is kept alive as a field so UdpSender can
        // send video from the same port (47921).  Disposed in ExitApplication / Dispose.
        _listener = new UdpClient(new IPEndPoint(lanIp, ProtocolConstants.VideoPort));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result = await _listener.ReceiveAsync(ct);

                // Link-quality feedback from the connected Mac steers the encoder bitrate.
                if (IsFeedback(result.Buffer)) { HandleFeedback(result.Buffer, result.RemoteEndPoint); continue; }

                if (!IsHandshakeRequest(result.Buffer)) continue;

                // Anti-replay: while the real Mac is actively confirming this session with
                // authenticated feedback, a new handshake can only be a replay (a genuine
                // reconnect arrives after feedback stops). Ignore it so a replayed handshake
                // can't tear down and disrupt the live stream. Feedback is unforgeable (v8),
                // so "healthy" reliably means the real Mac is here.
                if (_capture is not null && _lastValidFeedback != DateTime.MinValue
                    && (DateTime.UtcNow - _lastValidFeedback).TotalSeconds < HandshakeReplayGraceSeconds)
                    continue;

                // Authenticate the request and run the ephemeral ECDH (in a sync helper — an
                // async method can't hold Span locals). Verifies HMAC(authKey, request) — which
                // proves the Mac knows the code and binds its ephemeral key — before any pipeline
                // work, so an unauthenticated flood never touches the encoder.
                if (!TryAuthenticateHandshake(result.Buffer, out Guid clientId, out byte[] winPub,
                                              out AesGcm? sessionCipher, out byte[] feedbackKey, out byte[] macPubKey))
                {
                    RecordRejectedAttempt(result.RemoteEndPoint.Address);
                    // Surface the rejection locally (not to the network) so a wrong code is
                    // visible instead of a silent "stuck connecting".
                    if (_capture is null)
                        UpdateStatus($"Wrong code from {result.RemoteEndPoint.Address} — Mac must enter {_security.ConnectionCode}");
                    continue;
                }

                // A handshake always establishes a fresh session key, so tear down any running
                // pipeline (e.g. a relaunched or reconnecting Mac) and start clean on the new key.
                if (_capture is not null) TearDownPipeline();

                // This session's feedback authentication key + counter (reset per session).
                _feedbackKey = feedbackKey;
                _lastFeedbackCounter = 0;

                // Create the sender now so its nonce prefix is known before the response goes
                // out (the Mac needs it before any encrypted chunk). Reuse the listener socket
                // so video shares source port 47921 (the Mac's firewall sees it as a reply).
                _sender = new UdpSender(sessionCipher!, sendSocket: _listener);
                _sender.SetTarget(new IPEndPoint(result.RemoteEndPoint.Address, ProtocolConstants.VideoPort));

                await SendHandshakeResponseAsync(
                    _listener, result.RemoteEndPoint, display, clientId,
                    targetFps, _sender.NoncePrefix, winPub, macPubKey, ct);

                StartPipeline(display, targetFps, targetBitrate);
                UpdateStatus($"Connected – {result.RemoteEndPoint.Address} " +
                    $"({(_encoder!.IsHardwareAccelerated ? "HW" : "SW")} {targetFps} fps)");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            UpdateStatus($"Error: {ex.Message}");
        }
    }

    // Verifies a handshake request's HMAC and runs the ephemeral ECDH, returning the session
    // cipher. Synchronous so it can use Span locals (an async method cannot). Returns false on
    // a bad auth tag (wrong code) or a malformed peer key.
    private bool TryAuthenticateHandshake(byte[] buffer, out Guid clientId, out byte[] winPub,
                                          out AesGcm? sessionCipher, out byte[] feedbackKey, out byte[] macPubKey)
    {
        clientId = default;
        winPub = Array.Empty<byte>();
        sessionCipher = null;
        feedbackKey = Array.Empty<byte>();
        macPubKey = Array.Empty<byte>();

        ReadOnlySpan<byte> buf = buffer.AsSpan();
        ReadOnlySpan<byte> tag = buf.Slice(HandshakeLayout.ReqAuthTag, HandshakeLayout.AuthTagLen);
        if (!_security.VerifyAuthTag(buf[..HandshakeLayout.ReqSigned], tag)) return false;

        clientId = new Guid(buf.Slice(HandshakeLayout.ReqClientId, 16));
        macPubKey = buf.Slice(HandshakeLayout.ReqPubKey, HandshakeLayout.PubKeyLen).ToArray();

        // The ephemeral private key lives only for this call and is discarded (forward secrecy).
        using var ecdh = SecurityContext.CreateEphemeral(out winPub);
        return _security.DeriveSession(ecdh, macPubKey, out sessionCipher, out feedbackKey);
    }

    private static bool IsHandshakeRequest(byte[] buf)
    {
        if (buf.Length < HandshakeLayout.ReqSize) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(buf) != ProtocolConstants.Magic) return false;
        if (buf[2] != ProtocolConstants.CurrentVersion) return false;
        return buf[3] == (byte)PacketType.HandshakeRequest;
    }

    private static bool IsFeedback(byte[] buf)
    {
        if (buf.Length < FeedbackLayout.Size) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(buf) != ProtocolConstants.Magic) return false;
        if (buf[2] != ProtocolConstants.CurrentVersion) return false;
        return buf[3] == (byte)PacketType.Feedback;
    }

    // AIMD adaptive bitrate: cut fast when the Mac reports loss, probe back up slowly when
    // the link is clean. This is the congestion control that lets the stream ride out a
    // link that can't hold a fixed rate — the encoder is told to send only what fits.
    private void HandleFeedback(byte[] buf, IPEndPoint source)
    {
        if (_encoder is null || _sender?.Target is null || _adaptiveMax <= 0 || _feedbackKey.Length == 0) return;

        // Authenticate with the per-session feedback key (unforgeable) + reject replays via the
        // monotonic counter. The source-IP check stays as cheap defense-in-depth.
        ReadOnlySpan<byte> fb = buf.AsSpan();
        if (!SecurityContext.VerifyFeedbackTag(_feedbackKey,
                fb[..FeedbackLayout.Signed], fb.Slice(FeedbackLayout.AuthTag, 32))) return;
        if (!source.Address.Equals(_sender.Target.Address)) return;

        uint counter = BinaryPrimitives.ReadUInt32LittleEndian(fb.Slice(FeedbackLayout.Counter, 4));
        if (counter <= _lastFeedbackCounter) return;   // stale or replayed
        _lastFeedbackCounter = counter;

        // Authenticated feedback from the paired Mac = proof the stream is reaching a real
        // client. The liveness watchdog uses this to stop a stream nobody is consuming.
        _lastValidFeedback = DateTime.UtcNow;

        byte flags = fb[FeedbackLayout.Flags];
        // The Mac sets this when it sees a frameId gap (a lost/dropped frame broke the
        // P-frame reference chain). Sending an IDR now re-syncs it in ~1 RTT instead of
        // leaving it frozen until the next periodic keyframe (up to a second).
        if ((flags & FeedbackFlags.RequestKeyframe) != 0)
            _encoder.RequestKeyFrame();

        double loss = BinaryPrimitives.ReadUInt16LittleEndian(fb.Slice(FeedbackLayout.LossPermille, 2)) / 1000.0;

        // Adaptive FEC: raise redundancy as loss climbs (responds every feedback, cheap).
        // More parity costs bandwidth, but the bitrate back-off below frees room for it.
        int fecPct = loss > 0.20 ? 70 : loss > 0.08 ? 50 : loss > 0.02 ? 35 : 20;
        _sender.SetFecPercent(fecPct);

        // React on bitrate at the feedback cadence, not faster.
        DateTime now = DateTime.UtcNow;
        if ((now - _lastAdapt).TotalMilliseconds < 350) return;
        _lastAdapt = now;
        int cur  = _adaptiveBitrate > 0 ? _adaptiveBitrate : _adaptiveMax;
        int next = cur;
        if (loss > 0.10)                                  // heavy network loss: back off hard
        {
            next = (int)(cur * 0.6);
            _softCeiling = Math.Max(AdaptiveMinBitrate, next);
        }
        else if (loss > 0.03)                             // some loss: ease down
        {
            next = (int)(cur * 0.85);
            _softCeiling = Math.Max(AdaptiveMinBitrate, next);
        }
        else if (loss < 0.015)                            // clean link: maybe probe up
        {
            // Don't probe straight back to the hard max — that just re-saturates the link and
            // causes the decode-error sawtooth. Wait until the link's been quiet for a bit
            // after the last congestion, let the learned ceiling recover slowly (to re-test if
            // the link improved), and climb gently toward it, never past it.
            if ((now - _lastCongestion).TotalMilliseconds > 1500)
            {
                _softCeiling = Math.Min(_adaptiveMax, _softCeiling + 250_000);
                next = Math.Min(cur + 500_000, _softCeiling);
            }
        }

        next = Math.Clamp(next, AdaptiveMinBitrate, _adaptiveMax);
        if (next == cur) return;

        _adaptiveBitrate = next;
        _encoder.SetBitrate(next);
        UpdateStatus($"Adapting: {loss * 100:F0}% loss → {next / 1_000_000.0:F1} Mbps (cap {_softCeiling / 1_000_000.0:F1})");
    }

    // Send-side congestion control. Called on the encoder pump thread with the send-queue
    // depth just before each frame is enqueued. When the queue backs up, the Wi-Fi link
    // cannot carry the current bitrate; DropOldest then discards a frame, tearing a frameId
    // gap that makes every following P-frame fail to decode on the Mac (-12909) until a
    // keyframe. The Mac reports 0% loss because the drop is local, so this is the only place
    // that congestion is visible. Two responses: force a keyframe to heal the gap immediately
    // (rather than waiting up to a second for the periodic one), and back the bitrate off so
    // the send loop can catch up and stop dropping.
    private void OnSendQueueDepth(int depth)
    {
        if (depth < SendQueueHighWater || _encoder is null || _adaptiveMax <= 0) return;

        DateTime now = DateTime.UtcNow;
        // Mark congestion so the loss path holds off probing back up for a while.
        _lastCongestion = now;
        // Heal the reference chain fast, but don't spam keyframes (each one is large and
        // adds to the very congestion we're fighting).
        if ((now - _lastSendPressureKeyframe).TotalMilliseconds >= 250)
        {
            _lastSendPressureKeyframe = now;
            _encoder.RequestKeyFrame();
        }

        // Back off on the same cadence as the loss path so the two can't fight.
        if ((now - _lastAdapt).TotalMilliseconds < 350) return;
        _lastAdapt = now;
        int cur  = _adaptiveBitrate > 0 ? _adaptiveBitrate : _adaptiveMax;
        // Remember this rate overdrove the link, so the probe-up converges just below it
        // instead of climbing back to the hard max and re-saturating (the error sawtooth).
        _softCeiling = Math.Max(AdaptiveMinBitrate, (int)(cur * 0.9));
        int next = Math.Clamp((int)(cur * 0.7), AdaptiveMinBitrate, _adaptiveMax);
        if (next == cur) return;
        _adaptiveBitrate = next;
        _encoder.SetBitrate(next);
        UpdateStatus($"Adapting: send queue {depth} → {next / 1_000_000.0:F1} Mbps (cap {_softCeiling / 1_000_000.0:F1})");
    }

    // Builds the v9 handshake response: display params + Windows's ephemeral public key + the
    // GCM nonce prefix, authenticated by a trailing HMAC(authKey, response-bytes ‖ macPubKey).
    // Binding the request's macPubKey into the tag MACs the full exchange transcript (both
    // ephemeral keys), so the response can't be lifted onto a different request.
    private async Task SendHandshakeResponseAsync(
        UdpClient listener, IPEndPoint macEndpoint,
        DisplayInfo display, Guid clientId, int targetFps, ulong noncePrefix, byte[] winPub, byte[] macPub, CancellationToken ct)
    {
        byte[] buf = new byte[HandshakeLayout.RespSize];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, ProtocolConstants.Magic);
        buf[2] = ProtocolConstants.CurrentVersion;
        buf[3] = (byte)PacketType.HandshakeResponse;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(HandshakeLayout.RespWidth),  (ushort)display.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(HandshakeLayout.RespHeight), (ushort)display.Height);
        buf[HandshakeLayout.RespFps]      = (byte)targetFps;
        buf[HandshakeLayout.RespReserved] = 0;
        clientId.ToByteArray().CopyTo(buf.AsSpan(HandshakeLayout.RespClientId, 16));
        winPub.CopyTo(buf.AsSpan(HandshakeLayout.RespPubKey, HandshakeLayout.PubKeyLen));
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(HandshakeLayout.RespNoncePrefix), noncePrefix);

        // HMAC input = the signed response bytes followed by the request's macPubKey.
        byte[] mac = new byte[HandshakeLayout.RespSigned + HandshakeLayout.PubKeyLen];
        buf.AsSpan(0, HandshakeLayout.RespSigned).CopyTo(mac);
        macPub.CopyTo(mac.AsSpan(HandshakeLayout.RespSigned));
        byte[] tag = _security.ComputeAuthTag(mac);
        tag.CopyTo(buf.AsSpan(HandshakeLayout.RespAuthTag, HandshakeLayout.AuthTagLen));

        await listener.SendAsync(buf, macEndpoint, ct);
    }

    // ── Pipeline ─────────────────────────────────────────────────────────────

    private void StartPipeline(DisplayInfo display, int targetFps, int targetBitrate)
    {
        // Per-session cancellation so a re-handshake (which re-keys) can stop this session's
        // background tasks and start a fresh one, without tearing down the whole app.
        _sessionCts = new CancellationTokenSource();

        // _sender was created in HandshakeLoopAsync before the response was sent so that
        // its NoncePrefix could be included in the handshake response.
        _encoder = new H264Encoder(
            new EncoderOptions(display.Width, display.Height, targetFps, targetBitrate));

        // Adaptive bitrate starts at the configured target and only moves down from there
        // under loss (probing back up toward this ceiling when the link is clean).
        _adaptiveMax     = targetBitrate;
        _adaptiveBitrate = targetBitrate;
        _softCeiling     = targetBitrate;   // starts optimistic; congestion pulls it toward real capacity

        // Wire encoder output → a send queue drained by a dedicated send loop.
        // FrameEncoded fires synchronously on the encoder's pump thread (inside
        // SubmitFrame). Doing the UDP send there blocked that thread, so any send-side
        // work — pacing, and soon FEC/retransmission — stalled the encoder itself and
        // corrupted the stream. Enqueuing is non-blocking, so the pump is never held up;
        // the send loop owns all network timing. Bounded + DropOldest caps memory if the
        // network stalls badly (a dropped encoded frame resets at the next keyframe); in
        // normal operation the send loop keeps up and the queue stays near-empty.
        _sendChannel = Channel.CreateBounded<(EncodedFrame, uint)>(
            new BoundedChannelOptions(16)
            {
                FullMode     = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
        uint frameId = 0;
        _encoder.FrameEncoded += encoded =>
        {
            // Watch the queue depth BEFORE enqueuing: a backlog means the link can't carry
            // the current rate and frames are being dropped locally (a reference-chain gap
            // on the Mac). React to that congestion the loss-based feedback can't see.
            OnSendQueueDepth(_sendChannel!.Reader.Count);
            _sendChannel!.Writer.TryWrite((encoded, frameId++));
        };
        _sendTask = Task.Run(SendLoopAsync);

        _encoder.Initialize();

        string encoderLabel = _encoder.IsHardwareAccelerated ? "HW" : "SW";
        UpdateStatus($"Connected – {_sender!.Target!.Address} ({encoderLabel} {targetFps} fps)");

        // Bounded channel drops oldest frame when full so the pipeline never falls
        // more than 2 frames behind the capture thread.
        _channel = Channel.CreateBounded<(CapturedFrame, long)>(
            new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });

        // Wire capture output → channel.
        _capture = new DxgiCapture(
            new CaptureOptions { OutputIndex = display.Index, TargetFps = targetFps });
        _capture.CaptureFailed += ex => UpdateStatus($"Capture error: {ex.Message}");
        _capture.FrameCaptured += frame =>
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L; // ms → µs
            _channel.Writer.TryWrite((frame, ts));
        };

        // Pipeline task: drain channel → encode (→ FrameEncoded → send).
        _pipelineTask = Task.Run(PipelineLoopAsync);

        // Force an IDR every second. Telemetry showed NVENC emitting no periodic keyframes
        // on this config (IDR=0 per window), so P-frame prediction/quantization error had
        // nothing to reset it and accumulated over time. A once-a-second forced keyframe
        // bounds that — the standard low-latency approach when there's no intra-refresh.
        CancellationToken sessionToken = _sessionCts.Token;
        _keyframeTask = Task.Run(async () =>
        {
            try
            {
                while (!sessionToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, sessionToken);
                    _encoder?.RequestKeyFrame();
                }
            }
            catch (OperationCanceledException) { }
        });

        // Liveness + nonce-wrap watchdog. Stops the pipeline if (a) the paired Mac stops
        // sending authenticated feedback (a departed/force-quit Mac, or a spoofed handshake that
        // redirected the stream to a victim who can't produce valid feedback), or (b) the GCM
        // sequence counter is about to wrap (reusing a nonce would break GCM). Either way the
        // Mac's own watchdog then re-handshakes and a fresh session/key/nonce-prefix takes over.
        _sessionStart = DateTime.UtcNow;
        _lastValidFeedback = DateTime.MinValue;   // only real feedback counts as "alive"/"healthy"
        _livenessTask = Task.Run(async () =>
        {
            try
            {
                while (!sessionToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, sessionToken);
                    // Alive = fed by real feedback, or still within the start grace window.
                    DateTime alive = _lastValidFeedback > _sessionStart ? _lastValidFeedback : _sessionStart;
                    if ((DateTime.UtcNow - alive).TotalSeconds > LivenessTimeoutSeconds)
                    {
                        UpdateStatus("No response from Mac — stream stopped; waiting for reconnect…");
                        TearDownPipeline();   // safe: guarded by _pipelineLock; does not join this task
                        return;
                    }
                    if (_sender?.NonceSpaceNearlyExhausted == true)
                    {
                        UpdateStatus("Rekeying (nonce limit) — reconnecting…");
                        TearDownPipeline();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

        _capture.Start();
    }

    private async Task PipelineLoopAsync()
    {
        try
        {
            await foreach (var (frame, ts) in _channel!.Reader.ReadAllAsync(_cts.Token))
                _encoder!.SubmitFrame(frame.BgraData.Span, ts);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            UpdateStatus($"Pipeline error: {ex.Message}");
        }
    }

    // Drains encoded frames and ships them over UDP — off the encoder's pump thread, so
    // the send path can take its time (pacing / FEC / retransmission) without stalling
    // the encoder. Owns all network-side timing for the video stream.
    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var (frame, id) in _sendChannel!.Reader.ReadAllAsync(_cts.Token))
                await _sender!.SendFrameAsync(
                    frame.Data, id, (ulong)frame.TimestampUs, frame.IsKeyFrame, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            UpdateStatus($"Send error: {ex.Message}");
        }
    }

    // ── Shutdown ─────────────────────────────────────────────────────────────

    private void ExitApplication()
    {
        _trayIcon.Visible = false;

        // Tear down on a thread-pool thread, NOT the WinForms UI thread, so the bounded
        // .Wait()s below can't deadlock against task continuations captured on the UI
        // context. The virtual display itself is removed by the elevated helper when this
        // process exits, so there's nothing driver-related to do here.
        Task.Run(() =>
        {
            _cts.Cancel();
            _sessionCts?.Cancel();                         // stops the keyframe task
            _capture?.Stop();                              // joins the capture thread
            _channel?.Writer.TryComplete();               // signals the pipeline task to finish
            _sendChannel?.Writer.TryComplete();           // signals the send loop to finish
            _pipelineTask?.Wait(TimeSpan.FromSeconds(2)); // let encode drain
            _sendTask?.Wait(TimeSpan.FromSeconds(2));     // let the send loop drain
            _keyframeTask?.Wait(TimeSpan.FromSeconds(1)); // stop the keyframe timer
            _encoder?.Dispose();                          // NOTIFY_END_STREAMING + MFShutdown
            _sender?.Dispose();                           // does NOT close _listener (not owned)
            _listener?.Dispose();                         // close after the sender is done
        }).Wait(TimeSpan.FromSeconds(5));                 // bounded so a stuck teardown can't hang exit

        Application.Exit();
    }

    // ── Security log & connection-code reset ─────────────────────────────────

    private void RecordRejectedAttempt(IPAddress source)
    {
        lock (_securityLogLock)
        {
            _rejectedAttempts++;
            _securityLog.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {source}  (attempt #{_rejectedAttempts})");
            if (_securityLog.Count > 10) _securityLog.RemoveAt(0);
        }
    }

    private void ShowSecurityLog()
    {
        string body;
        lock (_securityLogLock)
        {
            body = _securityLog.Count == 0
                ? "No rejected connection attempts recorded."
                : string.Join(Environment.NewLine, _securityLog);
        }
        MessageBox.Show(body, "Montogo — Security log (last 10 rejected attempts)",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ResetConnectionCode()
    {
        if (MessageBox.Show(
                "This will disconnect the Mac app and require re-entering the code. Continue?",
                "Reset connection code", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
            != DialogResult.Yes) return;

        // New secret → DPAPI-encrypt + persist, then swap the live security context so
        // the handshake loop immediately authenticates against the new code and any new
        // pipeline uses a fresh session key.
        string newCode = SecurityContext.EncodeConnectionCode(RandomNumberGenerator.GetBytes(8));
        _settings.SetConnectionCode(newCode);
        _security = new SecurityContext(newCode);
        _codeItem.Text = $"Code: {_security.ConnectionCode}";
        UpdateStatus(_boundIp is null
            ? "Connection code reset — waiting for Mac…"
            : $"Connection code reset — waiting for Mac at {_boundIp}…");

        // Tear the running session down off the UI thread (blocking on the pipeline
        // drain here would freeze the tray). Dropping the stream makes the Mac's
        // watchdog fire; its next handshake carries the old code and is rejected, so it
        // falls back to its setup screen and must re-enter the new code.
        Task.Run(TearDownPipeline);
    }

    /// <summary>Stops and clears the capture→encode→send pipeline without touching the
    /// UDP listener or the handshake loop, so a fresh session can start on the next
    /// authenticated handshake. Does not cancel <c>_cts</c>.</summary>
    private void TearDownPipeline()
    {
        // Guarded + idempotent: the handshake loop (re-handshake), the liveness watchdog, and
        // "Reset connection code" can all call this, possibly at once.
        lock (_pipelineLock)
        {
            if (_sessionCts is null) return;           // already torn down
            _sessionCts.Cancel();                      // stops the keyframe + liveness tasks
            _capture?.Stop();                          // joins the capture thread
            _channel?.Writer.TryComplete();            // ends the pipeline task's read loop
            _sendChannel?.Writer.TryComplete();        // ends the send loop
            _pipelineTask?.Wait(TimeSpan.FromSeconds(2)); // let encode drain
            _sendTask?.Wait(TimeSpan.FromSeconds(2));     // let the send loop drain
            _keyframeTask?.Wait(TimeSpan.FromSeconds(1));
            // Do NOT Wait on _livenessTask — it may be the caller (the watchdog), which would
            // self-deadlock; cancelling _sessionCts already stops its loop.
            _encoder?.Dispose();
            _sender?.Dispose();                        // disposes the session cipher; not the listener (not owned)
            _sessionCts.Dispose();
            _capture      = null;
            _encoder      = null;
            _sender       = null;
            _channel      = null;
            _sendChannel  = null;
            _pipelineTask = null;
            _sendTask     = null;
            _keyframeTask = null;
            _livenessTask = null;
            _sessionCts   = null;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void UpdateStatus(string text)
    {
        if (_trayIcon.ContextMenuStrip is { } menu && menu.InvokeRequired)
            menu.BeginInvoke(() => UpdateStatus(text));
        else
        {
            _statusItem.Text = text;
            _trayIcon.Text   = $"Montogo – {text[..Math.Min(text.Length, 60)]}"; // NotifyIcon.Text max 63 chars
        }
    }

    // Always-visible "PC address" line so the IP to enter on the Mac is in the tray menu
    // (independent of the status line, which other events overwrite).
    private void SetIpText(string ip)
    {
        if (_trayIcon.ContextMenuStrip is { } menu && menu.InvokeRequired)
            menu.BeginInvoke(() => SetIpText(ip));
        else
            _ipItem.Text = $"PC address: {ip}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
            _driver?.Dispose();
            _listener?.Dispose();
            _trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
