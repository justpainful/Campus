using Campus.Desktop.Design.Icons;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

// Both markdown and WinUI text have a type called Block; the alias keeps the parse tree's one
// distinct from the one that ends up on screen.
using MarkdownBlock = Markdig.Syntax.Block;

namespace Campus.Desktop.Design.Controls;

/// <summary>
/// Draws markdown as real WinUI text.
///
/// Not a browser: there is no HTML anywhere in this path, which is the point. A note is text the
/// user wrote, and rendering it through a web view would mean shipping a rendering engine, a
/// second set of fonts, a second theme, and a way for a pasted document to run script inside the
/// workspace. Walking the parse tree costs a few hundred lines and none of that.
///
/// Emphasis, code, links, headings, lists, task lists, quotes, tables, rules and images are drawn
/// as themselves; anything unrecognised falls back to its own plain text so nothing is ever lost.
/// </summary>
public sealed partial class MarkdownView : StackPanel
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseFootnotes()
        .Build();

    public MarkdownView()
    {
        Spacing = 0;
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarkdownView),
        new PropertyMetadata(string.Empty, (d, _) => ((MarkdownView)d).Rebuild()));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Raised when a link is clicked. The host decides what a link means.</summary>
    public event EventHandler<string>? LinkInvoked;

    /// <summary>
    /// Asked for the picture behind an image reference. Returning null leaves a caption in its
    /// place; the viewer wires this to the vault so an image in a note stays encrypted.
    /// </summary>
    public Func<string, Task<ImageSource?>>? ImageResolver { get; set; }

    private void Rebuild()
    {
        Children.Clear();
        if (string.IsNullOrWhiteSpace(Text)) return;

        var document = Markdown.Parse(Text, Pipeline);
        foreach (var block in document) AddBlock(block, this);
    }

    // ------------------------------------------------------------------------ blocks

    private void AddBlock(MarkdownBlock block, Panel host)
    {
        switch (block)
        {
            case HeadingBlock heading: AddHeading(heading, host); break;
            case ParagraphBlock paragraph: AddParagraph(paragraph, host); break;
            case ListBlock list: AddList(list, host); break;
            case QuoteBlock quote: AddQuote(quote, host); break;
            case CodeBlock code: AddCode(code, host); break;
            case ThematicBreakBlock: AddRule(host); break;
            case Markdig.Extensions.Tables.Table table: AddTable(table, host); break;

            case ContainerBlock container:
                foreach (var child in container) AddBlock(child, host);
                break;

            case LeafBlock leaf when leaf.Lines.Count > 0:
                AddText(leaf.Lines.ToString(), host);
                break;
        }
    }

    private void AddHeading(HeadingBlock heading, Panel host)
    {
        // Six levels compressed to four sizes: below the fourth, markdown headings are a
        // structural device rather than a visual one, and printing them smaller than body text
        // reads as a mistake.
        var (size, weight, top) = heading.Level switch
        {
            1 => (26.0, FontWeights.Bold, 24.0),
            2 => (21.0, FontWeights.SemiBold, 22.0),
            3 => (17.0, FontWeights.SemiBold, 18.0),
            _ => (15.0, FontWeights.SemiBold, 16.0),
        };

        var text = new TextBlock
        {
            FontFamily = Font("Theme.Font.Text"),
            FontSize = size,
            FontWeight = weight,
            Foreground = Brush(ThemeTokens.Label.Primary),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, host.Children.Count == 0 ? 0 : top, 0, 6),
            IsTextSelectionEnabled = true,
        };

        if (heading.Inline is not null) AddInlines(heading.Inline, text.Inlines);
        AutomationProperties.SetHeadingLevel(text, HeadingLevelOf(heading.Level));
        host.Children.Add(text);
    }

    private static Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel HeadingLevelOf(int level)
        => level switch
        {
            1 => Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1,
            2 => Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2,
            3 => Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3,
            4 => Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level4,
            5 => Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level5,
            _ => Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level6,
        };

    private void AddParagraph(ParagraphBlock paragraph, Panel host)
    {
        var text = Body();
        if (paragraph.Inline is not null) AddInlines(paragraph.Inline, text.Inlines);
        host.Children.Add(text);
    }

    private void AddList(ListBlock list, Panel host, int depth = 0)
    {
        var number = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = new Grid { Margin = new Thickness(depth * 20, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var content = new StackPanel { Spacing = 0 };
            Grid.SetColumn(content, 1);

            // A task list item carries its checkbox as the first inline of its first paragraph,
            // and that inline must not also be drawn as text.
            var task = FindTask(item);

            row.Children.Add(task is not null
                ? TaskMarker(task.Checked)
                : list.IsOrdered ? NumberMarker(number) : Marker(depth));

            foreach (var child in item) AddBlock(child, content);
            row.Children.Add(content);
            host.Children.Add(row);

            number++;
        }
    }

    private static TaskList? FindTask(ListItemBlock item)
        => item.FirstOrDefault() is ParagraphBlock { Inline: { } inline }
            ? inline.FirstOrDefault() as TaskList
            : null;

    /// <summary>
    /// The bullet, drawn rather than typed.
    ///
    /// A bullet character depends on the font having one at a sensible size and baseline, and the
    /// display faces Campus uses do not agree about either. A small filled dot is the same shape
    /// in every theme and sits where a bullet should.
    /// </summary>
    private FrameworkElement Marker(int depth)
    {
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = depth == 0 ? 5 : 4,
            Height = depth == 0 ? 5 : 4,
            Fill = depth == 0 ? Brush(ThemeTokens.Label.Tertiary) : null,
            Stroke = depth == 0 ? null : Brush(ThemeTokens.Label.Tertiary),
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 11, 8, 0),
        };

        return dot;
    }

    private TextBlock NumberMarker(int number) => new()
    {
        Text = $"{number}.",
        FontFamily = Font("Theme.Font.Text"),
        FontSize = 14,
        Foreground = Brush(ThemeTokens.Label.Tertiary),
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 4, 8, 0),
    };

    private CampusIcon TaskMarker(bool done) => new()
    {
        Symbol = done ? CampusSymbols.CheckboxChecked : CampusSymbols.Checkbox,
        IconSize = 16,
        Foreground = Brush(done ? ThemeTokens.Success.Primary : ThemeTokens.Label.Quaternary),
        Margin = new Thickness(4, 3, 8, 0),
        VerticalAlignment = VerticalAlignment.Top,
    };

    private void AddQuote(QuoteBlock quote, Panel host)
    {
        var content = new StackPanel { Spacing = 0 };
        foreach (var child in quote) AddBlock(child, content);

        var rail = new Border
        {
            Width = 3,
            Background = Brush(ThemeTokens.Fill.Secondary),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 2, 12, 2),
        };

        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(content, 1);
        row.Children.Add(rail);
        row.Children.Add(content);

        host.Children.Add(row);
    }

    private void AddCode(CodeBlock code, Panel host)
    {
        var language = (code as FencedCodeBlock)?.Info;
        var text = code.Lines.ToString();

        var body = new TextBlock
        {
            Text = text,
            FontFamily = Font("Theme.Font.Mono"),
            FontSize = 13,
            LineHeight = 20,
            Foreground = Brush(ThemeTokens.Label.Primary),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
        };

        var scroller = new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
        };

        // A bar across the top naming the language, with a copy button on the right.
        //
        // This is the one part of a rendered answer that exists to be taken away and used
        // somewhere else, and selecting fourteen lines of Python with a mouse without catching
        // the paragraph above it is a small misery. Everything that shows code has settled on
        // the same bar for the same reason.
        var header = new Grid
        {
            Padding = new Thickness(14, 6, 6, 6),
            Background = Brush(ThemeTokens.Fill.Tertiary),
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "code" : language.Trim(),
            FontFamily = Font("Theme.Font.Small"),
            FontSize = 11,
            Foreground = Brush(ThemeTokens.Label.Tertiary),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var copyLabel = new TextBlock
        {
            Text = L.T("copy"),
            FontFamily = Font("Theme.Font.Small"),
            FontSize = 11,
            Foreground = Brush(ThemeTokens.Label.Secondary),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var copyContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        copyContent.Children.Add(new CampusIcon
        {
            Symbol = CampusSymbols.Copy,
            IconSize = 13,
            Foreground = Brush(ThemeTokens.Label.Secondary),
            VerticalAlignment = VerticalAlignment.Center,
        });
        copyContent.Children.Add(copyLabel);

        var copy = new Button
        {
            Content = copyContent,
            Style = (Style)Application.Current.Resources["Button.Plain"],
            Padding = new Thickness(8, 3, 8, 3),
            MinWidth = 0,
            MinHeight = 0,
        };

        AutomationProperties.SetName(copy, L.T("copy.this.code"));

        copy.Click += (_, _) =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);

            // Says so on the button rather than in a notification: the reader is looking at the
            // button, and a banner for something this small is an interruption.
            copyLabel.Text = L.T("copied");
            var revert = DispatcherQueue.CreateTimer();
            revert.Interval = TimeSpan.FromSeconds(2);
            revert.IsRepeating = false;
            revert.Tick += (t, _) => { copyLabel.Text = L.T("copy"); t.Stop(); };
            revert.Start();
        };

        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(header);
        stack.Children.Add(new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            Child = scroller,
        });

        host.Children.Add(new Border
        {
            Background = Brush(ThemeTokens.Fill.Quaternary),
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.Card"],
            Margin = new Thickness(0, 8, 0, 8),
            Child = stack,
        });
    }

    private void AddRule(Panel host) => host.Children.Add(new Border
    {
        Height = 1,
        Background = Brush(ThemeTokens.Separator.Standard),
        Margin = new Thickness(0, 20, 0, 20),
    });

    private void AddTable(Markdig.Extensions.Tables.Table table, Panel host)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 10, 0, 10),
            BorderBrush = Brush(ThemeTokens.Separator.Standard),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.S"],
            // Sized to its columns rather than to the page, so the border ends where the table
            // ends instead of running off to the right of the last cell.
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var rows = table.OfType<Markdig.Extensions.Tables.TableRow>().ToList();
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);

        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < rows[r].Count; c++)
            {
                if (rows[r][c] is not Markdig.Extensions.Tables.TableCell cell) continue;

                var content = new StackPanel { Spacing = 0 };
                foreach (var child in cell) AddBlock(child, content);

                var container = new Border
                {
                    Padding = new Thickness(12, 8, 12, 8),
                    Background = rows[r].IsHeader ? Brush(ThemeTokens.Fill.Quaternary) : null,
                    Child = content,
                };

                // A separator under each row, not a box around each cell.
                if (r < rows.Count - 1) container.BorderThickness = new Thickness(0, 0, 0, 1);
                container.BorderBrush = Brush(ThemeTokens.Separator.Standard);

                Grid.SetRow(container, r);
                Grid.SetColumn(container, c);
                grid.Children.Add(container);
            }
        }

        host.Children.Add(new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
    }

    private void AddText(string text, Panel host)
    {
        var block = Body();
        block.Inlines.Add(new Run { Text = text });
        host.Children.Add(block);
    }

    private TextBlock Body() => new()
    {
        FontFamily = Font("Theme.Font.Reading"),
        FontSize = 15,
        // Line height follows the reading preference rather than a constant, so the setting that
        // says "more air between lines" actually produces more air between lines.
        LineHeight = AccessibilityScaling.ReadingLineHeight(15),
        Foreground = Brush(ThemeTokens.Label.Primary),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 8),
        IsTextSelectionEnabled = true,
    };

    // ----------------------------------------------------------------------- inlines

    private void AddInlines(ContainerInline container, InlineCollection target)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run { Text = literal.Content.ToString() });
                    break;

                case EmphasisInline emphasis:
                    AddEmphasis(emphasis, target);
                    break;

                case CodeInline code:
                    target.Add(new Run
                    {
                        Text = code.Content,
                        FontFamily = Font("Theme.Font.Mono"),
                        FontSize = 13.5,
                        Foreground = Brush(ThemeTokens.Accent.Primary),
                    });
                    break;

                case LinkInline { IsImage: true } image:
                    target.Add(new Run
                    {
                        Text = image.Title is { Length: > 0 } title ? $"[{title}]" : "[image]",
                        Foreground = Brush(ThemeTokens.Label.Tertiary),
                    });
                    break;

                case LinkInline link:
                    AddLink(link, target);
                    break;

                case LineBreakInline lineBreak:
                    if (lineBreak.IsHard) target.Add(new LineBreak());
                    else target.Add(new Run { Text = " " });
                    break;

                case TaskList:
                    // Drawn as the list marker instead; adding it here would print "[x]" twice.
                    break;

                case AutolinkInline auto:
                    AddUri(auto.Url, auto.Url, target);
                    break;

                case ContainerInline nested:
                    AddInlines(nested, target);
                    break;

                default:
                    if (inline.ToString() is { Length: > 0 } fallback)
                        target.Add(new Run { Text = fallback });
                    break;
            }
        }
    }

    private void AddEmphasis(EmphasisInline emphasis, InlineCollection target)
    {
        Span span = emphasis.DelimiterChar switch
        {
            '~' when emphasis.DelimiterCount == 2 => new Span
            {
                TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough,
            },
            '=' => new Span
            {
                // A highlight is a background, and a Span cannot carry one, so it becomes the
                // one thing that does read as marked text without leaving the type system.
                Foreground = Brush(ThemeTokens.Warning.Primary),
                FontWeight = FontWeights.SemiBold,
            },
            _ when emphasis.DelimiterCount >= 2 => new Bold(),
            _ => new Italic(),
        };

        AddInlines(emphasis, span.Inlines);
        target.Add(span);
    }

    private void AddLink(LinkInline link, InlineCollection target)
    {
        var label = link.FirstChild is LiteralInline literal
            ? literal.Content.ToString()
            : link.Url ?? "";

        AddUri(link.Url ?? "", label, target);
    }

    private void AddUri(string url, string label, InlineCollection target)
    {
        var hyperlink = new Hyperlink
        {
            Foreground = Brush(ThemeTokens.Accent.Primary),
            UnderlineStyle = UnderlineStyle.None,
        };
        hyperlink.Inlines.Add(new Run { Text = label });

        // The host decides what a link does. A [[wiki link]] opens an object, an https link
        // leaves the app — and this control refuses to make that decision on its own.
        hyperlink.Click += (_, _) => LinkInvoked?.Invoke(this, url);

        target.Add(hyperlink);
    }

    // ------------------------------------------------------------------------ helpers

    private static Brush Brush(string token) => (Brush)Application.Current.Resources[token];
    private static FontFamily Font(string key) => (FontFamily)Application.Current.Resources[key];
}
