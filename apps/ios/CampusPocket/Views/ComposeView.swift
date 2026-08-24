import SwiftUI

/// The capture sheet: one text field, and everything else optional.
///
/// Save is reachable without leaving the keyboard, and the subject and date pickers are below the
/// fold on purpose — a capture that requires them is a capture that does not happen.
struct ComposeView: View {
    let kind: CaptureKind

    @Environment(Outbox.self) private var outbox
    @Environment(\.dismiss) private var dismiss

    @State private var text = ""
    @State private var subject = ""
    @State private var hasDueDate = false
    @State private var dueDate = Date()
    @FocusState private var focused: Bool

    private static let subjects = [
        "English", "Mathematics", "Physics", "Chemistry", "Biology", "Environmental Science",
    ]

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField(placeholder, text: $text, axis: .vertical)
                        .lineLimit(3...8)
                        .focused($focused)
                        .submitLabel(.done)
                }

                Section {
                    Picker("Subject", selection: $subject) {
                        Text("No subject").tag("")
                        ForEach(Self.subjects, id: \.self) { Text($0).tag($0) }
                    }

                    Toggle("Has a date", isOn: $hasDueDate.animation())
                    if hasDueDate {
                        DatePicker("Due", selection: $dueDate, displayedComponents: [.date])
                    }
                } footer: {
                    Text("Leave these empty if you are in a hurry. You can sort it out on the PC.")
                }
            }
            .navigationTitle(kind.title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Save", action: save)
                        .disabled(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
            .onAppear { focused = true }
        }
        .presentationDetents([.medium, .large])
        .presentationDragIndicator(.visible)
    }

    private var placeholder: String {
        switch kind {
        case .link: "Paste a link"
        case .requirement: "What do you have to bring?"
        case .assignment: "What was set?"
        default: "What is it?"
        }
    }

    private func save() {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }

        // The first line is the title and the rest is the body, so pasting a paragraph keeps its
        // detail instead of becoming a very long title.
        let lines = trimmed.split(separator: "\n", maxSplits: 1, omittingEmptySubsequences: false)
        let title = String(lines[0])
        let body = lines.count > 1
            ? String(lines[1]).trimmingCharacters(in: .whitespacesAndNewlines)
            : nil

        outbox.add(CaptureItem(
            kind: kind,
            title: title,
            body: body?.isEmpty == false ? body : nil,
            subjectName: subject.isEmpty ? nil : subject,
            dueAt: hasDueDate ? dueDate : nil))

        dismiss()
    }
}

#Preview {
    ComposeView(kind: .task).environment(Outbox.shared)
}
