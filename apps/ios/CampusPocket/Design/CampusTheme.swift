import SwiftUI

/// The same semantic colour system the desktop uses, expressed in SwiftUI.
///
/// iOS already ships most of these as system colours, and using them rather than redefining the
/// palette is the point: it means Campus Pocket follows Dynamic Type, Increase Contrast, Smart
/// Invert and Dark Mode without any code of its own. The two values that are genuinely Campus's
/// — the true-black canvas and the accent — are the only ones stated outright.
enum CampusTheme {

    // MARK: Background

    /// True black, matching the desktop's canvas. Not the system grouped background, which is
    /// #1C1C1E in dark mode and would make the app read a shade lighter than its other half.
    static let backgroundPrimary = Color(
        light: Color(white: 1.0),
        dark: Color(white: 0.0))

    static let backgroundSecondary = Color(uiColor: .secondarySystemBackground)
    static let groupedBackground = Color(uiColor: .systemGroupedBackground)

    // MARK: Surface

    static let surfacePrimary = Color(
        light: Color(white: 1.0),
        dark: Color(red: 0.110, green: 0.110, blue: 0.118))     // #1C1C1E

    static let surfaceSecondary = Color(
        light: Color(red: 0.949, green: 0.949, blue: 0.969),    // #F2F2F7
        dark: Color(red: 0.173, green: 0.173, blue: 0.180))     // #2C2C2E

    static let surfaceTertiary = Color(
        light: Color(white: 1.0),
        dark: Color(red: 0.227, green: 0.227, blue: 0.235))     // #3A3A3C

    // MARK: Label

    static let labelPrimary = Color(uiColor: .label)
    static let labelSecondary = Color(uiColor: .secondaryLabel)
    static let labelTertiary = Color(uiColor: .tertiaryLabel)
    static let labelQuaternary = Color(uiColor: .quaternaryLabel)

    // MARK: Fill

    static let fillPrimary = Color(uiColor: .systemFill)
    static let fillSecondary = Color(uiColor: .secondarySystemFill)
    static let fillTertiary = Color(uiColor: .tertiarySystemFill)
    static let fillQuaternary = Color(uiColor: .quaternarySystemFill)

    static let separator = Color(uiColor: .separator)
    static let separatorOpaque = Color(uiColor: .opaqueSeparator)

    // MARK: State

    /// The one accent. Blue acts, red destroys, amber warns, green confirms — the same rule the
    /// desktop follows, so the two halves never disagree about what a colour means.
    static let accent = Color(uiColor: .systemBlue)
    static let destructive = Color(uiColor: .systemRed)
    static let warning = Color(uiColor: .systemOrange)
    static let success = Color(uiColor: .systemGreen)

    // MARK: Metrics

    enum Radius {
        static let control: CGFloat = 10
        static let card: CGFloat = 14
        static let sheet: CGFloat = 20
    }

    enum Space {
        static let xs: CGFloat = 4
        static let s: CGFloat = 8
        static let m: CGFloat = 12
        static let l: CGFloat = 16
        static let xl: CGFloat = 24
    }
}

private extension Color {
    /// Builds a colour that resolves per appearance, so nothing has to branch on the mode.
    init(light: Color, dark: Color) {
        self.init(uiColor: UIColor { traits in
            traits.userInterfaceStyle == .dark ? UIColor(dark) : UIColor(light)
        })
    }
}

/// A grouped card, matching the desktop's Surface.Card role.
struct CampusCard: ViewModifier {
    func body(content: Content) -> some View {
        content
            .padding(CampusTheme.Space.l)
            .background(CampusTheme.surfacePrimary)
            .clipShape(RoundedRectangle(cornerRadius: CampusTheme.Radius.card, style: .continuous))
    }
}

extension View {
    func campusCard() -> some View { modifier(CampusCard()) }
}
