using Campus.Desktop.Design;
using Campus.Desktop.Design.Emoji;
using Campus.Desktop.Services;
using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

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
        DyslexiaToggle.IsOn = a11y.DyslexiaFriendlyReading;
        RulerToggle.IsOn = a11y.ReadingRuler;

        // The sliders are in percent because that is the unit people think in; the model keeps
        // the multiplier, because that is the unit the layout thinks in.
        TextScaleSlider.Value = Math.Clamp(a11y.TextScale * 100, 80, 180);
        UiScaleSlider.Value = Math.Clamp(a11y.UiScale * 100, 80, 140);
        LineSpacingSlider.Value = Math.Clamp(a11y.ReadingLineSpacing * 100, 100, 200);

        var backup = _settings.Backup;
        BackupToggle.IsOn = backup.Automatic;
        BackupCadenceChoice.SelectedIndex = (int)backup.Cadence;
        BackupFolderRow.Subtitle = backup.Destination ?? BackupService.DefaultFolder;
        BackupNowRow.Subtitle = DescribeLastBackup();

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

        EmojiPackRow.Subtitle = DescribePack(store.Active);
    }

    /// <summary>
    /// Says what a pack covers and what it does not. A pack built from an older font is missing
    /// whatever Unicode has added since, and saying so is more useful than a bare total.
    /// </summary>
    private static string DescribePack(EmojiPack? pack)
    {
        if (pack is null) return "No pack selected.";

        var missing = pack.Manifest.Missing;
        return missing > 0
            ? $"{pack.Manifest.Count} emoji · {missing} newer than this font"
            : $"{pack.Manifest.Count} emoji";
    }

    private void OnEmojiPackChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (EmojiPackChoice.SelectedItem is not ComboBoxItem { Tag: string id }) return;

        var store = EmojiPackStore.Current;
        store.Select(id);
        _emojiPreferences.PackId = id;

        EmojiPackRow.Subtitle = DescribePack(store.Active);
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
        ApplyAccessibility();
    }

    private void OnScaleChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        ApplyAccessibility();
    }

    private void ApplyAccessibility()
    {
        var a11y = _settings.Accessibility;

        a11y.IncreaseContrast = ContrastToggle.IsOn;
        a11y.ReduceMotion = MotionToggle.IsOn;
        a11y.ReduceTransparency = TransparencyToggle.IsOn;
        a11y.LargeHitTargets = HitTargetToggle.IsOn;
        a11y.AlwaysShowFocusRing = FocusRingToggle.IsOn;
        a11y.DyslexiaFriendlyReading = DyslexiaToggle.IsOn;
        a11y.ReadingRuler = RulerToggle.IsOn;
        a11y.TextScale = TextScaleSlider.Value / 100;
        a11y.UiScale = UiScaleSlider.Value / 100;
        a11y.ReadingLineSpacing = LineSpacingSlider.Value / 100;

        _theme.ApplySettings(a11y);

        // Sizes are resolved when a page is built, so the page showing has to be built again for
        // a scale change to be visible. Re-navigating is how that happens without a restart.
        Frame?.Navigate(typeof(SettingsPage));
    }

    /// <summary>
    /// Windows owns the mouse pointer, and no app can change its size. Opening the setting that
    /// can is more use than a switch here that quietly does nothing.
    /// </summary>
    private async void OnPointerSettingsClick(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:easeofaccess-cursorandpointersize"));

    // -------------------------------------------------------------------------- backup

    private string DescribeLastBackup()
    {
        var backups = BackupService.List(_settings.Backup.Destination);

        return backups.Count == 0
            ? "No backups yet"
            : $"Most recent {BoardPage.Ago(backups[0].CreatedAt)} · "
              + ViewModels.ObjectItem.FormatSize(backups[0].SizeBytes);
    }

    private void OnBackupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.Backup.Automatic = BackupToggle.IsOn;
        _settings.Backup.Cadence = (BackupCadence)Math.Max(0, BackupCadenceChoice.SelectedIndex);
    }

    private async void OnBackupFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        _settings.Backup.Destination = folder.Path;
        BackupFolderRow.Subtitle = folder.Path;
    }

    private async void OnBackupNowClick(object sender, RoutedEventArgs e)
    {
        if (!_workspace.IsUnlocked) return;

        BackupNowRow.Subtitle = "Backing up…";

        try
        {
            var backup = await App.GetService<BackupService>()
                .CreateAsync(_settings.Backup.Destination);

            BackupNowRow.Subtitle = backup is null
                ? "Nothing was backed up."
                : $"Backed up · {ViewModels.ObjectItem.FormatSize(backup.SizeBytes)}";

            Notifications.Show("Backed up. It needs your recovery key to open.", NoticeKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException)
        {
            BackupNowRow.Subtitle = ex.Message;
            Notifications.Show($"The backup failed: {ex.Message}", NoticeKind.Error);
        }
    }

    /// <summary>
    /// Unpacks a backup beside the live workspace rather than over it. Swapping one in is a
    /// deliberate act with the app closed, and the dialog says exactly where the copy landed.
    /// </summary>
    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".campusbackup");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var manifest = await BackupService.ReadManifestAsync(file.Path);
        if (manifest is null)
        {
            Notifications.Show("That is not a Campus backup.", NoticeKind.Error);
            return;
        }

        if (!await ObjectCommands.ConfirmAsync(XamlRoot,
            "Unpack this backup?",
            $"Taken {manifest.CreatedAt.ToLocalTime():f} on {manifest.Device}. It will be unpacked "
            + "into a folder of its own — your current workspace is not touched. Opening it needs "
            + "the recovery key from the machine it came from.",
            "Unpack")) return;

        try
        {
            var folder = Path.GetDirectoryName(file.Path)!;
            var target = await BackupService.RestoreAsync(file.Path, folder);

            Notifications.Show(target is null
                ? "That backup could not be unpacked."
                : $"Unpacked to {target}.",
                target is null ? NoticeKind.Error : NoticeKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notifications.Show($"The restore failed: {ex.Message}", NoticeKind.Error);
        }
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
