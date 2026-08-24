using Campus.Desktop.Design;
using Campus.Domain;
using Campus.Vault;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Campus.Desktop.Views;

public sealed partial class SettingsPage : Page
{
    private readonly ThemeService _theme = App.GetService<ThemeService>();
    private readonly WorkspaceSettings _settings = App.GetService<WorkspaceSettings>();
    private readonly CampusVault _vault = App.GetService<CampusVault>();
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

        // A preference the system already enforces cannot be turned off from here, so the
        // switch is shown on and disabled rather than lying about the state.
        if (_theme.SystemHighContrast) { ContrastToggle.IsOn = true; ContrastToggle.IsEnabled = false; }
        if (!_theme.SystemAnimationsEnabled) { MotionToggle.IsOn = true; MotionToggle.IsEnabled = false; }
        if (!_theme.SystemTransparencyEnabled) { TransparencyToggle.IsOn = true; TransparencyToggle.IsEnabled = false; }

        VersionText.Text = typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        VaultPathText.Text = _vault.Paths.Root;
        UpdateVaultRow();
    }

    private void UpdateVaultRow()
    {
        if (!_vault.IsInitialised)
        {
            VaultRow.Subtitle = "Not created yet";
            VaultButton.Content = "Create vault";
            VaultButton.IsEnabled = true;
        }
        else if (_vault.IsUnlocked)
        {
            VaultRow.Subtitle = "Unlocked";
            VaultButton.Content = "Locked";
            VaultButton.IsEnabled = false;
        }
        else
        {
            VaultRow.Subtitle = "Locked";
            VaultButton.Content = "Unlock";
            VaultButton.IsEnabled = true;
        }
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

    private async void OnVaultButtonClick(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsInitialised)
        {
            var recoveryKey = await _vault.CreateAsync();
            await ShowRecoveryKeyAsync(recoveryKey);
        }
        UpdateVaultRow();
    }

    /// <summary>
    /// The recovery key is shown exactly once, at creation. Campus never stores it, so this
    /// dialog insists the user has written it down before it can be dismissed.
    /// </summary>
    private async Task ShowRecoveryKeyAsync(string recoveryKey)
    {
        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(new TextBlock
        {
            Text = "Write this down and keep it somewhere safe. It is the only way back into "
                 + "your workspace if Windows Hello stops working or you move to another PC. "
                 + "Campus does not keep a copy.",
            TextWrapping = TextWrapping.Wrap,
        });

        var keyBox = new TextBox
        {
            Text = recoveryKey,
            IsReadOnly = true,
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["Theme.Font.Mono"],
            TextAlignment = TextAlignment.Center,
        };
        body.Children.Add(keyBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Your recovery key",
            Content = body,
            PrimaryButtonText = "I have written it down",
            DefaultButton = ContentDialogButton.Primary,
        };
        await dialog.ShowAsync();
    }

    private void OnLockNowClick(object sender, RoutedEventArgs e)
    {
        _vault.Lock();
        UpdateVaultRow();
    }
}
