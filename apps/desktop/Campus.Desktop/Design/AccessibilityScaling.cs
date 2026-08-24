using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Design;

/// <summary>
/// Makes the accessibility preferences actually change the interface.
///
/// Campus draws everything from a small set of named sizes, so scaling the interface is a matter
/// of overriding those names rather than of touching a thousand controls. Overrides are written
/// into the application's own resources, where they shadow the values in the design system: a page
/// built after that resolves the scaled numbers, so re-opening what you are looking at applies the
/// change without a restart.
///
/// The one thing not done here is the mouse pointer. Only Windows can change its size, and an app
/// that pretends otherwise by drawing its own cursor over half the screen is worse than one that
/// says so and opens the right settings page.
/// </summary>
public static class AccessibilityScaling
{
    /// <summary>The sizes that scale with the text-size preference.</summary>
    private static readonly string[] TextSizes =
    [
        "Theme.Text.LargeTitle", "Theme.Text.Title1", "Theme.Text.Title2", "Theme.Text.Title3",
        "Theme.Text.Headline", "Theme.Text.Body", "Theme.Text.Callout", "Theme.Text.Subheadline",
        "Theme.Text.Footnote", "Theme.Text.Caption1", "Theme.Text.Caption2",
        "Theme.Leading.LargeTitle", "Theme.Leading.Title1", "Theme.Leading.Title2",
        "Theme.Leading.Title3", "Theme.Leading.Headline", "Theme.Leading.Body",
        "Theme.Leading.Callout", "Theme.Leading.Caption", "Theme.Leading.Footnote",
    ];

    /// <summary>The sizes that scale with the interface-size preference.</summary>
    private static readonly string[] LayoutSizes =
    [
        "Theme.Icon.XS", "Theme.Icon.S", "Theme.Icon.M", "Theme.Icon.L", "Theme.Icon.XL",
        "Theme.Icon.XXL", "Theme.Icon.Display", "Theme.Icon.Hero",
        "Theme.Size.ActivityBarWidth", "Theme.Size.ActivityItem", "Theme.Size.TabHeight",
        "Theme.Size.StatusBarHeight", "Theme.Size.TitleBarHeight",
    ];

    /// <summary>The sizes a person's fingers have to hit.</summary>
    private static readonly string[] TargetSizes =
    [
        "Theme.Size.ControlHeightCompact", "Theme.Size.ControlHeight",
        "Theme.Size.ControlHeightComfortable", "Theme.Size.TouchTarget",
        "Theme.Size.RowHeight", "Theme.Size.RowHeightCompact",
    ];

    private static readonly string[] Durations =
    [
        "Theme.Motion.Instant", "Theme.Motion.Fast",
        "Theme.Motion.Normal", "Theme.Motion.Slow",
    ];

    /// <summary>The original values, captured once so scaling is never applied to a scaled value.</summary>
    private static readonly Dictionary<string, double> Original = new(StringComparer.Ordinal);

    public static void Apply(AccessibilitySettings settings)
    {
        var resources = Application.Current.Resources;

        Capture(resources, TextSizes);
        Capture(resources, LayoutSizes);
        Capture(resources, TargetSizes);
        Capture(resources, Durations);

        var text = Math.Clamp(settings.TextScale, 0.75, 2.0);
        var ui = Math.Clamp(settings.UiScale, 0.75, 1.5);

        foreach (var key in TextSizes) Scale(resources, key, text);
        foreach (var key in LayoutSizes) Scale(resources, key, ui);

        // Large hit targets are a floor rather than a multiplier: the point is that nothing is
        // smaller than a finger, not that everything doubles.
        foreach (var key in TargetSizes)
        {
            var value = Original.GetValueOrDefault(key, 32) * ui;
            resources[key] = settings.LargeHitTargets ? Math.Max(value, 44) : value;
        }

        // Reduced motion means no motion. A shorter animation is still an animation, and the
        // people who ask for this are asking because movement makes them ill.
        foreach (var key in Durations)
            resources[key] = settings.ReduceMotion ? 0d : Original.GetValueOrDefault(key, 200);

        Application.Current.FocusVisualKind = settings.AlwaysShowFocusRing
            ? FocusVisualKind.Reveal
            : FocusVisualKind.HighVisibility;

        ApplyReading(resources, settings);
    }

    /// <summary>
    /// The reading preferences, which apply to prose rather than to the interface: a wider,
    /// plainer face and more space between lines make a page of a textbook readable for people
    /// the default typography defeats.
    /// </summary>
    private static void ApplyReading(ResourceDictionary resources, AccessibilitySettings settings)
    {
        resources["Theme.Font.Reading"] = settings.DyslexiaFriendlyReading
            // Verdana and Tahoma are wide, open and on every Windows machine. Shipping a
            // dyslexia-specific font would be better still, and is a licensing question rather
            // than a technical one.
            ? new FontFamily("Verdana, Tahoma, Segoe UI")
            : new FontFamily("Georgia, Cambria, Segoe UI");

        resources["Theme.Reading.LineSpacing"] = Math.Clamp(settings.ReadingLineSpacing, 1.0, 2.0);
    }

    /// <summary>The line height a reading surface should use, given the current preference.</summary>
    public static double ReadingLineHeight(double fontSize)
    {
        var multiplier = Application.Current.Resources.TryGetValue(
            "Theme.Reading.LineSpacing", out var value) && value is double stored
            ? stored
            : 1.0;

        return fontSize * 1.55 * multiplier;
    }

    private static void Capture(ResourceDictionary resources, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (Original.ContainsKey(key)) continue;
            if (resources.TryGetValue(key, out var value) && value is double number)
                Original[key] = number;
        }
    }

    private static void Scale(ResourceDictionary resources, string key, double factor)
    {
        if (!Original.TryGetValue(key, out var original)) return;
        resources[key] = Math.Round(original * factor, 2);
    }
}
