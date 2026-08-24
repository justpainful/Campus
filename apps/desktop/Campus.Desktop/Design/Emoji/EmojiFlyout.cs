using Campus.Desktop.Design.Icons;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Campus.Desktop.Design.Emoji;

/// <summary>
/// Attaches the emoji picker to a button, and inserts what is chosen at the caret of a text box.
/// One helper so every text field in Campus gets the same picker rather than each growing its own.
/// </summary>
public static class EmojiFlyout
{
    /// <summary>Builds an emoji button wired to a text box.</summary>
    public static Button CreateButton(TextBox target)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Icon"],
            Content = new CampusIcon
            {
                Symbol = CampusSymbols.Emoji,
                IconSize = 18,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)
                    Application.Current.Resources[ThemeTokens.Label.Secondary],
            },
        };
        AutomationProperties.SetName(button, "Insert emoji");
        ToolTipService.SetToolTip(button, "Insert emoji");

        Attach(button, target);
        return button;
    }

    /// <summary>Attaches the picker to an existing button.</summary>
    public static void Attach(Button button, TextBox target)
    {
        var picker = new EmojiPicker();

        var flyout = new Flyout
        {
            Content = picker,
            Placement = FlyoutPlacementMode.Top,
            // The picker paints its own surface, so the flyout adds no padding of its own.
            FlyoutPresenterStyle = BuildPresenterStyle(),
        };

        picker.EmojiPicked += (_, text) =>
        {
            Insert(target, text);
            flyout.Hide();
        };

        button.Flyout = flyout;
    }

    private static Style BuildPresenterStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 460d));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty,
            Application.Current.Resources["Theme.Radius.Flyout"]));
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            Application.Current.Resources[ThemeTokens.Surface.Elevated]));
        style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Disabled));
        style.Setters.Add(new Setter(ScrollViewer.VerticalScrollModeProperty, ScrollMode.Disabled));
        return style;
    }

    /// <summary>
    /// Inserts at the caret, replacing any selection, and leaves the caret after what was
    /// inserted — so picking three emoji in a row types three emoji rather than overwriting one.
    /// </summary>
    public static void Insert(TextBox target, string text)
    {
        var start = target.SelectionStart;
        var length = target.SelectionLength;

        target.Text = target.Text.Remove(start, length).Insert(start, text);
        target.SelectionStart = start + text.Length;
        target.SelectionLength = 0;
        target.Focus(FocusState.Programmatic);
    }
}
