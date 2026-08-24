import SwiftUI

/// Pairing, storage, and an honest description of where things actually are.
struct SettingsView: View {
    @Environment(Outbox.self) private var outbox
    @Environment(\.dismiss) private var dismiss

    @State private var paired = Pairing.load()
    @State private var showingScanner = false
    @State private var pairingError: String?

    /// Remembered between sends: on a home network the PC keeps the same address, and retyping
    /// four numbers every time is the sort of friction that stops people syncing at all.
    @AppStorage("com.campus.pocket.lastHost") private var host = ""

    @State private var sync = SyncClient.shared

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
                    TextField("192.168.1.20", text: $host)
                        .keyboardType(.numbersAndPunctuation)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()

                    Button {
                        Task { await sync.send(to: host.trimmingCharacters(in: .whitespaces)) }
                    } label: {
                        Label("Send to your PC", systemImage: "arrow.up.circle")
                    }
                    .disabled(paired == nil || host.isEmpty || outbox.pendingCount == 0)

                    if let message = statusMessage {
                        Text(message)
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                    }
                } header: {
                    Text("Send")
                } footer: {
                    Text("On your PC, open Sync and choose \u{201C}Wait for a device\u{201D}. "
                         + "The address it shows goes above. Nothing leaves this phone until you "
                         + "press send, and only what the PC confirms is cleared from the outbox.")
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
    /// What the last send did, in the app's own words rather than a network error.
    private var statusMessage: String? {
        switch sync.status {
        case .idle: nil
        case .connecting(let host): "Connecting to \(host)…"
        case .sending(let done, let total): "Sending \(done) of \(total)…"
        case .finished(let message): message
        case .failed(let message): message
        }
    }

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
