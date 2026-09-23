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
    private const int SoftwareFps     = 30;
    private const int SoftwareBitrate =  5_000_000;

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
            Icon             = SystemIcons.Application,
            Text             = "Montogo – Starting…",
            Visible          = true,
            ContextMenuStrip = BuildMenu(),
        };

        _ = Task.Run(() => InitAsync(_cts.Token));
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
            // Set up virtual display
            string cliPath = CliPathResolver.Resolve(_settings);
            _driver = new VirtualDisplayManager(cliPath);

            await _driver.RemoveAllAsync(ct);
            await _driver.AddMonitorAsync(VirtualWidth, VirtualHeight, HardwareFps, ct);

            // DXGI may take up to ~3 s to expose the new output
            DisplayInfo? display = await WaitForVirtualDisplayAsync(ct);
            if (display is null)
            {
                UpdateStatus("Error: virtual display did not appear — is the driver service running?");
                return;
            }

            // Probe for GPU encoder before entering the handshake loop so we can
            // advertise the correct fps in the HandshakeResponse.
            bool hardware   = H264Encoder.IsHardwareEncoderAvailable();
            int  targetFps  = hardware ? HardwareFps     : SoftwareFps;
            int  targetBits = hardware ? HardwareBitrate : SoftwareBitrate;

            await HandshakeLoopAsync(display, targetFps, targetBits, ct);
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

                var req = MemoryMarshal.Read<HandshakeRequestPacket>(result.Buffer);

                // Reject requests whose token does not match: unknown device.
                // Validating before the pipeline-running check keeps the DoS
                // protection (unauthenticated floods never touch the pipeline)
                // while allowing a relaunched Mac to reconnect below.
                if (!_security.ValidateToken(req.ClientId, req.Token))
                {
                    RecordRejectedAttempt(result.RemoteEndPoint.Address);
                    // Surface the rejection locally (not to the network) so a wrong
                    // connection code is visible instead of a silent "stuck connecting".
                    // Only while not yet streaming, so it can't disturb a live session.
                    if (_capture is null)
                        UpdateStatus($"Wrong code from {result.RemoteEndPoint.Address} — Mac must enter {_security.ConnectionCode}");
                    continue;
                }

                if (_capture is not null)
                {
                    // Authenticated re-handshake: the Mac app was relaunched (new
                    // ClientId) or lost the connection. Re-target the running
                    // pipeline and resend the response with the existing nonce
                    // prefix — no pipeline restart. Force a keyframe so the new
                    // session can start decoding immediately (the GOP is long).
                    _encoder!.RequestKeyFrame();
                    _sender!.SetTarget(new IPEndPoint(result.RemoteEndPoint.Address, ProtocolConstants.VideoPort));
                    await SendHandshakeResponseAsync(
                        _listener, result.RemoteEndPoint, display, req.ClientId,
                        targetFps, _sender.NoncePrefix, ct);
                    UpdateStatus($"Connected – {result.RemoteEndPoint.Address} " +
                        $"({(_encoder!.IsHardwareAccelerated ? "HW" : "SW")} {targetFps} fps, re-handshake)");
                    continue;
                }

                // Create sender now so its nonce prefix is known before the response goes out.
                // The Mac must receive the prefix before any encrypted chunks arrive.
                // Pass the listener so video is sent from the same source port (47921).
                // This lets the Mac's stateful firewall treat video as a reply to the
                // outbound handshake rather than unsolicited inbound traffic.
                _sender = new UdpSender(_security.Cipher, sendSocket: _listener);
                _sender.SetTarget(new IPEndPoint(result.RemoteEndPoint.Address, ProtocolConstants.VideoPort));

                await SendHandshakeResponseAsync(
                    _listener, result.RemoteEndPoint, display, req.ClientId,
                    targetFps, _sender.NoncePrefix, ct);

                StartPipeline(display, targetFps, targetBitrate);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            UpdateStatus($"Error: {ex.Message}");
        }
    }

    private static bool IsHandshakeRequest(byte[] buf)
    {
        if (buf.Length < Unsafe.SizeOf<HandshakeRequestPacket>()) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(buf) != ProtocolConstants.Magic) return false;
        if (buf[2] != ProtocolConstants.CurrentVersion) return false;
        return buf[3] == (byte)PacketType.HandshakeRequest;
    }

    private static bool IsFeedback(byte[] buf)
    {
        if (buf.Length < Unsafe.SizeOf<FeedbackPacket>()) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(buf) != ProtocolConstants.Magic) return false;
        if (buf[2] != ProtocolConstants.CurrentVersion) return false;
        return buf[3] == (byte)PacketType.Feedback;
    }

    // AIMD adaptive bitrate: cut fast when the Mac reports loss, probe back up slowly when
    // the link is clean. This is the congestion control that lets the stream ride out a
    // link that can't hold a fixed rate — the encoder is told to send only what fits.
    private void HandleFeedback(byte[] buf, IPEndPoint source)
    {
        if (_encoder is null || _sender?.Target is null || _adaptiveMax <= 0) return;

        var fb = MemoryMarshal.Read<FeedbackPacket>(buf);
        if (!_security.ValidateToken(fb.ClientId, fb.Token)) return;      // only the paired Mac may steer
        if (!source.Address.Equals(_sender.Target.Address)) return;

        // The Mac sets this when it sees a frameId gap (a lost/dropped frame broke the
        // P-frame reference chain). Sending an IDR now re-syncs it in ~1 RTT instead of
        // leaving it frozen until the next periodic keyframe (up to a second).
        if ((fb.Flags & FeedbackFlags.RequestKeyframe) != 0)
            _encoder.RequestKeyFrame();

        double loss = fb.LossPermille / 1000.0;

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

    private static async Task SendHandshakeResponseAsync(
        UdpClient listener, IPEndPoint macEndpoint,
        DisplayInfo display, Guid clientId, int targetFps, ulong noncePrefix, CancellationToken ct)
    {
        var resp = new HandshakeResponsePacket
        {
            Magic             = ProtocolConstants.Magic,
            Version           = ProtocolConstants.CurrentVersion,
            PacketType        = (byte)PacketType.HandshakeResponse,
            NegotiatedVersion = ProtocolConstants.CurrentVersion,
            DisplayWidth      = (ushort)display.Width,
            DisplayHeight     = (ushort)display.Height,
            TargetFps         = (byte)targetFps,
            ClientId          = clientId,
            NoncePrefix       = noncePrefix,
        };

        byte[] buf = new byte[Unsafe.SizeOf<HandshakeResponsePacket>()];
        MemoryMarshal.Write(buf, in resp);
        await listener.SendAsync(buf, macEndpoint, ct);
    }

    // ── Pipeline ─────────────────────────────────────────────────────────────

    private void StartPipeline(DisplayInfo display, int targetFps, int targetBitrate)
    {
        // _sender was created in HandshakeLoopAsync before the response was sent so that
        // its NoncePrefix could be included in HandshakeResponsePacket.
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
        _keyframeTask = Task.Run(async () =>
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(1000, _cts.Token);
                    _encoder?.RequestKeyFrame();
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

        // Tear down on a thread-pool thread, NOT here on the WinForms UI thread.
        // RemoveAllAsync ultimately awaits the driver CLI's Process I/O; blocking on it
        // with GetResult() while on the UI thread deadlocks — those awaited continuations
        // try to resume on this thread's captured SynchronizationContext, but the thread
        // is blocked in GetResult(). That deadlock is what left the process hung
        // (Application.Exit was never reached) and the virtual display un-removed.
        // Off the UI thread there is no captured context, so the awaits resume on the
        // pool and RemoveAllAsync runs to completion before we exit.
        Task.Run(() =>
        {
            _cts.Cancel();
            _capture?.Stop();                              // joins the capture thread
            _channel?.Writer.TryComplete();               // signals the pipeline task to finish
            _sendChannel?.Writer.TryComplete();           // signals the send loop to finish
            _pipelineTask?.Wait(TimeSpan.FromSeconds(2)); // let encode drain
            _sendTask?.Wait(TimeSpan.FromSeconds(2));     // let the send loop drain
            _keyframeTask?.Wait(TimeSpan.FromSeconds(1)); // stop the keyframe timer
            _encoder?.Dispose();                          // NOTIFY_END_STREAMING + MFShutdown
            _sender?.Dispose();                           // does NOT close _listener (not owned)
            _listener?.Dispose();                         // close after the sender is done
            try { _driver?.RemoveAllAsync().GetAwaiter().GetResult(); } // remove the virtual display
            catch { }
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
        // the handshake loop immediately validates against the new code and any new
        // pipeline uses the new cipher.
        string newCode = SecurityContext.EncodeConnectionCode(RandomNumberGenerator.GetBytes(5));
        _settings.SetConnectionCode(newCode);
        SecurityContext oldSecurity = _security;
        _security = new SecurityContext(newCode);
        _codeItem.Text = $"Code: {_security.ConnectionCode}";
        UpdateStatus(_boundIp is null
            ? "Connection code reset — waiting for Mac…"
            : $"Connection code reset — waiting for Mac at {_boundIp}…");

        // Tear the running session down off the UI thread (blocking on the pipeline
        // drain here would freeze the tray). Dropping the stream makes the Mac's
        // watchdog fire; its next handshake carries the old code and is rejected, so it
        // falls back to its setup screen and must re-enter the new code.
        Task.Run(() =>
        {
            TearDownPipeline();
            oldSecurity.Dispose();
        });
    }

    /// <summary>Stops and clears the capture→encode→send pipeline without touching the
    /// UDP listener or the handshake loop, so a fresh session can start on the next
    /// authenticated handshake. Does not cancel <c>_cts</c>.</summary>
    private void TearDownPipeline()
    {
        _capture?.Stop();                              // joins the capture thread
        _channel?.Writer.TryComplete();               // ends the pipeline task's read loop
        _pipelineTask?.Wait(TimeSpan.FromSeconds(2)); // let encode + send drain
        _encoder?.Dispose();
        _sender?.Dispose();                            // does not close _listener (not owned)
        _capture      = null;
        _encoder      = null;
        _sender       = null;
        _channel      = null;
        _pipelineTask = null;
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
            _security.Dispose();
            _driver?.Dispose();
            _listener?.Dispose();
            _trayIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
