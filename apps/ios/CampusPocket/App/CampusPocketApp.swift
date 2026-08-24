import SwiftUI

@main
struct CampusPocketApp: App {
    @State private var outbox = Outbox.shared

    var body: some Scene {
        WindowGroup {
            CaptureView()
                .environment(outbox)
                .tint(CampusTheme.accent)
                // Dark first, matching the desktop's identity, while still honouring a light
                // system setting.
                .background(CampusTheme.backgroundPrimary)
        }
    }
}
