import SwiftUI
import MetalKit

struct StreamingView: View {
    @EnvironmentObject var vm: AppViewModel
    let fps: UInt8
    let width: UInt16
    let height: UInt16
    let isDisconnected: Bool

    var body: some View {
        ZStack {
            MetalVideoView()
                .ignoresSafeArea()

            if isDisconnected {
                DisconnectedOverlay()
            }

            // Live link monitor, top-right. Click-through so it never blocks the
            // disconnected overlay's buttons.
            VStack {
                HStack {
                    Spacer()
                    LinkMonitorView(stats: vm.linkStats)
                }
                Spacer()
            }
            .padding(14)
            .allowsHitTesting(false)
        }
    }
}

// MARK: - Link monitor (top-right HUD)

private struct LinkMonitorView: View {
    let stats: LinkStats?

    var body: some View {
        HStack(spacing: 12) {
            Circle()
                .fill(healthColor)
                .frame(width: 8, height: 8)
                .shadow(color: healthColor.opacity(0.8), radius: 3)

            if let s = stats {
                metric("\(Int(s.fps.rounded()))", "fps")
                metric("\(Int(s.latencyAvgMs.rounded()))", "ms")
                metric(String(format: "%.1f%%", s.lossPct), "loss")
            } else {
                Text("link idle").foregroundStyle(.white.opacity(0.6))
            }
        }
        .font(.system(size: 12, weight: .semibold, design: .monospaced))
        .foregroundStyle(.white)
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
        .background(.black.opacity(0.5), in: Capsule())
        .overlay(Capsule().strokeBorder(.white.opacity(0.12)))
    }

    private func metric(_ value: String, _ unit: String) -> some View {
        HStack(spacing: 3) {
            Text(value)
            Text(unit)
                .font(.system(size: 10, weight: .medium, design: .monospaced))
                .foregroundStyle(.white.opacity(0.55))
        }
    }

    // Green = healthy; yellow = minor loss / latency spike / fps dip;
    // red = real packet loss or decode errors (the usual causes of on-screen shredding).
    private var healthColor: Color {
        guard let s = stats else { return .gray }
        if s.lossPct > 1 || s.decodeErrors > 0 { return .red }
        if s.lossPct > 0 || s.latencyMaxMs > 60 || s.fps < 40 { return .yellow }
        return .green
    }
}

// MARK: - Metal surface

private struct MetalVideoView: NSViewRepresentable {
    @EnvironmentObject var vm: AppViewModel

    func makeNSView(context: Context) -> MTKView {
        let view = MTKView()
        view.layer?.isOpaque = true
        view.wantsLayer = true
        if let renderer = VideoRenderer(mtkView: view) {
            vm.videoRenderer = renderer
            vm.mtkView = view
        }
        return view
    }

    func updateNSView(_ nsView: MTKView, context: Context) {}
}

// MARK: - Disconnected overlay

private struct DisconnectedOverlay: View {
    @EnvironmentObject var vm: AppViewModel

    var body: some View {
        ZStack {
            Color.black.opacity(0.72).ignoresSafeArea()

            VStack(spacing: 20) {
                Image(systemName: "wifi.slash")
                    .font(.system(size: 48))
                    .foregroundStyle(.white)

                Text("Connection Lost")
                    .font(.title2.bold())
                    .foregroundStyle(.white)

                Text("Reconnecting…")
                    .foregroundStyle(.white.opacity(0.7))

                ProgressView()
                    .tint(.white)

                Button("Disconnect") { vm.disconnect() }
                    .buttonStyle(.bordered)
                    .tint(.white)
                    .foregroundStyle(.white)
                    .padding(.top, 12)

                Button("Forget This Connection") { vm.forget() }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.white.opacity(0.55))
            }
        }
    }
}
