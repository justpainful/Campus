using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Campus.Desktop.Design.Controls;

/// <summary>
/// A grouped section, the way Settings does it: an optional quiet header, one rounded surface
/// holding the rows, hairlines only between adjacent rows, and an optional footnote underneath.
///
/// This exists so no page ever invents its own grouping treatment — and so nothing turns into a
/// page of separately floating cards.
/// </summary>
public sealed class SettingsSection : ContentControl
{
    public SettingsSection()
    {
        DefaultStyleKey = typeof(SettingsSection);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        IsTabStop = false;
    }

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingsSection), new PropertyMetadata(null));

    /// <summary>Small caps label above the group. Null hides the header row entirely.</summary>
    public string? Header
    {
        get => (string?)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
        nameof(Footer), typeof(string), typeof(SettingsSection), new PropertyMetadata(null));

    /// <summary>Explanatory text under the group, for the sentence a row cannot hold.</summary>
    public string? Footer
    {
        get => (string?)GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    public static Visibility TextVisibility(string? text)
        => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
}
