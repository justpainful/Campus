import SwiftUI

@main
struct CampusPocketApp: App {
    @Environment(\.scenePhase) private var phase

    @State private var outbox = Outbox.shared
    @State private var cable = CableListener.shared

    var body: some Scene {
        WindowGroup {
            CaptureView()
                .environment(outbox)
                .environment(cable)
                .tint(CampusTheme.accent)
                // Dark first, matching the desktop's identity, while still honouring a light
                // system setting.
                .background(CampusTheme.backgroundPrimary)
        }
        // The cable listener is tied to the app being on screen rather than to anything the user
        // switches on. iOS takes the socket back shortly after the app is put down, so a toggle
        // would be a promise the system does not let the app keep — and a phone that is plugged
        // in with Campus Pocket open is exactly the moment the PC wants to reach it.
        .onChange(of: phase) { _, now in
            switch now {
            case .active:
                cable.start()
            case .background, .inactive:
                cable.stop()
            @unknown default:
                cable.stop()
            }
        }
    }
}
