using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Services;

public enum NoticeKind { Info, Success, Warning, Error }

/// <summary>
/// Short messages about things that already happened — a file exported, an import finished, a
/// pack installed.
///
/// Deliberately in-app rather than a Windows toast. A workspace whose whole point is that nothing
/// leaves the machine should not be putting the names of its files into the notification centre.
/// </summary>
public static class Notifications
{
    private static Panel? _host;
    private static DispatcherQueue? _dispatcher;

    /// <summary>Called once by the shell, with the panel notices should appear in.</summary>
    public static void Attach(Panel host, DispatcherQueue dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
    }

    public static void Show(string message, NoticeKind kind = NoticeKind.Info)
    {
        if (_host is null || _dispatcher is null) return;
        _dispatcher.TryEnqueue(() => Present(message, kind));
    }

    private static void Present(string message, NoticeKind kind)
    {
        if (_host is null) return;

        var (symbol, role) = kind switch
        {
            NoticeKind.Success => (CampusSymbols.Success, ThemeTokens.Success.Primary),
            NoticeKind.Warning => (CampusSymbols.Warning, ThemeTokens.Warning.Primary),
            NoticeKind.Error => (CampusSymbols.Error, ThemeTokens.Destructive.Primary),
            _ => (CampusSymbols.Info, ThemeTokens.Label.Secondary),
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new CampusIcon
        {
            Symbol = symbol,
            IconSize = 18,
            Foreground = (Brush)Application.Current.Resources[role],
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = message,
            MaxWidth = 420,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Primary],
        });

        var notice = new Border
        {
            Background = (Brush)Application.Current.Resources[ThemeTokens.Surface.Elevated],
            BorderBrush = (Brush)Application.Current.Resources[ThemeTokens.Separator.Standard],
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.Card"],
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 6, 0, 0),
            Child = content,
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 24),
        };

        AutomationProperties.SetLiveSetting(notice, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetName(notice, message);

        _host.Children.Add(notice);

        // Errors stay long enough to be read twice; everything else gets out of the way.
        var timer = _dispatcher!.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(kind == NoticeKind.Error ? 8 : 4);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => _host.Children.Remove(notice);
        timer.Start();
    }
}
