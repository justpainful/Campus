import SwiftUI

/// The first and usually only screen.
///
/// The whole app is built around one measurement: how long it takes to get a sentence out of your
/// head and into the workspace while a teacher is still talking. Everything else — the outbox,
/// pairing, settings — is one tap away and never in the path.
struct CaptureView: View {
    @Environment(Outbox.self) private var outbox

    @State private var composing: CaptureKind?
    @State private var showingOutbox = false
    @State private var showingSettings = false
    @State private var showingScanner = false

    private let columns = [GridItem(.adaptive(minimum: 150), spacing: CampusTheme.Space.m)]

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: CampusTheme.Space.xl) {
                    header

                    LazyVGrid(columns: columns, spacing: CampusTheme.Space.m) {
                        ForEach(CaptureKind.allCases) { kind in
                            CaptureTile(kind: kind) {
                                if kind == .photo { showingScanner = true }
                                else { composing = kind }
                            }
                        }
                    }

                    if outbox.pendingCount > 0 { waiting }
                }
                .padding(CampusTheme.Space.l)
            }
            .background(CampusTheme.backgroundPrimary)
            .navigationTitle("Capture")
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button { showingOutbox = true } label: {
                        Label("Outbox", systemImage: "tray.full")
                    }
                    .badge(outbox.pendingCount)
                }
                ToolbarItem(placement: .topBarLeading) {
                    Button { showingSettings = true } label: {
                        Label("Settings", systemImage: "gearshape")
                    }
                }
            }
            .sheet(item: $composing) { kind in
                ComposeView(kind: kind)
            }
            .sheet(isPresented: $showingOutbox) { OutboxView() }
            .sheet(isPresented: $showingSettings) { SettingsView() }
            .sheet(isPresented: $showingScanner) { ScannerView() }
            .task { openRequestedScreen() }
        }
    }

    /// Seeds the sample content and opens the screen named on the command line, for the
    /// screenshot build. Does nothing in a release build, where the type it reads does not exist.
    ///
    /// Done here rather than in the app's initialiser because the outbox belongs to the main
    /// actor, and this is the first place the app is reliably on it.
    private func openRequestedScreen() {
        #if DEBUG
        ScreenshotMode.prepare(outbox)

        switch ScreenshotMode.screen {
        case .outbox: showingOutbox = true
        case .settings: showingSettings = true
        case .compose: composing = .assignment
        case .capture, .none: break
        }
        #endif
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(greeting)
                .font(.largeTitle.weight(.semibold))
                .foregroundStyle(CampusTheme.labelPrimary)
            Text("Everything stays on this phone until your PC picks it up.")
                .font(.callout)
                .foregroundStyle(CampusTheme.labelSecondary)
        }
    }

    private var greeting: String {
        switch Calendar.current.component(.hour, from: Date()) {
        case ..<5: "Still up"
        case ..<12: "Good morning"
        case ..<17: "Good afternoon"
        default: "Good evening"
        }
    }

    private var waiting: some View {
        Button { showingOutbox = true } label: {
            HStack(spacing: CampusTheme.Space.m) {
                Image(systemName: "arrow.up.circle")
                    .font(.title2)
                    .foregroundStyle(CampusTheme.accent)
                VStack(alignment: .leading, spacing: 2) {
                    Text("\(outbox.pendingCount) waiting for Campus")
                        .font(.body)
                        .foregroundStyle(CampusTheme.labelPrimary)
                    Text("Connect to your PC or open it on the same Wi-Fi")
                        .font(.footnote)
                        .foregroundStyle(CampusTheme.labelSecondary)
                }
                Spacer()
                Image(systemName: "chevron.right")
                    .font(.footnote)
                    .foregroundStyle(CampusTheme.labelTertiary)
            }
            .campusCard()
        }
        .buttonStyle(.plain)
    }
}

/// One capture tile. Deliberately large: this is pressed with a thumb, in a hurry, one-handed.
private struct CaptureTile: View {
    let kind: CaptureKind
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: CampusTheme.Space.s) {
                Image(systemName: kind.symbol)
                    .font(.title2)
                    .foregroundStyle(CampusTheme.labelSecondary)
                Text(kind.title)
                    .font(.headline)
                    .foregroundStyle(CampusTheme.labelPrimary)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(CampusTheme.Space.l)
            .frame(minHeight: 96)
            .background(CampusTheme.surfacePrimary)
            .clipShape(RoundedRectangle(cornerRadius: CampusTheme.Radius.card, style: .continuous))
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Capture \(kind.title.lowercased())")
    }
}

#Preview {
    CaptureView().environment(Outbox.shared)
}
