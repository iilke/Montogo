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
        }
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
            }
        }
    }
}
