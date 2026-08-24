using Campus.Desktop.Design;
using Campus.Desktop.Design.Controls;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Desktop.ViewModels;
using Campus.Domain;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Campus.Desktop.Views;

/// <summary>
/// Everything in the vault, grouped by the subject it belongs to.
///
/// Deliberately not a folder tree: files are not in folders here, they are filed under subjects,
/// and one file can be in a subject, a collection and an exam's reading list at once without any
/// copies existing. Grouping is a view of that, not a location on disk.
/// </summary>
public sealed partial class FilesPage : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();
    private readonly ImportService _import = App.GetService<ImportService>();
    private readonly ShellRouter _router = App.GetService<ShellRouter>();
    private readonly NavigationState _navigation = App.GetService<NavigationState>();

    private readonly Dictionary<string, string> _subjectNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _subjectAccents = new(StringComparer.Ordinal);

    private string _filter = "";
    private bool _asGrid = true;

    public FilesPage()
    {
        InitializeComponent();
        _navigation.FilterChanged += OnFilterStateChanged;
        Unloaded += (_, _) => _navigation.FilterChanged -= OnFilterStateChanged;
    }

    private async void OnFilterStateChanged(object? sender, EventArgs e) => await ReloadAsync();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        Groups.Children.Clear();
        if (!_workspace.IsUnlocked) return;

        await LoadSubjectsAsync();

        var query = new CampusQuery
        {
            Kinds = { ObjectKind.File },
            Sort = SortField.UpdatedAt,
        };
        _navigation.Apply(query);
        if (_filter.Length > 0) query.Text = _filter;

        var files = await _workspace.Objects.QueryAsync(query);

        EmptyState.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (files.Count == 0)
        {
            Subtitle.Text = "Nothing stored yet";
            if (_filter.Length > 0)
            {
                EmptyTitle.Text = "No matches";
                EmptyMessage.Text = $"No file here matches “{_filter}”.";
            }
            return;
        }

        var bytes = files.Sum(f => f.PayloadAs<FilePayload>()?.SizeBytes ?? 0);
        Subtitle.Text = $"{files.Count} file{(files.Count == 1 ? "" : "s")} · {ObjectItem.FormatSize(bytes)}";

        // Grouped by subject, with the unfiled ones last — they are the ones needing attention,
        // and putting them at the top would make the page feel like a list of chores.
        var grouped = files
            .GroupBy(f => f.SubjectId?.Value ?? "")
            .OrderBy(g => g.Key.Length == 0 ? 1 : 0)
            .ThenBy(g => _subjectNames.TryGetValue(g.Key, out var name) ? name : "");

        foreach (var group in grouped) Groups.Children.Add(BuildGroup(group.Key, [.. group]));
    }

    private async Task LoadSubjectsAsync()
    {
        if (_subjectNames.Count > 0) return;

        var subjects = await _workspace.Objects.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Subject },
            Sort = SortField.Manual,
            Descending = false,
        });

        foreach (var subject in subjects)
        {
            _subjectNames[subject.Id.Value] = subject.Title;
            _subjectAccents[subject.Id.Value] = ThemeTokens.Subject.FromName(
                subject.PayloadAs<SubjectPayload>()?.AccentName);
        }
    }

    // ------------------------------------------------------------------------ groups

    private FrameworkElement BuildGroup(string subjectId, IReadOnlyList<CampusObject> files)
    {
        var known = _subjectNames.TryGetValue(subjectId, out var name);

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 12),
        };

        header.Children.Add(new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brush(known
                ? _subjectAccents[subjectId]
                : ThemeTokens.Label.Quaternary),
        });
        header.Children.Add(new TextBlock
        {
            Text = (known ? name : "Not filed under a subject").ToUpperInvariant(),
            Style = (Style)Application.Current.Resources["Text.SectionHeader"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = files.Count.ToString(),
            Style = (Style)Application.Current.Resources["Text.Caption"],
            VerticalAlignment = VerticalAlignment.Center,
        });

        var section = new StackPanel { Spacing = 0 };
        section.Children.Add(header);

        if (_asGrid)
        {
            var wrap = new WrapPanel { ItemSpacing = 14, LineSpacing = 14 };
            foreach (var file in files) wrap.Children.Add(BuildTile(file));
            section.Children.Add(wrap);
        }
        else
        {
            var rows = new StackPanel { Spacing = 2 };
            foreach (var file in files) rows.Children.Add(BuildRow(file));
            section.Children.Add(rows);
        }

        return section;
    }

    /// <summary>
    /// A tile: the thumbnail if there is one, the file's own icon if not. The thumbnail is loaded
    /// after the tile is in the tree so a hundred files do not decode a hundred images up front.
    /// </summary>
    private FrameworkElement BuildTile(CampusObject file)
    {
        var payload = file.PayloadAs<FilePayload>();
        var item = new ObjectItem(file);

        var preview = new Grid
        {
            Height = 132,
            Background = Brush(ThemeTokens.Fill.Quaternary),
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.S"],
        };

        preview.Children.Add(new CampusIcon
        {
            Symbol = item.Symbol,
            IconSize = 38,
            Weight = IconWeight.Light,
            Foreground = Brush(ThemeTokens.Label.Tertiary),
        });

        var image = new Image
        {
            Stretch = Stretch.UniformToFill,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        preview.Children.Add(image);

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(preview);

        var labels = new StackPanel { Spacing = 1 };
        labels.Children.Add(new TextBlock
        {
            Text = file.Title,
            FontFamily = Font("Theme.Font.Text"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(ThemeTokens.Label.Primary),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        labels.Children.Add(new TextBlock
        {
            Text = Describe(payload),
            Style = (Style)Application.Current.Resources["Text.Caption"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        body.Children.Add(labels);

        var tile = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Plain"],
            Width = 176,
            Padding = new Thickness(10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.Card"],
            Content = body,
        };

        AutomationProperties.SetName(tile, file.Title);
        tile.Click += (_, _) => _router.Open(file.Id);
        tile.RightTapped += (_, e) =>
        {
            ObjectCommands.Build(file, XamlRoot, ReloadAsync).ShowAt(tile, e.GetPosition(tile));
            e.Handled = true;
        };

        // Kicked off after the tile exists so scrolling is never blocked on decoding.
        tile.Loaded += async (_, _) =>
        {
            var thumbnail = await _import.LoadThumbnailAsync(payload?.ThumbnailHash, 320);
            if (thumbnail is not null) image.Source = thumbnail;
        };

        return tile;
    }

    private FrameworkElement BuildRow(CampusObject file)
    {
        var payload = file.PayloadAs<FilePayload>();
        var item = new ObjectItem(file);

        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(10, 8, 10, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new CampusIcon
        {
            Symbol = item.Symbol,
            IconSize = 20,
            Foreground = Brush(ThemeTokens.Label.Secondary),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var title = new TextBlock
        {
            Text = file.Title,
            FontFamily = Font("Theme.Font.Text"),
            FontSize = 14,
            Foreground = Brush(ThemeTokens.Label.Primary),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        Grid.SetColumn(title, 1);
        row.Children.Add(title);

        var detail = new TextBlock
        {
            Text = Describe(payload),
            Style = (Style)Application.Current.Resources["Text.Caption"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(detail, 2);
        row.Children.Add(detail);

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Plain"],
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.M"],
            Content = row,
        };

        AutomationProperties.SetName(button, file.Title);
        button.Click += (_, _) => _router.Open(file.Id);
        button.RightTapped += (_, e) =>
        {
            ObjectCommands.Build(file, XamlRoot, ReloadAsync).ShowAt(button, e.GetPosition(button));
            e.Handled = true;
        };

        return button;
    }

    private static string Describe(FilePayload? payload)
    {
        if (payload is null) return "";

        var parts = new List<string> { ObjectItem.FormatSize(payload.SizeBytes) };
        if (payload.PageCount is { } pages) parts.Add($"{pages} pp");
        if (payload.Extension.Length > 0) parts.Add(payload.Extension.TrimStart('.').ToUpperInvariant());
        return string.Join(" · ", parts);
    }

    // ----------------------------------------------------------------------- actions

    private async void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        _filter = FilterBox.Text.Trim();
        await ReloadAsync();
    }

    private async void OnToggleViewClick(object sender, RoutedEventArgs e)
    {
        _asGrid = !_asGrid;
        ViewGlyph.Symbol = _asGrid ? CampusSymbols.ListView : CampusSymbols.GridView;
        await ReloadAsync();
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add("*");

        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;

        await ImportAsync(files.Select(f => f.Path).ToList());
    }

    /// <summary>
    /// Runs an import, saying what it is working on. A long import with no sign of life is
    /// indistinguishable from a hung program.
    /// </summary>
    private async Task ImportAsync(IReadOnlyList<string> paths)
    {
        Progress.Visibility = Visibility.Visible;
        ProgressText.Text = $"Adding {paths.Count} file{(paths.Count == 1 ? "" : "s")}…";

        var done = 0;
        void OnFile(object? sender, ImportResult result)
        {
            done++;
            DispatcherQueue.TryEnqueue(() =>
                ProgressText.Text = $"{result.FileName} — {done} of {paths.Count}");
        }

        _import.FileImported += OnFile;

        try
        {
            var results = await _import.ImportAsync(paths, _navigation.SubjectId);

            var added = results.Count(r => r.Succeeded);
            var duplicates = results.Count(r => r.AlreadyHeld);
            var failed = results.Count - added;

            var message = $"Added {added} file{(added == 1 ? "" : "s")}";
            if (duplicates > 0) message += $", {duplicates} already stored";
            if (failed > 0) message += $", {failed} could not be read";

            Notifications.Show(message + ".", failed > 0 ? NoticeKind.Warning : NoticeKind.Success);
        }
        finally
        {
            _import.FileImported -= OnFile;
            Progress.Visibility = Visibility.Collapsed;
        }

        await ReloadAsync();
    }

    // ------------------------------------------------------------------ drag and drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!_workspace.IsUnlocked || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to Campus";
        e.DragUIOverride.IsGlyphVisible = false;
        DropHint.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
        => DropHint.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (!_workspace.IsUnlocked || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<Windows.Storage.StorageFile>().Select(f => f.Path).ToList();
            if (paths.Count > 0) await ImportAsync(paths);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static Brush Brush(string token) => (Brush)Application.Current.Resources[token];
    private static FontFamily Font(string key) => (FontFamily)Application.Current.Resources[key];
}
