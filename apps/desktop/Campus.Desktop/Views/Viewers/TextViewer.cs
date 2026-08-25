using System.Text;
using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Documents;
using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Views.Viewers;

/// <summary>
/// Plain text and source code.
///
/// Numbered lines, a gutter that scrolls with the text, wrapping that can be turned off for code,
/// and a find that marks every match rather than only the next one. Read-only on purpose: the
/// stored file is addressed by the hash of its bytes, so editing it in place would make it a
/// different file, and Campus does not silently rewrite what was imported.
/// </summary>
public sealed class TextViewer : Grid, IContentViewer
{
    private const int MaxCharacters = 2_000_000;

    private readonly ScrollViewer _scroller = new();
    private readonly TextBlock _gutter = new();
    private readonly RichTextBlock _body = new();
    private readonly TextBlock _status = ViewerChrome.ToolLabel();
    private readonly TextBox _find = new();

    private string[] _lines = [];
    private bool _wrap = true;

    public TextViewer()
    {
        Background = ViewerChrome.Brush(ThemeTokens.Background.Primary);

        var mono = (FontFamily)Application.Current.Resources["Theme.Font.Mono"];

        _gutter.FontFamily = mono;
        _gutter.FontSize = 13;
        _gutter.LineHeight = 20;
        _gutter.TextAlignment = TextAlignment.Right;
        _gutter.Foreground = ViewerChrome.Brush(ThemeTokens.Label.Quaternary);
        _gutter.Padding = new Thickness(16, 0, 12, 0);
        _gutter.IsTextSelectionEnabled = false;
        // The gutter is decoration; a screen reader announcing four thousand numbers is noise.
        AutomationProperties.SetAccessibilityView(_gutter, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        _body.FontFamily = mono;
        _body.FontSize = 13;
        _body.LineHeight = 20;
        _body.Foreground = ViewerChrome.Brush(ThemeTokens.Label.Primary);
        _body.IsTextSelectionEnabled = true;
        _body.TextWrapping = TextWrapping.Wrap;
        _body.Padding = new Thickness(0, 0, 24, 40);
        AutomationProperties.SetName(_body, L.T("file.contents"));

        var columns = new Grid { Padding = new Thickness(0, 16, 0, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.Children.Add(_gutter);
        SetColumn(_body, 1);
        columns.Children.Add(_body);

        _scroller.Content = columns;
        _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroller.HorizontalScrollMode = ScrollMode.Auto;

        Children.Add(_scroller);

        // A guide for people who lose their line, if they have asked for one.
        Design.Controls.ReadingRuler.Attach(this);
    }

    public async Task LoadAsync(Stream content, CampusObject entity, FilePayload payload)
    {
        var busy = ViewerChrome.Busy("Reading");
        Children.Add(busy);

        try
        {
            var text = await Task.Run(() => ReadCapped(content));
            _lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            Render();
        }
        finally
        {
            Children.Remove(busy);
        }
    }

    /// <summary>
    /// Reads at most a couple of million characters. A log file that turns out to be a gigabyte
    /// should open showing its beginning, not spend a minute filling memory first.
    /// </summary>
    private static string ReadCapped(Stream content)
    {
        content.Position = 0;
        using var reader = new StreamReader(content, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        var buffer = new char[MaxCharacters];
        var read = reader.Read(buffer, 0, buffer.Length);
        var text = new string(buffer, 0, Math.Max(read, 0));

        return reader.EndOfStream ? text : text + "\n\n… truncated at 2,000,000 characters.";
    }

    // ------------------------------------------------------------------------ render

    private void Render()
    {
        var query = _find.Text.Trim();

        var numbers = new StringBuilder();
        for (var i = 1; i <= _lines.Length; i++) numbers.Append(i).Append('\n');
        _gutter.Text = numbers.ToString().TrimEnd('\n');

        _body.Blocks.Clear();
        var matches = 0;

        foreach (var line in _lines)
        {
            var paragraph = new Paragraph();

            if (query.Length == 0)
            {
                paragraph.Inlines.Add(new Run { Text = line });
            }
            else
            {
                matches += AddHighlighted(paragraph, line, query);
            }

            _body.Blocks.Add(paragraph);
        }

        _status.Text = query.Length > 0
            ? $"{matches} match{(matches == 1 ? "" : "es")}  ·  {_lines.Length:N0} lines"
            : $"{_lines.Length:N0} lines";
    }

    /// <summary>
    /// Splits one line around every occurrence of the query, so all of them are marked at once.
    /// A find that only moves to the next hit makes you press Enter to learn how many there are.
    /// </summary>
    private static int AddHighlighted(Paragraph paragraph, string line, string query)
    {
        var found = 0;
        var index = 0;

        while (index < line.Length)
        {
            var hit = line.IndexOf(query, index, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) break;

            if (hit > index) paragraph.Inlines.Add(new Run { Text = line[index..hit] });

            paragraph.Inlines.Add(new Run
            {
                Text = line.Substring(hit, query.Length),
                Foreground = ViewerChrome.Brush(ThemeTokens.Label.Primary),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });

            found++;
            index = hit + query.Length;
        }

        if (index < line.Length) paragraph.Inlines.Add(new Run { Text = line[index..] });
        return found;
    }

    // ------------------------------------------------------------------------- tools

    public IEnumerable<FrameworkElement> BuildTools()
    {
        _find.PlaceholderText = L.T("find");
        _find.Width = 180;
        _find.Style = (Style)Application.Current.Resources["Input.Search"];
        _find.TextChanged += (_, _) => Render();
        AutomationProperties.SetName(_find, L.T("find.in.file"));
        yield return _find;

        yield return _status;

        yield return ViewerChrome.ToolToggle(CampusSymbols.TextSize, "Wrap lines", _wrap, wrap =>
        {
            _wrap = wrap;
            _body.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        });
    }
}
