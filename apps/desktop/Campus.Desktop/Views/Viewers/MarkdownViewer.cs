using System.Text;
using Campus.Desktop.Design;
using Campus.Desktop.Design.Controls;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Documents;
using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Views.Viewers;

/// <summary>
/// Reads a markdown file the way it was meant to be read, with a switch to see the source it came
/// from. A [[wiki link]] in it opens the object it names, which is what makes a folder of notes
/// behave like one connected set rather than a pile of files.
/// </summary>
public sealed class MarkdownViewer : Grid, IContentViewer
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();

    private readonly ScrollViewer _scroller = new();
    private readonly MarkdownView _rendered = new();
    private readonly TextBlock _source = new();
    private readonly StackPanel _sourcePanel = new();

    private string _text = "";

    public MarkdownViewer()
    {
        Background = ViewerChrome.Brush(ThemeTokens.Background.Primary);

        _rendered.MaxWidth = 720;
        _rendered.HorizontalAlignment = HorizontalAlignment.Left;
        _rendered.Margin = new Thickness(40, 32, 40, 80);
        _rendered.LinkInvoked += (_, url) => _ = FollowAsync(url);

        _source.FontFamily = (FontFamily)Application.Current.Resources["Theme.Font.Mono"];
        _source.FontSize = 13;
        _source.LineHeight = 20;
        _source.Foreground = ViewerChrome.Brush(ThemeTokens.Label.Secondary);
        _source.IsTextSelectionEnabled = true;
        _source.TextWrapping = TextWrapping.Wrap;

        _sourcePanel.Margin = new Thickness(40, 32, 40, 80);
        _sourcePanel.MaxWidth = 900;
        _sourcePanel.HorizontalAlignment = HorizontalAlignment.Left;
        _sourcePanel.Visibility = Visibility.Collapsed;
        _sourcePanel.Children.Add(_source);

        var stack = new StackPanel();
        stack.Children.Add(_rendered);
        stack.Children.Add(_sourcePanel);

        _scroller.Content = stack;
        _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Children.Add(_scroller);

        // A guide for people who lose their line, if they have asked for one.
        Design.Controls.ReadingRuler.Attach(this);
    }

    public async Task LoadAsync(Stream content, CampusObject entity, FilePayload payload)
    {
        content.Position = 0;
        using var reader = new StreamReader(content, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        _text = await reader.ReadToEndAsync();
        _rendered.Text = _text;
        _source.Text = _text;
    }

    public IEnumerable<FrameworkElement> BuildTools()
    {
        yield return ViewerChrome.ToolToggle(CampusSymbols.Code, "Show source", false, source =>
        {
            _rendered.Visibility = source ? Visibility.Collapsed : Visibility.Visible;
            _sourcePanel.Visibility = source ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    /// <summary>
    /// Decides what a link means. Anything that looks like a web address leaves Campus, and
    /// anything else is treated as the name of something in the workspace — which is how a note
    /// written on a phone, with no ids in it, still links to the right object here.
    /// </summary>
    private async Task FollowAsync(string url)
    {
        if (url.Length == 0) return;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "mailto")
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
            return;
        }

        var target = await _workspace.Relations.FindByTitleAsync(url.Trim('[', ']'));
        if (target is null)
        {
            Notifications.Show($"Nothing here is called “{url}”.", NoticeKind.Warning);
            return;
        }

        App.GetService<ShellRouter>().Open(target.Value);
    }
}
