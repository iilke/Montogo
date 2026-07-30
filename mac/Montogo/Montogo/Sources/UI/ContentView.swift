import SwiftUI

struct ContentView: View {
    @EnvironmentObject var vm: AppViewModel

    var body: some View {
        Group {
            switch vm.screen {
            case .setup:
                SetupView()
            case .connecting:
                ConnectingView()
            case .streaming(let fps, let w, let h):
                StreamingView(fps: fps, width: w, height: h, isDisconnected: false)
            case .disconnected:
                // Keep the last video frame visible under the overlay.
                StreamingView(fps: 0, width: 0, height: 0, isDisconnected: true)
            }
        }
        .animation(.easeInOut(duration: 0.25), value: vm.screen.discriminator)
    }
}

extension AppScreen {
    var discriminator: Int {
        switch self {
        case .setup:        return 0
        case .connecting:   return 1
        case .streaming:    return 2
        case .disconnected: return 3
        }
    }
}
