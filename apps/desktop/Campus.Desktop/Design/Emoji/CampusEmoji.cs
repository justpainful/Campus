using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Design.Emoji;

/// <summary>
/// Draws one emoji from the active pack.
///
/// Everywhere in Campus that shows an emoji goes through this, so switching packs changes every
/// emoji at once and no corner of the app quietly keeps rendering with a system font.
///
/// There is deliberately no fallback to the system emoji font. Falling back would mean that the
/// moment a pack was missing one glyph, a Segoe UI Emoji face would appear in the middle of an
/// otherwise consistent set — which is the exact thing packs exist to prevent. A missing emoji
/// draws nothing, and the picker says plainly when no pack is installed.
/// </summary>
public sealed class CampusEmoji : Control
{
    private Image? _image;

    public CampusEmoji()
    {
        DefaultStyleKey = typeof(CampusEmoji);
        IsTabStop = false;
        EmojiPackStore.Current.ActivePackChanged += OnPackChanged;
        Unloaded += (_, _) => EmojiPackStore.Current.ActivePackChanged -= OnPackChanged;
    }

    public static readonly DependencyProperty SequenceProperty = DependencyProperty.Register(
        nameof(Sequence), typeof(string), typeof(CampusEmoji),
        new PropertyMetadata(string.Empty, OnVisualChanged));

    /// <summary>Code points, space separated, as they appear in the catalogue: "1F44B 1F3FB".</summary>
    public string Sequence
    {
        get => (string)GetValue(SequenceProperty);
        set => SetValue(SequenceProperty, value);
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(CampusEmoji),
        new PropertyMetadata(string.Empty, OnVisualChanged));

    /// <summary>
    /// The characters themselves. Not drawn — it is what gets inserted when this emoji is
    /// chosen, and what a screen reader announces.
    /// </summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty EmojiSizeProperty = DependencyProperty.Register(
        nameof(EmojiSize), typeof(double), typeof(CampusEmoji),
        new PropertyMetadata(24d, OnVisualChanged));

    public double EmojiSize
    {
        get => (double)GetValue(EmojiSizeProperty);
        set => SetValue(EmojiSizeProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _image = GetTemplateChild("PART_Image") as Image;
        Update();
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CampusEmoji)d).Update();

    private void OnPackChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(Update);

    private void Update()
    {
        if (_image is null) return;

        Width = Height = EmojiSize;
        _image.Width = _image.Height = EmojiSize;

        // Decoded at twice the layout size so it stays crisp on a 200% display without holding
        // the full-size bitmap for every cell in a two-thousand-cell grid.
        var source = Sequence.Length > 0
            ? EmojiPackStore.Current.Image(Sequence, (int)Math.Ceiling(EmojiSize * 2))
            : null;

        _image.Source = source;
        _image.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;

        AutomationProperties.SetName(this, Text);
    }
}
