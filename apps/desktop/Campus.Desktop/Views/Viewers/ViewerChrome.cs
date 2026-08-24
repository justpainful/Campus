using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Views.Viewers;

/// <summary>
/// The small controls every viewer's toolbar is made of. They live here so that a button in the
/// PDF reader and a button in the image viewer are the same size, the same colour, and reachable
/// by the same keyboard and screen-reader names.
/// </summary>
internal static class ViewerChrome
{
    public static Brush Brush(string token) => (Brush)Application.Current.Resources[token];

    public static Button ToolButton(string symbol, string tooltip, Action action)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Icon"],
            Content = Icon(symbol),
        };

        AutomationProperties.SetName(button, tooltip);
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>A button that stays pressed, for the things that are on or off.</summary>
    public static ToggleButton ToolToggle(string symbol, string tooltip, bool isOn, Action<bool> changed)
    {
        var button = new ToggleButton
        {
            Style = (Style)Application.Current.Resources["Toggle.Icon"],
            Content = Icon(symbol),
            IsChecked = isOn,
        };

        AutomationProperties.SetName(button, tooltip);
        ToolTipService.SetToolTip(button, tooltip);
        button.Checked += (_, _) => changed(true);
        button.Unchecked += (_, _) => changed(false);
        return button;
    }

    /// <summary>
    /// A button that opens a short list of choices — playback speed, a sheet to look at. A menu
    /// rather than a row of buttons keeps the toolbar from turning into a control panel.
    /// </summary>
    public static Button ToolMenu(
        string symbol,
        string tooltip,
        IEnumerable<(string Label, Action Invoke)> items,
        string? initialLabel = null)
    {
        var label = new TextBlock
        {
            Text = initialLabel ?? "",
            Style = (Style)Application.Current.Resources["Text.Caption"],
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = initialLabel is null ? Visibility.Collapsed : Visibility.Visible,
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(Icon(symbol));
        content.Children.Add(label);

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Bottom };
        foreach (var (text, invoke) in items)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) =>
            {
                label.Text = text;
                label.Visibility = Visibility.Visible;
                invoke();
            };
            flyout.Items.Add(item);
        }

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Icon"],
            Content = content,
            Flyout = flyout,
        };

        AutomationProperties.SetName(button, tooltip);
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    public static TextBlock ToolLabel(string text = "")
    {
        return new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.Resources["Text.Caption"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 6, 0),
        };
    }

    public static CampusIcon Icon(string symbol, double size = 18, string? token = null) => new()
    {
        Symbol = symbol,
        IconSize = size,
        Foreground = Brush(token ?? ThemeTokens.Label.Secondary),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// What a viewer shows while it is still reading a large file. Deliberately quiet: a
    /// spinner over an empty page, not a progress bar pretending to know how long it will take.
    /// </summary>
    public static StackPanel Busy(string message)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12,
        };

        panel.Children.Add(new ProgressRing
        {
            IsActive = true,
            Width = 28,
            Height = 28,
            Foreground = Brush(ThemeTokens.Label.Tertiary),
        });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            Style = (Style)Application.Current.Resources["Text.Caption"],
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        return panel;
    }
}
