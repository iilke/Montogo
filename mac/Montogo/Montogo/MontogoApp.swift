import SwiftUI

@main
struct MontogoApp: App {
    @StateObject private var vm = AppViewModel()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(vm)
                .frame(minWidth: 640, minHeight: 400)
                .onAppear { vm.bootstrap() }   // auto-reconnect if a pairing is saved
        }
        .windowStyle(.hiddenTitleBar)
        .windowResizability(.contentSize)
        .commands {
            CommandGroup(replacing: .newItem) {}   // hide File > New Window
            CommandMenu("Connection") {
                Button("Forget This Connection") { vm.forget() }
                    .keyboardShortcut("k", modifiers: [.command, .shift])
            }
            CommandGroup(after: .windowArrangement) {
                Button("Enter Full Screen") {
                    NSApp.keyWindow?.toggleFullScreen(nil)
                }
                .keyboardShortcut("f", modifiers: [.control, .command])
            }
        }
    }
}
