import SwiftUI

struct ConnectingView: View {
    @EnvironmentObject var vm: AppViewModel

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            VStack(spacing: 20) {
                ProgressView()
                    .progressViewStyle(.circular)
                    .scaleEffect(1.5)
                    .tint(.white)

                Text("Connecting…")
                    .font(.title3)
                    .foregroundStyle(.white)

                Button("Cancel") { vm.disconnect() }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.white.opacity(0.6))
                    .padding(.top, 8)

                Button("Forget This Connection") { vm.forget() }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.white.opacity(0.4))
            }
        }
    }
}
