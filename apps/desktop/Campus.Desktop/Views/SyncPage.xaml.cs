using Campus.Desktop.Design;
using Campus.Desktop.Design.Controls;
using Campus.Sync;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Domain;
using Campus.Sync;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Campus.Desktop.Views;

/// <summary>
/// Pairing devices and moving changes between them.
///
/// The page is honest about what sync is here: a file, or a socket, and a code you type. There is
/// no "syncing…" that runs forever in the background, because there is nothing in the middle to
/// sync with — which is the reason the workspace can be trusted in the first place.
/// </summary>
public sealed partial class SyncPage : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();
    private readonly SyncService _sync = App.GetService<SyncService>();

    public SyncPage()
    {
        InitializeComponent();
        _sync.Progress += OnProgress;
        Unloaded += (_, _) =>
        {
            _sync.Progress -= OnProgress;
            // Nothing keeps a socket open once you have left the page.
            _sync.StopListening();
        };
    }

    private void OnProgress(object? sender, string message)
        => DispatcherQueue.TryEnqueue(() => Status(message));

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_workspace.IsUnlocked) return;

        BuildTransfer();
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        var devices = await _sync.DevicesAsync();
        var position = await _sync.PositionAsync();

        Subtitle.Text = devices.Count == 0
            ? $"No devices paired · {position} changes recorded"
            : $"{devices.Count} device{(devices.Count == 1 ? "" : "s")} · {position} changes recorded";

        DeviceList.Children.Clear();

        if (devices.Count == 0)
        {
            DeviceList.Children.Add(new SettingsRow
            {
                Title = L.T("nothing.paired.yet"),
                Subtitle = "Pair your phone to capture on the move and pick it up here",
                Symbol = CampusSymbols.Phone,
                ShowSeparator = false,
            });
        }

        var first = true;
        foreach (var device in devices)
        {
            DeviceList.Children.Add(await BuildDeviceRowAsync(device, first));
            first = false;
        }

        await LoadConflictsAsync();
    }

    private async Task<FrameworkElement> BuildDeviceRowAsync(PairedDevice device, bool first)
    {
        var pending = await _sync.PendingForAsync(device);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var send = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Secondary"],
            Content = pending > 0 ? $"Send {pending}" : "Send",
            MinWidth = 0,
            IsEnabled = pending > 0,
        };
        send.Click += async (_, _) => await SendToAsync(device);
        actions.Children.Add(send);

        var forget = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Icon"],
            Content = new CampusIcon
            {
                Symbol = CampusSymbols.Close,
                IconSize = 15,
                Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Tertiary],
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(forget, $"Forget {device.DisplayName}");
        forget.Click += async (_, _) =>
        {
            if (!await ObjectCommands.ConfirmAsync(XamlRoot, $"Forget {device.DisplayName}?",
                "Nothing is deleted. The next sync with it would start from the beginning.",
                "Forget")) return;

            await _sync.ForgetAsync(device.DeviceId);
            await ReloadAsync();
        };
        actions.Children.Add(forget);

        var seen = device.LastSeenAt is { } when
            ? "Last synced " + BoardPage.Ago(when)
            : "Never synced";

        return new SettingsRow
        {
            Title = device.DisplayName,
            Subtitle = $"{device.Platform} · {seen}",
            Symbol = device.Platform switch
            {
                DevicePlatform.IOS or DevicePlatform.Android => CampusSymbols.Phone,
                _ => CampusSymbols.Laptop,
            },
            ShowSeparator = !first,
            Content = actions,
        };
    }

    /// <summary>
    /// Sends to one device, asking how it should travel. Both routes carry the same bundle, so
    /// the choice is about the cable rather than about what gets sent.
    /// </summary>
    private async Task SendToAsync(PairedDevice peer)
    {
        var choice = new ComboBox { Width = 300, SelectedIndex = 0 };
        choice.Items.Add("Write a file I can carry");
        choice.Items.Add("Send over the local network");

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Send to {peer.DisplayName}",
            Content = choice,
            PrimaryButtonText = L.T("continue.31fb"),
            CloseButtonText = L.T("cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (choice.SelectedIndex == 1) await ServeToAsync(peer);
        else await ExportToFileAsync(peer);
    }

    // ---------------------------------------------------------------------- transfer

    private void BuildTransfer()
    {
        var rows = new StackPanel();

        rows.Children.Add(Row(
            "Send to a file",
            "Write a bundle you can carry on a stick",
            CampusSymbols.Export, "Write bundle", first: true,
            async () => await ExportToFileAsync()));

        rows.Children.Add(Row(
            "Apply a bundle",
            "Read one written by another device",
            CampusSymbols.Import, "Choose file", first: false,
            async () => await ImportFromFileAsync()));

        var addresses = SyncService.LocalAddresses();

        rows.Children.Add(Row(
            "Serve over the local network",
            addresses.Count > 0
                ? $"This machine is {string.Join(", ", addresses)} on port {SyncService.DefaultPort}"
                : "No local network address found",
            CampusSymbols.Wifi, "Wait for a device", first: false,
            async () => await ServeAsync()));

        rows.Children.Add(Row(
            "Fetch from a device",
            "Connect to one that is waiting",
            CampusSymbols.Download, "Connect", first: false,
            async () => await FetchAsync()));

        rows.Children.Add(Row(
            "Take what the phone caught",
            addresses.Count > 0
                ? $"Campus Pocket connects to {addresses[0]} on port {PhoneSync.Port}"
                : "No local network address found",
            CampusSymbols.Phone, "Wait for the phone", first: false,
            async () => await ReceiveFromPhoneAsync()));

        TransferSection.Content = rows;
    }

    private static SettingsRow Row(
        string title, string? subtitle, string symbol, string action, bool first, Func<Task> invoke)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Secondary"],
            Content = action,
            MinWidth = 0,
        };
        button.Click += (_, _) => _ = invoke();

        return new SettingsRow
        {
            Title = title,
            Subtitle = subtitle,
            Symbol = symbol,
            ShowSeparator = !first,
            Content = button,
        };
    }

    private async Task<PairedDevice?> ChoosePeerAsync()
    {
        var devices = await _sync.DevicesAsync();

        if (devices.Count == 0)
        {
            Notifications.Show(L.T("pair.a.device.first"), NoticeKind.Warning);
            return null;
        }

        if (devices.Count == 1) return devices[0];

        var list = new ComboBox { Width = 300, SelectedIndex = 0 };
        foreach (var device in devices) list.Items.Add(device.DisplayName);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.T("which.device"),
            Content = list,
            PrimaryButtonText = L.T("continue.31fb"),
            CloseButtonText = L.T("cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? devices[Math.Max(0, list.SelectedIndex)]
            : null;
    }

    private async Task ExportToFileAsync()
    {
        if (await ChoosePeerAsync() is { } chosen) await ExportToFileAsync(chosen);
    }

    private async Task ExportToFileAsync(PairedDevice peer)
    {
        if (await AskCodeAsync("Code for this bundle",
            "The other device will need this code to open it. Make one up, or reuse the one you "
            + "paired with.") is not { } code) return;

        var picker = new FileSavePicker { SuggestedFileName = $"campus-{DateTime.Now:yyyy-MM-dd}" };
        InitialiseWithWindow(picker);
        picker.FileTypeChoices.Add("Campus sync bundle", [".campussync"]);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        Status("Writing");
        try
        {
            var manifest = await _sync.ExportAsync(peer, code, file.Path);
            Done($"Wrote {manifest.EntryCount} changes and {manifest.FileCount} files.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Done($"Could not write the bundle: {ex.Message}", failed: true);
        }

        await ReloadAsync();
    }

    private async Task ImportFromFileAsync()
    {
        var picker = new FileOpenPicker();
        InitialiseWithWindow(picker);
        picker.FileTypeFilter.Add(".campussync");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var manifest = await _sync.InspectAsync(file.Path);
        if (manifest is null)
        {
            Done("That file is not a Campus bundle.", failed: true);
            return;
        }

        // The manifest is readable without the code, so the user can see what they are about to
        // let in before they type anything.
        if (await AskCodeAsync(
            $"Code from {manifest.FromDeviceName}",
            $"{manifest.EntryCount} changes and {manifest.FileCount} files, written "
            + $"{BoardPage.Ago(manifest.CreatedAt)}.") is not { } code) return;

        Status("Applying");
        var result = await _sync.ImportAsync(file.Path, code);

        if (result is null)
        {
            Done("That code did not open the bundle. Nothing was changed.", failed: true);
            return;
        }

        var message = $"Applied {result.Applied}";
        if (result.FilesReceived > 0) message += $", {result.FilesReceived} files";
        if (result.Ignored > 0) message += $", {result.Ignored} already up to date";
        if (result.Conflicted > 0) message += $", {result.Conflicted} need a decision";

        Done(message + ".");
        await ReloadAsync();
    }

    private async Task ServeAsync()
    {
        if (await ChoosePeerAsync() is { } chosen) await ServeToAsync(chosen);
    }

    private async Task ServeToAsync(PairedDevice peer)
    {
        if (await AskCodeAsync("Code for this transfer",
            "Type the same code on the other device.") is not { } code) return;

        Status($"Waiting for {peer.DisplayName} to connect…");

        try
        {
            var manifest = await _sync.ServeAsync(peer, code);
            Done(manifest is null
                ? "Nothing was sent."
                : $"Sent {manifest.EntryCount} changes and {manifest.FileCount} files.");
        }
        catch (OperationCanceledException)
        {
            Done("Stopped waiting.");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            Done($"The transfer failed: {ex.Message}", failed: true);
        }

        await ReloadAsync();
    }

    private async Task FetchAsync()
    {
        var host = new TextBox
        {
            PlaceholderText = "192.168.1.20",
            Style = (Style)Application.Current.Resources["Input.Text"],
        };
        var code = new TextBox
        {
            PlaceholderText = L.T("pairing.code"),
            Style = (Style)Application.Current.Resources["Input.Text"],
        };

        var body = new StackPanel { Spacing = 12, Width = 320 };
        body.Children.Add(new TextBlock
        {
            Text = L.T("the.address.shown.on.the.other.device.and.the.0bc2a9"),
            Style = (Style)Application.Current.Resources["Text.Footnote"],
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(host);
        body.Children.Add(code);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.T("fetch.from.a.device"),
            Content = body,
            PrimaryButtonText = L.T("connect"),
            CloseButtonText = L.T("cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (host.Text.Trim().Length == 0 || code.Text.Trim().Length == 0) return;

        Status("Connecting");

        try
        {
            var result = await _sync.FetchAsync(host.Text.Trim(), code.Text.Trim());

            Done(result is null
                ? "That code did not open what arrived. Nothing was changed."
                : $"Applied {result.Applied} changes and {result.FilesReceived} files.",
                failed: result is null);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException
                                      or InvalidDataException)
        {
            Done($"Could not fetch: {ex.Message}", failed: true);
        }

        await ReloadAsync();
    }

    /// <summary>
    /// Waits for Campus Pocket to connect and takes what it has caught.
    ///
    /// One phone, once, while somebody is looking at this page. Nothing listens in the background,
    /// because a workspace that encrypts itself should not also be answering the school Wi-Fi.
    /// </summary>
    private async Task ReceiveFromPhoneAsync()
    {
        Status("Waiting for the phone to connect…");

        try
        {
            var result = await _sync.ReceiveFromPhoneAsync();

            if (result is null)
            {
                Done("Nothing connected.");
                return;
            }

            var message = $"Took {result.Accepted} from {result.DeviceName}";
            if (result.Attachments > 0) message += $", including {result.Attachments} files";
            if (result.Rejected > 0) message += $". {result.Rejected} could not be stored";

            Done(message + ".");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException
                                      or InvalidDataException)
        {
            Done($"The phone could not be read: {ex.Message}", failed: true);
        }

        await ReloadAsync();
    }

    /// <summary>
    /// Pairs a phone by showing it a code to scan.
    ///
    /// The secret is inside the code because the code is on a screen being read by a camera in the
    /// same room — a better channel than anything the two could negotiate over a network neither
    /// of them trusts. It is shown once and is not recoverable afterwards.
    /// </summary>
    private async void OnPairPhoneClick(object sender, RoutedEventArgs e)
    {
        if (!_workspace.IsUnlocked) return;

        var name = await ObjectCommands.AskAsync(XamlRoot, L.T("what.is.the.phone.called"), "", "My iPhone");
        if (name is null) return;

        var code = await _sync.BeginPhonePairingAsync(name);

        var body = new StackPanel { Spacing = 16, Width = 380 };

        body.Children.Add(new TextBlock
        {
            Text = L.T("in.campus.pocket.open.settings.and.choose.u201.9f6514"),
            Style = (Style)Application.Current.Resources["Text.Callout"],
            TextWrapping = TextWrapping.Wrap,
        });

        body.Children.Add(new Border
        {
            Padding = new Thickness(16),
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.Card"],
            // The white plate the code sits on. Label.OnAccent happened to be white in dark mode
            // and black in light mode, which put a black border around a black-on-white code.
            Background = (Brush)Application.Current.Resources[ThemeTokens.Machine.Paper],
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new QrCode { Payload = code, ModuleSize = 5 },
        });

        body.Children.Add(new TextBlock
        {
            Text = L.T("this.code.contains.the.secret.the.two.devices.7db7f4"),
            Style = (Style)Application.Current.Resources["Text.Footnote"],
            TextWrapping = TextWrapping.Wrap,
        });

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Pair {name}",
            Content = body,
            CloseButtonText = L.T("done"),
        }.ShowAsync();

        await ReloadAsync();
    }

    // ----------------------------------------------------------------------- pairing

    private async void OnPairClick(object sender, RoutedEventArgs e)
    {
        var name = new TextBox
        {
            PlaceholderText = L.T("my.phone"),
            Style = (Style)Application.Current.Resources["Input.Text"],
        };

        var id = new TextBox
        {
            PlaceholderText = L.T("the.other.device.s.id"),
            Style = (Style)Application.Current.Resources["Input.Text"],
        };

        var platform = new ComboBox { Width = 200, SelectedIndex = 1 };
        foreach (var value in Enum.GetValues<DevicePlatform>()) platform.Items.Add(value.ToString());

        var body = new StackPanel { Spacing = 12, Width = 340 };
        body.Children.Add(new TextBlock
        {
            Text = L.T("campus.does.not.go.looking.for.devices.add.the.f2f830"),
            Style = (Style)Application.Current.Resources["Text.Footnote"],
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(name);
        body.Children.Add(id);
        body.Children.Add(platform);

        var suggestion = Pairing.GenerateCode();
        body.Children.Add(new TextBlock
        {
            Text = $"A code you could use: {suggestion}",
            Style = (Style)Application.Current.Resources["Text.Mono"],
            IsTextSelectionEnabled = true,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.T("pair.a.device"),
            Content = body,
            PrimaryButtonText = L.T("pair"),
            CloseButtonText = L.T("cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (name.Text.Trim().Length == 0 || id.Text.Trim().Length == 0)
        {
            Notifications.Show(L.T("a.device.needs.a.name.and.an.id"), NoticeKind.Warning);
            return;
        }

        await _sync.PairAsync(
            id.Text.Trim(),
            name.Text.Trim(),
            Enum.GetValues<DevicePlatform>()[Math.Max(0, platform.SelectedIndex)]);

        Notifications.Show($"Paired with {name.Text.Trim()}.", NoticeKind.Success);
        await ReloadAsync();
    }

    private async Task<string?> AskCodeAsync(string title, string explanation)
    {
        var box = new TextBox
        {
            PlaceholderText = L.T("xxxx.xxxx.xxxx"),
            Style = (Style)Application.Current.Resources["Input.Text"],
        };

        var body = new StackPanel { Spacing = 12, Width = 320 };
        body.Children.Add(new TextBlock
        {
            Text = explanation,
            Style = (Style)Application.Current.Resources["Text.Footnote"],
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = body,
            PrimaryButtonText = L.T("continue.31fb"),
            CloseButtonText = L.T("cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        var code = box.Text.Trim();
        if (Pairing.IsWellFormed(code)) return code;

        Notifications.Show(L.T("a.pairing.code.is.twelve.characters"), NoticeKind.Warning);
        return null;
    }

    // --------------------------------------------------------------------- conflicts

    private async Task LoadConflictsAsync()
    {
        var conflicts = await _sync.ConflictsAsync();

        ConflictSection.Visibility = conflicts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConflictList.Children.Clear();

        var first = true;
        foreach (var conflict in conflicts)
        {
            ConflictList.Children.Add(BuildConflictRow(conflict, first));
            first = false;
        }
    }

    private FrameworkElement BuildConflictRow(SyncConflict conflict, bool first)
    {
        var local = Storage.SnapshotSerializer.Deserialize(conflict.LocalSnapshot);
        var remote = Storage.SnapshotSerializer.Deserialize(conflict.RemoteSnapshot);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        actions.Children.Add(Choice("Keep mine", ConflictResolution.KeepLocal, conflict));
        actions.Children.Add(Choice("Take theirs", ConflictResolution.KeepRemote, conflict));
        actions.Children.Add(Choice("Keep both", ConflictResolution.KeepBoth, conflict));

        return new SettingsRow
        {
            Title = local?.Title ?? remote?.Title ?? "Something changed on both sides",
            Subtitle = $"Yours {BoardPage.Ago(conflict.LocalUpdatedAt)}, "
                     + $"theirs {BoardPage.Ago(conflict.RemoteUpdatedAt)}",
            Symbol = CampusSymbols.Warning,
            ShowSeparator = !first,
            Content = actions,
        };
    }

    private Button Choice(string label, ConflictResolution resolution, SyncConflict conflict)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Secondary"],
            Content = label,
            MinWidth = 0,
        };

        button.Click += async (_, _) =>
        {
            await _sync.ResolveAsync(conflict, resolution);
            await ReloadAsync();
        };

        return button;
    }

    // ------------------------------------------------------------------------ status

    private void Status(string message)
    {
        StatusPanel.Visibility = Visibility.Visible;
        StatusRing.IsActive = true;
        StatusText.Text = message;
    }

    private void Done(string message, bool failed = false)
    {
        StatusPanel.Visibility = Visibility.Visible;
        StatusRing.IsActive = false;
        StatusText.Text = message;
        Notifications.Show(message, failed ? NoticeKind.Error : NoticeKind.Success);
    }

    private static void InitialiseWithWindow(object picker)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
    }
}
