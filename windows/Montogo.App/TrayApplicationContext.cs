using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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

    // Hardware path: 60 fps @ 10 Mbps; software fallback: 30 fps @ 5 Mbps
    private const int HardwareFps     = 60;
    private const int HardwareBitrate = 10_000_000;
    private const int SoftwareFps     = 30;
    private const int SoftwareBitrate =  5_000_000;

    private readonly NotifyIcon       _trayIcon;
    private readonly CancellationTokenSource _cts = new();
    private readonly AppSettings      _settings;
    private readonly SecurityContext  _security;
    private ToolStripMenuItem _codeItem   = null!;
    private ToolStripMenuItem _statusItem = null!;

    private VirtualDisplayManager? _driver;

    // Pipeline components — null until the Mac connects
    private UdpClient?   _listener;    // bound to LAN:47921; kept alive for the pipeline so video shares the same source port
    private DxgiCapture? _capture;
    private H264Encoder? _encoder;
    private UdpSender?   _sender;
    private Channel<(CapturedFrame Frame, long TimestampUs)>? _channel;
    private Task?        _pipelineTask;

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
        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_codeItem);
        menu.Items.Add(_statusItem);
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
                if (!IsHandshakeRequest(result.Buffer)) continue;

                var req = MemoryMarshal.Read<HandshakeRequestPacket>(result.Buffer);

                // Reject requests whose token does not match: unknown device.
                // Validating before the pipeline-running check keeps the DoS
                // protection (unauthenticated floods never touch the pipeline)
                // while allowing a relaunched Mac to reconnect below.
                if (!_security.ValidateToken(req.ClientId, req.Token))
                {
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

        // Wire encoder output → UDP sender.
        // FrameEncoded fires synchronously from SubmitFrame on the pipeline task thread.
        uint frameId = 0;
        _encoder.FrameEncoded += encoded =>
            _sender!.SendFrameAsync(
                encoded.Data,
                frameId++,
                (ulong)encoded.TimestampUs,
                encoded.IsKeyFrame,
                _cts.Token
            ).GetAwaiter().GetResult();

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

    // ── Shutdown ─────────────────────────────────────────────────────────────

    private void ExitApplication()
    {
        _trayIcon.Visible = false;
        _cts.Cancel();
        _capture?.Stop();                              // joins capture thread
        _channel?.Writer.TryComplete();               // signals pipeline task to finish
        _pipelineTask?.Wait(TimeSpan.FromSeconds(2)); // wait for encode+send to drain
        _encoder?.Dispose();                          // sends NOTIFY_END_STREAMING + MFShutdown
        _sender?.Dispose();                            // does NOT close _listener (not owned)
        _listener?.Dispose();                          // close after sender is done
        try { _driver?.RemoveAllAsync().GetAwaiter().GetResult(); } catch { }
        Application.Exit();
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
