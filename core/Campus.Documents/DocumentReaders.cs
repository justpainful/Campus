using System.Text;
using Campus.Domain;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Campus.Documents;

/// <summary>
/// Reads what a document can tell about itself: how many pages, what it says, what it is called.
///
/// Everything here is best-effort by design. A corrupt PDF or a password-protected spreadsheet
/// must not stop a file being imported — the bytes still go into the vault and the file is still
/// yours; it simply arrives without a page count or without being searchable inside.
/// </summary>
public static class DocumentReaders
{
    /// <summary>Fills in whatever this format can cheaply say about itself.</summary>
    public static void Enrich(FileFacts facts, string path)
    {
        try
        {
            switch (facts.Media)
            {
                case MediaKind.Pdf: ReadPdf(facts, path); break;
                case MediaKind.Document when facts.Extension is ".docx": ReadWord(facts, path); break;
                case MediaKind.Spreadsheet when facts.Extension is ".xlsx": ReadExcel(facts, path); break;
                case MediaKind.Spreadsheet when facts.Extension is ".csv" or ".tsv": ReadDelimited(facts, path); break;
                case MediaKind.Presentation when facts.Extension is ".pptx": ReadPowerPoint(facts, path); break;
                case MediaKind.Text or MediaKind.Markdown or MediaKind.Web: ReadText(facts, path); break;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                      or FormatException or NotSupportedException
                                      or UnauthorizedAccessException)
        {
            // Import continues without the extras. The file is what matters.
        }
    }

    // -------------------------------------------------------------------------- PDF

    private static void ReadPdf(FileFacts facts, string path)
    {
        using var stream = File.OpenRead(path);
        facts.PageCount = PDFtoImage.Conversion.GetPageCount(stream);

        if (facts.PageCount is > 0)
        {
            stream.Position = 0;
            var size = PDFtoImage.Conversion.GetPageSize(stream, 0);
            facts.PixelWidth = (int)size.Width;
            facts.PixelHeight = (int)size.Height;
        }
    }

    // ------------------------------------------------------------------------- Word

    private static void ReadWord(FileFacts facts, string path)
    {
        using var document = WordprocessingDocument.Open(path, isEditable: false);

        facts.EmbeddedTitle = Clean(document.PackageProperties.Title);

        var body = document.MainDocumentPart?.Document?.Body;
        if (body is not null) facts.Text = Truncate(body.InnerText);

        // Word records a page count when it last saved. It is an estimate, and labelled as one
        // in the UI, but it beats showing nothing for a hundred-page textbook.
        var pages = document.ExtendedFilePropertiesPart?.Properties?.Pages?.Text;
        if (int.TryParse(pages, out var count) && count > 0) facts.PageCount = count;
    }

    // ------------------------------------------------------------------------ Excel

    private static void ReadExcel(FileFacts facts, string path)
    {
        using var document = SpreadsheetDocument.Open(path, isEditable: false);

        facts.EmbeddedTitle = Clean(document.PackageProperties.Title);

        var workbook = document.WorkbookPart;
        var sheets = workbook?.Workbook?.Sheets?.Elements<Sheet>().ToList() ?? [];
        facts.PageCount = sheets.Count;

        // Cells hold indexes into a shared string table rather than the strings themselves, so
        // the table is read once and looked up per cell.
        var shared = workbook?.SharedStringTablePart?.SharedStringTable?
            .Elements<SharedStringItem>().Select(i => i.InnerText).ToArray() ?? [];

        var text = new StringBuilder();
        foreach (var sheet in sheets)
        {
            if (sheet.Name?.Value is { } name) text.Append(name).Append(' ');
            if (workbook?.GetPartById(sheet.Id!) is not WorksheetPart part) continue;

            foreach (var cell in part.Worksheet.Descendants<Cell>().Take(2_000))
            {
                var value = cell.DataType?.Value == CellValues.SharedString
                    && int.TryParse(cell.CellValue?.Text, out var index)
                    && index < shared.Length
                        ? shared[index]
                        : cell.CellValue?.Text;

                if (!string.IsNullOrWhiteSpace(value)) text.Append(value).Append(' ');
                if (text.Length > 40_000) break;
            }
            if (text.Length > 40_000) break;
        }

        facts.Text = Truncate(text.ToString());
    }

    private static void ReadDelimited(FileFacts facts, string path)
    {
        var lines = File.ReadLines(path).Take(500).ToList();
        facts.PageCount = 1;
        facts.Text = Truncate(string.Join(' ', lines));
    }

    // ------------------------------------------------------------------- PowerPoint

    private static void ReadPowerPoint(FileFacts facts, string path)
    {
        using var document = PresentationDocument.Open(path, isEditable: false);

        facts.EmbeddedTitle = Clean(document.PackageProperties.Title);

        var slides = document.PresentationPart?.SlideParts.ToList() ?? [];
        facts.PageCount = slides.Count;

        var text = new StringBuilder();
        foreach (var slide in slides)
        {
            text.Append(slide.Slide.InnerText).Append(' ');
            // Speaker notes are often where the actual explanation lives, so they are searchable.
            if (slide.NotesSlidePart?.NotesSlide is { } notes) text.Append(notes.InnerText).Append(' ');
            if (text.Length > 60_000) break;
        }

        facts.Text = Truncate(text.ToString());
    }

    // ------------------------------------------------------------------------- text

    private static void ReadText(FileFacts facts, string path)
    {
        // Capped rather than read whole: a log file should be searchable without being held in
        // memory in its entirety.
        var buffer = new char[80_000];
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var read = reader.Read(buffer, 0, buffer.Length);
        facts.Text = Truncate(new string(buffer, 0, Math.Max(read, 0)));

        if (facts.Media == MediaKind.Markdown) facts.EmbeddedTitle = FirstHeading(facts.Text);
    }

    /// <summary>A markdown file's first `#` heading is a better title than its file name.</summary>
    private static string? FirstHeading(string? text)
    {
        if (text is null) return null;

        foreach (var raw in text.Split('\n').Take(40))
        {
            var line = raw.Trim();
            if (!line.StartsWith('#')) continue;
            var heading = line.TrimStart('#').Trim();
            if (heading.Length > 0) return heading.Length > 120 ? heading[..120] : heading;
        }
        return null;
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Collapses whitespace and caps the length. The index wants words, not layout, and a
    /// hundred-megabyte extraction would be a hundred megabytes inside the encrypted database.
    /// </summary>
    private static string? Truncate(string? text, int limit = 100_000)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var builder = new StringBuilder(Math.Min(text.Length, limit));
        var lastWasSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) builder.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                builder.Append(c);
                lastWasSpace = false;
            }
            if (builder.Length >= limit) break;
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? null : result;
    }
}
