import Foundation

/// The one folder the app and the share extension can both see.
///
/// iOS keeps an extension out of its containing app's Documents folder, which is where the outbox
/// lives — and the outbox lives there because Documents is what a cable can read. So the two halves
/// meet in an app group instead: the extension drops a small file in, and Campus Pocket picks it up
/// the next time it opens and turns it into a real capture.
///
/// One file per share, written atomically and named by time, so two things shared in the same
/// second cannot overwrite each other and a share interrupted halfway leaves nothing behind.
enum SharedInbox {
    /// Must match the app group on both targets. Without it, sharing quietly does nothing, so
    /// both halves check and say so rather than failing silently.
    static let group = "group.com.campus.pocket"

    static var directory: URL? {
        FileManager.default
            .containerURL(forSecurityApplicationGroupIdentifier: group)?
            .appendingPathComponent("shared-inbox", isDirectory: true)
    }

    /// What the extension writes and the app reads.
    struct Item: Codable, Sendable {
        var title: String
        var note: String
        var url: String?
        var sharedAt: Date
    }

    @discardableResult
    static func write(title: String, note: String, url: URL?) -> Bool {
        guard let directory else { return false }

        do {
            try FileManager.default.createDirectory(
                at: directory, withIntermediateDirectories: true)

            let item = Item(
                title: title,
                note: note,
                url: url?.absoluteString,
                sharedAt: Date())

            let encoder = JSONEncoder()
            encoder.dateEncodingStrategy = .iso8601

            let name = "\(Int(Date().timeIntervalSince1970 * 1000))-\(UUID().uuidString).json"
            try encoder.encode(item).write(
                to: directory.appendingPathComponent(name), options: .atomic)

            return true
        } catch {
            return false
        }
    }

    /// Everything waiting, oldest first. The files are removed as they are read: they are a
    /// hand-off, and leaving them would mean importing the same share on every launch.
    static func drain() -> [Item] {
        guard let directory,
              let names = try? FileManager.default.contentsOfDirectory(atPath: directory.path)
        else { return [] }

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601

        var items: [Item] = []

        for name in names.sorted() where name.hasSuffix(".json") {
            let url = directory.appendingPathComponent(name)

            if let data = try? Data(contentsOf: url),
               let item = try? decoder.decode(Item.self, from: data) {
                items.append(item)
            }

            // Removed whether or not it could be read: a file that will never decode would
            // otherwise be retried for ever.
            try? FileManager.default.removeItem(at: url)
        }

        return items
    }
}
