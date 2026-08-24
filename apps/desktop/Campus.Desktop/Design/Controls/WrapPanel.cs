using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Campus.Desktop.Design.Controls;

/// <summary>
/// Lays children out in rows, wrapping when the line is full. WinUI has no built-in wrap panel,
/// and tag lists, icon grids and chip rows all need one.
/// </summary>
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty ItemSpacingProperty = DependencyProperty.Register(
        nameof(ItemSpacing), typeof(double), typeof(WrapPanel),
        new PropertyMetadata(8d, OnLayoutPropertyChanged));

    /// <summary>Horizontal gap between items on the same line.</summary>
    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    public static readonly DependencyProperty LineSpacingProperty = DependencyProperty.Register(
        nameof(LineSpacing), typeof(double), typeof(WrapPanel),
        new PropertyMetadata(8d, OnLayoutPropertyChanged));

    /// <summary>Vertical gap between lines.</summary>
    public double LineSpacing
    {
        get => (double)GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((WrapPanel)d).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        var lineWidth = 0d;
        var lineHeight = 0d;
        var totalHeight = 0d;
        var widest = 0d;

        var childConstraint = new Size(availableSize.Width, double.PositiveInfinity);

        foreach (var child in Children)
        {
            child.Measure(childConstraint);
            var size = child.DesiredSize;

            var needed = lineWidth == 0 ? size.Width : lineWidth + ItemSpacing + size.Width;
            if (needed > availableSize.Width && lineWidth > 0)
            {
                widest = Math.Max(widest, lineWidth);
                totalHeight += lineHeight + LineSpacing;
                lineWidth = size.Width;
                lineHeight = size.Height;
            }
            else
            {
                lineWidth = needed;
                lineHeight = Math.Max(lineHeight, size.Height);
            }
        }

        widest = Math.Max(widest, lineWidth);
        totalHeight += lineHeight;

        return new Size(
            double.IsInfinity(availableSize.Width) ? widest : Math.Min(widest, availableSize.Width),
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        var y = 0d;
        var lineHeight = 0d;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;

            if (x > 0 && x + ItemSpacing + size.Width > finalSize.Width)
            {
                x = 0;
                y += lineHeight + LineSpacing;
                lineHeight = 0;
            }

            var left = x == 0 ? 0 : x + ItemSpacing;
            child.Arrange(new Rect(left, y, size.Width, size.Height));

            x = left + size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        return new Size(finalSize.Width, y + lineHeight);
    }
}
