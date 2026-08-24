using Campus.Desktop.Design;
using Campus.Desktop.Design.Emoji;
using Campus.Desktop.Services;
using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Campus.Desktop.Views;

public sealed partial class SettingsPage : Page
{
    private readonly ThemeService _theme = App.GetService<ThemeService>();
    private readonly WorkspaceSettings _settings = App.GetService<WorkspaceSettings>();
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();
    private readonly EmojiPreferences _emojiPreferences = EmojiPreferences.Load();
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        Load();
        Loaded += (_, _) => { _loading = false; };
    }

    private void Load()
    {
        (_settings.Appearance switch
        {
            AppearanceMode.Light => AppearanceLight,
            AppearanceMode.Dark => AppearanceDark,
            _ => AppearanceSystem,
        }).IsChecked = true;

        var a11y = _settings.Accessibility;
        ContrastToggle.IsOn = a11y.IncreaseContrast;
        MotionToggle.IsOn = a11y.ReduceMotion;
        TransparencyToggle.IsOn = a11y.ReduceTransparency;
        HitTargetToggle.IsOn = a11y.LargeHitTargets;
        FocusRingToggle.IsOn = a11y.AlwaysShowFocusRing;

        // A preference the system already enforces cannot be turned off from here, so the switch
        // is shown on and disabled rather than lying about the state.
        if (_theme.SystemHighContrast) { ContrastToggle.IsOn = true; ContrastToggle.IsEnabled = false; }
        if (!_theme.SystemAnimationsEnabled) { MotionToggle.IsOn = true; MotionToggle.IsEnabled = false; }
        if (!_theme.SystemTransparencyEnabled) { TransparencyToggle.IsOn = true; TransparencyToggle.IsEnabled = false; }

        AutoLockChoice.SelectedIndex = _settings.AutoLock switch
        {
            AutoLockPolicy.After5Minutes => 1,
            AutoLockPolicy.After10Minutes => 2,
            AutoLockPolicy.After30Minutes => 3,
            _ => 0,
        };

        LoadEmojiPacks();

        VersionText.Text = typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        VaultPathText.Text = _workspace.Paths.Root;
        _ = UpdateHelloRowAsync();
    }

    private void LoadEmojiPacks()
    {
        var store = EmojiPackStore.Current;
        store.Refresh();

        EmojiPackChoice.Items.Clear();
        foreach (var pack in store.Packs)
        {
            EmojiPackChoice.Items.Add(new ComboBoxItem
            {
                Content = $"{pack.DisplayName} ({pack.Manifest.Count})",
                Tag = pack.Id,
            });
        }

        if (store.Packs.Count == 0)
        {
            EmojiPackChoice.IsEnabled = false;
            EmojiPackRow.Subtitle = "No pack installed. Emoji will not render until one is.";
            return;
        }

        EmojiPackChoice.IsEnabled = true;
        EmojiPackChoice.SelectedIndex = Math.Max(0,
            store.Packs.ToList().FindIndex(p => p.Id == store.Active?.Id));

        var active = store.Active;
        EmojiPackRow.Subtitle = active is null
            ? "No pack selected."
            : $"{active.Manifest.Count} emoji · {active.Manifest.License}";
    }

    private void OnEmojiPackChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (EmojiPackChoice.SelectedItem is not ComboBoxItem { Tag: string id }) return;

        var store = EmojiPackStore.Current;
        store.Select(id);
        _emojiPreferences.PackId = id;

        var active = store.Active;
        if (active is not null)
            EmojiPackRow.Subtitle = $"{active.Manifest.Count} emoji · {active.Manifest.License}";
    }

    private async void OnOpenPacksFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(EmojiPackStore.UserRoot);
        await Windows.System.Launcher.LaunchFolderPathAsync(EmojiPackStore.UserRoot);
    }

    private async Task UpdateHelloRowAsync()
    {
        var enrolled = await _workspace.IsHelloAvailableAsync();
        HelloRow.Subtitle = enrolled
            ? "Enrolled. Your face, fingerprint or PIN unlocks this workspace."
            : "Not set up. This workspace opens with its recovery key.";
        HelloButton.Content = enrolled ? "Re-enrol" : "Set up";
    }

    private void OnAppearanceChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not RadioButton { Tag: string tag }) return;

        _settings.Appearance = tag switch
        {
            "Light" => AppearanceMode.Light,
            "Dark" => AppearanceMode.Dark,
            _ => AppearanceMode.System,
        };
        _theme.Appearance = _settings.Appearance;
    }

    private void OnAccessibilityToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var a11y = _settings.Accessibility;
        a11y.IncreaseContrast = ContrastToggle.IsOn;
        a11y.ReduceMotion = MotionToggle.IsOn;
        a11y.ReduceTransparency = TransparencyToggle.IsOn;
        a11y.LargeHitTargets = HitTargetToggle.IsOn;
        a11y.AlwaysShowFocusRing = FocusRingToggle.IsOn;
        _theme.ApplySettings(a11y);
    }

    private void OnOpenGalleryClick(object sender, RoutedEventArgs e)
        => Frame?.Navigate(typeof(ThemeGalleryPage));

    private async void OnHelloClick(object sender, RoutedEventArgs e)
    {
        HelloButton.IsEnabled = false;
        try
        {
            if (!await _workspace.EnrolHelloAsync())
            {
                HelloRow.Subtitle = "Windows Hello is not available on this PC.";
                return;
            }
            await UpdateHelloRowAsync();
        }
        finally
        {
            HelloButton.IsEnabled = true;
        }
    }

    private void OnAutoLockChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (AutoLockChoice.SelectedItem is not ComboBoxItem { Tag: string tag }) return;

        _settings.AutoLock = tag switch
        {
            "5" => AutoLockPolicy.After5Minutes,
            "10" => AutoLockPolicy.After10Minutes,
            "30" => AutoLockPolicy.After30Minutes,
            _ => AutoLockPolicy.Never,
        };
        _workspace.StartAutoLock(DispatcherQueue);
    }

    private void OnLockNowClick(object sender, RoutedEventArgs e) => _workspace.Lock();
}
