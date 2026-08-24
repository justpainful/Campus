using Campus.Domain;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Campus.Desktop.Design;

/// <summary>
/// The one place a semantic role turns into an actual colour. It takes the role, the resolved
/// appearance, the accessibility settings and the control state, and returns the final value —
/// so no component ever performs that reasoning itself.
/// </summary>
public sealed class ThemeResolver(ThemeService theme)
{
    private readonly ThemeService _theme = theme;

    /// <summary>Resolves a role to a brush from the active theme dictionary.</summary>
    public Brush Brush(string token)
    {
        if (Application.Current.Resources.TryGetValue(token, out var value) && value is Brush brush)
            return brush;
        // A missing role is a bug in the theme, not something to paper over with a guessed colour.
        throw new KeyNotFoundException($"No theme role named '{token}'.");
    }

    /// <summary>Resolves a role to a colour, for code that draws rather than composes.</summary>
    public Color Color(string token)
    {
        var colourKey = ToColourKey(token);
        if (Application.Current.Resources.TryGetValue(colourKey, out var value) && value is Color colour)
            return colour;
        if (Application.Current.Resources.TryGetValue(token, out var brushValue) && brushValue is SolidColorBrush brush)
            return brush.Color;
        throw new KeyNotFoundException($"No theme role named '{token}'.");
    }

    private static string ToColourKey(string token)
        => token.StartsWith("Theme.Color.", StringComparison.Ordinal)
            ? token
            : "Theme.Color." + token["Theme.".Length..];

    /// <summary>
    /// Resolves the fill for an interactive surface given its state. Callers pass the role they
    /// want at rest and get the correct hover / pressed / disabled variant back.
    /// </summary>
    public Brush InteractiveFill(InteractionRole role, ControlState state) => (role, state) switch
    {
        (_, ControlState.Disabled) => Brush(ThemeTokens.State.DisabledFill),

        (InteractionRole.Accent, ControlState.Rest) => Brush(ThemeTokens.Accent.Primary),
        (InteractionRole.Accent, ControlState.Hover) => Brush(ThemeTokens.Accent.Hover),
        (InteractionRole.Accent, ControlState.Pressed) => Brush(ThemeTokens.Accent.Pressed),

        (InteractionRole.Destructive, ControlState.Rest) => Brush(ThemeTokens.Destructive.Primary),
        (InteractionRole.Destructive, ControlState.Hover) => Brush(ThemeTokens.Destructive.Hover),
        (InteractionRole.Destructive, ControlState.Pressed) => Brush(ThemeTokens.Destructive.Pressed),

        (InteractionRole.Neutral, ControlState.Rest) => Brush(ThemeTokens.Fill.Tertiary),
        (InteractionRole.Neutral, ControlState.Hover) => Brush(ThemeTokens.Fill.Secondary),
        (InteractionRole.Neutral, ControlState.Pressed) => Brush(ThemeTokens.Fill.Primary),

        (InteractionRole.Plain, ControlState.Rest) => new SolidColorBrush(Colors.Transparent),
        (InteractionRole.Plain, ControlState.Hover) => Brush(ThemeTokens.Fill.Quaternary),
        (InteractionRole.Plain, ControlState.Pressed) => Brush(ThemeTokens.Fill.Tertiary),

        (InteractionRole.Selected, _) => Brush(ThemeTokens.State.SelectedFill),

        _ => new SolidColorBrush(Colors.Transparent),
    };

    /// <summary>Foreground that belongs on top of <see cref="InteractiveFill"/> for the same role.</summary>
    public Brush InteractiveLabel(InteractionRole role, ControlState state) => (role, state) switch
    {
        (_, ControlState.Disabled) => Brush(ThemeTokens.State.DisabledLabel),
        (InteractionRole.Accent, _) => Brush(ThemeTokens.Label.OnAccent),
        (InteractionRole.Destructive, _) => Brush(ThemeTokens.Label.OnAccent),
        (InteractionRole.Selected, _) => Brush(ThemeTokens.Label.Primary),
        _ => Brush(ThemeTokens.Label.Primary),
    };

    /// <summary>
    /// Separator visibility. Hierarchy normally comes from surface level and spacing, so a
    /// separator is only drawn where content genuinely abuts — unless the user has asked for
    /// more contrast, in which case separators become an explicit aid.
    /// </summary>
    public Brush Separator(bool betweenAdjacentRows)
    {
        if (_theme.Accessibility.IncreaseContrast) return Brush(ThemeTokens.Separator.Opaque);
        return betweenAdjacentRows ? Brush(ThemeTokens.Separator.Standard) : Brush(ThemeTokens.Separator.Standard);
    }

    /// <summary>Motion duration, collapsed to zero when the user has asked to reduce motion.</summary>
    public TimeSpan Duration(MotionSpeed speed)
    {
        if (_theme.Accessibility.ReduceMotion) return TimeSpan.Zero;
        var ms = speed switch
        {
            MotionSpeed.Fast => 120,
            MotionSpeed.Normal => 200,
            MotionSpeed.Slow => 320,
            _ => 0,
        };
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>Shadow depth, suppressed entirely under high contrast where it only adds mud.</summary>
    public double ShadowDepth(ElevationRole role)
    {
        if (_theme.Accessibility.IncreaseContrast) return 0;
        return role switch
        {
            ElevationRole.Flyout => 16,
            ElevationRole.Dialog => 32,
            ElevationRole.Dragged => 24,
            _ => 0,
        };
    }

    /// <summary>The background a page should paint, decided by what kind of page it is.</summary>
    public Brush PageBackground(PageKind kind) => kind switch
    {
        PageKind.Grouped => Brush(ThemeTokens.GroupedBackground.Primary),
        _ => Brush(ThemeTokens.Background.Primary),
    };

    /// <summary>The surface a section within a page should paint.</summary>
    public Brush SectionSurface(PageKind kind) => kind switch
    {
        PageKind.Grouped => Brush(ThemeTokens.GroupedBackground.Secondary),
        _ => Brush(ThemeTokens.Surface.Primary),
    };
}

public enum InteractionRole
{
    /// <summary>Transparent at rest — toolbar and list-row buttons.</summary>
    Plain = 0,
    /// <summary>A visible neutral control: secondary buttons, segmented controls.</summary>
    Neutral = 1,
    /// <summary>The primary action on a surface.</summary>
    Accent = 2,
    /// <summary>Delete and other irreversible actions.</summary>
    Destructive = 3,
    Selected = 4,
}

public enum ControlState { Rest = 0, Hover = 1, Pressed = 2, Disabled = 3 }

public enum MotionSpeed { Instant = 0, Fast = 1, Normal = 2, Slow = 3 }

public enum ElevationRole { Flat = 0, Flyout = 1, Dialog = 2, Dragged = 3 }

/// <summary>
/// Standard pages paint content edge to edge; grouped pages sit on the grouped canvas with
/// sections floating on it, the way Settings does.
/// </summary>
public enum PageKind { Standard = 0, Grouped = 1 }
