using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
// System.IO.Path arrives through implicit usings, so the shape is aliased explicitly.
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Campus.Desktop.Design.Brand;

public enum LogoForm
{
    /// <summary>The layered C on its own.</summary>
    Mark = 0,
    /// <summary>The word "Campus" on its own.</summary>
    Wordmark = 1,
    /// <summary>Mark and wordmark side by side, at the correct relative size and gap.</summary>
    Lockup = 2,
}

/// <summary>
/// Draws the Campus identity from the generated geometry. It takes its colour from
/// <see cref="Control.Foreground"/>, so the same control serves the black title bar, a light
/// sheet and the print header without a second asset.
/// </summary>
public sealed class CampusLogo : Control
{
    private Canvas? _canvas;

    public CampusLogo()
    {
        DefaultStyleKey = typeof(CampusLogo);
        IsTabStop = false;
        AutomationProperties.SetName(this, "Campus");
    }

    public static readonly DependencyProperty FormProperty = DependencyProperty.Register(
        nameof(Form), typeof(LogoForm), typeof(CampusLogo),
        new PropertyMetadata(LogoForm.Mark, OnVisualChanged));

    public LogoForm Form
    {
        get => (LogoForm)GetValue(FormProperty);
        set => SetValue(FormProperty, value);
    }

    public static readonly DependencyProperty LogoHeightProperty = DependencyProperty.Register(
        nameof(LogoHeight), typeof(double), typeof(CampusLogo),
        new PropertyMetadata(24d, OnVisualChanged));

    /// <summary>Rendered height in pixels. Width follows from the form's aspect ratio.</summary>
    public double LogoHeight
    {
        get => (double)GetValue(LogoHeightProperty);
        set => SetValue(LogoHeightProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _canvas = GetTemplateChild("PART_Canvas") as Canvas;
        Rebuild();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CampusLogo)d).Rebuild();

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];

    private static Geometry Parse(string data)
        => (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data);

    private void Rebuild()
    {
        if (_canvas is null) return;
        _canvas.Children.Clear();

        switch (Form)
        {
            case LogoForm.Mark: BuildMark(); break;
            case LogoForm.Wordmark: BuildWordmark(); break;
            default: BuildLockup(); break;
        }
    }

    private void BuildMark()
    {
        var grid = Resource<double>("Brand.Mark.Grid");
        var scale = LogoHeight / grid;

        _canvas!.Children.Add(new Path
        {
            Data = Parse(Resource<string>("Brand.Mark.Path")),
            Fill = Foreground,
            RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale },
        });

        Width = LogoHeight;
        Height = LogoHeight;
    }

    private void BuildWordmark()
    {
        var stroke = Resource<double>("Brand.Wordmark.Stroke");
        var designWidth = Resource<double>("Brand.Wordmark.Width");
        var capHeight = Resource<double>("Brand.Wordmark.CapHeight");

        // The wordmark is measured by cap height, so a 24px logo has 24px capitals rather than
        // 24px including the descender of the p.
        var scale = LogoHeight / capHeight;
        var inset = stroke / 2;

        _canvas!.Children.Add(new Path
        {
            Data = Parse(Resource<string>("Brand.Wordmark.Path")),
            Stroke = Foreground,
            StrokeThickness = stroke,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = null,
            RenderTransform = new CompositeTransform
            {
                ScaleX = scale,
                ScaleY = scale,
                TranslateX = inset * scale,
            },
        });

        Width = (designWidth + stroke) * scale;
        Height = LogoHeight * 1.3;   // room for the descender
    }

    private void BuildLockup()
    {
        var markGrid = Resource<double>("Brand.Mark.Grid");
        var stroke = Resource<double>("Brand.Wordmark.Stroke");
        var wordWidth = Resource<double>("Brand.Wordmark.Width");
        var wordHeight = Resource<double>("Brand.Wordmark.Height");
        var capHeight = Resource<double>("Brand.Wordmark.CapHeight");
        var markHeight = Resource<double>("Brand.Lockup.MarkHeight");
        var gap = Resource<double>("Brand.Lockup.Gap");

        // Everything is laid out in wordmark design units first, then scaled once.
        var scale = LogoHeight / markHeight;
        var markScale = markHeight / markGrid;

        _canvas!.Children.Add(new Path
        {
            Data = Parse(Resource<string>("Brand.Mark.Path")),
            Fill = Foreground,
            RenderTransform = new CompositeTransform
            {
                ScaleX = markScale * scale,
                ScaleY = markScale * scale,
            },
        });

        var wordX = markHeight + gap + (stroke / 2);
        // Sit the wordmark's cap line on the mark's optical centre.
        var wordY = (markHeight - wordHeight) / 2 + (capHeight * 0.06);

        _canvas.Children.Add(new Path
        {
            Data = Parse(Resource<string>("Brand.Wordmark.Path")),
            Stroke = Foreground,
            StrokeThickness = stroke,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Fill = null,
            RenderTransform = new CompositeTransform
            {
                ScaleX = scale,
                ScaleY = scale,
                TranslateX = wordX * scale,
                TranslateY = wordY * scale,
            },
        });

        Width = (wordX + wordWidth + (stroke / 2)) * scale;
        Height = LogoHeight;
    }
}
