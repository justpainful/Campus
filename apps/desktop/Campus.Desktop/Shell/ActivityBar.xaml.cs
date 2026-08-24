using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Shell;

/// <summary>
/// The left rail. It owns nothing but selection; choosing a destination raises an event and the
/// shell decides what that means.
/// </summary>
public sealed partial class ActivityBar : UserControl
{
    private IReadOnlyList<ShellDestination> _destinations = [];

    public ActivityBar()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the user picks a destination, with its id.</summary>
    public event EventHandler<string>? DestinationInvoked;

    public IReadOnlyList<ShellDestination> Destinations
    {
        get => _destinations;
        set
        {
            _destinations = value;
            MainItems.ItemsSource = value.Where(d => d.Placement == DestinationPlacement.Main).ToList();
            BottomItems.ItemsSource = value.Where(d => d.Placement == DestinationPlacement.Bottom).ToList();
        }
    }

    /// <summary>Marks one destination selected and clears the rest.</summary>
    public void Select(string id)
    {
        foreach (var destination in _destinations)
            destination.IsSelected = string.Equals(destination.Id, id, StringComparison.Ordinal);
    }

    private void OnDestinationClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
            DestinationInvoked?.Invoke(this, id);
    }

    /// <summary>Badges above 99 read as "99+" so the pill never grows wider than the rail.</summary>
    public static string BadgeText(int count) => count > 99 ? "99+" : count.ToString();

    public static Visibility BadgeVisibility(int count)
        => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility SelectionVisibility(bool selected)
        => selected ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The selected destination reads at full label strength; the rest sit back at secondary,
    /// so the rail has one focal point rather than sixteen equally loud icons.
    /// </summary>
    public static Brush IconBrush(bool selected)
        => (Brush)Application.Current.Resources[
            selected ? ThemeTokens.Label.Primary : ThemeTokens.Label.Secondary];

    /// <summary>Selection also thickens the stroke, so it survives greyscale and high contrast.</summary>
    public static IconWeight IconWeight(bool selected)
        => selected ? Design.Icons.IconWeight.Semibold : Design.Icons.IconWeight.Regular;
}
