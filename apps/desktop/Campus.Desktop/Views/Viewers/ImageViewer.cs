using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Documents;
using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Campus.Desktop.Views.Viewers;

/// <summary>
/// Shows a picture.
///
/// Pinch and Ctrl+wheel zoom, double-click to fit, and a rotation that turns the image on screen
/// without touching the stored bytes — a scan that arrived sideways can be read without editing
/// the original, which would change its hash and its identity in the vault.
/// </summary>
public sealed class ImageViewer : Grid, IContentViewer
{
    private readonly ScrollViewer _scroller = new();
    private readonly Image _image = new();
    private readonly RotateTransform _rotation = new();
    private readonly TextBlock _status = ViewerChrome.ToolLabel();
    private readonly Border _frame = new();

    private int _turns;

    public ImageViewer()
    {
        Background = ViewerChrome.Brush(ThemeTokens.Background.Secondary);

        _image.Stretch = Stretch.Uniform;
        _image.RenderTransformOrigin = new Point(0.5, 0.5);
        _image.RenderTransform = _rotation;
        AutomationProperties.SetName(_image, L.T("image"));

        // The image sits inside a container so that a rotation can swap the space it occupies
        // without fighting the scroller over layout.
        _frame.Child = _image;
        _frame.HorizontalAlignment = HorizontalAlignment.Center;
        _frame.VerticalAlignment = VerticalAlignment.Center;

        _scroller.Content = _frame;
        _scroller.ZoomMode = ZoomMode.Enabled;
        _scroller.MinZoomFactor = 0.1f;
        _scroller.MaxZoomFactor = 16f;
        _scroller.HorizontalScrollMode = ScrollMode.Auto;
        _scroller.VerticalScrollMode = ScrollMode.Auto;
        _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroller.Padding = new Thickness(24);
        _scroller.ViewChanged += (_, _) => UpdateStatus();
        _scroller.DoubleTapped += OnDoubleTapped;

        Children.Add(_scroller);
    }

    private BitmapImage? _bitmap;
    private FilePayload? _payload;

    public async Task LoadAsync(Stream content, CampusObject entity, FilePayload payload)
    {
        _payload = payload;

        var busy = ViewerChrome.Busy("Opening");
        Children.Add(busy);

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await content.CopyToAsync(stream.AsStreamForWrite());
            stream.Seek(0);

            _bitmap = new BitmapImage();
            await _bitmap.SetSourceAsync(stream);
            _image.Source = _bitmap;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            Children.Add(new TextBlock
            {
                Text = L.T("this.image.could.not.be.decoded"),
                Style = (Style)Application.Current.Resources["Text.Callout"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        finally
        {
            Children.Remove(busy);
        }

        UpdateStatus();
    }

    // ------------------------------------------------------------------------- tools

    public IEnumerable<FrameworkElement> BuildTools()
    {
        yield return _status;
        yield return ViewerChrome.ToolButton(CampusSymbols.ZoomOut, "Zoom out", () => Zoom(1 / 1.25f));
        yield return ViewerChrome.ToolButton(CampusSymbols.ZoomIn, "Zoom in", () => Zoom(1.25f));
        yield return ViewerChrome.ToolButton(CampusSymbols.FitPage, "Fit", Fit);
        yield return ViewerChrome.ToolButton(CampusSymbols.Rotate, "Rotate", Rotate);
    }

    private void Zoom(float factor)
    {
        var target = Math.Clamp(_scroller.ZoomFactor * factor,
            _scroller.MinZoomFactor, _scroller.MaxZoomFactor);
        _scroller.ChangeView(null, null, target);
    }

    private void Fit() => _scroller.ChangeView(null, null, 1f);

    private void Rotate()
    {
        _turns = (_turns + 1) % 4;
        _rotation.Angle = _turns * 90;

        // A quarter turn swaps the picture's width and height, and the container has to swap
        // with it or the rotated image is cropped by its own bounds.
        if (_bitmap is not null)
        {
            var sideways = _turns % 2 == 1;
            _frame.Width = sideways ? _bitmap.PixelHeight : double.NaN;
            _frame.Height = sideways ? _bitmap.PixelWidth : double.NaN;
        }

        UpdateStatus();
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Second click on an already-fitted image goes to actual pixels, which is the gesture
        // every other image viewer has trained people to expect.
        _scroller.ChangeView(null, null, Math.Abs(_scroller.ZoomFactor - 1f) < 0.01f ? 2f : 1f);
    }

    private void UpdateStatus()
    {
        var size = _payload is { PixelWidth: { } w, PixelHeight: { } h } ? $"{w}×{h}  ·  " : "";
        var turned = _turns == 0 ? "" : $"  ·  {_turns * 90}°";
        _status.Text = $"{size}{_scroller.ZoomFactor * 100:0}%{turned}";
    }
}
