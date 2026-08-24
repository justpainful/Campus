import Social
import SwiftUI
import UniformTypeIdentifiers

/// The share sheet target: catching something from another app without leaving it.
///
/// An extension cannot reach the containing app's Documents folder, so it does not try. It writes
/// what was shared into a folder both halves can see, and Campus Pocket drains that folder into
/// the outbox the next time it opens. The alternative — moving the outbox into the shared
/// container — would take it out of Documents, and Documents is how the desktop reads it over a
/// cable.
///
/// It never talks to the network, never opens the workspace, and holds no key. It writes a file
/// and closes.
final class ShareViewController: UIViewController {
    override func viewDidLoad() {
        super.viewDidLoad()

        let view = ShareComposer(
            onSave: { [weak self] title, note in
                self?.save(title: title, note: note)
            },
            onCancel: { [weak self] in
                self?.extensionContext?.completeRequest(returningItems: nil)
            })

        let hosting = UIHostingController(rootView: view)
        addChild(hosting)
        hosting.view.frame = self.view.bounds
        hosting.view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        self.view.addSubview(hosting.view)
        hosting.didMove(toParent: self)
    }

    private func save(title: String, note: String) {
        Task {
            let attachments = (extensionContext?.inputItems as? [NSExtensionItem] ?? [])
                .flatMap { $0.attachments ?? [] }

            var url: URL?
            var text: String?

            for provider in attachments {
                if provider.hasItemConformingToTypeIdentifier(UTType.url.identifier) {
                    url = try? await provider.loadItem(
                        forTypeIdentifier: UTType.url.identifier) as? URL
                } else if provider.hasItemConformingToTypeIdentifier(UTType.plainText.identifier) {
                    text = try? await provider.loadItem(
                        forTypeIdentifier: UTType.plainText.identifier) as? String
                } else if provider.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) {
                    url = try? await provider.loadItem(
                        forTypeIdentifier: UTType.fileURL.identifier) as? URL
                }
            }

            SharedInbox.write(
                title: title.isEmpty ? (url?.absoluteString ?? "Shared") : title,
                note: [note, text].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: "\n\n"),
                url: url)

            extensionContext?.completeRequest(returningItems: nil)
        }
    }
}

/// What the share sheet shows: a title, a line of context, and two buttons.
private struct ShareComposer: View {
    @State private var title = ""
    @State private var note = ""

    let onSave: (String, String) -> Void
    let onCancel: () -> Void

    var body: some View {
        NavigationStack {
            Form {
                Section("Send to Campus") {
                    TextField("Title", text: $title)
                    TextField("Anything worth remembering", text: $note, axis: .vertical)
                        .lineLimit(3...6)
                }

                Section {
                    Text("This lands in your phone's outbox and reaches your PC the next time "
                         + "you sync. Nothing is sent anywhere now.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }
            }
            .navigationTitle("Campus")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel", action: onCancel)
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Save") { onSave(title, note) }
                }
            }
        }
    }
}
