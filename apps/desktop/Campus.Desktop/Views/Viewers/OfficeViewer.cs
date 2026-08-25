using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Documents;
using Campus.Domain;
using Microsoft.UI.Text;
using Windows.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Views.Viewers;

/// <summary>
/// Reads Word documents and PowerPoint decks.
///
/// It shows the document's structure in Campus's own type rather than trying to reproduce Word's
/// page layout — headings are headings, lists are lists, tables are tables, and speaker notes are
/// visible instead of hidden behind a mode. For revising from a handout that is better than a
/// pixel-accurate copy, and it opens instantly on files Word takes seconds to load. When the
/// layout itself matters, "Open in another app" is one click away in the toolbar.
/// </summary>
public sealed class OfficeViewer : Grid, IContentViewer
{
    private readonly ScrollViewer _scroller = new();
    private readonly StackPanel _content = new();
    private readonly ListView _outline = new();
    private readonly ColumnDefinition _outlineColumn = new() { Width = new GridLength(240) };

    private readonly List<FrameworkElement> _sectionAnchors = [];
    private IReadOnlyList<DocSection> _sections = [];
    private bool _isDeck;

    public OfficeViewer()
    {
        Background = ViewerChrome.Brush(ThemeTokens.Background.Primary);

        ColumnDefinitions.Add(_outlineColumn);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _outline.SelectionMode = ListViewSelectionMode.Single;
        _outline.Padding = new Thickness(8, 12, 8, 24);
        _outline.SelectionChanged += OnOutlineSelected;
        AutomationProperties.SetName(_outline, L.T("outline"));

        var outlinePanel = new Border
        {
            Background = ViewerChrome.Brush(ThemeTokens.Background.Secondary),
            BorderBrush = ViewerChrome.Brush(ThemeTokens.Separator.Standard),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = _outline,
        };
        Children.Add(outlinePanel);

        _content.MaxWidth = 760;
        _content.HorizontalAlignment = HorizontalAlignment.Left;
        _content.Margin = new Thickness(40, 32, 40, 96);

        _scroller.Content = _content;
        _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        SetColumn(_scroller, 1);
        Children.Add(_scroller);

        // A guide for people who lose their line, if they have asked for one.
        Design.Controls.ReadingRuler.Attach(this);
    }

    public async Task LoadAsync(Stream content, CampusObject entity, FilePayload payload)
    {
        var busy = ViewerChrome.Busy("Reading the document");
        Children.Add(busy);
        SetColumnSpan(busy, 2);

        try
        {
            _isDeck = payload.Extension == ".pptx";

            // OpenXml opens the whole package; on a large deck that is worth a thread of its own.
            _sections = await Task.Run(() => _isDeck
                ? OfficeOutline.ReadPresentation(content)
                : OfficeOutline.ReadWord(content));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                      or FormatException or ArgumentException)
        {
            ShowFailure();
            return;
        }
        finally
        {
            Children.Remove(busy);
        }

        Render();
    }

    private void ShowFailure()
    {
        _content.Children.Add(new TextBlock
        {
            Text = L.T("this.document.could.not.be.read.it.may.be.pass.543894"),
            Style = (Style)Application.Current.Resources["Text.Callout"],
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private void Render()
    {
        _content.Children.Clear();
        _sectionAnchors.Clear();
        _outline.Items.Clear();

        if (_sections.Count == 0)
        {
            _content.Children.Add(new TextBlock
            {
                Text = L.T("this.document.has.no.readable.text"),
                Style = (Style)Application.Current.Resources["Text.Callout"],
            });
            _outlineColumn.Width = new GridLength(0);
            return;
        }

        var number = 0;
        foreach (var section in _sections)
        {
            number++;

            // Each section gets an anchor so the outline can scroll to it; a slide additionally
            // gets a visible break, because slides are separate things and a deck read as one
            // continuous page loses the boundaries that made it a deck.
            var anchor = _isDeck
                ? SlideHeader(section.Title, number)
                : (FrameworkElement)new Border { Height = 0 };

            _content.Children.Add(anchor);
            _sectionAnchors.Add(anchor);

            foreach (var block in section.Blocks)
            {
                if (_isDeck && block.Kind == DocBlockKind.Heading && block.Level == 1) continue;
                AddBlock(block);
            }

            _outline.Items.Add(new ListViewItem
            {
                Content = section.Title.Length > 0 ? section.Title : $"Section {number}",
                Padding = new Thickness(10, 6, 10, 6),
            });
        }
    }

    private FrameworkElement SlideHeader(string title, int number)
    {
        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(0, number == 1 ? 0 : 44, 0, 12) };

        stack.Children.Add(new TextBlock
        {
            Text = $"Slide {number}",
            Style = (Style)Application.Current.Resources["Text.Caption"],
        });
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)Application.Current.Resources["Theme.Font.Text"],
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = ViewerChrome.Brush(ThemeTokens.Label.Primary),
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new Border
        {
            Height = 1,
            Background = ViewerChrome.Brush(ThemeTokens.Separator.Standard),
            Margin = new Thickness(0, 8, 0, 0),
        });

        return stack;
    }

    private void AddBlock(DocBlock block)
    {
        switch (block.Kind)
        {
            case DocBlockKind.Heading:
                var (size, weight) = block.Level switch
                {
                    1 => (24.0, FontWeights.Bold),
                    2 => (19.0, FontWeights.SemiBold),
                    3 => (16.0, FontWeights.SemiBold),
                    _ => (15.0, FontWeights.SemiBold),
                };
                var heading = Text(block.Text, size, weight, ThemeTokens.Label.Primary);
                heading.Margin = new Thickness(0, 22, 0, 6);
                _content.Children.Add(heading);
                break;

            case DocBlockKind.Bullet:
            case DocBlockKind.Numbered:
                _content.Children.Add(BulletRow(block.Text, block.Level));
                break;

            case DocBlockKind.Quote:
                _content.Children.Add(new Border
                {
                    BorderBrush = ViewerChrome.Brush(ThemeTokens.Fill.Secondary),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(14, 2, 0, 2),
                    Margin = new Thickness(0, 8, 0, 8),
                    Child = Text(block.Text, 15, FontWeights.Normal, ThemeTokens.Label.Secondary),
                });
                break;

            case DocBlockKind.Note:
                _content.Children.Add(SpeakerNote(block.Text));
                break;

            case DocBlockKind.Table when block.Rows is { Count: > 0 } rows:
                _content.Children.Add(TableView.Build(rows, hasHeader: true));
                break;

            case DocBlockKind.Code:
                var code = Text(block.Text, 13, FontWeights.Normal, ThemeTokens.Label.Primary);
                code.FontFamily = (FontFamily)Application.Current.Resources["Theme.Font.Mono"];
                _content.Children.Add(code);
                break;

            default:
                _content.Children.Add(Text(block.Text, 15, FontWeights.Normal, ThemeTokens.Label.Primary));
                break;
        }
    }

    private Grid BulletRow(string text, int depth)
    {
        var row = new Grid { Margin = new Thickness(depth * 20, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var marker = Text(depth == 0 ? "•" : "◦", 15, FontWeights.Normal, ThemeTokens.Label.Tertiary);
        marker.TextAlignment = TextAlignment.Center;

        var body = Text(text, 15, FontWeights.Normal, ThemeTokens.Label.Primary);
        SetColumn(body, 1);

        row.Children.Add(marker);
        row.Children.Add(body);
        return row;
    }

    private Border SpeakerNote(string text)
    {
        var stack = new StackPanel { Spacing = 6 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(ViewerChrome.Icon(CampusSymbols.Comment, 14, ThemeTokens.Label.Tertiary));
        header.Children.Add(new TextBlock
        {
            Text = L.T("speaker.notes"),
            Style = (Style)Application.Current.Resources["Text.Caption"],
        });

        stack.Children.Add(header);
        stack.Children.Add(Text(text, 14, FontWeights.Normal, ThemeTokens.Label.Secondary));

        return new Border
        {
            Background = ViewerChrome.Brush(ThemeTokens.Fill.Quaternary),
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.Card"],
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 14, 0, 6),
            Child = stack,
        };
    }

    private static TextBlock Text(string text, double size, FontWeight weight, string token) => new()
    {
        Text = text,
        FontFamily = (FontFamily)Application.Current.Resources["Theme.Font.Reading"],
        FontSize = size,
        LineHeight = AccessibilityScaling.ReadingLineHeight(size),
        FontWeight = weight,
        Foreground = ViewerChrome.Brush(token),
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        Margin = new Thickness(0, 2, 0, 6),
    };

    // ------------------------------------------------------------------------- tools

    public IEnumerable<FrameworkElement> BuildTools()
    {
        yield return ViewerChrome.ToolToggle(CampusSymbols.Outline, "Outline", true, shown =>
            _outlineColumn.Width = new GridLength(shown ? 240 : 0));
    }

    private void OnOutlineSelected(object sender, SelectionChangedEventArgs e)
    {
        var index = _outline.SelectedIndex;
        if (index < 0 || index >= _sectionAnchors.Count) return;

        // One anchor was recorded per section, in order, so the nth entry in the outline and the
        // nth anchor are the same place.
        var target = _sectionAnchors[index]
            .TransformToVisual(_content)
            .TransformPoint(new Windows.Foundation.Point(0, 0));

        _scroller.ChangeView(null, target.Y, null);
    }
}

/// <summary>
/// A grid of strings, drawn once. Shared by the document viewer and the spreadsheet viewer so a
/// table looks the same wherever it came from.
/// </summary>
internal static class TableView
{
    public static FrameworkElement Build(IReadOnlyList<IReadOnlyList<string>> rows, bool hasHeader)
    {
        var grid = new Grid
        {
            BorderBrush = ViewerChrome.Brush(ThemeTokens.Separator.Standard),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.S"],
        };

        var columns = rows.Max(r => r.Count);
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < rows[r].Count; c++)
            {
                var isHeader = hasHeader && r == 0;

                var cell = new Border
                {
                    Padding = new Thickness(12, 8, 12, 8),
                    Background = isHeader ? ViewerChrome.Brush(ThemeTokens.Fill.Quaternary) : null,
                    BorderBrush = ViewerChrome.Brush(ThemeTokens.Separator.Standard),
                    BorderThickness = new Thickness(0, 0, 0, r < rows.Count - 1 ? 1 : 0),
                    Child = new TextBlock
                    {
                        Text = rows[r][c],
                        FontSize = 13.5,
                        FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = ViewerChrome.Brush(
                            isHeader ? ThemeTokens.Label.Primary : ThemeTokens.Label.Secondary),
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 320,
                        IsTextSelectionEnabled = true,
                    },
                };

                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            Margin = new Thickness(0, 10, 0, 14),
        };
    }
}
