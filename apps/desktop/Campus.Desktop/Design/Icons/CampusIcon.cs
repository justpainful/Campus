using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
// System.IO.Path comes in through implicit usings, so the shape is aliased explicitly.
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Campus.Desktop.Design.Icons;

/// <summary>
/// Optical weight, the way SF Symbols varies stroke rather than scaling one drawing. A small
/// icon next to 11pt text needs a thinner stroke than the same icon at 32px, or it turns to mud.
/// </summary>
public enum IconWeight
{
    Ultralight = 0,
    Light = 1,
    Regular = 2,
    Medium = 3,
    Semibold = 4,
    Bold = 5,
}

/// <summary>Outline is the default; Filled is used to mark the selected state.</summary>
public enum IconVariant { Outline = 0, Filled = 1 }

/// <summary>
/// Draws a Campus symbol. Icons are geometry authored on a 24×24 grid and stroked at render
/// time, so a single definition stays crisp from 12px to 48px and picks up the current label
/// colour automatically. Emoji are never used as icons.
/// </summary>
public sealed class CampusIcon : Control
{
    private Path? _path;
    private Canvas? _canvas;

    public CampusIcon()
    {
        DefaultStyleKey = typeof(CampusIcon);
        IsTabStop = false;
        IsHitTestVisible = false;
    }

    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol), typeof(string), typeof(CampusIcon),
        new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

    /// <summary>Symbol name, from <see cref="CampusSymbols"/>.</summary>
    public string Symbol
    {
        get => (string)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(CampusIcon),
        new PropertyMetadata(16d, OnVisualPropertyChanged));

    /// <summary>Rendered size in pixels. Drives both the box and the optical stroke weight.</summary>
    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public static readonly DependencyProperty WeightProperty = DependencyProperty.Register(
        nameof(Weight), typeof(IconWeight), typeof(CampusIcon),
        new PropertyMetadata(IconWeight.Regular, OnVisualPropertyChanged));

    public IconWeight Weight
    {
        get => (IconWeight)GetValue(WeightProperty);
        set => SetValue(WeightProperty, value);
    }

    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(IconVariant), typeof(CampusIcon),
        new PropertyMetadata(IconVariant.Outline, OnVisualPropertyChanged));

    public IconVariant Variant
    {
        get => (IconVariant)GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _path = GetTemplateChild("PART_Path") as Path;
        _canvas = GetTemplateChild("PART_Canvas") as Canvas;
        UpdateVisual();
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CampusIcon)d).UpdateVisual();

    private void UpdateVisual()
    {
        Width = Height = IconSize;
        if (_path is null) return;

        var glyph = IconRegistry.Resolve(Symbol, Variant);
        _path.Data = glyph.Geometry;

        // The canvas is the authored 24-unit grid; scaling it, rather than the path, is what
        // keeps the whole drawing visible at sizes below 24.
        var scale = IconSize / IconRegistry.GridSize;
        if (_canvas is not null)
            _canvas.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };

        if (glyph.IsFilled)
        {
            _path.Fill = Foreground;
            _path.Stroke = null;
            _path.StrokeThickness = 0;
        }
        else
        {
            _path.Fill = null;
            _path.Stroke = Foreground;
            // Stroke is specified in grid units then scaled with the path, which is what keeps
            // a 12px icon and a 32px icon looking like the same drawing.
            _path.StrokeThickness = StrokeUnitsFor(Weight, IconSize);
        }
    }

    /// <summary>
    /// Optical stroke correction. Below 16px a Regular stroke of 1.5 units reads as fuzzy, and
    /// above 24px it reads as spindly, so the weight shifts slightly with size.
    /// </summary>
    private static double StrokeUnitsFor(IconWeight weight, double renderedSize)
    {
        var baseUnits = weight switch
        {
            IconWeight.Ultralight => 1.0,
            IconWeight.Light => 1.25,
            IconWeight.Regular => 1.5,
            IconWeight.Medium => 1.75,
            IconWeight.Semibold => 2.0,
            IconWeight.Bold => 2.4,
            _ => 1.5,
        };

        var opticalAdjustment = renderedSize switch
        {
            <= 12 => 0.20,
            <= 14 => 0.10,
            <= 20 => 0.0,
            <= 28 => -0.10,
            _ => -0.20,
        };

        return Math.Max(0.75, baseUnits + opticalAdjustment);
    }

    protected override void OnPointerEntered(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        UpdateVisual();
    }
}
