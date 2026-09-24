import SwiftUI

struct SetupView: View {
    @EnvironmentObject var vm: AppViewModel

    @State private var codeField = ""
    @State private var ipField = ""
    @FocusState private var focusedField: Field?

    enum Field { case code, ip }

    var body: some View {
        ZStack {
            Color(nsColor: .windowBackgroundColor).ignoresSafeArea()

            VStack(spacing: 0) {
                Spacer()

                // Card
                VStack(spacing: 28) {
                    // Icon + headline
                    VStack(spacing: 12) {
                        Image(nsImage: NSImage(named: "AppIcon") ?? NSImage())
                            .resizable()
                            .frame(width: 80, height: 80)
                            .cornerRadius(18)

                        Text("Montogo")
                            .font(.system(size: 28, weight: .semibold, design: .rounded))

                        Text("Enter the connection code shown in the Windows tray menu.")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                            .multilineTextAlignment(.center)
                            .frame(maxWidth: 300)
                    }

                    // Fields
                    VStack(alignment: .leading, spacing: 16) {
                        VStack(alignment: .leading, spacing: 6) {
                            Label("Connection Code", systemImage: "key.fill")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                            TextField("XXXX-XXXX-XXXXX", text: $codeField)
                                .textFieldStyle(.roundedBorder)
                                .font(.system(.body, design: .monospaced))
                                .autocorrectionDisabled()
                                .focused($focusedField, equals: .code)
                                .onChange(of: codeField) { new in
                                    codeField = formatCode(new)
                                }
                                .onSubmit { focusedField = .ip }
                        }

                        VStack(alignment: .leading, spacing: 6) {
                            Label("Windows IP Address", systemImage: "network")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                            TextField("192.168.x.x", text: $ipField)
                                .textFieldStyle(.roundedBorder)
                                .focused($focusedField, equals: .ip)
                                .onSubmit { connect() }
                        }

                        if let err = vm.setupError {
                            Text(err)
                                .font(.caption)
                                .foregroundStyle(.red)
                        }
                    }
                    .frame(width: 300)

                    // Connect button
                    Button(action: connect) {
                        Text("Connect")
                            .frame(width: 300)
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .disabled(codeField.replacingOccurrences(of: "-", with: "").count < 13 || ipField.isEmpty)
                    .keyboardShortcut(.return, modifiers: [])
                }
                .padding(40)
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 20, style: .continuous))
                .shadow(color: .black.opacity(0.12), radius: 24, y: 8)
                .frame(width: 420)

                Spacer()
            }
        }
        .onAppear {
            // Pre-fill with the last-used values (from Keychain + UserDefaults) so the
            // user only fixes what changed; focus the first empty field.
            if codeField.isEmpty { codeField = vm.prefillCode }
            if ipField.isEmpty   { ipField   = vm.prefillIP }
            focusedField = codeField.isEmpty ? .code : .ip
        }
    }

    private func connect() {
        vm.connect(code: codeField, windowsIP: ipField)
    }

    // Auto-insert dashes into 4-4-5 groups, cap at 13 chars (XXXX-XXXX-XXXXX).
    private func formatCode(_ raw: String) -> String {
        let stripped = raw.replacingOccurrences(of: "-", with: "")
                          .uppercased()
                          .filter { "23456789ABCDEFGHJKLMNPQRSTUVWXYZ".contains($0) }
        let capped = Array(stripped.prefix(13))
        var groups: [String] = []
        groups.append(String(capped.prefix(4)))
        if capped.count > 4 { groups.append(String(capped[4..<min(8, capped.count)])) }
        if capped.count > 8 { groups.append(String(capped[8..<capped.count])) }
        return groups.joined(separator: "-")
    }
}
