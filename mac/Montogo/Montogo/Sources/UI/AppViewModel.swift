import SwiftUI
import MetalKit

enum AppScreen {
    case setup
    case connecting
    case streaming(fps: UInt8, width: UInt16, height: UInt16)
    case disconnected     // was connected, now lost; still retrying
}

@MainActor
final class AppViewModel: ObservableObject {
    @Published var screen: AppScreen = .setup
    @Published var setupError: String?

    // Pre-fill for SetupView: the last-used values, so falling back to setup only
    // asks the user to fix what changed.
    @Published var prefillCode = ""
    @Published var prefillIP = ""

    // Live link health for the on-screen monitor (nil = no data yet / link idle).
    @Published var linkStats: LinkStats?

    private var receiver: UDPReceiver?
    var decoder: H264Decoder?
    var videoRenderer: VideoRenderer?
    weak var mtkView: MTKView?   // set by StreamingView once the Metal view is created

    // The pairing currently being attempted (persisted once the handshake succeeds).
    private var currentCode = ""
    private var currentIP = ""
    private var autoConnectTimeout: Task<Void, Never>?
    private var didBootstrap = false

    // Dedicated serial queue for VTDecompressionSessionDecodeFrame.  Decoding on
    // the MainActor made every frame wait for UI work (frame timing showed the
    // decode stage dominated by main-thread scheduling, not VideoToolbox); a
    // serial queue keeps H264Decoder's single-caller contract and frame order.
    private let decodeQueue = DispatchQueue(label: "com.montogo.decode", qos: .userInteractive)

    // Auto-reconnect: attempt fallback after ~8 s (≈16 handshake retries).
    private let autoConnectTimeoutSeconds: UInt64 = 8

    // Called once at launch. If a pairing is saved, skip setup and connect immediately;
    // otherwise show the setup screen.
    func bootstrap() {
        guard !didBootstrap else { return }
        didBootstrap = true

        if let saved = ConnectionStore.saved() {
            prefillCode = saved.code
            prefillIP = saved.ip
            connect(code: saved.code, windowsIP: saved.ip, isAuto: true)
        } else {
            screen = .setup
        }
    }

    // Called by SetupView (manual) and bootstrap (auto).
    func connect(code: String, windowsIP: String, isAuto: Bool = false) {
        setupError = nil
        // Feed the on-screen link monitor from the frame-timing instrumentation.
        FrameTimingLog.onLiveStats = { [weak self] stats in
            Task { @MainActor in self?.linkStats = stats }
        }
        do {
            let connectionCode = try ConnectionCode(code: code)
            let keys = DerivedKeys(from: connectionCode)
            let rec = UDPReceiver(keys: keys)

            currentCode = code
            currentIP = windowsIP
            self.receiver = rec
            screen = .connecting
            Task { [weak self] in
                await rec.configure(
                    onStateChange: { state in
                        Task { @MainActor in self?.handleState(state) }
                    },
                    onFrame: { _, data, isIDR, trace in
                        guard let self else { return }
                        self.decodeQueue.async {
                            self.decoder?.decode(nalData: data, isIDR: isIDR, trace: trace)
                        }
                    }
                )
                await rec.setWindowsHost(windowsIP)
                await rec.start()
            }

            // On an auto-reconnect, fall back to the setup screen (values pre-filled)
            // if the handshake hasn't completed within the timeout — so a changed IP
            // is a quick fix rather than an endless spinner.
            autoConnectTimeout?.cancel()
            autoConnectTimeout = nil
            if isAuto {
                let seconds = autoConnectTimeoutSeconds
                autoConnectTimeout = Task { [weak self] in
                    try? await Task.sleep(nanoseconds: seconds * 1_000_000_000)
                    if Task.isCancelled { return }
                    await MainActor.run {
                        guard let self else { return }
                        if case .streaming = self.screen { return }   // connected in time
                        self.autoConnectFailed()
                    }
                }
            }
        } catch {
            setupError = "Invalid connection code. Use the 8-character code from the Windows tray."
        }
    }

    // Timed-out auto-reconnect → stop, keep the saved values pre-filled, show setup.
    private func autoConnectFailed() {
        teardownReceiver()
        setupError = "Couldn't reach the PC. Check the code and Windows IP, then Connect."
        screen = .setup
    }

    func disconnect() {
        autoConnectTimeout?.cancel()
        autoConnectTimeout = nil
        teardownReceiver()
        screen = .setup   // saved values remain, so a relaunch still auto-connects
    }

    // Clear the saved pairing and return to a blank setup screen; the next launch
    // won't auto-connect until the user pairs again.
    func forget() {
        autoConnectTimeout?.cancel()
        autoConnectTimeout = nil
        ConnectionStore.forget()
        teardownReceiver()
        prefillCode = ""
        prefillIP = ""
        setupError = nil
        screen = .setup
    }

    private func teardownReceiver() {
        Task { [receiver] in await receiver?.stop() }
        receiver = nil
        decoder = nil
        videoRenderer = nil
        mtkView = nil
        linkStats = nil
    }

    // MARK: - State handler

    private func handleState(_ state: ConnectionState) {
        switch state {
        case .connecting:
            if case .streaming = screen { screen = .disconnected }
            else if case .disconnected = screen { break }
            else { screen = .connecting }
        case .connected(let fps, let w, let h):
            // Handshake succeeded — persist the pairing and stop the fallback timer.
            autoConnectTimeout?.cancel()
            autoConnectTimeout = nil
            ConnectionStore.save(code: currentCode, ip: currentIP)
            prefillCode = currentCode
            prefillIP = currentIP

            let dec = H264Decoder()
            dec.onFrame = { [weak self] buf, trace in
                // The MTKView renders continuously at vsync and picks up the latest frame
                // set here, so no setNeedsDisplay is needed.
                self?.videoRenderer?.present(buf, trace: trace)
            }
            self.decoder = dec
            screen = .streaming(fps: fps, width: w, height: h)
        case .lost:
            linkStats = nil   // grey the monitor while reconnecting
            screen = .disconnected
        case .idle:
            break
        }
    }
}
