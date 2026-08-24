using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Campus.Documents;

public enum DocBlockKind
{
    Paragraph,
    Heading,
    Bullet,
    Numbered,
    Quote,
    Code,
    Table,
    Note,
}

/// <summary>
/// One piece of a document, already reduced to what a reader needs: what kind of thing it is,
/// what it says, and how deep it sits. Fonts, margins and colours are deliberately dropped —
/// Campus shows documents in its own type, not in Word's.
/// </summary>
public sealed record DocBlock(
    DocBlockKind Kind,
    string Text,
    int Level = 0,
    IReadOnlyList<IReadOnlyList<string>>? Rows = null);

/// <summary>A chapter of a document, or a single slide.</summary>
public sealed record DocSection(string Title, IReadOnlyList<DocBlock> Blocks);

/// <summary>One sheet of a workbook, or one delimited file.</summary>
public sealed record SheetTable(
    string Name,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    bool Truncated);

/// <summary>
/// Turns Office files into something Campus can draw.
///
/// This is not a converter and does not try to be: it recovers structure — headings, lists,
/// tables, slides, speaker notes — and throws away presentation. That is the right trade for
/// reading a lecture handout, and it is why these viewers open instantly on files that take Word
/// several seconds.
/// </summary>
public static class OfficeOutline
{
    private const int MaxBlocks = 4_000;

    // ------------------------------------------------------------------------- Word

    public static IReadOnlyList<DocSection> ReadWord(Stream stream)
    {
        stream.Position = 0;
        using var document = WordprocessingDocument.Open(stream, isEditable: false);

        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null) return [];

        var sections = new List<DocSection>();
        var current = new List<DocBlock>();
        var title = "";
        var total = 0;

        void Flush()
        {
            if (current.Count == 0 && title.Length == 0) return;
            sections.Add(new DocSection(title, current));
            current = [];
        }

        foreach (var element in body.ChildElements)
        {
            if (total > MaxBlocks) break;

            switch (element)
            {
                case Paragraph paragraph:
                {
                    var text = ReadRuns(paragraph);
                    if (text.Length == 0) continue;

                    var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
                    var level = HeadingLevel(style);
                    total++;

                    if (level > 0)
                    {
                        // A top-level heading starts a new section, which is what makes the
                        // outline beside the viewer possible.
                        if (level == 1) { Flush(); title = text; }
                        current.Add(new DocBlock(DocBlockKind.Heading, text, level));
                    }
                    else if (style.Contains("Quote", StringComparison.OrdinalIgnoreCase))
                    {
                        current.Add(new DocBlock(DocBlockKind.Quote, text));
                    }
                    else if (paragraph.ParagraphProperties?.NumberingProperties is not null)
                    {
                        var indent = paragraph.ParagraphProperties.NumberingProperties
                            .NumberingLevelReference?.Val?.Value ?? 0;
                        current.Add(new DocBlock(DocBlockKind.Bullet, text, indent));
                    }
                    else
                    {
                        current.Add(new DocBlock(DocBlockKind.Paragraph, text));
                    }
                    break;
                }

                case Table table:
                {
                    var rows = table.Elements<TableRow>()
                        .Take(200)
                        .Select(row => (IReadOnlyList<string>)row.Elements<TableCell>()
                            .Take(30)
                            .Select(cell => Collapse(cell.InnerText))
                            .ToList())
                        .ToList();

                    total += rows.Count;
                    if (rows.Count > 0) current.Add(new DocBlock(DocBlockKind.Table, "", Rows: rows));
                    break;
                }
            }
        }

        Flush();
        return sections;
    }

    /// <summary>
    /// Reads a paragraph's runs, keeping line breaks and tabs, because in a handout those are
    /// often the only thing separating a question from its answer.
    /// </summary>
    private static string ReadRuns(Paragraph paragraph)
    {
        var text = new StringBuilder();

        foreach (var node in paragraph.Descendants())
        {
            switch (node)
            {
                case Text run: text.Append(run.Text); break;
                case TabChar: text.Append('\t'); break;
                case Break: text.Append('\n'); break;
            }
        }

        return text.ToString().Trim();
    }

    private static int HeadingLevel(string style)
    {
        if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(style.AsSpan(7), out var level))
        {
            return Math.Clamp(level, 1, 6);
        }
        return style.Equals("Title", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    // ------------------------------------------------------------------- PowerPoint

    public static IReadOnlyList<DocSection> ReadPresentation(Stream stream)
    {
        stream.Position = 0;
        using var document = PresentationDocument.Open(stream, isEditable: false);

        var slides = document.PresentationPart?.SlideParts.ToList() ?? [];
        var sections = new List<DocSection>(slides.Count);
        var number = 0;

        foreach (var slidePart in slides)
        {
            number++;
            var blocks = new List<DocBlock>();
            var title = "";

            foreach (var shape in slidePart.Slide?.Descendants<P.Shape>() ?? [])
            {
                var lines = ReadShape(shape);
                if (lines.Count == 0) continue;

                // The shape wearing the title placeholder is the slide's title; everything else
                // is body text, in the order the deck lays it out.
                // PlaceholderValues is a struct in OpenXml 3, so this is a comparison rather
                // than a pattern match.
                var placeholder = shape.NonVisualShapeProperties?
                    .ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value;

                var isTitle = placeholder == P.PlaceholderValues.Title
                    || placeholder == P.PlaceholderValues.CenteredTitle;

                if (isTitle && title.Length == 0)
                {
                    title = string.Join(" ", lines);
                    blocks.Insert(0, new DocBlock(DocBlockKind.Heading, title, 1));
                }
                else
                {
                    foreach (var line in lines) blocks.Add(new DocBlock(DocBlockKind.Bullet, line));
                }
            }

            if (slidePart.NotesSlidePart?.NotesSlide is { } notes)
            {
                var text = Collapse(notes.InnerText);
                if (text.Length > 0) blocks.Add(new DocBlock(DocBlockKind.Note, text));
            }

            sections.Add(new DocSection(title.Length > 0 ? title : $"Slide {number}", blocks));
        }

        return sections;
    }

    private static List<string> ReadShape(P.Shape shape)
    {
        var lines = new List<string>();
        if (shape.TextBody is null) return lines;

        foreach (var paragraph in shape.TextBody.Elements<D.Paragraph>())
        {
            var text = Collapse(string.Concat(paragraph.Descendants<D.Text>().Select(t => t.Text)));
            if (text.Length > 0) lines.Add(text);
        }

        return lines;
    }

    // ------------------------------------------------------------------------ Excel

    public static IReadOnlyList<SheetTable> ReadWorkbook(Stream stream, int maxRows = 2_000)
    {
        stream.Position = 0;
        using var document = SpreadsheetDocument.Open(stream, isEditable: false);

        var workbook = document.WorkbookPart;
        var sheets = workbook?.Workbook?.Sheets?.Elements<S.Sheet>().ToList() ?? [];

        var shared = workbook?.SharedStringTablePart?.SharedStringTable?
            .Elements<S.SharedStringItem>().Select(i => i.InnerText).ToArray() ?? [];

        var tables = new List<SheetTable>(sheets.Count);

        foreach (var sheet in sheets)
        {
            if (sheet.Id?.Value is not { } id) continue;
            if (workbook?.GetPartById(id) is not WorksheetPart part) continue;

            var rows = new List<IReadOnlyList<string>>();
            var truncated = false;
            var width = 0;

            foreach (var row in part.Worksheet?.Descendants<S.Row>() ?? [])
            {
                if (rows.Count >= maxRows) { truncated = true; break; }

                var cells = new List<string>();
                foreach (var cell in row.Elements<S.Cell>())
                {
                    // Blank cells are not written to the file at all, so the column reference is
                    // the only way to know a value belongs in column D rather than column B.
                    var column = ColumnIndex(cell.CellReference?.Value);
                    while (column > cells.Count) cells.Add("");
                    cells.Add(ReadCell(cell, shared));
                }

                width = Math.Max(width, cells.Count);
                rows.Add(cells);
            }

            // Every row is padded to the widest, so the grid is rectangular and columns line up.
            var padded = rows
                .Select(r =>
                {
                    var list = r.ToList();
                    while (list.Count < width) list.Add("");
                    return (IReadOnlyList<string>)list;
                })
                .ToList();

            tables.Add(new SheetTable(
                sheet.Name?.Value ?? "Sheet",
                padded.Count > 0 ? padded[0] : [],
                padded.Count > 1 ? padded.Skip(1).ToList() : [],
                truncated));
        }

        return tables;
    }

    private static string ReadCell(S.Cell cell, string[] shared)
    {
        var raw = cell.CellValue?.Text;

        if (cell.DataType?.Value == S.CellValues.SharedString
            && int.TryParse(raw, out var index)
            && index >= 0 && index < shared.Length)
        {
            return shared[index];
        }

        if (cell.DataType?.Value == S.CellValues.InlineString) return Collapse(cell.InnerText);
        if (cell.DataType?.Value == S.CellValues.Boolean) return raw == "1" ? "TRUE" : "FALSE";

        return raw ?? "";
    }

    /// <summary>Turns a cell reference such as "AB7" into the zero-based column 27.</summary>
    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return 0;

        var index = 0;
        foreach (var c in reference)
        {
            if (!char.IsAsciiLetter(c)) break;
            index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }
        return Math.Max(0, index - 1);
    }

    // -------------------------------------------------------------------- delimited

    public static SheetTable ReadDelimited(Stream stream, string name, char delimiter, int maxRows = 5_000)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        var rows = new List<IReadOnlyList<string>>();
        var truncated = false;

        while (reader.ReadLine() is { } line)
        {
            if (rows.Count >= maxRows) { truncated = true; break; }
            rows.Add(SplitDelimited(line, delimiter));
        }

        var width = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var padded = rows.Select(r =>
        {
            var list = r.ToList();
            while (list.Count < width) list.Add("");
            return (IReadOnlyList<string>)list;
        }).ToList();

        return new SheetTable(
            name,
            padded.Count > 0 ? padded[0] : [],
            padded.Count > 1 ? padded.Skip(1).ToList() : [],
            truncated);
    }

    /// <summary>
    /// Splits one CSV line, honouring quoted fields and doubled quotes inside them. Splitting
    /// naively on commas breaks the moment a row contains an address.
    /// </summary>
    private static List<string> SplitDelimited(string line, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == delimiter) { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }

        fields.Add(field.ToString());
        return fields;
    }

    private static string Collapse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) builder.Append(' ');
                lastWasSpace = true;
            }
            else { builder.Append(c); lastWasSpace = false; }
        }

        return builder.ToString().Trim();
    }
}
