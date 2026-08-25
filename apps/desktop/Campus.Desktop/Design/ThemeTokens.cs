namespace Campus.Desktop.Design;

/// <summary>
/// Every semantic colour role, as a resource key. Code that needs a colour asks for a role from
/// here; it never writes a literal value and never guesses a key name.
/// </summary>
public static class ThemeTokens
{
    public static class Background
    {
        public const string Primary = "Theme.Background.Primary";
        public const string Secondary = "Theme.Background.Secondary";
        public const string Tertiary = "Theme.Background.Tertiary";
    }

    public static class GroupedBackground
    {
        public const string Primary = "Theme.GroupedBackground.Primary";
        public const string Secondary = "Theme.GroupedBackground.Secondary";
        public const string Tertiary = "Theme.GroupedBackground.Tertiary";
    }

    public static class Surface
    {
        public const string Primary = "Theme.Surface.Primary";
        public const string Secondary = "Theme.Surface.Secondary";
        public const string Tertiary = "Theme.Surface.Tertiary";
        public const string Elevated = "Theme.Surface.Elevated";
    }

    public static class Label
    {
        public const string Primary = "Theme.Label.Primary";
        public const string Secondary = "Theme.Label.Secondary";
        public const string Tertiary = "Theme.Label.Tertiary";
        public const string Quaternary = "Theme.Label.Quaternary";
        public const string OnAccent = "Theme.Label.OnAccent";
    }

    public static class Fill
    {
        public const string Primary = "Theme.Fill.Primary";
        public const string Secondary = "Theme.Fill.Secondary";
        public const string Tertiary = "Theme.Fill.Tertiary";
        public const string Quaternary = "Theme.Fill.Quaternary";
    }

    /// <summary>
    /// The two colours that do not follow the theme, for things a camera reads rather than a
    /// person: dark modules on a light field, in that order, in every appearance.
    /// </summary>
    public static class Machine
    {
        public const string Ink = "Theme.Machine.Ink";
        public const string Paper = "Theme.Machine.Paper";
    }

    public static class Separator
    {
        public const string Standard = "Theme.Separator.Standard";
        public const string Opaque = "Theme.Separator.Opaque";
    }

    public static class Accent
    {
        public const string Primary = "Theme.Accent.Primary";
        public const string Hover = "Theme.Accent.Hover";
        public const string Pressed = "Theme.Accent.Pressed";
        public const string Disabled = "Theme.Accent.Disabled";
        public const string Subtle = "Theme.Accent.Subtle";
    }

    public static class Destructive
    {
        public const string Primary = "Theme.Destructive.Primary";
        public const string Hover = "Theme.Destructive.Hover";
        public const string Pressed = "Theme.Destructive.Pressed";
        public const string Subtle = "Theme.Destructive.Subtle";
    }

    public static class Success
    {
        public const string Primary = "Theme.Success.Primary";
        public const string Subtle = "Theme.Success.Subtle";
    }

    public static class Warning
    {
        public const string Primary = "Theme.Warning.Primary";
        public const string Subtle = "Theme.Warning.Subtle";
    }

    public static class Info
    {
        public const string Primary = "Theme.Info.Primary";
        public const string Subtle = "Theme.Info.Subtle";
    }

    public static class State
    {
        public const string DisabledLabel = "Theme.Disabled.Label";
        public const string DisabledFill = "Theme.Disabled.Fill";
        public const string SelectedFill = "Theme.Selected.Fill";
        public const string SelectedRail = "Theme.Selected.Rail";
        public const string FocusRing = "Theme.Focused.Ring";
        public const string FocusRingOuter = "Theme.Focused.RingOuter";
        public const string Scrim = "Theme.Scrim";
    }

    /// <summary>Named subject accents. Subjects store the name, never a colour value.</summary>
    public static class Subject
    {
        public const string Blue = "Theme.Subject.Blue";
        public const string Teal = "Theme.Subject.Teal";
        public const string Green = "Theme.Subject.Green";
        public const string Indigo = "Theme.Subject.Indigo";
        public const string Orange = "Theme.Subject.Orange";
        public const string Pink = "Theme.Subject.Pink";
        public const string Purple = "Theme.Subject.Purple";
        public const string Graphite = "Theme.Subject.Graphite";

        public static readonly string[] All =
            [Blue, Teal, Green, Indigo, Orange, Pink, Purple, Graphite];

        /// <summary>Resolves a stored short name such as "Green" to its token.</summary>
        public static string FromName(string? name) => name switch
        {
            "Blue" => Blue,
            "Teal" => Teal,
            "Green" => Green,
            "Indigo" => Indigo,
            "Orange" => Orange,
            "Pink" => Pink,
            "Purple" => Purple,
            "Graphite" => Graphite,
            _ => Graphite,
        };

        public static string ToName(string token) => token[(token.LastIndexOf('.') + 1)..];
    }

    /// <summary>Highlight colours for annotations, again by name rather than value.</summary>
    public static class Highlight
    {
        public const string Yellow = "Theme.Highlight.Yellow";
        public const string Green = "Theme.Highlight.Green";
        public const string Blue = "Theme.Highlight.Blue";
        public const string Pink = "Theme.Highlight.Pink";
        public const string Purple = "Theme.Highlight.Purple";

        public static readonly string[] All = [Yellow, Green, Blue, Pink, Purple];

        public static string FromName(string? name) => name switch
        {
            "Green" => Green,
            "Blue" => Blue,
            "Pink" => Pink,
            "Purple" => Purple,
            _ => Yellow,
        };
    }
}
