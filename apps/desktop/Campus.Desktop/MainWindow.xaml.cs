using Campus.Desktop.Design;
using Microsoft.UI.Input;
using Campus.Desktop.Shell;
using Campus.Desktop.Views;
using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Campus.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly IReadOnlyList<ShellDestination> _destinations = ShellDestinations.CreateDefault();
    private readonly ThemeService _theme;
    private double _sidebarWidth = 280;
    private double _inspectorWidth = 300;

    public MainWindow(string? startDestination = null)
    {
        InitializeComponent();

        _theme = App.GetService<ThemeService>();
        _theme.ThemeChanged += (_, _) => UpdateStatusBar();

        Title = "Campus";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Campus.ico"));

        // The system still draws minimise, maximise and close over the right-hand end of the
        // title bar, so our own actions are held clear of whatever width those take — which
        // changes with DPI and with the language.
        KeepClearOfCaptionButtons();
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange) KeepClearOfCaptionButtons();
        };

        Rail.Destinations = _destinations;
        Rail.DestinationInvoked += OnDestinationInvoked;

        RootLayout.KeyDown += OnRootKeyDown;

        Navigate(startDestination == "gallery" || _destinations.Any(d => d.Id == startDestination)
            ? startDestination!
            : ShellDestinations.Home);
        UpdateStatusBar();
    }

    private void KeepClearOfCaptionButtons()
    {
        var inset = AppWindow.TitleBar.RightInset;
        var scale = RootLayout.XamlRoot?.RasterizationScale ?? 1.0;
        TitleActions.Margin = new Thickness(0, 0, (inset / scale) + 8, 0);
    }

    /// <summary>The destination currently showing in the workspace.</summary>
    public string CurrentDestination { get; private set; } = ShellDestinations.Home;

    private void OnDestinationInvoked(object? sender, string id) => Navigate(id);

    private void Navigate(string id)
    {
        CurrentDestination = id;
        Rail.Select(id);

        var destination = _destinations.FirstOrDefault(d => d.Id == id);
        SidebarTitle.Text = destination?.Title.ToUpperInvariant() ?? string.Empty;

        // Only the pages that exist are wired up; the rest land on a placeholder that names
        // what will live there rather than showing an empty frame.
        switch (id)
        {
            // Reachable directly so the whole theme can be reviewed without walking the UI.
            case "gallery":
                ContentFrame.Navigate(typeof(ThemeGalleryPage));
                SidebarTitle.Text = "THEME";
                Rail.Select(ShellDestinations.Settings);
                break;
            case ShellDestinations.Settings:
                // Navigated rather than assigned, so Settings can push the theme gallery and
                // the gallery's back button has somewhere to go.
                ContentFrame.Navigate(typeof(SettingsPage));
                break;
            default:
                ContentFrame.Content = new PlaceholderPage(destination?.Title ?? id, destination?.Symbol ?? "file.unknown");
                break;
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (!ctrl) return;

        // Ctrl+1..9 jump straight to the first nine destinations, the way the rail's tooltips say.
        if (e.Key is >= VirtualKey.Number1 and <= VirtualKey.Number9)
        {
            var index = e.Key - VirtualKey.Number1;
            var main = _destinations.Where(d => d.Placement == DestinationPlacement.Main).ToList();
            if (index < main.Count)
            {
                Navigate(main[index].Id);
                e.Handled = true;
            }
            return;
        }

        if (shift && e.Key == VirtualKey.L)
        {
            LockWorkspace();
            e.Handled = true;
        }
    }

    private void OnCommandPaletteClick(object sender, RoutedEventArgs e)
    {
        // The palette lands with the command registry; the button is present so the shortcut
        // and the affordance ship together rather than one before the other.
    }

    private void OnQuickCaptureClick(object sender, RoutedEventArgs e)
    {
    }

    private void OnLockClick(object sender, RoutedEventArgs e) => LockWorkspace();

    private void LockWorkspace()
    {
        App.GetService<Vault.CampusVault>().Lock();
        UpdateStatusBar();
    }

    private void OnInspectorCloseClick(object sender, RoutedEventArgs e) => SetInspectorVisible(false);

    /// <summary>Shows or hides the inspector, keeping its divider in step.</summary>
    public void SetInspectorVisible(bool visible)
    {
        Inspector.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InspectorGrip.Visibility = Inspector.Visibility;
        InspectorColumn.Width = new GridLength(visible ? _inspectorWidth : 0);
    }

    private void OnSidebarResize(object? sender, double delta)
    {
        _sidebarWidth = Math.Clamp(_sidebarWidth + delta, 200, 480);
        SidebarColumn.Width = new GridLength(_sidebarWidth);
    }

    private void OnInspectorResize(object? sender, double delta)
    {
        // The inspector grows leftwards, so a rightward drag makes it narrower.
        _inspectorWidth = Math.Clamp(_inspectorWidth - delta, 240, 460);
        InspectorColumn.Width = new GridLength(_inspectorWidth);
    }

    private void UpdateStatusBar()
    {
        var vault = App.GetService<Vault.CampusVault>();
        VaultStatus.Text = vault.IsUnlocked ? "Vault unlocked"
            : vault.IsInitialised ? "Vault locked" : "No vault yet";

        AppearanceStatus.Text = _theme.Appearance switch
        {
            AppearanceMode.Light => "Light",
            AppearanceMode.Dark => "Dark",
            _ => _theme.IsDark ? "System · Dark" : "System · Light",
        };
    }
}
