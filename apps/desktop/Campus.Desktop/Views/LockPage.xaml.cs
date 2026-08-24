using Campus.Desktop.Services;
using Campus.Vault;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Campus.Desktop.Views;

public sealed partial class LockPage : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();
    private bool _busy;

    public LockPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_workspace.IsInitialised)
        {
            HeadlineText.Text = "Welcome to Campus";
            SubtitleText.Text = "Everything you keep here is encrypted on this device. "
                              + "Create your workspace to begin.";
            PrimaryLabel.Text = "Create workspace";
            PrimaryIcon.Symbol = "add";
            RecoveryLink.Visibility = Visibility.Collapsed;
            return;
        }

        var hasHello = await _workspace.IsHelloAvailableAsync();
        SubtitleText.Text = hasHello
            ? "Unlock to read your files, notes and assignments."
            : "Windows Hello is not set up for this workspace. Use your recovery key.";

        if (!hasHello)
        {
            PrimaryButton.Visibility = Visibility.Collapsed;
            ShowRecoveryPanel();
        }
        else
        {
            // Offer Hello immediately rather than making the user click twice to reach the
            // prompt they came here for.
            await UnlockWithHelloAsync();
        }
    }

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!_workspace.IsInitialised) await CreateWorkspaceAsync();
        else await UnlockWithHelloAsync();
    }

    private async Task CreateWorkspaceAsync()
    {
        using (Working())
        {
            try
            {
                var recoveryKey = await _workspace.CreateAsync();
                await ShowRecoveryKeyAsync(recoveryKey);
            }
            catch (Exception ex)
            {
                ShowError($"The workspace could not be created. {ex.Message}");
            }
        }
    }

    private async Task UnlockWithHelloAsync()
    {
        using (Working())
        {
            var outcome = await _workspace.UnlockAsync();
            switch (outcome)
            {
                case UnlockOutcome.Success:
                    return;
                case UnlockOutcome.Cancelled:
                    ShowError("Verification was cancelled.");
                    break;
                case UnlockOutcome.ProtectorUnavailable:
                    ShowError("Windows Hello is not available. Use your recovery key.");
                    ShowRecoveryPanel();
                    break;
                case UnlockOutcome.VerificationFailed:
                    ShowError("That did not unlock the workspace. If Windows Hello was re-enrolled "
                            + "on this PC, use your recovery key.");
                    ShowRecoveryPanel();
                    break;
                default:
                    ShowError("The workspace could not be opened.");
                    break;
            }
        }
    }

    private void OnRecoveryClick(object sender, RoutedEventArgs e) => ShowRecoveryPanel();

    private void ShowRecoveryPanel()
    {
        RecoveryPanel.Visibility = Visibility.Visible;
        RecoveryLink.Visibility = Visibility.Collapsed;
        RecoveryInput.Focus(FocusState.Programmatic);
    }

    private void OnRecoveryTextChanged(object sender, TextChangedEventArgs e)
        => RecoverySubmit.IsEnabled = RecoveryKey.IsWellFormed(RecoveryInput.Text);

    private async void OnRecoverySubmitClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        using (Working())
        {
            var outcome = await _workspace.UnlockWithRecoveryKeyAsync(RecoveryInput.Text);
            if (outcome == UnlockOutcome.Success)
            {
                // A machine that just needed the recovery key is a machine where Hello is not
                // enrolled yet, so offer to fix that rather than asking for the key every time.
                await OfferHelloEnrolmentAsync();
                return;
            }

            ShowError(outcome == UnlockOutcome.VerificationFailed
                ? "That recovery key does not match this workspace."
                : "The workspace could not be opened.");
        }
    }

    private async Task OfferHelloEnrolmentAsync()
    {
        if (await _workspace.IsHelloAvailableAsync()) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Set up Windows Hello?",
            Content = "You can unlock Campus with your face, fingerprint or PIN instead of "
                    + "typing the recovery key. Keep the recovery key regardless — it is the "
                    + "only way in if Windows Hello stops working.",
            PrimaryButtonText = "Set up",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await _workspace.EnrolHelloAsync();
    }

    /// <summary>
    /// The recovery key is shown once, at creation, and Campus keeps no copy. The dialog cannot
    /// be dismissed by clicking away for that reason.
    /// </summary>
    private async Task ShowRecoveryKeyAsync(string recoveryKey)
    {
        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(new TextBlock
        {
            Text = "Write this down and keep it somewhere safe, away from this PC. It is the only "
                 + "way back into your workspace if Windows Hello stops working or you move to "
                 + "another computer. Campus does not keep a copy — not even encrypted.",
            TextWrapping = TextWrapping.Wrap,
        });

        body.Children.Add(new TextBox
        {
            Text = recoveryKey,
            IsReadOnly = true,
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["Theme.Font.Mono"],
            FontSize = 16,
            TextAlignment = TextAlignment.Center,
        });

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

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private IDisposable Working()
    {
        _busy = true;
        ErrorText.Visibility = Visibility.Collapsed;
        Busy.Visibility = Visibility.Visible;
        Busy.IsActive = true;
        PrimaryButton.IsEnabled = false;
        RecoverySubmit.IsEnabled = false;

        return new Scope(() =>
        {
            _busy = false;
            Busy.IsActive = false;
            Busy.Visibility = Visibility.Collapsed;
            PrimaryButton.IsEnabled = true;
            RecoverySubmit.IsEnabled = RecoveryKey.IsWellFormed(RecoveryInput.Text);
        });
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
