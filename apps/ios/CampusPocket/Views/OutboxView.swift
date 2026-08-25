import SwiftUI

/// What is waiting, and what has already reached the PC.
///
/// The list is deliberately honest about the difference: an item that has been delivered is shown
/// as delivered rather than quietly disappearing, so it is possible to tell "the PC has it" from
/// "I imagined capturing that".
struct OutboxView: View {
    @Environment(Outbox.self) private var outbox
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Group {
                if outbox.items.isEmpty { empty } else { list }
            }
            .navigationTitle("Outbox")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("Done") { dismiss() }
                }
                ToolbarItem(placement: .topBarLeading) {
                    if outbox.items.contains(where: \.isSynced) {
                        Button("Clear delivered") { outbox.clearSynced() }
                    }
                }
            }
        }
    }

    private var empty: some View {
        ContentUnavailableView(
            "Nothing waiting",
            systemImage: "tray",
            description: Text("Anything you capture appears here until your PC picks it up."))
    }

    private var list: some View {
        List {
            let pending = outbox.pending
            if !pending.isEmpty {
                Section("Waiting") {
                    ForEach(pending) { row($0) }
                        .onDelete { offsets in
                            for index in offsets { outbox.remove(pending[index]) }
                        }
                }
            }

            let delivered = outbox.items.filter(\.isSynced).sorted { $0.capturedAt > $1.capturedAt }
            if !delivered.isEmpty {
                Section("Delivered") {
                    ForEach(delivered) { row($0) }
                }
            }
        }
    }

    private func row(_ item: CaptureItem) -> some View {
        HStack(spacing: CampusTheme.Space.m) {
            Image(systemName: item.kind.symbol)
                .font(.body)
                .foregroundStyle(item.isSynced ? CampusTheme.success : CampusTheme.labelSecondary)
                .frame(width: 24)

            VStack(alignment: .leading, spacing: 2) {
                Text(item.title)
                    .font(.body)
                    .foregroundStyle(CampusTheme.labelPrimary)
                    .lineLimit(2)
                if let detail = subtitle(item) {
                    Text(detail)
                        .font(.footnote)
                        .foregroundStyle(CampusTheme.labelSecondary)
                }
            }

            Spacer()

            Text(Self.ago(item.capturedAt))
                .font(.caption)
                .foregroundStyle(CampusTheme.labelTertiary)
        }
    }

    /// How long ago something was captured, in words a person would use.
    ///
    /// Foundation's relative formatter is happy to say "in 0 seconds" about something that just
    /// happened, and about anything a fraction of a second in the future — which, with a clock
    /// that ticks between saving a capture and drawing the row, is most of them.
    static func ago(_ date: Date) -> String {
        let elapsed = Date().timeIntervalSince(date)
        if elapsed < 60 { return "Just now" }

        return date.formatted(.relative(presentation: .numeric))
    }

    private func subtitle(_ item: CaptureItem) -> String? {
        var parts: [String] = []
        if let subject = item.subjectName { parts.append(subject) }
        if let due = item.dueAt {
            parts.append(due.formatted(.dateTime.weekday(.wide).day().month(.abbreviated)))
        }
        if let bytes = item.attachmentBytes {
            parts.append(ByteCountFormatter.string(fromByteCount: Int64(bytes), countStyle: .file))
        }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }
}

#Preview {
    OutboxView().environment(Outbox.shared)
}
