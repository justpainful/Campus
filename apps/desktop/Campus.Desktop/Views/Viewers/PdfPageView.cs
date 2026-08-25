using Campus.Desktop.Design;
using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Campus.Desktop.Views.Viewers;

/// <summary>What the pointer is doing on a page.</summary>
public enum PageTool
{
    /// <summary>Reading. Drags scroll and select nothing.</summary>
    None = 0,
    Highlight = 1,
    Ink = 2,
    Comment = 3,
}

/// <summary>
/// One page of a PDF: the rendered image, and everything drawn on top of it.
///
/// Annotations are held in page coordinates from zero to one rather than in pixels. That is the
/// whole trick that makes them survive zooming, rotating, re-rendering at a different resolution
/// and being opened on a phone with a different screen — a highlight is on a piece of the page,
/// not at a place on the screen.
/// </summary>
public sealed class PdfPageView : Grid
{
    private readonly Border _sheet = new();
    private readonly Image _image = new();
    private readonly Canvas _overlay = new();
    private readonly RotateTransform _rotation = new();

    private readonly List<Annotation> _annotations = [];
    private Polyline? _drawing;
    private Rectangle? _dragging;
    private Point _origin;

    public PdfPageView(int index, double aspectRatio)
    {
        PageIndex = index;
        AspectRatio = aspectRatio;

        HorizontalAlignment = HorizontalAlignment.Center;
        Margin = new Thickness(0, 0, 0, 16);

        _image.Stretch = Stretch.Fill;

        _overlay.Background = null;
        _overlay.IsHitTestVisible = true;

        var content = new Grid();
        content.Children.Add(_image);
        content.Children.Add(_overlay);

        _sheet.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
        _sheet.CornerRadius = new CornerRadius(2);
        _sheet.Child = content;
        _sheet.RenderTransformOrigin = new Point(0.5, 0.5);
        _sheet.RenderTransform = _rotation;
        _sheet.Shadow = new ThemeShadow();
        _sheet.Translation = new System.Numerics.Vector3(0, 0, 8);

        Children.Add(_sheet);

        AutomationProperties.SetName(this, $"Page {index + 1}");

        _overlay.PointerPressed += OnPointerPressed;
        _overlay.PointerMoved += OnPointerMoved;
        _overlay.PointerReleased += OnPointerReleased;
    }

    public int PageIndex { get; }

    /// <summary>Height over width. Assumed from the first page until this one has been rendered.</summary>
    public double AspectRatio { get; private set; }

    /// <summary>
    /// Corrects the shape once the page has actually been rendered.
    ///
    /// Opening a document measures only its first page, because measuring all of them costs a
    /// round trip each and most documents have one shape throughout. The ones that do not — a
    /// scan with a landscape page in the middle — put themselves right here, as they are read.
    /// </summary>
    public void Reshape(double aspectRatio)
    {
        if (aspectRatio <= 0 || Math.Abs(aspectRatio - AspectRatio) < 0.01) return;

        AspectRatio = aspectRatio;
        Resize(_sheet.Width);
    }

    /// <summary>True once the image has been asked for, so it is not asked for twice.</summary>
    public bool IsRendered { get; private set; }

    /// <summary>Which tool the pointer is holding. Set by the viewer for every page at once.</summary>
    public PageTool Tool { get; set; }

    /// <summary>The highlight colour, as a theme token name rather than a value.</summary>
    public string ColourToken { get; set; } = ThemeTokens.Highlight.Yellow;

    /// <summary>Raised when a new annotation has been drawn and needs saving.</summary>
    public event EventHandler<Annotation>? AnnotationDrawn;

    /// <summary>Raised when one is clicked, so the viewer can offer what to do with it.</summary>
    public event EventHandler<Annotation>? AnnotationInvoked;

    // ------------------------------------------------------------------------ layout

    public void Resize(double width)
    {
        var turned = ((int)_rotation.Angle / 90) % 2 == 1;

        _sheet.Width = width;
        _sheet.Height = width * AspectRatio;

        // A page turned on its side occupies its own height horizontally, so the row it sits in
        // has to make room for that rather than for the page's unrotated shape.
        Width = turned ? _sheet.Height : _sheet.Width;
        Height = turned ? _sheet.Width : _sheet.Height;

        _overlay.Width = _sheet.Width;
        _overlay.Height = _sheet.Height;

        Redraw();
    }

    public void Rotate(int quarterTurns)
    {
        _rotation.Angle = quarterTurns * 90;
        Resize(_sheet.Width);
    }

    public void SetImage(ImageSource? source)
    {
        _image.Source = source;
        IsRendered = source is not null;
    }

    public void Forget()
    {
        _image.Source = null;
        IsRendered = false;
    }

    /// <summary>Replaces what is drawn on this page.</summary>
    public void SetAnnotations(IEnumerable<Annotation> annotations)
    {
        _annotations.Clear();
        _annotations.AddRange(annotations);
        Redraw();
    }

    // ------------------------------------------------------------------------ drawing

    private void Redraw()
    {
        _overlay.Children.Clear();
        if (_sheet.Width <= 0) return;

        foreach (var annotation in _annotations)
        {
            var shape = Build(annotation);
            if (shape is null) continue;

            shape.Tapped += (_, e) =>
            {
                AnnotationInvoked?.Invoke(this, annotation);
                e.Handled = true;
            };

            _overlay.Children.Add(shape);
        }
    }

    private FrameworkElement? Build(Annotation annotation)
    {
        var colour = (Brush)Application.Current.Resources[
            ThemeTokens.Highlight.FromName(annotation.ColorName)];

        switch (annotation.Kind)
        {
            case AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.Strikethrough:
            {
                if (annotation is not { X: { } x, Y: { } y, Width: { } w, Height: { } h }) return null;

                var block = new Rectangle
                {
                    Width = w * _sheet.Width,
                    Height = annotation.Kind == AnnotationKind.Highlight
                        ? h * _sheet.Height
                        : 2,
                    Fill = colour,
                    // A highlight is ink on paper, not a sticker over it: the words underneath
                    // have to stay readable, which is what the alpha is for.
                    Opacity = annotation.Kind == AnnotationKind.Highlight ? 0.35 : 0.9,
                    RadiusX = 2,
                    RadiusY = 2,
                };

                Canvas.SetLeft(block, x * _sheet.Width);
                Canvas.SetTop(block, annotation.Kind == AnnotationKind.Strikethrough
                    ? (y + h / 2) * _sheet.Height
                    : annotation.Kind == AnnotationKind.Underline
                        ? (y + h) * _sheet.Height
                        : y * _sheet.Height);

                AutomationProperties.SetName(block, annotation.Text ?? "Highlight");
                return block;
            }

            case AnnotationKind.Ink when annotation.Geometry is { Length: > 0 } geometry:
            {
                var line = new Polyline
                {
                    Stroke = colour,
                    StrokeThickness = 2.5,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };

                foreach (var point in ParsePoints(geometry))
                    line.Points.Add(new Point(point.X * _sheet.Width, point.Y * _sheet.Height));

                AutomationProperties.SetName(line, "Drawing");
                return line;
            }

            case AnnotationKind.Comment or AnnotationKind.TextBox or AnnotationKind.Bookmark:
            {
                if (annotation is not { X: { } cx, Y: { } cy }) return null;

                var pin = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11, 11, 11, 2),
                    Background = colour,
                    Child = new TextBlock
                    {
                        Text = "•",
                        FontSize = 16,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Primary],
                    },
                };

                ToolTipService.SetToolTip(pin, annotation.Text ?? "Note");
                AutomationProperties.SetName(pin, annotation.Text ?? "Note");

                Canvas.SetLeft(pin, cx * _sheet.Width);
                Canvas.SetTop(pin, cy * _sheet.Height);
                return pin;
            }

            default:
                return null;
        }
    }

    // -------------------------------------------------------------------------- input

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Tool == PageTool.None) return;

        _origin = e.GetCurrentPoint(_overlay).Position;
        _overlay.CapturePointer(e.Pointer);
        e.Handled = true;

        var colour = (Brush)Application.Current.Resources[ColourToken];

        switch (Tool)
        {
            case PageTool.Highlight:
                _dragging = new Rectangle
                {
                    Fill = colour,
                    Opacity = 0.35,
                    RadiusX = 2,
                    RadiusY = 2,
                };
                Canvas.SetLeft(_dragging, _origin.X);
                Canvas.SetTop(_dragging, _origin.Y);
                _overlay.Children.Add(_dragging);
                break;

            case PageTool.Ink:
                _drawing = new Polyline
                {
                    Stroke = colour,
                    StrokeThickness = 2.5,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                _drawing.Points.Add(_origin);
                _overlay.Children.Add(_drawing);
                break;

            case PageTool.Comment:
                Commit(new Annotation
                {
                    Kind = AnnotationKind.Comment,
                    Page = PageIndex,
                    X = _origin.X / _sheet.Width,
                    Y = _origin.Y / _sheet.Height,
                    ColorName = NameOf(ColourToken),
                });
                break;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging is null && _drawing is null) return;

        var point = e.GetCurrentPoint(_overlay).Position;

        if (_dragging is not null)
        {
            Canvas.SetLeft(_dragging, Math.Min(_origin.X, point.X));
            Canvas.SetTop(_dragging, Math.Min(_origin.Y, point.Y));
            _dragging.Width = Math.Abs(point.X - _origin.X);
            _dragging.Height = Math.Abs(point.Y - _origin.Y);
        }

        // Points are thinned as they are collected: a freehand line sampled at pointer rate is
        // thousands of points that all draw the same curve.
        if (_drawing is not null && Far(_drawing.Points[^1], point)) _drawing.Points.Add(point);

        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _overlay.ReleasePointerCapture(e.Pointer);

        if (_dragging is { } box)
        {
            _overlay.Children.Remove(box);
            _dragging = null;

            // A click rather than a drag is not a highlight of nothing; it is a miss.
            if (box.Width > 6 && box.Height > 4)
            {
                Commit(new Annotation
                {
                    Kind = AnnotationKind.Highlight,
                    Page = PageIndex,
                    X = Canvas.GetLeft(box) / _sheet.Width,
                    Y = Canvas.GetTop(box) / _sheet.Height,
                    Width = box.Width / _sheet.Width,
                    Height = box.Height / _sheet.Height,
                    ColorName = NameOf(ColourToken),
                });
            }
        }

        if (_drawing is { } line)
        {
            _overlay.Children.Remove(line);
            _drawing = null;

            if (line.Points.Count > 2)
            {
                Commit(new Annotation
                {
                    Kind = AnnotationKind.Ink,
                    Page = PageIndex,
                    Geometry = string.Join(" ", line.Points.Select(p =>
                        $"{p.X / _sheet.Width:0.####},{p.Y / _sheet.Height:0.####}")),
                    ColorName = NameOf(ColourToken),
                });
            }
        }

        e.Handled = true;
    }

    private void Commit(Annotation annotation)
    {
        _annotations.Add(annotation);
        Redraw();
        AnnotationDrawn?.Invoke(this, annotation);
    }

    private static bool Far(Point a, Point b)
        => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) > 2.5;

    private static string NameOf(string token) => token[(token.LastIndexOf('.') + 1)..];

    private static IEnumerable<Point> ParsePoints(string geometry)
    {
        foreach (var pair in geometry.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(',');
            if (parts.Length != 2) continue;
            if (double.TryParse(parts[0], out var x) && double.TryParse(parts[1], out var y))
                yield return new Point(x, y);
        }
    }
}
