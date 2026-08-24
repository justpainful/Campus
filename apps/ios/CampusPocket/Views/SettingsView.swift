import SwiftUI

/// Pairing, storage, and an honest description of where things actually are.
struct SettingsView: View {
    @Environment(Outbox.self) private var outbox
    @Environment(\.dismiss) private var dismiss

    @State private var paired = Pairing.load()
    @State private var showingScanner = false
    @State private var pairingError: String?

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    if let computer = paired {
                        LabeledContent("Paired with", value: computer.name)
                        LabeledContent(
                            "Last sync",
                            value: computer.lastSyncedAt?.formatted(.relative(presentation: .named))
                                ?? "Never")
                        Button("Forget this PC", role: .destructive) {
                            Pairing.forget()
                            paired = nil
                        }
                    } else {
                        Button {
                            showingScanner = true
                        } label: {
                            Label("Pair with your PC", systemImage: "qrcode.viewfinder")
                        }
                    }
                } header: {
                    Text("Your PC")
                } footer: {
                    Text("Open Campus on your PC, go to Sync, and scan the code it shows. "
                         + "Pairing happens once and does not involve any server.")
                }

                Section {
                    LabeledContent("Waiting", value: "\(outbox.pendingCount)")
                    LabeledContent("Captured", value: "\(outbox.items.count)")
                    if outbox.items.contains(where: \.isSynced) {
                        Button("Clear delivered items") { outbox.clearSynced() }
                    }
                } header: {
                    Text("Outbox")
                } footer: {
                    Text("Captures live in this app's Documents folder, which is also how your PC "
                         + "reads them over USB. They leave the phone no other way.")
                }

                Section("About") {
                    LabeledContent("Campus Pocket", value: version)
                    LabeledContent("Works offline", value: "Always")
                }
            }
            .navigationTitle("Settings")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
            }
            .sheet(isPresented: $showingScanner) {
                PairingScannerView { payload in
                    showingScanner = false
                    handle(payload)
                }
            }
            .alert("Pairing failed", isPresented: .constant(pairingError != nil)) {
                Button("OK") { pairingError = nil }
            } message: {
                Text(pairingError ?? "")
            }
        }
    }

    /// A scanned code is untrusted input. The only thing done with one is an attempt to read a
    /// pairing record out of it; anything else is reported and discarded.
    private func handle(_ payload: String) {
        guard let computer = Pairing.parse(payload) else {
            pairingError = "That code is not a Campus pairing code."
            return
        }
        do {
            try Pairing.save(computer)
            paired = computer
        } catch {
            pairingError = "The pairing could not be saved to the keychain."
        }
    }

    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.1.0"
    }
}

#Preview {
    SettingsView().environment(Outbox.shared)
}
