using Campus.Domain;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Campus.Desktop.Design;

/// <summary>
/// What changes when somebody asks for more contrast, or for less transparency.
///
/// Both preferences are about the same thing from opposite directions: a surface that is
/// translucent is a surface whose contrast depends on what happens to be behind it. Campus draws
/// separators as a translucent hairline and fills as a wash over the background, which is right
/// when everything is legible and wrong when it is not.
///
/// So when either preference is on, the translucent roles are replaced with opaque ones derived
/// from the theme's own colours. Nothing here invents a palette — it flattens the one already
/// defined, which is why it works in light, dark and high contrast without a second set of values.
/// </summary>
public static class ContrastAdaptation
{
    /// <summary>The roles that are normally translucent, and what they flatten onto.</summary>
    private static readonly (string Role, string Behind, double Weight)[] Translucent =
    [
        ("Theme.Separator.Standard", "Theme.Background.Primary", 1.0),
        ("Theme.Fill.Primary", "Theme.Background.Primary", 1.0),
        ("Theme.Fill.Secondary", "Theme.Background.Primary", 1.0),
        ("Theme.Fill.Tertiary", "Theme.Background.Primary", 1.0),
        ("Theme.Fill.Quaternary", "Theme.Background.Primary", 1.0),
        ("Theme.Selected.Fill", "Theme.Background.Primary", 1.0),
    ];

    private static readonly Dictionary<string, Brush> Original = new(StringComparer.Ordinal);

    /// <summary>
    /// Applies the preferences to the live resource dictionary. Called whenever they change or
    /// the theme does, because a colour flattened against the light background is wrong the
    /// moment the window turns dark.
    /// </summary>
    public static void Apply(AccessibilitySettings settings)
    {
        var resources = Application.Current.Resources;
        var flatten = settings.IncreaseContrast || settings.ReduceTransparency;

        Capture(resources);

        foreach (var (role, behind, weight) in Translucent)
        {
            if (!Original.TryGetValue(role, out var original)) continue;

            if (!flatten)
            {
                resources[role] = original;
                continue;
            }

            if (original is not SolidColorBrush brush) continue;
            if (resources[behind] is not SolidColorBrush background) continue;

            var flat = Flatten(brush.Color, background.Color, weight);

            // Separators earn a little more than a flattening when contrast is asked for: the
            // point of the preference is that a hairline should be visible, not merely opaque.
            if (settings.IncreaseContrast && role == "Theme.Separator.Standard"
                && resources["Theme.Separator.Opaque"] is SolidColorBrush opaque)
            {
                flat = opaque.Color;
            }

            resources[role] = new SolidColorBrush(flat);
        }
    }

    private static void Capture(ResourceDictionary resources)
    {
        foreach (var (role, _, _) in Translucent)
        {
            if (Original.ContainsKey(role)) continue;
            if (resources.TryGetValue(role, out var value) && value is Brush brush)
                Original[role] = brush;
        }
    }

    /// <summary>
    /// Composites a translucent colour over an opaque one, which is what the compositor was
    /// going to do anyway — done here so the result is a colour rather than a dependency on
    /// whatever happens to be behind it.
    /// </summary>
    private static Color Flatten(Color foreground, Color background, double weight)
    {
        var alpha = Math.Clamp(foreground.A / 255.0 * weight, 0, 1);

        return ColorHelper.FromArgb(
            255,
            Mix(foreground.R, background.R, alpha),
            Mix(foreground.G, background.G, alpha),
            Mix(foreground.B, background.B, alpha));
    }

    private static byte Mix(byte foreground, byte background, double alpha)
        => (byte)Math.Round(foreground * alpha + background * (1 - alpha));
}
