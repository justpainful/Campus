import Foundation
import Observation

/// The outbox: everything captured on this phone, on disk, waiting for the PC.
///
/// It lives in Documents rather than in Application Support because Documents is what
/// `UIFileSharingEnabled` exposes, and that is the directory the desktop reads over USB. The
/// consequence is that the file is visible in the Files app — which is a feature here, not a
/// leak, since the phone holds captures rather than the workspace.
@Observable
@MainActor
final class Outbox {
    private(set) var items: [CaptureItem] = []
    private(set) var lastError: String?

    /// Items that have not reached the PC yet, newest first.
    var pending: [CaptureItem] {
        items.filter { !$0.isSynced }.sorted { $0.capturedAt > $1.capturedAt }
    }

    var pendingCount: Int { items.count(where: { !$0.isSynced }) }

    static let shared = Outbox()

    private let queue = DispatchQueue(label: "com.campus.pocket.outbox", qos: .utility)

    private var documents: URL {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
    }

    var storeURL: URL { documents.appendingPathComponent("outbox.json") }
    var attachmentsURL: URL { documents.appendingPathComponent("attachments", isDirectory: true) }

    private init() {
        try? FileManager.default.createDirectory(
            at: attachmentsURL, withIntermediateDirectories: true)
        load()
    }

    // MARK: - Capture

    @discardableResult
    func add(_ item: CaptureItem) -> CaptureItem {
        items.append(item)
        save()
        return item
    }

    /// Stores an attachment beside the outbox and returns the file name to reference it by.
    func writeAttachment(_ data: Data, extension ext: String) throws -> (name: String, bytes: Int) {
        let name = "\(CaptureItem.makeIdentifier()).\(ext)"
        let url = attachmentsURL.appendingPathComponent(name)
        try data.write(to: url, options: .atomic)
        return (name, data.count)
    }

    func remove(_ item: CaptureItem) {
        if let attachment = item.attachment {
            try? FileManager.default.removeItem(
                at: attachmentsURL.appendingPathComponent(attachment))
        }
        items.removeAll { $0.id == item.id }
        save()
    }

    /// Marks items as delivered. Called by the sync client once the PC has acknowledged them.
    func markSynced(ids: Set<String>, at date: Date = Date()) {
        for index in items.indices where ids.contains(items[index].id) {
            items[index].syncedAt = date
        }
        save()
    }

    /// Drops delivered items and their attachments. The PC has them; the phone does not need them.
    func clearSynced() {
        let delivered = items.filter(\.isSynced)
        for item in delivered {
            if let attachment = item.attachment {
                try? FileManager.default.removeItem(
                    at: attachmentsURL.appendingPathComponent(attachment))
            }
        }
        items.removeAll(where: \.isSynced)
        save()
    }

    // MARK: - Persistence

    private func load() {
        guard let data = try? Data(contentsOf: storeURL) else { return }
        do {
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .iso8601
            items = try decoder.decode([CaptureItem].self, from: data)
        } catch {
            // A corrupt outbox must not stop capture. The file is kept aside rather than deleted,
            // because it may still be recoverable by hand and it is the only copy.
            let salvage = storeURL.appendingPathExtension("damaged")
            try? FileManager.default.moveItem(at: storeURL, to: salvage)
            lastError = "The outbox could not be read and was set aside."
        }
    }

    private func save() {
        let snapshot = items
        let url = storeURL

        queue.async {
            do {
                let encoder = JSONEncoder()
                encoder.dateEncodingStrategy = .iso8601
                encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
                let data = try encoder.encode(snapshot)
                // Written atomically so a capture interrupted by the app being killed cannot
                // truncate everything captured before it.
                try data.write(to: url, options: .atomic)
            } catch {
                Task { @MainActor in
                    Outbox.shared.lastError = "Could not save: \(error.localizedDescription)"
                }
            }
        }
    }
}
