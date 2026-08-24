import Foundation

/// The kinds a capture can be. Mirrors `ObjectKind` on the desktop, restricted to the kinds it
/// makes sense to create in three seconds during a lesson.
enum CaptureKind: Int, Codable, CaseIterable, Identifiable, Sendable {
    case inbox = 1
    case task = 4
    case note = 3
    case assignment = 5
    case requirement = 6
    case link = 7
    case photo = 2

    var id: Int { rawValue }

    var title: String {
        switch self {
        case .inbox: "Inbox"
        case .task: "Task"
        case .note: "Note"
        case .assignment: "Assignment"
        case .requirement: "Requirement"
        case .link: "Link"
        case .photo: "Photo"
        }
    }

    /// SF Symbols, which is what the desktop's icon set was modelled on.
    var symbol: String {
        switch self {
        case .inbox: "tray"
        case .task: "checkmark.circle"
        case .note: "note.text"
        case .assignment: "list.bullet.rectangle"
        case .requirement: "flag"
        case .link: "link"
        case .photo: "doc.viewfinder"
        }
    }
}

/// One thing captured on the phone, waiting to reach the PC.
///
/// Everything here is written locally and stays local. There is no account, no server and no
/// cloud: the desktop reads this over USB or the local network, and that is the only way it
/// leaves the device.
struct CaptureItem: Codable, Identifiable, Hashable, Sendable {
    let id: String
    var kind: CaptureKind
    var title: String
    var body: String?
    var subjectName: String?
    var dueAt: Date?
    var capturedAt: Date
    var syncedAt: Date?

    /// File name inside the outbox's `attachments` directory, for photos and scans.
    var attachment: String?
    var attachmentBytes: Int?

    var isSynced: Bool { syncedAt != nil }

    init(
        kind: CaptureKind,
        title: String,
        body: String? = nil,
        subjectName: String? = nil,
        dueAt: Date? = nil,
        attachment: String? = nil,
        attachmentBytes: Int? = nil
    ) {
        // A sortable identifier, matching the desktop's CampusId layout closely enough that the
        // two can be compared without either side needing to translate.
        self.id = CaptureItem.makeIdentifier()
        self.kind = kind
        self.title = title
        self.body = body
        self.subjectName = subjectName
        self.dueAt = dueAt
        self.capturedAt = Date()
        self.attachment = attachment
        self.attachmentBytes = attachmentBytes
    }

    private static let alphabet = Array("0123456789ABCDEFGHJKMNPQRSTVWXYZ")

    /// 26 characters: a 48-bit timestamp followed by randomness, so identifiers sort by creation.
    static func makeIdentifier() -> String {
        var milliseconds = UInt64(Date().timeIntervalSince1970 * 1000)
        var characters = [Character](repeating: "0", count: 26)

        for index in stride(from: 9, through: 0, by: -1) {
            characters[index] = alphabet[Int(milliseconds % 32)]
            milliseconds /= 32
        }
        for index in 10..<26 {
            characters[index] = alphabet[Int.random(in: 0..<32)]
        }
        return String(characters)
    }
}
