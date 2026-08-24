using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Design;

/// <summary>Collapses an element when its text is null or empty.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Shows an element when the bound value is true. Pass "Invert" to reverse it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>
/// Picks a label role by state: destructive rows read red, everything else primary. Kept as a
/// converter so the row template never names a colour.
/// </summary>
public sealed class DestructiveLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var destructive = value is bool b && b;
        return Application.Current.Resources[
            destructive ? ThemeTokens.Destructive.Primary : ThemeTokens.Label.Primary];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Resolves a named theme role to its brush, for data that stores a name rather than a colour.</summary>
public sealed class ThemeRoleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var token = value as string;
        if (string.IsNullOrEmpty(token)) return Application.Current.Resources[ThemeTokens.Label.Primary];
        if (!token.StartsWith("Theme.", StringComparison.Ordinal))
            token = ThemeTokens.Subject.FromName(token);
        return Application.Current.Resources.TryGetValue(token, out var brush)
            ? brush
            : Application.Current.Resources[ThemeTokens.Label.Primary];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
