using Campus.Desktop.Design;
using Microsoft.UI.Input;
using Campus.Desktop.Services;
using Campus.Desktop.Shell;
using Campus.Desktop.ViewModels;
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
    private readonly WorkspaceService _workspace;
    private double _sidebarWidth = 280;
    private CommandRegistry _commands = new();
    private readonly ShellRouter _router;
    private bool _sidebarVisible = true;
    private bool _focusMode;
    private double _inspectorWidth = 300;
    private bool _sidebarWasVisible = true;
    private bool _inspectorWasVisible;

    public MainWindow(string? startDestination = null)
    {
        InitializeComponent();

        _theme = App.GetService<ThemeService>();
        _theme.ThemeChanged += (_, _) => UpdateStatusBar();

        _workspace = App.GetService<WorkspaceService>();
        // Unlock finishes on a background thread, so the UI change is marshalled rather than
        // applied wherever the continuation happened to land.
        _workspace.LockStateChanged += (_, unlocked) =>
            DispatcherQueue.TryEnqueue(() => ApplyLockState(unlocked));

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

        Notifications.Attach(NoticeHost, DispatcherQueue);

        // Pages ask the shell to open things rather than reaching for the frame themselves.
        _router = App.GetService<ShellRouter>();
        _router.NavigationRequested += (_, request) =>
            DispatcherQueue.TryEnqueue(() => Handle(request));

        RootLayout.KeyDown += OnRootKeyDown;

        Navigate(startDestination == "gallery" || _destinations.Any(d => d.Id == startDestination)
            ? startDestination!
            : ShellDestinations.Home);

        // Any pointer or key anywhere in the window counts as activity, which is what auto-lock
        // measures idleness against.
        RootLayout.PointerMoved += (_, _) => _workspace.NoteActivity();
        RootLayout.PointerPressed += (_, _) => _workspace.NoteActivity();
        RootLayout.KeyDown += (_, _) => _workspace.NoteActivity();
        _workspace.StartAutoLock(DispatcherQueue);

        _commands = CommandRegistry.CreateDefault(this, _workspace);
        Palette.Initialise(_commands, _workspace);

        // A workspace full of invented content must never be mistaken for the real one.
        if (DeveloperWorkspace.Requested)
        {
            SampleBadge.Visibility = Visibility.Visible;
            WorkspaceLabel.Text = "Sample workspace";
        }

        ApplyLockState(_workspace.IsUnlocked);
        UpdateStatusBar();

#if DEBUG
        // Deferred to Loaded: a dialog needs a XamlRoot, which does not exist yet in the
        // constructor.
        RootLayout.Loaded += (_, _) => ShowDebugSurface();
#endif
    }

    private void KeepClearOfCaptionButtons()
    {
        var inset = AppWindow.TitleBar.RightInset;
        var scale = RootLayout.XamlRoot?.RasterizationScale ?? 1.0;
        TitleActions.Margin = new Thickness(0, 0, (inset / scale) + 8, 0);
    }

    /// <summary>The destination currently showing in the workspace.</summary>
    public string CurrentDestination { get; private set; } = ShellDestinations.Home;

#if DEBUG
    /// <summary>Opens a transient surface named by --dev-show, for development screenshots.</summary>
    private void ShowDebugSurface()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(args, a => a == "--dev-show");
        if (index < 0 || index + 1 >= args.Length) return;

        var surface = args[index + 1];
        // One more hop, so the workspace has had a chance to unlock before a surface that
        // reads from it appears.
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(1200);
            switch (surface)
            {
                case "palette": Palette.Show(PaletteMode.Commands); break;
                case "search": Palette.Show(PaletteMode.Search); break;
                case "inspector": SetInspectorVisible(true); break;
                case "focus": ToggleFocusMode(); break;
                case "emoji": _ = ShowEmojiSheetAsync(); break;
                case "detail": _ = OpenFirstObjectAsync(); break;
            }
        });
    }
#endif

#if DEBUG
    /// <summary>Opens whatever the current list holds first, for development screenshots.</summary>
    private async Task OpenFirstObjectAsync()
    {
        if (!_workspace.IsUnlocked) return;

        var first = (await _workspace.Objects.QueryAsync(new Campus.Domain.CampusQuery
        {
            Kinds = { ObjectKind.Assignment },
            Sort = Campus.Domain.SortField.DueAt,
            Descending = false,
            Limit = 1,
        })).FirstOrDefault();

        if (first is not null) ContentFrame.Navigate(typeof(ObjectDetailPage), first.Id);
    }
#endif

    /// <summary>Opens the emoji picker on its own, for screenshots and for the palette.</summary>
    public async Task ShowEmojiSheetAsync()
    {
        if (RootLayout.XamlRoot is not { } root) return;

        var field = new TextBox
        {
            Style = (Style)Application.Current.Resources["Input.Text"],
            PlaceholderText = "Pick an emoji",
        };

        var picker = new Design.Emoji.EmojiPicker();
        picker.EmojiPicked += (_, text) => Design.Emoji.EmojiFlyout.Insert(field, text);

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(field);
        body.Children.Add(picker);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Emoji",
            Content = body,
            CloseButtonText = "Done",
        };
        await dialog.ShowAsync();
    }

    private void OnDestinationInvoked(object? sender, string id) => Navigate(id);

    /// <summary>
    /// Carries out a request from the router. Opening an object means opening the right page for
    /// it: a file whose format Campus can draw goes to the viewer, and everything else goes to
    /// the detail page. Deciding that here, once, is what keeps a link in a note behaving the
    /// same as the same link in a search result.
    /// </summary>
    private async void Handle(NavigationRequest request)
    {
        if (request.Destination is { } destination)
        {
            Navigate(destination, request.Argument);
            return;
        }

        if (request.ObjectId is not { } id || !_workspace.IsUnlocked) return;

        var entity = await _workspace.Objects.GetAsync(id);
        if (entity is null)
        {
            Notifications.Show("That item is no longer here.", NoticeKind.Warning);
            return;
        }

        ContentFrame.Navigate(
            entity.Kind == ObjectKind.File ? typeof(Views.Viewers.ViewerHost) : typeof(ObjectDetailPage),
            id);
    }

    /// <summary>Moves the shell to a destination. Used by pages whose counts link to a list.</summary>
    public void NavigateTo(string destinationId) => Navigate(destinationId);

    private void Navigate(string id, string? argument = null)
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
            case ShellDestinations.Home:
                ContentFrame.Navigate(typeof(HomePage));
                break;
            case ShellDestinations.Subjects:
                ContentFrame.Navigate(typeof(SubjectsPage));
                break;
            case ShellDestinations.Planner:
                ContentFrame.Navigate(typeof(PlannerPage));
                break;
            case ShellDestinations.Files:
                ContentFrame.Navigate(typeof(FilesPage));
                break;
            case ShellDestinations.Goals:
                ContentFrame.Navigate(typeof(GoalsPage));
                break;
            default:
                // Most destinations are the same page over a different query.
                if (CollectionCatalog.For(id) is { } collection)
                    ContentFrame.Navigate(typeof(CollectionPage), collection);
                else
                    ContentFrame.Content = new PlaceholderPage(
                        destination?.Title ?? id, destination?.Symbol ?? "file.unknown");
                break;
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == VirtualKey.Escape && Palette.IsOpen)
        {
            Palette.Hide();
            e.Handled = true;
            return;
        }

        if (!ctrl) return;

        if (shift && e.Key == VirtualKey.P)
        {
            Palette.Show(PaletteMode.Commands);
            e.Handled = true;
            return;
        }

        if (!shift && e.Key == VirtualKey.P)
        {
            Palette.Show(PaletteMode.Search);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.B)
        {
            ToggleSidebar();
            e.Handled = true;
            return;
        }

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
            return;
        }

        var alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (alt && e.Key == VirtualKey.N)
        {
            _ = CaptureAsync();
            e.Handled = true;
        }
    }

    private void OnCommandPaletteClick(object sender, RoutedEventArgs e)
        => Palette.Show(PaletteMode.Commands);

    /// <summary>Opens the quick capture sheet. Exposed so the palette can reach it too.</summary>
    public Task QuickCaptureAsync(ObjectKind kind = ObjectKind.InboxItem) => CaptureAsync(kind);

    /// <summary>Shows or hides the sidebar. The workspace takes the space it leaves.</summary>
    public void ToggleSidebar()
    {
        _sidebarVisible = !_sidebarVisible;
        Sidebar.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        SidebarGrip.Visibility = Sidebar.Visibility;
        SidebarColumn.Width = new GridLength(_sidebarVisible ? _sidebarWidth : 0);
    }

    public void ToggleInspector() => SetInspectorVisible(Inspector.Visibility != Visibility.Visible);

    /// <summary>
    /// Focus mode strips the shell back to the thing being read: no rail, no sidebar, no
    /// inspector, no status bar. Leaving it restores whatever was showing before.
    /// </summary>
    public void ToggleFocusMode()
    {
        _focusMode = !_focusMode;

        if (_focusMode)
        {
            _sidebarWasVisible = _sidebarVisible;
            _inspectorWasVisible = Inspector.Visibility == Visibility.Visible;
            if (_sidebarVisible) ToggleSidebar();
            SetInspectorVisible(false);
            Rail.Visibility = Visibility.Collapsed;
            StatusBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            Rail.Visibility = Visibility.Visible;
            StatusBar.Visibility = Visibility.Visible;
            if (_sidebarWasVisible && !_sidebarVisible) ToggleSidebar();
            if (_inspectorWasVisible) SetInspectorVisible(true);
        }
    }

    public void SetAppearance(AppearanceMode mode)
    {
        App.GetService<WorkspaceSettings>().Appearance = mode;
        _theme.Appearance = mode;
        UpdateStatusBar();
    }

    private async void OnQuickCaptureClick(object sender, RoutedEventArgs e) => await CaptureAsync();

    private async Task CaptureAsync(ObjectKind kind = ObjectKind.InboxItem)
    {
        if (!_workspace.IsUnlocked) return;
        if (RootLayout.XamlRoot is not { } root) return;

        if (await QuickCapture.ShowAsync(root, kind) is not null) Navigate(CurrentDestination);
    }

    private void OnLockClick(object sender, RoutedEventArgs e) => LockWorkspace();

    private void LockWorkspace() => _workspace.Lock();

    /// <summary>Swaps between the workspace and the lock screen, and keeps the chrome honest.</summary>
    private void ApplyLockState(bool unlocked)
    {
        BodyLayout.Visibility = unlocked ? Visibility.Visible : Visibility.Collapsed;
        LockFrame.Visibility = unlocked ? Visibility.Collapsed : Visibility.Visible;
        TitleActions.Visibility = unlocked ? Visibility.Visible : Visibility.Collapsed;
        TitleCentre.Visibility = unlocked ? Visibility.Visible : Visibility.Collapsed;

        if (unlocked)
        {
            LockFrame.Content = null;
            _workspace.StartAutoLock(DispatcherQueue);
            // The current page was built against a locked workspace and read nothing, so it is
            // rebuilt now that there is something to read.
            Navigate(CurrentDestination);
            _ = SidebarContent.RefreshAsync();
        }
        else
        {
            LockFrame.Navigate(typeof(LockPage));
        }

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
        VaultStatus.Text = DeveloperWorkspace.Requested
            ? "Sample workspace — not your data"
            : _workspace.IsUnlocked ? "Vault unlocked"
            : _workspace.IsInitialised ? "Vault locked" : "No vault yet";

        AppearanceStatus.Text = _theme.Appearance switch
        {
            AppearanceMode.Light => "Light",
            AppearanceMode.Dark => "Dark",
            _ => _theme.IsDark ? "System · Dark" : "System · Light",
        };
    }
}
