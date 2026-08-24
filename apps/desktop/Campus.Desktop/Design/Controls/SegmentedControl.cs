using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Campus.Desktop.Design.Controls;

/// <summary>
/// A row of mutually exclusive choices in one pill — Today / Upcoming / Overdue / Done.
///
/// Built from real toggle buttons in a radio group rather than a styled list, so arrow keys move
/// between segments, a screen reader announces "3 of 4", and the selected segment is announced as
/// selected rather than merely looking different.
/// </summary>
public sealed class SegmentedControl : Control
{
    private StackPanel? _panel;
    private readonly List<ToggleButton> _buttons = [];

    public SegmentedControl()
    {
        DefaultStyleKey = typeof(SegmentedControl);
        IsTabStop = false;
        AutomationProperties.SetAutomationControlType(this, AutomationControlType.Tab);
    }

    /// <summary>Raised when the selected segment changes, with its index.</summary>
    public event EventHandler<int>? SelectionChanged;

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IList<string>), typeof(SegmentedControl),
        new PropertyMetadata(null, OnSegmentsChanged));

    public IList<string>? Segments
    {
        get => (IList<string>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(SegmentedControl),
        new PropertyMetadata(0, OnSelectedIndexChanged));

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _panel = GetTemplateChild("PART_Panel") as StackPanel;
        Rebuild();
    }

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SegmentedControl)d).Rebuild();

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SegmentedControl)d).SyncSelection();

    private void Rebuild()
    {
        if (_panel is null) return;

        _panel.Children.Clear();
        _buttons.Clear();
        if (Segments is null) return;

        for (var i = 0; i < Segments.Count; i++)
        {
            var button = new ToggleButton
            {
                Content = Segments[i],
                Tag = i,
                Style = (Style)Application.Current.Resources["Segment.Button"],
            };
            AutomationProperties.SetName(button, Segments[i]);
            AutomationProperties.SetPositionInSet(button, i + 1);
            AutomationProperties.SetSizeOfSet(button, Segments.Count);

            button.Click += OnSegmentClick;
            button.KeyDown += OnSegmentKeyDown;

            _buttons.Add(button);
            _panel.Children.Add(button);
        }

        SyncSelection();
    }

    private void OnSegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: int index }) SelectedIndex = index;
    }

    private void OnSegmentKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_buttons.Count == 0) return;

        var delta = e.Key switch
        {
            Windows.System.VirtualKey.Left => -1,
            Windows.System.VirtualKey.Right => 1,
            _ => 0,
        };
        if (delta == 0) return;

        // Arrow keys wrap, the way a segmented control on a phone does.
        SelectedIndex = (SelectedIndex + delta + _buttons.Count) % _buttons.Count;
        _buttons[SelectedIndex].Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    private void SyncSelection()
    {
        for (var i = 0; i < _buttons.Count; i++)
        {
            var selected = i == SelectedIndex;
            _buttons[i].IsChecked = selected;
            // Only the selected segment is a tab stop, so Tab moves past the whole group at once.
            _buttons[i].IsTabStop = selected;
        }

        SelectionChanged?.Invoke(this, SelectedIndex);
    }
}
