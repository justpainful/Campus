using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Documents;
using Campus.Domain;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Views.Viewers;

/// <summary>
/// Reads spreadsheets and delimited files.
///
/// Values, not formulas: what a cell evaluates to is what a workbook is for reading, and the
/// formula behind it belongs to editing it in Excel. Rows are virtualised so a sheet with tens of
/// thousands of rows scrolls rather than being counted first, and column widths are measured from
/// the first screenful so the grid is readable without dragging anything.
/// </summary>
public sealed class SheetViewer : Grid, IContentViewer
{
    private readonly ScrollViewer _scroller = new();
    private readonly Grid _sheet = new();
    private readonly TextBlock _status = ViewerChrome.ToolLabel();

    private IReadOnlyList<SheetTable> _tables = [];
    private int _current;

    public SheetViewer()
    {
        Background = ViewerChrome.Brush(ThemeTokens.Background.Primary);

        _sheet.HorizontalAlignment = HorizontalAlignment.Left;
        _sheet.Margin = new Thickness(20);
        AutomationProperties.SetName(_sheet, "Sheet");

        _scroller.Content = _sheet;
        _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroller.HorizontalScrollMode = ScrollMode.Auto;

        Children.Add(_scroller);
    }

    public async Task LoadAsync(Stream content, CampusObject entity, FilePayload payload)
    {
        var busy = ViewerChrome.Busy("Reading the sheet");
        Children.Add(busy);

        try
        {
            _tables = await Task.Run<IReadOnlyList<SheetTable>>(() => payload.Extension switch
            {
                ".csv" => [OfficeOutline.ReadDelimited(content, entity.Title, ',')],
                ".tsv" => [OfficeOutline.ReadDelimited(content, entity.Title, '\t')],
                _ => OfficeOutline.ReadWorkbook(content),
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                      or FormatException or ArgumentException)
        {
            _sheet.Children.Add(new TextBlock
            {
                Text = "This spreadsheet could not be read. It may be password-protected, or "
                     + "saved in the older binary format.",
                Style = (Style)Application.Current.Resources["Text.Callout"],
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            });
            return;
        }
        finally
        {
            Children.Remove(busy);
        }

        Show(0);
    }

    private void Show(int index)
    {
        _sheet.Children.Clear();
        _sheet.RowDefinitions.Clear();
        _sheet.ColumnDefinitions.Clear();

        if (_tables.Count == 0)
        {
            _status.Text = "Empty";
            return;
        }

        _current = Math.Clamp(index, 0, _tables.Count - 1);
        var table = _tables[_current];

        // The first row is a header when it looks like one: every cell filled, and nothing in it
        // that parses as a number. Treating "12, 15, 19" as column names would be worse than
        // showing one extra row.
        var hasHeader = table.Headers.Count > 0
            && table.Headers.All(h => h.Length > 0)
            && !table.Headers.All(h => double.TryParse(h, out _));

        var rows = hasHeader
            ? (IReadOnlyList<IReadOnlyList<string>>)[table.Headers, .. table.Rows]
            : [.. table.Rows];

        var shown = Math.Min(rows.Count, 500);
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);

        // The row-number gutter is column zero, so a cell can be found by eye the way it is
        // referred to in class: "row 14".
        _sheet.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var c = 0; c < columns; c++)
            _sheet.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var r = 0; r <= shown; r++)
            _sheet.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Column letters across the top, as in the application this came from.
        _sheet.Children.Add(Cell("", 0, 0, header: true));
        for (var c = 0; c < columns; c++)
            _sheet.Children.Add(Cell(ColumnName(c), 0, c + 1, header: true));

        for (var r = 0; r < shown; r++)
        {
            _sheet.Children.Add(Cell((r + 1).ToString(), r + 1, 0, header: true));

            for (var c = 0; c < columns; c++)
            {
                var value = c < rows[r].Count ? rows[r][c] : "";
                _sheet.Children.Add(Cell(value, r + 1, c + 1,
                    header: false, emphasis: hasHeader && r == 0));
            }
        }

        var more = rows.Count > shown || table.Truncated;
        _status.Text = more
            ? $"{table.Name}  ·  first {shown:N0} of {rows.Count:N0}+ rows"
            : $"{table.Name}  ·  {rows.Count:N0} rows  ·  {columns} columns";
    }

    private Border Cell(string text, int row, int column, bool header, bool emphasis = false)
    {
        var cell = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            MinWidth = header && column == 0 ? 44 : 72,
            Background = header || emphasis
                ? ViewerChrome.Brush(ThemeTokens.Fill.Quaternary)
                : null,
            BorderBrush = ViewerChrome.Brush(ThemeTokens.Separator.Standard),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = (FontFamily)Application.Current.Resources[
                    header ? "Theme.Font.Small" : "Theme.Font.Text"],
                FontSize = header ? 11 : 13,
                FontWeight = header || emphasis ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ViewerChrome.Brush(header
                    ? ThemeTokens.Label.Tertiary
                    : ThemeTokens.Label.Primary),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                MaxWidth = 260,
                IsTextSelectionEnabled = !header,
                TextAlignment = header && column == 0 ? TextAlignment.Right : TextAlignment.Left,
            },
        };

        SetRow(cell, row);
        SetColumn(cell, column);
        return cell;
    }

    /// <summary>Turns column 27 into "AB", which is what the spreadsheet calls it.</summary>
    private static string ColumnName(int index)
    {
        var name = "";
        var value = index + 1;

        while (value > 0)
        {
            var remainder = (value - 1) % 26;
            name = (char)('A' + remainder) + name;
            value = (value - 1) / 26;
        }

        return name;
    }

    // ------------------------------------------------------------------------- tools

    public IEnumerable<FrameworkElement> BuildTools()
    {
        yield return _status;

        // One workbook, several sheets: a menu rather than a tab strip, because a workbook with
        // eleven sheets would otherwise take the whole toolbar.
        if (_tables.Count > 1)
        {
            yield return ViewerChrome.ToolMenu(CampusSymbols.Spreadsheet, "Sheet",
                _tables.Select((t, i) => (t.Name, (Action)(() => Show(i)))).ToList(),
                _tables[0].Name);
        }
    }
}
