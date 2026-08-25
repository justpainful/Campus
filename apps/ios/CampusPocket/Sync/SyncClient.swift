import CryptoKit
import Foundation
import Network

#if canImport(UIKit)
import UIKit
#endif

/// Sends what this phone has caught to the PC it is paired with.
///
/// One direction, on purpose. The phone holds captures rather than the workspace, so there is
/// nothing to pull back: it says what it caught, the PC says what it kept, and only what the PC
/// confirms is cleared from the outbox. An interrupted transfer costs a retry, never a capture.
///
/// Everything after the greeting is encrypted with the secret both devices took from the pairing
/// code, so being on the same Wi-Fi is worth nothing to anyone watching it.
@MainActor
@Observable
final class SyncClient {
    enum Status: Equatable {
        case idle
        case connecting(String)
        case sending(Int, Int)
        case finished(String)
        case failed(String)
    }

    private(set) var status: Status = .idle

    static let shared = SyncClient()

    private init() {}

    /// Pushes everything pending to `host`, on the port Campus listens on.
    func send(
        to host: String,
        port: UInt16 = SyncProtocol.port,
        outbox: Outbox = .shared
    ) async {
        status = .connecting(host)

        do {
            let channel = try await Channel.connect(host: host, port: port)
            await exchange(over: channel, outbox: outbox, closing: true)
        } catch let failure as Channel.Failure {
            status = .failed(failure.message)
        } catch {
            status = .failed(error.localizedDescription)
        }
    }

    /// Runs the exchange over a connection that already exists.
    ///
    /// Split out from `send(to:)` because over the cable the PC dials the phone rather than the
    /// other way round — usbmux only carries connections in that direction. Who opened the socket
    /// makes no difference to what is said over it: the phone still greets first, still proves it
    /// holds the pairing secret, and still encrypts everything after the greeting. The cable is a
    /// wire, not a permission.
    func exchange(
        over channel: Channel,
        outbox: Outbox = .shared,
        closing: Bool = true
    ) async {
        defer { if closing { channel.close() } }

        guard let computer = Pairing.load() else {
            status = .failed("This phone is not paired with a computer yet.")
            return
        }

        let pending = outbox.pending
        guard !pending.isEmpty else {
            status = .finished("Nothing to send.")
            return
        }

        guard let keyData = Data(base64Encoded: computer.key), keyData.count == 32 else {
            status = .failed("The pairing key on this phone is not usable. Pair again.")
            return
        }

        let key = SymmetricKey(data: keyData)
        let deviceId = Device.identifier

        do {
            // ---- the greeting, in the clear: it carries no content, only proof of pairing
            let nonce = Pairing.makeNonce()
            guard let signature = Pairing.sign(nonce: nonce, key: computer.key) else {
                status = .failed("This phone could not sign the greeting.")
                return
            }

            try await channel.writeJSON(SyncProtocol.Hello(
                deviceId: deviceId,
                deviceName: Device.name,
                nonce: nonce,
                signature: signature))

            let ack: SyncProtocol.HelloAck = try await channel.readJSON()
            guard ack.accepted else {
                status = .failed(ack.reason ?? "That computer refused the connection.")
                return
            }

            // ---- the captures, encrypted
            status = .sending(0, pending.count)
            try await channel.writeSealed(
                SyncProtocol.Push(items: pending), key: key, deviceId: deviceId)

            // Attachments follow in the order their captures were listed. The PC reads them in
            // that order, so one missing file would misalign every file after it — a capture
            // whose attachment has gone is sent as an empty one rather than skipped.
            var sent = 0
            for item in pending {
                guard let attachment = item.attachment else { continue }

                let url = outbox.attachmentsURL.appendingPathComponent(attachment)
                let bytes = (try? Data(contentsOf: url)) ?? Data()

                try await channel.writeSealedData(bytes, key: key, deviceId: deviceId)

                sent += 1
                status = .sending(sent, pending.count)
            }

            // ---- what the PC actually kept
            let result: SyncProtocol.PushAck = try await channel.readSealed(
                key: key, deviceId: deviceId)

            outbox.markSynced(ids: Set(result.acceptedIds))

            var message = "Sent \(result.acceptedIds.count) to \(ack.workspaceName ?? "Campus")."
            if !result.rejected.isEmpty {
                message += " \(result.rejected.count) could not be stored and are still here."
            }

            status = .finished(message)
        } catch let failure as Channel.Failure {
            status = .failed(failure.message)
        } catch {
            status = .failed(error.localizedDescription)
        }
    }
}

/// This device, as the PC knows it.
enum Device {
    /// Stable for as long as the app is installed, which is what pairing is tied to.
    static var identifier: String {
        if let stored = UserDefaults.standard.string(forKey: "com.campus.pocket.deviceId") {
            return stored
        }

        let made = CaptureItem.makeIdentifier()
        UserDefaults.standard.set(made, forKey: "com.campus.pocket.deviceId")
        return made
    }

    static var name: String {
        #if canImport(UIKit)
        UIDevice.current.name
        #else
        "iPhone"
        #endif
    }
}

/// A connection to the PC, and the framing on top of it.
///
/// Length-prefixed frames over TCP, because attachments are binary and there is no byte that
/// cannot appear inside one. Network.framework rather than URLSession: this is a socket to a
/// machine on the same network, not a request to a server.
///
/// Marked unchecked-Sendable deliberately. `NWConnection` is safe to use from several queues —
/// that is what its own queue parameter is for — but it is not annotated as such, and pretending
/// otherwise here is more honest than scattering isolation around a class that owns exactly one
/// socket and hands out nothing but `Data`.
final class Channel: @unchecked Sendable {
    struct Failure: Error {
        let message: String
    }

    private let connection: NWConnection

    private init(connection: NWConnection) {
        self.connection = connection
    }

    /// Wraps a connection that arrived rather than one that was dialled.
    static func accepted(_ connection: NWConnection) -> Channel {
        Channel(connection: connection)
    }

    static func connect(host: String, port: UInt16) async throws -> Channel {
        guard let port = NWEndpoint.Port(rawValue: port) else {
            throw Failure(message: "That port is not usable.")
        }

        let connection = NWConnection(host: NWEndpoint.Host(host), port: port, using: .tcp)
        let once = Once()

        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            // The state handler is called several times on the way to ready, and exactly once is
            // allowed to resume: resuming a continuation twice is a crash, not a warning.
            connection.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    once.run { continuation.resume() }
                case .waiting(let error):
                    once.run {
                        continuation.resume(throwing: Failure(
                            message: "Could not reach \(host): \(error.localizedDescription)"))
                    }
                case .failed(let error):
                    once.run {
                        continuation.resume(throwing: Failure(
                            message: "Could not reach \(host): \(error.localizedDescription)"))
                    }
                case .cancelled:
                    once.run {
                        continuation.resume(throwing: Failure(message: "The connection was cancelled."))
                    }
                default:
                    break
                }
            }

            connection.start(queue: .global(qos: .userInitiated))
        }

        return Channel(connection: connection)
    }

    func close() {
        connection.stateUpdateHandler = nil
        connection.cancel()
    }

    // MARK: Frames

    func write(_ payload: Data) async throws {
        var header = Data(count: 4)
        let length = UInt32(payload.count).bigEndian
        withUnsafeBytes(of: length) { header.replaceSubrange(0..<4, with: $0) }

        try await send(header + payload)
    }

    func read(limit: Int = 512 * 1024 * 1024) async throws -> Data {
        let header = try await receive(exactly: 4)
        let length = Int(header.withUnsafeBytes { $0.loadUnaligned(as: UInt32.self) }.bigEndian)

        guard length >= 0, length <= limit else {
            throw Failure(message: "That computer sent a message of an implausible size.")
        }

        return length == 0 ? Data() : try await receive(exactly: length)
    }

    func writeJSON<T: Encodable>(_ value: T) async throws {
        try await write(try Self.encoder.encode(value))
    }

    func readJSON<T: Decodable>() async throws -> T {
        try Self.decoder.decode(T.self, from: try await read())
    }

    // MARK: Sealed frames

    /// AES-GCM, laid out as [nonce][tag][ciphertext] to match what Campus writes and reads.
    static func seal(_ plaintext: Data, key: SymmetricKey, deviceId: String) throws -> Data {
        let box = try AES.GCM.seal(plaintext, using: key, authenticating: Data(deviceId.utf8))
        return Data(box.nonce) + box.tag + box.ciphertext
    }

    static func open(_ envelope: Data, key: SymmetricKey, deviceId: String) throws -> Data {
        guard envelope.count >= 28 else {
            throw Failure(message: "That reply was too short to be genuine.")
        }

        let nonce = try AES.GCM.Nonce(data: envelope.prefix(12))
        let tag = envelope.dropFirst(12).prefix(16)
        let ciphertext = envelope.dropFirst(28)

        let box = try AES.GCM.SealedBox(nonce: nonce, ciphertext: ciphertext, tag: tag)
        return try AES.GCM.open(box, using: key, authenticating: Data(deviceId.utf8))
    }

    func writeSealed<T: Encodable>(_ value: T, key: SymmetricKey, deviceId: String) async throws {
        try await write(try Self.seal(try Self.encoder.encode(value), key: key, deviceId: deviceId))
    }

    func writeSealedData(_ data: Data, key: SymmetricKey, deviceId: String) async throws {
        try await write(try Self.seal(data, key: key, deviceId: deviceId))
    }

    func readSealed<T: Decodable>(key: SymmetricKey, deviceId: String) async throws -> T {
        let plaintext = try Self.open(try await read(), key: key, deviceId: deviceId)
        return try Self.decoder.decode(T.self, from: plaintext)
    }

    // MARK: Primitives

    private func send(_ data: Data) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            connection.send(content: data, completion: .contentProcessed { error in
                if let error {
                    continuation.resume(throwing: Failure(message: error.localizedDescription))
                } else {
                    continuation.resume()
                }
            })
        }
    }

    /// Reads exactly this many bytes. TCP hands over whatever has arrived, which is rarely a
    /// whole message, so a frame is assembled rather than assumed.
    private func receive(exactly count: Int) async throws -> Data {
        var collected = Data()

        while collected.count < count {
            let remaining = count - collected.count

            let chunk: Data = try await withCheckedThrowingContinuation { continuation in
                connection.receive(
                    minimumIncompleteLength: 1,
                    maximumLength: remaining
                ) { data, _, isComplete, error in
                    if let error {
                        continuation.resume(throwing: Failure(message: error.localizedDescription))
                    } else if let data, !data.isEmpty {
                        continuation.resume(returning: data)
                    } else if isComplete {
                        continuation.resume(throwing: Failure(
                            message: "The computer closed the connection early."))
                    } else {
                        continuation.resume(returning: Data())
                    }
                }
            }

            guard !chunk.isEmpty else {
                throw Failure(message: "The computer stopped sending.")
            }

            collected.append(chunk)
        }

        return collected
    }

    private static var encoder: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }

    private static var decoder: JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }
}

/// Runs a block once, whatever calls it and from wherever.
///
/// Needed because a connection reports several states on the way to being ready, and a checked
/// continuation resumed twice takes the process with it.
/// A latch, so a continuation is resumed exactly once. Shared with the cable listener, which has
/// the same problem: a connection reports several states on its way to being usable, and resuming
/// twice is a crash rather than a warning.
final class Once: @unchecked Sendable {
    private let lock = NSLock()
    private var done = false

    func run(_ body: () -> Void) {
        lock.lock()
        defer { lock.unlock() }

        guard !done else { return }
        done = true
        body()
    }
}
