import Foundation
import Network
import Observation

/// Answers the PC when it calls down the cable.
///
/// Over Wi-Fi the phone dials the PC, because the phone is the one that knows it has something to
/// send. Over USB it cannot: the tunnel Windows opens through Apple's device service only carries
/// connections in one direction, host to device. So the phone listens, and the PC dials.
///
/// Nothing else changes. The phone still speaks first, still signs a nonce with the pairing
/// secret, and still encrypts every message after the greeting — a cable is a wire, not a
/// permission, and a computer that never paired gets exactly as far down it as one on the wrong
/// Wi-Fi network.
///
/// The listener runs only while the app is on screen. That is a real limitation and it is stated
/// in the interface rather than worked around: iOS gives a foreground app a socket and takes it
/// away again shortly after the app is put down, and pretending otherwise would mean a sync that
/// works when you are watching and silently does not when you are not.
@Observable
@MainActor
final class CableListener {
    enum State: Equatable {
        case off
        case listening
        case talking
        case failed(String)
    }

    private(set) var state: State = .off

    /// What happened on the last connection, so the sync screen can say so.
    private(set) var lastResult: String?

    static let shared = CableListener()

    private var listener: NWListener?

    private init() {}

    // MARK: Lifecycle

    func start() {
        guard listener == nil else { return }
        guard Pairing.load() != nil else {
            // Nothing to answer with. Listening anyway would mean accepting connections only to
            // refuse them, which looks like a fault rather than like "not paired yet".
            state = .off
            return
        }

        do {
            let parameters = NWParameters.tcp
            // The tunnel surfaces on the device's own loopback, so reuse has to be allowed:
            // a previous connection in TIME_WAIT would otherwise hold the port.
            parameters.allowLocalEndpointReuse = true

            guard let port = NWEndpoint.Port(rawValue: SyncProtocol.port) else {
                state = .failed("That port is not usable.")
                return
            }

            let listener = try NWListener(using: parameters, on: port)

            listener.stateUpdateHandler = { [weak self] update in
                Task { @MainActor in
                    switch update {
                    case .ready:
                        self?.state = .listening
                    case .failed(let error):
                        self?.state = .failed(error.localizedDescription)
                        self?.stop()
                    case .cancelled:
                        self?.state = .off
                    default:
                        break
                    }
                }
            }

            listener.newConnectionHandler = { [weak self] connection in
                Task { @MainActor in await self?.answer(connection) }
            }

            listener.start(queue: .global(qos: .userInitiated))
            self.listener = listener
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    func stop() {
        listener?.stateUpdateHandler = nil
        listener?.newConnectionHandler = nil
        listener?.cancel()
        listener = nil
        state = .off
    }

    // MARK: One conversation

    private func answer(_ connection: NWConnection) async {
        state = .talking

        // The connection arrives before it is usable; the exchange must not start until it is
        // ready or the first write goes nowhere.
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            let once = Once()

            connection.stateUpdateHandler = { update in
                switch update {
                case .ready, .failed, .cancelled:
                    once.run { continuation.resume() }
                default:
                    break
                }
            }

            connection.start(queue: .global(qos: .userInitiated))
        }

        let channel = Channel.accepted(connection)
        await SyncClient.shared.exchange(over: channel)

        switch SyncClient.shared.status {
        case .finished(let message):
            lastResult = message
        case .failed(let message):
            lastResult = message
        default:
            lastResult = nil
        }

        state = listener == nil ? .off : .listening
    }
}
