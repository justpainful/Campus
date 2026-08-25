using Campus.Desktop.Design;
using Campus.Desktop.Design.Controls;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Extensions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Campus.Desktop.Views;

/// <summary>
/// The extensions manager.
///
/// Installing does not enable. Enabling asks first, in sentences rather than in permission names,
/// and only then does any of the extension's code get loaded. Anyone who wants to know what an
/// extension can do can read it here without having to trust a summary.
/// </summary>
public sealed partial class ExtensionsPage : Page
{
    private readonly ExtensionService _extensions = App.GetService<ExtensionService>();

    public ExtensionsPage()
    {
        InitializeComponent();
        _extensions.Message += OnMessage;
        Unloaded += (_, _) => _extensions.Message -= OnMessage;
    }

    private void OnMessage(object? sender, string message)
        => DispatcherQueue.TryEnqueue(() => Notifications.Show(message, NoticeKind.Warning));

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        await _extensions.RefreshAsync();

        var installed = _extensions.Extensions.Where(x => !x.Manifest.IsBuiltIn).ToList();
        var builtIn = _extensions.Extensions.Where(x => x.Manifest.IsBuiltIn).ToList();

        Subtitle.Text = installed.Count == 0
            ? $"{builtIn.Count} built in · nothing else installed"
            : $"{installed.Count} installed · {builtIn.Count} built in";

        InstalledSection.Visibility = installed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InstalledSection.Content = Build(installed);
        BuiltInSection.Content = Build(builtIn);
    }

    private StackPanel Build(IReadOnlyList<InstalledExtension> extensions)
    {
        var rows = new StackPanel();

        for (var i = 0; i < extensions.Count; i++)
            rows.Children.Add(BuildRow(extensions[i], first: i == 0));

        return rows;
    }

    private FrameworkElement BuildRow(InstalledExtension extension, bool first)
    {
        var manifest = extension.Manifest;

        var toggle = new ToggleSwitch
        {
            IsOn = extension.IsEnabled,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
        };
        AutomationProperties.SetName(toggle, $"Enable {manifest.Name}");

        toggle.Toggled += async (_, _) =>
        {
            // Turning something on is where consent belongs — not at install time, when the user
            // is thinking about whether they want it at all.
            if (toggle.IsOn && !extension.IsGranted)
            {
                if (!await AskConsentAsync(extension))
                {
                    toggle.IsOn = false;
                    return;
                }

                await _extensions.GrantAsync(extension);
            }

            await _extensions.SetEnabledAsync(extension, toggle.IsOn);

            if (extension.Failure is { Length: > 0 } failure)
                Notifications.Show($"{manifest.Name}: {failure}", NoticeKind.Error);

            await ReloadAsync();
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var details = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Icon"],
            Content = new CampusIcon
            {
                Symbol = CampusSymbols.Info,
                IconSize = 16,
                Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Tertiary],
            },
        };
        AutomationProperties.SetName(details, $"What {manifest.Name} can do");
        details.Click += async (_, _) => await ShowDetailsAsync(extension);
        actions.Children.Add(details);

        if (!manifest.IsBuiltIn)
        {
            var remove = new Button
            {
                Style = (Style)Application.Current.Resources["Button.Icon"],
                Content = new CampusIcon
                {
                    Symbol = CampusSymbols.Trash,
                    IconSize = 16,
                    Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Tertiary],
                },
            };
            AutomationProperties.SetName(remove, $"Remove {manifest.Name}");
            remove.Click += async (_, _) =>
            {
                if (!await ObjectCommands.ConfirmAsync(XamlRoot, $"Remove {manifest.Name}?",
                    "Its files are deleted. Nothing in your workspace is touched.",
                    "Remove")) return;

                await _extensions.UninstallAsync(extension);
                await ReloadAsync();
            };
            actions.Children.Add(remove);
        }

        actions.Children.Add(toggle);

        var state = extension.Failure is { Length: > 0 } problem
            ? problem
            : extension.IsRunning ? "Running"
            : manifest.Description ?? $"Version {manifest.Version}";

        return new SettingsRow
        {
            Title = manifest.Name,
            Subtitle = state,
            Symbol = manifest.Symbol ?? CampusSymbols.Extensions,
            ShowSeparator = !first,
            IsDestructive = extension.Failure is { Length: > 0 },
            Content = actions,
        };
    }

    /// <summary>
    /// The consent dialog. Sentences, not permission names — "read your notes" is something a
    /// person can weigh; "ReadWorkspace" is something they will click past.
    /// </summary>
    private async Task<bool> AskConsentAsync(InstalledExtension extension)
    {
        var manifest = extension.Manifest;

        var body = new StackPanel { Spacing = 14, Width = 380 };

        body.Children.Add(new TextBlock
        {
            Text = manifest.Description ?? $"Version {manifest.Version}",
            Style = (Style)Application.Current.Resources["Text.Callout"],
            TextWrapping = TextWrapping.Wrap,
        });

        var list = new StackPanel { Spacing = 8 };
        foreach (var sentence in ExtensionManifest.Describe(manifest.Permissions))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new CampusIcon
            {
                Symbol = CampusSymbols.Check,
                IconSize = 15,
                Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Tertiary],
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0),
            });
            row.Children.Add(new TextBlock
            {
                Text = sentence,
                Style = (Style)Application.Current.Resources["Text.Body"],
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
            });
            list.Children.Add(row);
        }

        body.Children.Add(new Border
        {
            Background = (Brush)Application.Current.Resources[ThemeTokens.Fill.Quaternary],
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.Card"],
            Padding = new Thickness(14),
            Child = list,
        });

        if (manifest.Author is { Length: > 0 } author)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"By {author}. Campus cannot verify who wrote an extension — install ones "
                     + "you have a reason to trust.",
                Style = (Style)Application.Current.Resources["Text.Footnote"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Allow {manifest.Name} to…",
            Content = body,
            PrimaryButtonText = L.T("allow"),
            CloseButtonText = L.T("not.now"),
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowDetailsAsync(InstalledExtension extension)
    {
        var manifest = extension.Manifest;
        var body = new StackPanel { Spacing = 12, Width = 380 };

        body.Children.Add(new TextBlock
        {
            Text = manifest.Description ?? "No description.",
            Style = (Style)Application.Current.Resources["Text.Callout"],
            TextWrapping = TextWrapping.Wrap,
        });

        body.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", new[]
            {
                $"Version {manifest.Version}",
                manifest.Author,
                manifest.IsBuiltIn ? "Built into Campus" : "Installed",
            }.Where(v => !string.IsNullOrWhiteSpace(v))),
            Style = (Style)Application.Current.Resources["Text.Footnote"],
        });

        if (manifest.Contributes.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = L.T("it.adds"),
                Style = (Style)Application.Current.Resources["Text.SectionHeader"],
            });

            foreach (var contribution in manifest.Contributes)
            {
                var detail = contribution.FileTypes.Count > 0
                    ? $"{contribution.Kind} · {string.Join(" ", contribution.FileTypes)}"
                    : contribution.Kind.ToString();

                body.Children.Add(new TextBlock
                {
                    Text = $"{contribution.Title} — {detail}",
                    Style = (Style)Application.Current.Resources["Text.Footnote"],
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        body.Children.Add(new TextBlock
        {
            Text = L.T("it.may"),
            Style = (Style)Application.Current.Resources["Text.SectionHeader"],
        });

        foreach (var sentence in ExtensionManifest.Describe(manifest.Permissions))
        {
            body.Children.Add(new TextBlock
            {
                Text = "• " + sentence,
                Style = (Style)Application.Current.Resources["Text.Footnote"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = manifest.Name,
            Content = body,
            CloseButtonText = L.T("done"),
        }.ShowAsync();
    }

    // ---------------------------------------------------------------------- installing

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitialiseWithWindow(picker);
        picker.FileTypeFilter.Add(".campusx");
        picker.FileTypeFilter.Add(".zip");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        await InstallAsync(file.Path);
    }

    private async void OnFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitialiseWithWindow(picker);
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        await InstallAsync(folder.Path);
    }

    private async Task InstallAsync(string path)
    {
        var installed = await _extensions.InstallAsync(path);

        if (installed is null)
        {
            Notifications.Show(L.T("nothing.was.installed"), NoticeKind.Warning);
            return;
        }

        Notifications.Show(
            $"{installed.Manifest.Name} is installed but not enabled yet.", NoticeKind.Success);

        await ReloadAsync();
    }

    private static void InitialiseWithWindow(object picker)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
    }
}
