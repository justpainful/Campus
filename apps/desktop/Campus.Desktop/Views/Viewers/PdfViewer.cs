using System.Collections.ObjectModel;
using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Documents;
using Campus.Domain;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.System;

namespace Campus.Desktop.Views.Viewers;

/// <summary>
/// Reads a PDF.
///
/// Pages render on demand and at the size they are being shown, so opening a three-hundred-page
/// textbook costs one page of work and zooming re-renders rather than magnifying a blurry bitmap.
/// Beside them sits whichever of three things is useful at the time: the page thumbnails, the
/// document's own table of contents, or the results of searching inside it.
///
/// Highlights and notes are kept next to the file rather than written into it. Writing into the
/// PDF would change its bytes, and the bytes are its identity in the vault — the same textbook on
/// two devices would stop being the same object the moment one of them marked a paragraph.
/// </summary>
public sealed class PdfViewer : Grid, IContentViewer
{
    private const double BaseWidth = 900;

    /// <summary>
    /// One reader at a time on the document's stream.
    ///
    /// A viewer counts pages, measures them, renders several of them as they scroll into view,
    /// reads the outline and searches the text — and all of it comes from one seekable stream over
    /// one encrypted file. Every one of those operations seeks to the beginning first, so two of
    /// them running at once do not merely queue: they move the position under each other, and the
    /// second one finds a document that appears to start in the middle.
    /// </summary>
    private readonly SemaphoreSlim _stream = new(1, 1);

    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();

    private readonly ObservableCollection<PdfPageView> _pages = [];
    private readonly ListView _list = new();
    private readonly PdfPageRail _rail = new();
    private ScrollViewer? _scroller;

    private readonly ColumnDefinition _sideColumn = new() { Width = new GridLength(0) };
    private readonly Grid _side = new();
    private readonly ListView _outline = new();
    private readonly ListView _thumbnails = new();
    private readonly StackPanel _searchPanel = new();
    private readonly TextBox _searchBox = new();
    private readonly ListView _searchResults = new();

    private readonly TextBox _pageBox = new();
    private readonly TextBlock _pageLabel = ViewerChrome.ToolLabel();
    private readonly Dictionary<int, ImageSource> _rendered = [];

    private Stream? _content;
    private CampusObject? _entity;
    private double _zoom = 1.0;
    private int _turns;
    private int _current;
    private PageTool _tool = PageTool.None;
    private string _colour = ThemeTokens.Highlight.Yellow;
    private IReadOnlyList<PdfOutlineEntry> _outlineEntries = [];
    private bool _outlineLoaded;
    private bool _thumbnailsBuilt;
    private IReadOnlyList<PdfMatch> _matches = [];

    public PdfViewer()
    {
        Background = ViewerChrome.Brush(ThemeTokens.Background.Secondary);

        ColumnDefinitions.Add(_sideColumn);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        BuildSide();

        _list.ItemsSource = _pages;
        _list.SelectionMode = ListViewSelectionMode.None;
        _list.Padding = new Thickness(0, 20, 22, 60);
        // The list fills the pane and centres its pages, rather than shrinking to the width of a
        // page. Those look identical until you go looking for the scrollbar, which in the second
        // case is floating somewhere in the middle of the window beside the paper instead of down
        // the edge where a reader reaches for it.
        _list.HorizontalAlignment = HorizontalAlignment.Stretch;
        _list.HorizontalContentAlignment = HorizontalAlignment.Center;
        _list.ItemContainerStyle = ContainerStyle();
        _list.ContainerContentChanging += OnContainerChanging;
        AutomationProperties.SetName(_list, L.T("pages"));

        // A permanent scrollbar, not the thin overlay that appears when the pointer is already
        // moving. In a four-hundred-page document the bar is how you know where you are and the
        // only way to cover distance by dragging — hiding it until it is touched means it cannot
        // be touched.
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Visible);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);

        SetColumn(_list, 1);
        Children.Add(_list);

        _rail.HorizontalAlignment = HorizontalAlignment.Right;
        _rail.PageScrubbed += (_, page) =>
        {
            _current = page;
            UpdatePageLabel();
        };
        SetColumn(_rail, 1);
        Children.Add(_rail);

        // Not in the constructor: a ListView has no template, and therefore no ScrollViewer inside
        // it, until it has been loaded — so asking now returns null and the page indicator never
        // moves again.
        _list.Loaded += (_, _) =>
        {
            if (_scroller is not null) return;

            _scroller = _list.FindScrollViewer();
            if (_scroller is null) return;

            _scroller.ViewChanged += (_, _) => UpdatePageLabel();
            _rail.Attach(_scroller);
            UpdatePageLabel();
        };

        // The whole viewer takes the keyboard, so the usual reading keys work without first
        // having to click on the right thing.
        IsTabStop = true;
        KeyboardAccelerators.Add(Accelerator(VirtualKey.G, VirtualKeyModifiers.Control,
            () => { _pageBox.Focus(FocusState.Programmatic); _pageBox.SelectAll(); }));
        KeyDown += OnKeyDown;
    }

    private static KeyboardAccelerator Accelerator(VirtualKey key, VirtualKeyModifiers modifiers,
        Action invoke)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            e.Handled = true;
            invoke();
        };
        return accelerator;
    }

    /// <summary>
    /// Reading keys: a page at a time, or straight to either end of the document.
    ///
    /// Space and the arrows are left alone — those scroll by a screenful, which is what a reader
    /// expects them to do, and the list already handles them.
    /// </summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Not while a number is being typed into the page box, or a phrase into the search box.
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;

        switch (e.Key)
        {
            case VirtualKey.PageDown:
                Step(1);
                break;
            case VirtualKey.PageUp:
                Step(-1);
                break;
            case VirtualKey.Home:
                GoTo(0);
                break;
            case VirtualKey.End:
                GoTo(_pages.Count - 1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private static Style ContainerStyle()
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        // A Setter refuses a null value, so "no background" has to be said with a brush.
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center));
        return style;
    }

    // -------------------------------------------------------------------- the sidebar

    private void BuildSide()
    {
        _side.Background = ViewerChrome.Brush(ThemeTokens.Background.Primary);
        _side.BorderBrush = ViewerChrome.Brush(ThemeTokens.Separator.Standard);
        _side.BorderThickness = new Thickness(0, 0, 1, 0);

        _outline.SelectionMode = ListViewSelectionMode.Single;
        _outline.Visibility = Visibility.Collapsed;
        _outline.Padding = new Thickness(6, 10, 6, 20);
        _outline.SelectionChanged += (_, _) =>
        {
            if (_outline.SelectedIndex >= 0 && _outline.SelectedIndex < _outlineEntries.Count)
                GoTo(_outlineEntries[_outline.SelectedIndex].PageIndex);
        };
        AutomationProperties.SetName(_outline, L.T("contents"));

        _thumbnails.SelectionMode = ListViewSelectionMode.Single;
        _thumbnails.Visibility = Visibility.Collapsed;
        _thumbnails.Padding = new Thickness(8, 10, 8, 20);
        _thumbnails.SelectionChanged += (_, _) =>
        {
            if (_thumbnails.SelectedIndex >= 0) GoTo(_thumbnails.SelectedIndex);
        };
        AutomationProperties.SetName(_thumbnails, L.T("page.thumbnails"));

        _searchBox.PlaceholderText = L.T("find.in.document");
        _searchBox.Style = (Style)Application.Current.Resources["Input.Search"];
        _searchBox.Margin = new Thickness(10, 10, 10, 8);
        _searchBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) await SearchAsync();
        };
        AutomationProperties.SetName(_searchBox, L.T("find.in.document"));

        _searchResults.SelectionMode = ListViewSelectionMode.Single;
        _searchResults.Padding = new Thickness(4, 0, 4, 20);
        _searchResults.SelectionChanged += (_, _) =>
        {
            if (_searchResults.SelectedIndex >= 0 && _searchResults.SelectedIndex < _matches.Count)
                GoTo(_matches[_searchResults.SelectedIndex].PageIndex);
        };

        _searchPanel.Visibility = Visibility.Collapsed;
        _searchPanel.Children.Add(_searchBox);
        _searchPanel.Children.Add(_searchResults);

        _side.Children.Add(_thumbnails);
        _side.Children.Add(_outline);
        _side.Children.Add(_searchPanel);
        Children.Add(_side);
    }

    // ------------------------------------------------------------------------ loading

    public async Task LoadAsync(Stream content, CampusObject entity, FilePayload payload)
    {
        _content = content;
        _entity = entity;

        var busy = ViewerChrome.Busy("Opening");
        SetColumn(busy, 1);
        Children.Add(busy);

        try
        {
            // Two cheap reads, and then the document is on screen.
            //
            // It used to measure every page before showing anything, which on a three-hundred-page
            // textbook is three hundred round trips into PDFium before the first pixel — and then
            // parse the whole document again for its table of contents. Both are now avoided: the
            // first page's shape stands in for the rest, each page corrects itself the moment it
            // renders, and the contents are read only if somebody opens the contents pane.
            var count = await ReadAsync(() => PdfRenderer.PageCount(content));
            if (count == 0)
            {
                Notifications.Show(L.T("this.pdf.could.not.be.read"), NoticeKind.Error);
                return;
            }

            var first = await ReadAsync(() => PdfRenderer.PageSize(content, 0));
            var ratio = first is { Width: > 0 } size ? size.Height / size.Width : 1.414;

            _pages.Clear();
            for (var i = 0; i < count; i++) _pages.Add(BuildPage(i, ratio));

            _rail.PageCount = count;
            await LoadAnnotationsAsync();
        }
        finally
        {
            Children.Remove(busy);
        }

        UpdatePageLabel();
    }

    private PdfPageView BuildPage(int index, double ratio)
    {
        var page = new PdfPageView(index, ratio) { Tool = _tool, ColourToken = _colour };
        page.Resize(BaseWidth * _zoom);

        page.AnnotationDrawn += async (_, annotation) =>
        {
            if (_entity is null) return;

            annotation.ObjectId = _entity.Id;
            await _workspace.Annotations.SaveAsync(annotation);
            await _workspace.History.RecordAsync(_entity.Id, "annotated", $"page {index + 1}");
        };

        page.AnnotationInvoked += (sender, annotation) =>
        {
            if (sender is FrameworkElement anchor) ShowAnnotationMenu(anchor, annotation);
        };

        return page;
    }

    private async Task LoadAnnotationsAsync()
    {
        if (_entity is null) return;

        var annotations = await _workspace.Annotations.ForObjectAsync(_entity.Id);
        var byPage = annotations.Where(a => a.Page is not null).ToLookup(a => a.Page!.Value);

        foreach (var page in _pages) page.SetAnnotations(byPage[page.PageIndex]);
    }

    // ------------------------------------------------------------------------- render

    private void OnContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not PdfPageView page) return;
        if (!page.IsRendered) _ = RenderAsync(page);
    }

    private async Task RenderAsync(PdfPageView page)
    {
        if (_content is null) return;

        if (_rendered.TryGetValue(page.PageIndex, out var cached))
        {
            page.SetImage(cached);
            return;
        }

        var width = (int)Math.Clamp(BaseWidth * _zoom * 1.5, 400, 2400);

        var png = await ReadAsync(() => PdfRenderer.RenderPage(_content, page.PageIndex, width));
        if (png is null) return;

        var image = await DecodeAsync(png);
        _rendered[page.PageIndex] = image;

        // The rendered bitmap knows the page's real shape, so a document whose pages are not all
        // the same size corrects itself as it is read rather than being measured up front.
        if (image is BitmapImage { PixelWidth: > 0 } bitmap)
            page.Reshape((double)bitmap.PixelHeight / bitmap.PixelWidth);

        page.SetImage(image);
    }

    /// <summary>Runs one read of the document, off the UI thread and alone on the stream.</summary>
    private async Task<T> ReadAsync<T>(Func<T> read)
    {
        await _stream.WaitAsync();
        try
        {
            return await Task.Run(read);
        }
        finally
        {
            _stream.Release();
        }
    }

    private static async Task<BitmapImage> DecodeAsync(byte[] png)
    {
        var image = new BitmapImage();

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        await image.SetSourceAsync(stream);
        return image;
    }

    // -------------------------------------------------------------------------- sides

    /// <summary>
    /// Reads the document's table of contents, once, the first time it is asked for.
    ///
    /// Parsing a large PDF for its bookmarks costs about as much as opening it, and most reading
    /// never needs them — so it is not part of opening the file.
    /// </summary>
    private async Task LoadOutlineAsync()
    {
        if (_outlineLoaded || _content is null) return;
        _outlineLoaded = true;

        _outline.Items.Clear();
        _outline.Items.Add(new TextBlock
        {
            Text = L.T("reading.the.contents"),
            Style = (Style)Application.Current.Resources["Text.Caption"],
            Margin = new Thickness(10, 6, 10, 6),
        });

        _outlineEntries = await ReadAsync(() => PdfText.Outline(_content));
        BuildOutline();

        if (_outlineEntries.Count == 0)
        {
            _outline.Items.Add(new TextBlock
            {
                Text = L.T("this.document.has.no.table.of.contents"),
                Style = (Style)Application.Current.Resources["Text.Caption"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 6, 10, 6),
            });
        }
    }

    private void BuildOutline()
    {
        _outline.Items.Clear();

        foreach (var entry in _outlineEntries)
        {
            _outline.Items.Add(new ListViewItem
            {
                Content = new TextBlock
                {
                    Text = entry.Title,
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    Foreground = ViewerChrome.Brush(entry.Level == 0
                        ? ThemeTokens.Label.Primary
                        : ThemeTokens.Label.Secondary),
                    FontWeight = entry.Level == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                },
                Padding = new Thickness(10 + entry.Level * 14, 6, 8, 6),
                MinHeight = 0,
            });
        }
    }

    /// <summary>
    /// Fills the thumbnail strip, once, the first time it is opened.
    ///
    /// Building a row per page is cheap per page and expensive per book — a thousand-page
    /// reference costs five thousand elements — so it is not part of opening the document. The
    /// images inside the rows are lazier still: see below.
    /// </summary>
    private void BuildThumbnails()
    {
        if (_thumbnailsBuilt || _content is null) return;
        _thumbnailsBuilt = true;

        _thumbnails.Items.Clear();

        for (var i = 0; i < _pages.Count; i++)
        {
            var image = new Image { Stretch = Stretch.Fill };

            var sheet = new Border
            {
                Width = 108,
                Height = 108 * _pages[i].AspectRatio,
                Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                CornerRadius = new CornerRadius(2),
                Child = image,
            };

            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(sheet);
            stack.Children.Add(new TextBlock
            {
                Text = (i + 1).ToString(),
                Style = (Style)Application.Current.Resources["Text.Caption"],
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            var index = i;
            var item = new ListViewItem { Content = stack, Padding = new Thickness(4) };

            // A thumbnail renders only when its row actually appears, at a size that costs a
            // fraction of a page.
            item.Loaded += async (_, _) =>
            {
                if (image.Source is not null || _content is null) return;

                var png = await ReadAsync(() => PdfRenderer.RenderPage(_content, index, 160));
                if (png is not null) image.Source = await DecodeAsync(png);
            };

            _thumbnails.Items.Add(item);
        }
    }

    private async Task SearchAsync()
    {
        _searchResults.Items.Clear();

        var phrase = _searchBox.Text.Trim();
        if (phrase.Length < 2 || _content is null) return;

        _searchResults.Items.Add(new TextBlock
        {
            Text = L.T("searching"),
            Style = (Style)Application.Current.Resources["Text.Caption"],
            Margin = new Thickness(10, 4, 10, 4),
        });

        _matches = await ReadAsync(() => PdfText.Search(_content, phrase));
        _searchResults.Items.Clear();

        if (_matches.Count == 0)
        {
            _searchResults.Items.Add(new TextBlock
            {
                Text = $"Nothing in this document says “{phrase}”.",
                Style = (Style)Application.Current.Resources["Text.Caption"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10, 4, 10, 4),
            });
            return;
        }

        foreach (var match in _matches)
        {
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new TextBlock
            {
                Text = $"Page {match.PageIndex + 1}",
                Style = (Style)Application.Current.Resources["Text.Caption"],
            });
            stack.Children.Add(new TextBlock
            {
                Text = match.Context,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ViewerChrome.Brush(ThemeTokens.Label.Secondary),
            });

            _searchResults.Items.Add(new ListViewItem
            {
                Content = stack,
                Padding = new Thickness(10, 8, 10, 8),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MinHeight = 0,
            });
        }
    }

    private void ShowSide(FrameworkElement panel)
    {
        var closing = panel.Visibility == Visibility.Visible;

        _thumbnails.Visibility = Visibility.Collapsed;
        _outline.Visibility = Visibility.Collapsed;
        _searchPanel.Visibility = Visibility.Collapsed;

        if (closing)
        {
            _sideColumn.Width = new GridLength(0);
            return;
        }

        panel.Visibility = Visibility.Visible;
        _sideColumn.Width = new GridLength(panel == _thumbnails ? 150 : 280);

        if (panel == _searchPanel) _searchBox.Focus(FocusState.Programmatic);
    }

    // -------------------------------------------------------------------------- tools

    public IEnumerable<FrameworkElement> BuildTools()
    {
        yield return ViewerChrome.ToolButton(CampusSymbols.ChevronUp, "Previous page",
            () => Step(-1));
        yield return ViewerChrome.ToolButton(CampusSymbols.ChevronDown, "Next page",
            () => Step(1));

        // A page number you can type into. Everything else about reading a long document is
        // scrolling; going to page 214 is not, and hunting for it by dragging is miserable.
        _pageBox.Width = 52;
        _pageBox.TextAlignment = TextAlignment.Center;
        _pageBox.Style = (Style)Application.Current.Resources["Input.Text"];
        _pageBox.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            JumpToTypedPage();
        };
        _pageBox.LostFocus += (_, _) => UpdatePageLabel();
        AutomationProperties.SetName(_pageBox, L.T("page.number"));
        yield return _pageBox;

        yield return _pageLabel;

        yield return ViewerChrome.ToolButton(CampusSymbols.Thumbnails, "Page thumbnails",
            () => { BuildThumbnails(); ShowSide(_thumbnails); });

        // Always offered: whether the document has a table of contents is not known until it has
        // been read, and reading it is what opening this does.
        yield return ViewerChrome.ToolButton(CampusSymbols.Outline, "Contents",
            () => _ = ShowOutlineAsync());

        yield return ViewerChrome.ToolButton(CampusSymbols.Search, "Find in document",
            () => ShowSide(_searchPanel));

        yield return ViewerChrome.ToolMenu(CampusSymbols.Highlighter, "Mark up",
        [
            ("Read", () => SetTool(PageTool.None)),
            ("Highlight", () => SetTool(PageTool.Highlight)),
            ("Draw", () => SetTool(PageTool.Ink)),
            ("Note", () => SetTool(PageTool.Comment)),
        ], "Read");

        yield return ViewerChrome.ToolMenu(CampusSymbols.Palette, "Colour",
            ThemeTokens.Highlight.All
                .Select(token => (Name(token), (Action)(() => Apply(token))))
                .ToList(),
            "Yellow");

        yield return ViewerChrome.ToolButton(CampusSymbols.ZoomOut, "Zoom out", () => SetZoom(_zoom / 1.25));
        yield return ViewerChrome.ToolButton(CampusSymbols.ZoomIn, "Zoom in", () => SetZoom(_zoom * 1.25));
        yield return ViewerChrome.ToolButton(CampusSymbols.FitWidth, "Fit width", () => SetZoom(FitWidth()));
        yield return ViewerChrome.ToolButton(CampusSymbols.Rotate, "Rotate", Rotate);
        yield return ViewerChrome.ToolButton(CampusSymbols.Print, "Print", () => _ = PrintAsync());
    }

    private static string Name(string token) => token[(token.LastIndexOf('.') + 1)..];

    private void Apply(string token)
    {
        _colour = token;
        foreach (var page in _pages) page.ColourToken = token;
    }

    private void SetTool(PageTool tool)
    {
        _tool = tool;
        foreach (var page in _pages) page.Tool = tool;

        Notifications.Show(tool switch
        {
            PageTool.Highlight => "Drag across a line to highlight it.",
            PageTool.Ink => "Draw on the page.",
            PageTool.Comment => "Click where the note belongs.",
            _ => "Back to reading.",
        });
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.25, 4.0);

        // Every cached bitmap is now the wrong resolution. Scaling them instead is exactly what
        // makes a PDF reader look blurry when you zoom in.
        _rendered.Clear();

        foreach (var page in _pages)
        {
            page.Resize(BaseWidth * _zoom);
            page.Forget();
            _ = RenderAsync(page);
        }

        UpdatePageLabel();
    }

    private void Rotate()
    {
        _turns = (_turns + 1) % 4;
        foreach (var page in _pages) page.Rotate(_turns);
    }

    private double FitWidth()
    {
        var available = _scroller?.ViewportWidth ?? ActualWidth;
        return available <= 0 ? 1.0 : Math.Clamp((available - 48) / BaseWidth, 0.2, 4.0);
    }

    private void GoTo(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _pages.Count) return;

        _current = pageIndex;
        _list.ScrollIntoView(_pages[pageIndex], ScrollIntoViewAlignment.Leading);
        UpdatePageLabel();
    }

    /// <summary>Moves one page forward or back from wherever the reader is.</summary>
    private void Step(int direction) => GoTo(Math.Clamp(_current + direction, 0, _pages.Count - 1));

    private void JumpToTypedPage()
    {
        if (!int.TryParse(_pageBox.Text.Trim(), out var page))
        {
            UpdatePageLabel();
            return;
        }

        if (page < 1 || page > _pages.Count)
        {
            Notifications.Show(
                L.T("document.has", Plural.Of("page.count", _pages.Count)), NoticeKind.Warning);
            UpdatePageLabel();
            return;
        }

        // Typed page numbers are what is printed on the page, which starts at one.
        GoTo(page - 1);
    }

    private async Task ShowOutlineAsync()
    {
        await LoadOutlineAsync();
        ShowSide(_outline);
    }

    /// <summary>
    /// Prints through whatever Windows normally prints a PDF with. Campus does not reimplement a
    /// print pipeline to produce a worse copy of the one already on the machine.
    /// </summary>
    private async Task PrintAsync()
    {
        if (_entity?.PayloadAs<FilePayload>() is not { } payload) return;

        var temporary = Path.Combine(Path.GetTempPath(), "Campus", payload.OriginalFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);

        try
        {
            await _workspace.Vault.Objects.ExportAsync(payload.ContentHash, temporary);

            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = temporary,
                    Verb = "print",
                    UseShellExecute = true,
                });

            Notifications.Show(L.T("sent.to.the.printer.the.copy.it.printed.from.i.41c94d"));
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            Notifications.Show(L.T("windows.could.not.print.this.file"), NoticeKind.Error);
        }
    }

    // -------------------------------------------------------------------- annotations

    private void ShowAnnotationMenu(FrameworkElement anchor, Annotation annotation)
    {
        var menu = new MenuFlyout();

        menu.Items.Add(ObjectCommands.Item(
            annotation.Text is { Length: > 0 } ? "Edit note" : "Add a note",
            CampusSymbols.Comment,
            () => EditAnnotationAsync(annotation)));

        var colours = new MenuFlyoutSubItem { Text = L.T("colour") };
        foreach (var token in ThemeTokens.Highlight.All)
        {
            var name = Name(token);
            var item = new MenuFlyoutItem { Text = name };
            item.Click += async (_, _) =>
            {
                annotation.ColorName = name;
                await _workspace.Annotations.SaveAsync(annotation);
                await LoadAnnotationsAsync();
            };
            colours.Items.Add(item);
        }
        menu.Items.Add(colours);

        menu.Items.Add(new MenuFlyoutSeparator());

        var remove = ObjectCommands.Item(L.T("remove"), CampusSymbols.Delete, async () =>
        {
            await _workspace.Annotations.DeleteAsync(annotation.Id);
            await LoadAnnotationsAsync();
        });
        remove.Foreground = ViewerChrome.Brush(ThemeTokens.Destructive.Primary);
        menu.Items.Add(remove);

        menu.ShowAt(anchor);
    }

    private async Task EditAnnotationAsync(Annotation annotation)
    {
        var input = new TextBox
        {
            Text = annotation.Text ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 130,
            Width = 360,
            Style = (Style)Application.Current.Resources["Input.Text"],
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Note on page {(annotation.Page ?? 0) + 1}",
            Content = input,
            PrimaryButtonText = L.T("save"),
            CloseButtonText = L.T("cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        annotation.Text = input.Text.Trim() is { Length: > 0 } text ? text : null;
        await _workspace.Annotations.SaveAsync(annotation);
        await LoadAnnotationsAsync();
    }

    private void UpdatePageLabel()
    {
        if (_pages.Count == 0) return;

        if (_scroller is { ExtentHeight: > 0 })
        {
            var progress = _scroller.VerticalOffset / Math.Max(1, _scroller.ExtentHeight);
            _current = Math.Clamp((int)(progress * _pages.Count), 0, _pages.Count - 1);
        }

        // Not while it is being typed into: rewriting the box under the cursor is how a page
        // number becomes impossible to enter.
        if (_pageBox.FocusState == FocusState.Unfocused)
            _pageBox.Text = (_current + 1).ToString();

        _pageLabel.Text = $"of {_pages.Count}  ·  {_zoom * 100:0}%";
    }
}

internal static class ListViewExtensions
{
    /// <summary>Finds the ScrollViewer inside a list, which is where its scroll position lives.</summary>
    public static ScrollViewer? FindScrollViewer(this DependencyObject root)
    {
        if (root is ScrollViewer found) return found;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var result = VisualTreeHelper.GetChild(root, i).FindScrollViewer();
            if (result is not null) return result;
        }

        return null;
    }
}
