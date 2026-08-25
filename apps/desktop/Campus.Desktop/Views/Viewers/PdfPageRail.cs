using Campus.Desktop.Design;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Campus.Desktop.Views.Viewers;

/// <summary>
/// The strip down the right-hand side of a document that says where you are and takes you
/// somewhere else.
///
/// Windows hides its own scrollbars until the pointer moves, and collapses them to a hairline
/// when it does show them. That is a reasonable default for a list of settings and a bad one for
/// a four-hundred-page book, where the bar is the map: it has to be visible before you look for
/// it, wide enough to catch, and it should tell you what page you are dragging towards rather
/// than making you guess and then read the heading you land on.
///
/// So this is not a scrollbar. It is always there, it is a comfortable size, and it names the
/// page under the thumb while you drag.
/// </summary>
public sealed class PdfPageRail : Grid
{
    private const double TrackWidth = 14;
    private const double MinimumThumb = 28;

    private readonly Rectangle _track = new();
    private readonly Rectangle _thumb = new();
    private readonly Border _callout = new();
    private readonly TextBlock _calloutText = new();

    private ScrollViewer? _scroller;
    private bool _dragging;
    private double _grabOffset;

    /// <summary>Total pages, used only to turn a fraction of the document into a page number.</summary>
    public int PageCount { get; set; }

    public PdfPageRail()
    {
        Width = TrackWidth + 8;
        Padding = new Thickness(4, 8, 4, 8);
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        IsHitTestVisible = true;

        _track.Width = TrackWidth;
        _track.RadiusX = TrackWidth / 2;
        _track.RadiusY = TrackWidth / 2;
        _track.HorizontalAlignment = HorizontalAlignment.Center;
        _track.VerticalAlignment = VerticalAlignment.Stretch;
        _track.Fill = ViewerChrome.Brush(ThemeTokens.Fill.Quaternary);
        Children.Add(_track);

        _thumb.Width = TrackWidth;
        _thumb.RadiusX = TrackWidth / 2;
        _thumb.RadiusY = TrackWidth / 2;
        _thumb.HorizontalAlignment = HorizontalAlignment.Center;
        _thumb.VerticalAlignment = VerticalAlignment.Top;
        _thumb.Fill = ViewerChrome.Brush(ThemeTokens.Fill.Secondary);
        _thumb.Height = MinimumThumb;
        Children.Add(_thumb);

        _calloutText.Style = (Style)Application.Current.Resources["Text.Caption"];
        _calloutText.Foreground = ViewerChrome.Brush(ThemeTokens.Label.Primary);

        _callout.Child = _calloutText;
        _callout.Background = ViewerChrome.Brush(ThemeTokens.Background.Tertiary);
        _callout.BorderBrush = ViewerChrome.Brush(ThemeTokens.Separator.Standard);
        _callout.BorderThickness = new Thickness(1);
        _callout.CornerRadius = new CornerRadius(6);
        _callout.Padding = new Thickness(8, 3, 8, 3);
        _callout.HorizontalAlignment = HorizontalAlignment.Right;
        _callout.VerticalAlignment = VerticalAlignment.Top;
        _callout.Margin = new Thickness(0, 0, TrackWidth + 12, 0);
        _callout.Visibility = Visibility.Collapsed;
        // Outside the rail's own width, so the callout can sit over the page it refers to.
        _callout.IsHitTestVisible = false;
        Children.Add(_callout);

        AutomationProperties.SetName(this, "Document position");

        PointerEntered += (_, _) => Highlight(true);
        PointerExited += (_, _) => { if (!_dragging) Highlight(false); };
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => EndDrag();

        SizeChanged += (_, _) => Sync();
    }

    /// <summary>Raised while the thumb is dragged, so the viewer can update its page indicator.</summary>
    public event EventHandler<int>? PageScrubbed;

    /// <summary>Attaches to the scroller that actually moves. Safe to call more than once.</summary>
    public void Attach(ScrollViewer scroller)
    {
        if (ReferenceEquals(_scroller, scroller)) return;

        _scroller = scroller;
        _scroller.ViewChanged += (_, _) => { if (!_dragging) Sync(); };
        Sync();
    }

    // ------------------------------------------------------------------ where the thumb sits

    /// <summary>Redraws the thumb from the scroller's position. Ignored while it is being dragged.</summary>
    public void Sync()
    {
        if (_scroller is null || ActualHeight <= 0) return;

        var extent = _scroller.ExtentHeight;
        var viewport = _scroller.ViewportHeight;
        if (extent <= 0 || viewport <= 0) return;

        var travel = TrackHeight;
        if (travel <= 0) return;

        // A thumb whose height is the fraction of the document on screen, floored so that a very
        // long document still leaves something you can actually grab.
        var height = Math.Max(MinimumThumb, travel * Math.Min(1, viewport / extent));
        _thumb.Height = height;

        var scrollable = Math.Max(1, extent - viewport);
        var progress = Math.Clamp(_scroller.VerticalOffset / scrollable, 0, 1);

        _thumb.Margin = new Thickness(0, Padding.Top + progress * (travel - height), 0, 0);
        _callout.Margin = new Thickness(
            0, Padding.Top + progress * (travel - height) + height / 2 - 12, TrackWidth + 12, 0);
    }

    private double TrackHeight => ActualHeight - Padding.Top - Padding.Bottom;

    private void Highlight(bool on) =>
        _thumb.Fill = ViewerChrome.Brush(on ? ThemeTokens.Fill.Primary : ThemeTokens.Fill.Secondary);

    // ------------------------------------------------------------------ dragging

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_scroller is null) return;

        var y = e.GetCurrentPoint(this).Position.Y;
        var top = Padding.Top + _thumb.Margin.Top - Padding.Top;

        // Clicking the track rather than the thumb jumps there, the way a scrollbar's page-jump
        // does — and then keeps dragging from that point, which is what a reader means by it.
        if (y < _thumb.Margin.Top || y > _thumb.Margin.Top + _thumb.Height)
        {
            _grabOffset = _thumb.Height / 2;
            ScrubTo(y);
        }
        else
        {
            _grabOffset = y - _thumb.Margin.Top;
        }

        _ = top;
        _dragging = true;
        Highlight(true);
        _callout.Visibility = Visibility.Visible;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        ScrubTo(e.GetCurrentPoint(this).Position.Y);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (!_dragging) return;

        _dragging = false;
        _callout.Visibility = Visibility.Collapsed;
        Highlight(false);
        Sync();
    }

    private void ScrubTo(double pointerY)
    {
        if (_scroller is null) return;

        var travel = TrackHeight - _thumb.Height;
        if (travel <= 0) return;

        var top = Math.Clamp(pointerY - _grabOffset, 0, travel);
        var progress = top / travel;

        var scrollable = Math.Max(1, _scroller.ExtentHeight - _scroller.ViewportHeight);
        _scroller.ChangeView(null, progress * scrollable, null, disableAnimation: true);

        _thumb.Margin = new Thickness(0, Padding.Top + top, 0, 0);
        _callout.Margin = new Thickness(0, Padding.Top + top + _thumb.Height / 2 - 12, TrackWidth + 12, 0);

        if (PageCount <= 0) return;

        var page = Math.Clamp((int)(progress * PageCount) + 1, 1, PageCount);
        _calloutText.Text = $"Page {page}";
        PageScrubbed?.Invoke(this, page - 1);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PdfPageRailPeer(this);

    /// <summary>
    /// Named for screen readers as a scrollbar, because that is what it is for even though it is
    /// not what it is built from.
    /// </summary>
    private sealed class PdfPageRailPeer(PdfPageRail owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.ScrollBar;
    }
}
