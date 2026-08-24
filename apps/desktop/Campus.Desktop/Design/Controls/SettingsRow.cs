using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Campus.Desktop.Design.Controls;

/// <summary>
/// One row inside a <see cref="SettingsSection"/>: an optional leading icon, a title, an
/// optional subtitle, and whatever control belongs on the trailing edge.
///
/// The row draws its own hairline on top and lets the section suppress it for the first row,
/// which is what produces separators between rows rather than a border around each one.
/// </summary>
public sealed class SettingsRow : ContentControl
{
    public SettingsRow()
    {
        DefaultStyleKey = typeof(SettingsRow);
        HorizontalContentAlignment = HorizontalAlignment.Right;
        VerticalContentAlignment = VerticalAlignment.Center;
        IsTabStop = false;
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingsRow), new PropertyMetadata(string.Empty));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(SettingsRow), new PropertyMetadata(null));

    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol), typeof(string), typeof(SettingsRow), new PropertyMetadata(null));

    /// <summary>Leading icon name. Null leaves the row without one and the text aligns left.</summary>
    public string? Symbol
    {
        get => (string?)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public static readonly DependencyProperty ShowSeparatorProperty = DependencyProperty.Register(
        nameof(ShowSeparator), typeof(bool), typeof(SettingsRow), new PropertyMetadata(true));

    /// <summary>Set false on the first row of a section, where a hairline would sit on the edge.</summary>
    public bool ShowSeparator
    {
        get => (bool)GetValue(ShowSeparatorProperty);
        set => SetValue(ShowSeparatorProperty, value);
    }

    public static readonly DependencyProperty IsDestructiveProperty = DependencyProperty.Register(
        nameof(IsDestructive), typeof(bool), typeof(SettingsRow), new PropertyMetadata(false));

    /// <summary>Colours the title red. For rows whose action cannot be undone.</summary>
    public bool IsDestructive
    {
        get => (bool)GetValue(IsDestructiveProperty);
        set => SetValue(IsDestructiveProperty, value);
    }

    public static Visibility TextVisibility(string? text)
        => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility BoolVisibility(bool value)
        => value ? Visibility.Visible : Visibility.Collapsed;
}
