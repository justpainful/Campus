#if DEBUG
import CryptoKit
import Foundation

/// Puts the app in a known state so a screenshot shows something real.
///
/// A screenshot of an empty outbox proves nothing, and posing one by hand means the picture and
/// the app drift apart the moment either changes. So the sample content is seeded through the same
/// `Outbox` the app uses, and the screen to open is named on the command line — the phone's
/// equivalent of the desktop's `--dev-show`.
///
/// Compiled out of release builds entirely. This exists to photograph the app, not to ship in it.
enum ScreenshotMode {
    /// Which screen to open on launch.
    enum Screen: String {
        case capture
        case outbox
        case settings
        case compose
    }

    private static var arguments: [String] { ProcessInfo.processInfo.arguments }

    static var isActive: Bool { arguments.contains("--sample-data") }

    static var screen: Screen? {
        guard let index = arguments.firstIndex(of: "--screen"),
              index + 1 < arguments.count
        else { return nil }

        return Screen(rawValue: arguments[index + 1])
    }

    /// Fills the outbox with a day that looks like a real one, not like a test fixture.
    @MainActor
    static func prepare(_ outbox: Outbox = .shared) {
        guard isActive else { return }

        // Started from whatever the last run left behind, so repeated launches do not stack up
        // twelve copies of the same homework.
        outbox.removeAll()

        var homework = CaptureItem(
            kind: .assignment,
            title: "English Workbook, page 220",
            body: "Questions 1 to 8, the ones about the present perfect.",
            subjectName: "English",
            dueAt: Calendar.current.date(byAdding: .day, value: 1, to: Date()))

        var scan = CaptureItem(
            kind: .photo,
            title: "Chemistry worksheet",
            subjectName: "Chemistry",
            attachment: "sample.pdf",
            attachmentBytes: 428_000)

        let reminder = CaptureItem(
            kind: .requirement,
            title: "Bring the chemistry notebook Wednesday",
            subjectName: "Chemistry")

        let question = CaptureItem(
            kind: .inbox,
            title: "Ask whether the physics test moved",
            body: "The teacher mentioned it at the end of the lesson.")

        let link = CaptureItem(
            kind: .link,
            title: "Present perfect explained",
            body: "https://www.youtube.com/watch?v=example",
            subjectName: "English")

        // One already delivered, so the outbox shows the difference between "the PC has it" and
        // "I imagined capturing that" — which is the whole reason that list has two sections.
        var delivered = CaptureItem(
            kind: .note,
            title: "Ionic bonds transfer electrons, covalent bonds share them",
            subjectName: "Chemistry")
        delivered.syncedAt = Date().addingTimeInterval(-3_600)

        homework.capturedAt = Date().addingTimeInterval(-240)
        scan.capturedAt = Date().addingTimeInterval(-900)

        for item in [homework, scan, reminder, question, link, delivered] {
            outbox.add(item)
        }

        pairIfNeeded()
    }

    /// A paired PC, so the settings screen shows what it looks like once pairing has happened.
    private static func pairIfNeeded() {
        guard Pairing.load() == nil else { return }

        let key = SymmetricKey(size: .bits256).withUnsafeBytes { Data($0) }

        try? Pairing.save(PairedComputer(
            id: "01JQZK7C4M9WY2X8N5RTVB3HDG",
            name: "Kuroi's PC",
            key: key.base64EncodedString(),
            pairedAt: Date().addingTimeInterval(-86_400 * 3),
            lastSyncedAt: Date().addingTimeInterval(-5_400)))
    }
}
#endif
