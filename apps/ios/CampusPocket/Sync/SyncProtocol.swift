import Foundation

/// The wire format between the phone and the PC.
///
/// Deliberately small and versioned. Both halves are written together, but they ship separately —
/// a phone updated from the App Store can meet a PC that has not been updated in months, so the
/// version is checked before anything else and a mismatch is reported rather than guessed at.
enum SyncProtocol {
    static let version = 1
    static let serviceType = "_campus-sync._tcp"
    static let port: UInt16 = 47_821

    /// Sent by the phone to open a session. The nonce is signed with the paired key, so a device
    /// on the same Wi-Fi that never completed pairing cannot open one.
    struct Hello: Codable, Sendable {
        var version: Int = SyncProtocol.version
        var deviceId: String
        var deviceName: String
        var nonce: String
        var signature: String
    }

    struct HelloAck: Codable, Sendable {
        var version: Int
        var accepted: Bool
        var workspaceName: String?
        var reason: String?
    }

    /// A batch of captures. Attachments follow as length-prefixed blobs in the order referenced.
    struct Push: Codable, Sendable {
        var items: [CaptureItem]
        var attachmentBytes: [String: Int]
    }

    /// What the PC actually stored. The phone marks only these as delivered, so an interrupted
    /// transfer costs a retry rather than a lost capture.
    struct PushAck: Codable, Sendable {
        var acceptedIds: [String]
        var rejected: [String: String]
    }
}

/// A PC this phone has been paired with.
struct PairedComputer: Codable, Identifiable, Sendable {
    var id: String
    var name: String
    /// Shared secret established during pairing, base64. Never leaves the keychain.
    var key: String
    var pairedAt: Date
    var lastSyncedAt: Date?
}
