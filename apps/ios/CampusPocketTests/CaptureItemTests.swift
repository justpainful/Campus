import XCTest
@testable import CampusPocket

final class CaptureItemTests: XCTestCase {

    func testIdentifiersSortByCreationTime() throws {
        let first = CaptureItem.makeIdentifier()
        Thread.sleep(forTimeInterval: 0.005)
        let second = CaptureItem.makeIdentifier()

        XCTAssertEqual(first.count, 26)
        XCTAssertLessThan(first, second, "Identifiers must sort in creation order")
    }

    func testIdentifiersAreUnique() throws {
        let identifiers = Set((0..<2_000).map { _ in CaptureItem.makeIdentifier() })
        XCTAssertEqual(identifiers.count, 2_000)
    }

    func testCaptureRoundTripsThroughJSON() throws {
        let item = CaptureItem(
            kind: .assignment,
            title: "English Workbook, page 220",
            body: "Exercises A to C",
            subjectName: "English",
            dueAt: Date(timeIntervalSince1970: 1_756_000_000))

        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601

        let restored = try decoder.decode(CaptureItem.self, from: encoder.encode(item))

        XCTAssertEqual(restored.id, item.id)
        XCTAssertEqual(restored.kind, .assignment)
        XCTAssertEqual(restored.title, item.title)
        XCTAssertEqual(restored.subjectName, "English")
        XCTAssertFalse(restored.isSynced)
    }
}

final class PairingTests: XCTestCase {

    func testParsesAValidPairingCode() throws {
        let payload = "campus-pair:v1:abc123:Study%20PC:aGVsbG8gd29ybGQgc2VjcmV0IGtleSE="
        let computer = try XCTUnwrap(Pairing.parse(payload))

        XCTAssertEqual(computer.id, "abc123")
        XCTAssertEqual(computer.name, "Study PC")
    }

    func testRejectsCodesThatAreNotPairingCodes() {
        XCTAssertNil(Pairing.parse("https://example.com"))
        XCTAssertNil(Pairing.parse("campus-pair:v2:abc:Name:aGk="))
        XCTAssertNil(Pairing.parse("campus-pair:v1:abc:Name:not base64 at all"))
    }

    func testSignatureIsStableAndDependsOnTheKey() throws {
        let key = Data("a-shared-secret-key-of-some-length".utf8).base64EncodedString()
        let other = Data("a-different-secret-key-entirely!".utf8).base64EncodedString()
        let nonce = Pairing.makeNonce()

        let signature = try XCTUnwrap(Pairing.sign(nonce: nonce, key: key))

        XCTAssertEqual(signature, Pairing.sign(nonce: nonce, key: key))
        XCTAssertNotEqual(signature, Pairing.sign(nonce: nonce, key: other))
        XCTAssertNotEqual(signature, Pairing.sign(nonce: Pairing.makeNonce(), key: key))
    }
}
