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
            HeadlineText.Text = L.T("welcome.to.campus");
            SubtitleText.Text = L.T("everything.you.keep.here.is.encrypted.on.this.fa2f48");
            PrimaryLabel.Text = L.T("create.workspace");
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
            Title = L.T("set.up.windows.hello"),
            Content = L.T("you.can.unlock.campus.with.your.face.fingerpri.c3f828"),
            PrimaryButtonText = L.T("set.up"),
            CloseButtonText = L.T("not.now"),
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
            Text = L.T("write.this.down.and.keep.it.somewhere.safe.awa.0191de"),
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
            Title = L.T("your.recovery.key"),
            Content = body,
            PrimaryButtonText = L.T("i.have.written.it.down"),
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
