using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Localization;

/// <summary>
/// Swaps the interface's faces for ones that have Arabic in them.
///
/// Segoe UI Variable — the family the whole design system is set in — has no Arabic coverage at
/// all. Left alone, Windows substitutes something per run of text, which means a heading and the
/// sentence under it can end up in different typefaces, at different apparent sizes, with
/// different vertical alignment, and nobody chose any of it.
///
/// So when the interface is Arabic, the named faces are pointed at a stack that was drawn for it.
/// Segoe UI is first because it ships on every Windows machine and its Arabic is good; the two
/// after it are for machines where it has been stripped. The Latin in these faces is also
/// perfectly reasonable, which matters — an Arabic interface still says Wi-Fi and Ctrl+G.
/// </summary>
public static class ArabicTypography
{
    /// <summary>The faces that carry prose, and are therefore the ones that have to change.</summary>
    private static readonly string[] Roles =
    [
        "Theme.Font.Display",
        "Theme.Font.Text",
        "Theme.Font.Small",
        "Theme.Font.Reading",
    ];

    /// <summary>What was there before, so switching back is a restore rather than a guess.</summary>
    private static readonly Dictionary<string, FontFamily> Original = new(StringComparer.Ordinal);

    public static void Apply(bool arabic)
    {
        var resources = Application.Current.Resources;

        foreach (var role in Roles)
        {
            if (!Original.ContainsKey(role)
                && resources.TryGetValue(role, out var value) && value is FontFamily face)
            {
                Original[role] = face;
            }
        }

        // Not touched: Theme.Font.Mono, because code and a recovery key should stay monospaced in
        // any language, and Theme.Font.Emoji, which is artwork rather than a typeface.
        foreach (var role in Roles)
        {
            if (!arabic)
            {
                if (Original.TryGetValue(role, out var previous)) resources[role] = previous;
                continue;
            }

            resources[role] = role == "Theme.Font.Reading"
                // Prose gets the wider of the two, for the same reason the Latin reading face is
                // wider than the Latin interface face.
                ? new FontFamily("Segoe UI, Tahoma, Arial")
                : new FontFamily("Segoe UI, Segoe UI Variable Text, Tahoma, Arial");
        }
    }
}
