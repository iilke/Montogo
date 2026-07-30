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

    private var receiver: UDPReceiver?
    var decoder: H264Decoder?
    var videoRenderer: VideoRenderer?
    weak var mtkView: MTKView?   // set by StreamingView once the Metal view is created

    // Dedicated serial queue for VTDecompressionSessionDecodeFrame.  Decoding on
    // the MainActor made every frame wait for UI work (frame timing showed the
    // decode stage dominated by main-thread scheduling, not VideoToolbox); a
    // serial queue keeps H264Decoder's single-caller contract and frame order.
    private let decodeQueue = DispatchQueue(label: "com.montogo.decode", qos: .userInteractive)

    // Called by SetupView when the user taps Connect.
    func connect(code: String, windowsIP: String) {
        setupError = nil
        do {
            let connectionCode = try ConnectionCode(code: code)
            let keys = DerivedKeys(from: connectionCode)
            let rec = UDPReceiver(keys: keys)

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
        } catch {
            setupError = "Invalid connection code. Use the 8-character code from the Windows tray."
        }
    }

    func disconnect() {
        Task { await receiver?.stop() }
        receiver = nil
        decoder = nil
        videoRenderer = nil
        mtkView = nil
        screen = .setup
    }

    // MARK: - State handler

    private func handleState(_ state: ConnectionState) {
        switch state {
        case .connecting:
            if case .streaming = screen { screen = .disconnected }
            else if case .disconnected = screen { break }
            else { screen = .connecting }
        case .connected(let fps, let w, let h):
            let dec = H264Decoder()
            dec.onFrame = { [weak self] buf, trace in
                guard let self else { return }
                self.videoRenderer?.present(buf, trace: trace)
                if let view = self.mtkView {
                    DispatchQueue.main.async { view.setNeedsDisplay(view.bounds) }
                }
            }
            self.decoder = dec
            screen = .streaming(fps: fps, width: w, height: h)
        case .lost:
            screen = .disconnected
        case .idle:
            break
        }
    }
}
