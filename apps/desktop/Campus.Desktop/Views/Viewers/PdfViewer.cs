using System.Collections.ObjectModel;
using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Documents;
using Campus.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Campus.Desktop.Views.Viewers;

/// <summary>One page. The image arrives when the page is actually on screen, not before.</summary>
public sealed partial class PdfPage : ObservableObject
{
    public required int Index { get; init; }
    public required double AspectRatio { get; init; }

    [ObservableProperty]
    public partial ImageSource? Image { get; set; }

    [ObservableProperty]
    public partial double DisplayWidth { get; set; } = 900;

    public double DisplayHeight => DisplayWidth * AspectRatio;
    public string Label => $"Page {Index + 1}";

    partial void OnDisplayWidthChanged(double value) => OnPropertyChanged(nameof(DisplayHeight));
}

/// <summary>
/// Reads a PDF.
///
/// Pages render on demand and at the width they are being shown: opening a three-hundred-page
/// textbook costs one page of work, and zooming re-renders rather than scaling a blurry bitmap.
/// The rendered pages are cached, and the cache is dropped when the zoom changes because those
/// bitmaps are now the wrong size.
/// </summary>
public sealed class PdfViewer : Grid, IContentViewer
{
    private readonly ObservableCollection<PdfPage> _pages = [];
    private readonly ListView _list = new();
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _pageLabel = new();
    private readonly Dictionary<int, ImageSource> _rendered = [];

    private Stream? _content;
    private CampusObject? _entity;
    private double _zoom = 1.0;
    private const double BaseWidth = 900;

    public PdfViewer()
    {
        Background = (Brush)Application.Current.Resources[ThemeTokens.Background.Secondary];

        _list.ItemsSource = _pages;
        _list.SelectionMode = ListViewSelectionMode.None;
        _list.Padding = new Thickness(0, 20, 0, 40);
        _list.HorizontalAlignment = HorizontalAlignment.Center;
        _list.ItemTemplate = BuildTemplate();
        _list.ContainerContentChanging += OnContainerChanging;
        AutomationProperties.SetName(_list, "Pages");

        _list.ItemContainerStyle = BuildContainerStyle();

        Children.Add(_list);
        _scroller = _list.FindScrollViewer();
    }

    private static Style BuildContainerStyle()
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 16)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, null));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center));
        return style;
    }

    /// <summary>
    /// A page is a white sheet with a shadow, sized before its image arrives so that scrolling
    /// does not jump as pages render.
    /// </summary>
    private static DataTemplate BuildTemplate() => (DataTemplate)XamlReader.Load("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <Border Background="White"
                    Width="{Binding DisplayWidth}"
                    Height="{Binding DisplayHeight}"
                    CornerRadius="2">
                <Border.Shadow><ThemeShadow /></Border.Shadow>
                <Grid>
                    <Image Source="{Binding Image}" Stretch="Uniform" />
                </Grid>
            </Border>
        </DataTemplate>
        """);

    public async Task LoadAsync(Stream content, CampusObject entity, FilePayload payload)
    {
        _content = content;
        _entity = entity;

        var count = await Task.Run(() => PdfRenderer.PageCount(content));
        if (count == 0)
        {
            Notifications.Show("This PDF could not be read.", NoticeKind.Error);
            return;
        }

        // Page sizes are read up front so every page can be laid out at the right shape before
        // any of them has been rendered. Otherwise the scroll position moves under the reader.
        var sizes = await Task.Run(() =>
        {
            var ratios = new double[count];
            for (var i = 0; i < count; i++)
            {
                var size = PdfRenderer.PageSize(content, i);
                ratios[i] = size is { Width: > 0 } s ? s.Height / s.Width : 1.414;
            }
            return ratios;
        });

        _pages.Clear();
        for (var i = 0; i < count; i++)
        {
            _pages.Add(new PdfPage
            {
                Index = i,
                AspectRatio = sizes[i],
                DisplayWidth = BaseWidth * _zoom,
            });
        }

        UpdatePageLabel();
        if (_scroller is not null) _scroller.ViewChanged += (_, _) => UpdatePageLabel();
    }

    private void OnContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not PdfPage page) return;
        if (page.Image is not null) return;

        // Phase 0 is when the container first appears; rendering is deferred to a background
        // thread so a fast scroll never blocks on PDFium.
        _ = RenderAsync(page);
    }

    private async Task RenderAsync(PdfPage page)
    {
        if (_content is null) return;

        if (_rendered.TryGetValue(page.Index, out var cached))
        {
            page.Image = cached;
            return;
        }

        var width = (int)Math.Clamp(page.DisplayWidth * 1.5, 400, 2400);
        byte[]? png = null;

        try
        {
            // PDFium is not reentrant on one stream, so renders are serialised.
            await RenderGate.WaitAsync();
            png = await Task.Run(() => PdfRenderer.RenderPage(_content, page.Index, width));
        }
        finally
        {
            RenderGate.Release();
        }

        if (png is null) return;

        var image = new BitmapImage();
        using (var stream = new InMemoryRandomAccessStream())
        {
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await image.SetSourceAsync(stream);
        }

        _rendered[page.Index] = image;
        page.Image = image;
    }

    private static readonly SemaphoreSlim RenderGate = new(1, 1);

    // ------------------------------------------------------------------------- tools

    public IEnumerable<FrameworkElement> BuildTools()
    {
        _pageLabel.Style = (Style)Application.Current.Resources["Text.Caption"];
        _pageLabel.VerticalAlignment = VerticalAlignment.Center;
        _pageLabel.Margin = new Thickness(0, 0, 8, 0);
        yield return _pageLabel;

        yield return ToolButton(CampusSymbols.ZoomOut, "Zoom out", () => SetZoom(_zoom / 1.25));
        yield return ToolButton(CampusSymbols.ZoomIn, "Zoom in", () => SetZoom(_zoom * 1.25));
        yield return ToolButton(CampusSymbols.FitWidth, "Fit width", () => SetZoom(FitWidthZoom()));
        yield return ToolButton(CampusSymbols.FitPage, "Actual size", () => SetZoom(1.0));
    }

    private static Button ToolButton(string symbol, string tooltip, Action action)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Icon"],
            Content = new CampusIcon
            {
                Symbol = symbol,
                IconSize = 18,
                Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Secondary],
            },
        };
        AutomationProperties.SetName(button, tooltip);
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private double FitWidthZoom()
    {
        var available = _scroller?.ViewportWidth ?? ActualWidth;
        return available <= 0 ? 1.0 : Math.Clamp((available - 48) / BaseWidth, 0.2, 4.0);
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.25, 4.0);

        // Every cached bitmap is now the wrong resolution, so they go and the visible pages
        // render again at the new size. Scaling them instead is what makes a PDF reader look
        // blurry when you zoom in.
        _rendered.Clear();

        foreach (var page in _pages)
        {
            page.DisplayWidth = BaseWidth * _zoom;
            page.Image = null;
        }

        UpdatePageLabel();
    }

    private void UpdatePageLabel()
    {
        if (_pages.Count == 0) return;

        var current = 1;
        if (_scroller is { ExtentHeight: > 0 })
        {
            var progress = _scroller.VerticalOffset / Math.Max(1, _scroller.ExtentHeight);
            current = Math.Clamp((int)(progress * _pages.Count) + 1, 1, _pages.Count);
        }

        _pageLabel.Text = $"{current} of {_pages.Count}  ·  {_zoom * 100:0}%";
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
