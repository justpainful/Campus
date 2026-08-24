import CryptoKit
import Foundation

/// Pairing, and the small amount of cryptography that makes a sync session mean something.
///
/// The PC shows a QR code once; the phone reads it and keeps the shared key in the keychain. From
/// then on the phone proves it holds that key by signing a nonce, so being on the same Wi-Fi is
/// not enough to be trusted — which matters on a school network.
enum Pairing {
    private static let account = "com.campus.pocket.paired"

    /// Parses the QR payload: `campus-pair:v1:<deviceId>:<name>:<base64 key>`.
    static func parse(_ payload: String) -> PairedComputer? {
        let parts = payload.split(separator: ":", maxSplits: 4, omittingEmptySubsequences: false)
        guard parts.count == 5,
              parts[0] == "campus-pair",
              parts[1] == "v1",
              let name = String(parts[3]).removingPercentEncoding,
              Data(base64Encoded: String(parts[4])) != nil
        else { return nil }

        return PairedComputer(
            id: String(parts[2]),
            name: name,
            key: String(parts[4]),
            pairedAt: Date())
    }

    /// Signs a nonce with the paired key. HMAC rather than a plain hash, so the key cannot be
    /// recovered from a captured signature by extending it.
    static func sign(nonce: String, key: String) -> String? {
        guard let keyData = Data(base64Encoded: key),
              let nonceData = nonce.data(using: .utf8)
        else { return nil }

        let mac = HMAC<SHA256>.authenticationCode(
            for: nonceData, using: SymmetricKey(data: keyData))
        return Data(mac).base64EncodedString()
    }

    static func makeNonce() -> String {
        var bytes = [UInt8](repeating: 0, count: 16)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return Data(bytes).base64EncodedString()
    }

    // MARK: Keychain

    static func save(_ computer: PairedComputer) throws {
        let data = try JSONEncoder().encode(computer)

        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)

        var attributes = query
        attributes[kSecValueData as String] = data
        // The key is useless while the phone is locked, and should not travel to a restored
        // backup on a different device.
        attributes[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly

        let status = SecItemAdd(attributes as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw NSError(domain: NSOSStatusErrorDomain, code: Int(status))
        }
    }

    static func load() -> PairedComputer? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]

        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data
        else { return nil }

        return try? JSONDecoder().decode(PairedComputer.self, from: data)
    }

    static func forget() {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)
    }
}
