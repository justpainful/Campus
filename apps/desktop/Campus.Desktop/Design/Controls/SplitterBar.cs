using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Campus.Desktop.Design.Controls;

/// <summary>
/// The draggable divider between panes. It exists as its own control because the resize cursor
/// can only be set from inside the element that shows it, and because a splitter should be
/// reachable from the keyboard as well as the pointer.
/// </summary>
public sealed class SplitterBar : Control
{
    public SplitterBar()
    {
        DefaultStyleKey = typeof(SplitterBar);
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
    }

    /// <summary>Raised while dragging, with the movement along the splitter's axis.</summary>
    public event EventHandler<double>? Dragged;

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(SplitterBar),
        new PropertyMetadata(Orientation.Vertical));

    /// <summary>Vertical splits left from right; Horizontal splits top from bottom.</summary>
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <summary>How far one arrow-key press moves the divider.</summary>
    public double KeyboardStep { get; set; } = 16;

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        ProtectedCursor = InputSystemCursor.Create(Orientation == Orientation.Vertical
            ? InputSystemCursorShape.SizeWestEast
            : InputSystemCursorShape.SizeNorthSouth);
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    protected override void OnManipulationDelta(ManipulationDeltaRoutedEventArgs e)
    {
        base.OnManipulationDelta(e);
        Dragged?.Invoke(this, Orientation == Orientation.Vertical
            ? e.Delta.Translation.X
            : e.Delta.Translation.Y);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);

        var step = Orientation == Orientation.Vertical
            ? e.Key switch
            {
                Windows.System.VirtualKey.Left => -KeyboardStep,
                Windows.System.VirtualKey.Right => KeyboardStep,
                _ => 0,
            }
            : e.Key switch
            {
                Windows.System.VirtualKey.Up => -KeyboardStep,
                Windows.System.VirtualKey.Down => KeyboardStep,
                _ => 0,
            };

        if (step == 0) return;
        Dragged?.Invoke(this, step);
        e.Handled = true;
    }
}
