using Campus.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Design.Controls;

/// <summary>
/// A band that follows the pointer across a page of prose.
///
/// It exists for people who lose their line in a wall of text — a common enough experience that
/// physical rulers under a line of a textbook are a normal study habit. The band is dim rather
/// than opaque and never captures the pointer, so it guides reading without getting in the way of
/// selecting, clicking or scrolling.
/// </summary>
public static class ReadingRuler
{
    /// <summary>
    /// Puts a ruler over a reading surface if the preference is on. Returns the element added,
    /// or null when the preference is off.
    /// </summary>
    public static FrameworkElement? Attach(Grid surface)
    {
        var theme = App.GetService<ThemeService>();
        if (!theme.Accessibility.ReadingRuler) return null;

        var band = new Border
        {
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Background = (Brush)Application.Current.Resources[ThemeTokens.Fill.Quaternary],
            BorderBrush = (Brush)Application.Current.Resources[ThemeTokens.Separator.Standard],
            BorderThickness = new Thickness(0, 1, 0, 1),
            Opacity = 0.55,

            // The whole point is that it does not interfere: it is a guide drawn over the text,
            // not a surface between the reader and it.
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        Grid.SetColumnSpan(band, Math.Max(1, surface.ColumnDefinitions.Count));
        Grid.SetRowSpan(band, Math.Max(1, surface.RowDefinitions.Count));
        surface.Children.Add(band);

        void Follow(object sender, PointerRoutedEventArgs e)
        {
            var y = e.GetCurrentPoint(surface).Position.Y;
            band.Margin = new Thickness(0, Math.Max(0, y - band.Height / 2), 0, 0);
            band.Visibility = Visibility.Visible;
        }

        surface.PointerMoved += Follow;
        surface.PointerExited += (_, _) => band.Visibility = Visibility.Collapsed;

        return band;
    }
}
