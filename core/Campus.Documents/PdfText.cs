using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Outline;

namespace Campus.Documents;

/// <summary>One place a search term appears inside a document.</summary>
public sealed record PdfMatch(int PageIndex, string Context);

/// <summary>An entry in a PDF's own table of contents.</summary>
public sealed record PdfOutlineEntry(string Title, int PageIndex, int Level);

/// <summary>
/// The words in a PDF.
///
/// Rendering a page and reading a page are different problems, and PDFium does the first well and
/// does not expose the second. Without this, "search inside your documents" would quietly mean
/// "search the file names of your documents" — which is the sort of half-truth that makes people
/// stop trusting a search box.
///
/// Everything here is best-effort. A scanned page has no text in it at all; the answer for that
/// page is honestly nothing rather than a guess.
/// </summary>
public static class PdfText
{
    /// <summary>All of the text, capped, for the search index.</summary>
    public static string? Extract(Stream pdf, int limit = 400_000)
    {
        try
        {
            pdf.Position = 0;
            using var document = PdfDocument.Open(pdf);

            var text = new StringBuilder();

            foreach (var page in document.GetPages())
            {
                text.Append(page.Text).Append('\n');
                if (text.Length >= limit) break;
            }

            var result = text.ToString().Trim();
            return result.Length == 0 ? null : result;
        }
        catch (Exception)
        {
            // Encrypted, malformed, or a scan. Caught broadly on purpose: this reads the least
            // trustworthy input Campus handles, and the honest answer for a file whose text
            // cannot be read is no text — never a crash.
            return null;
        }
    }

    /// <summary>The text of one page, for showing a match in place.</summary>
    public static string? Page(Stream pdf, int pageIndex)
    {
        try
        {
            pdf.Position = 0;
            using var document = PdfDocument.Open(pdf);

            if (pageIndex < 0 || pageIndex >= document.NumberOfPages) return null;

            // PdfPig numbers pages from one; everything else in Campus counts from zero.
            return document.GetPage(pageIndex + 1).Text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Every page the phrase appears on, with enough words around it to recognise which one is
    /// the one you meant.
    /// </summary>
    public static IReadOnlyList<PdfMatch> Search(Stream pdf, string phrase, int limit = 200)
    {
        if (phrase.Trim().Length == 0) return [];

        var matches = new List<PdfMatch>();

        try
        {
            pdf.Position = 0;
            using var document = PdfDocument.Open(pdf);

            var index = 0;
            foreach (var page in document.GetPages())
            {
                var text = Haystack(page, phrase);

                var at = text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
                while (at >= 0 && matches.Count < limit)
                {
                    var from = Math.Max(0, at - 60);
                    var to = Math.Min(text.Length, at + phrase.Length + 60);

                    matches.Add(new PdfMatch(index, Ellipsis(text[from..to], from > 0, to < text.Length)));

                    at = text.IndexOf(phrase, at + phrase.Length, StringComparison.OrdinalIgnoreCase);
                }

                index++;
                if (matches.Count >= limit) break;
            }
        }
        catch (Exception)
        {
            // Whatever was found before the failure is still worth returning.
        }

        return matches;
    }

    /// <summary>
    /// The text of a page, in whichever form the phrase can be found in.
    ///
    /// Two forms are needed because PDFs disagree about spaces. Some write real space characters,
    /// and their raw text reads properly; others position every glyph individually, and their raw
    /// text is one long word until the letters are grouped. Trying the raw text first keeps
    /// reading order intact for the documents that have it, and falling back to grouped words
    /// finds the phrase in the ones that do not.
    /// </summary>
    private static string Haystack(UglyToad.PdfPig.Content.Page page, string phrase)
    {
        var raw = page.Text;
        if (raw.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return raw;

        var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
        var joined = string.Join(' ', words.Select(w => w.Text));

        return joined.Contains(phrase, StringComparison.OrdinalIgnoreCase) ? joined : raw;
    }

    /// <summary>
    /// The document's own table of contents, where it has one. A textbook's outline is the
    /// difference between reading a PDF and navigating one.
    /// </summary>
    public static IReadOnlyList<PdfOutlineEntry> Outline(Stream pdf)
    {
        try
        {
            pdf.Position = 0;
            using var document = PdfDocument.Open(pdf);

            if (!document.TryGetBookmarks(out var bookmarks)) return [];

            var entries = new List<PdfOutlineEntry>();
            foreach (var node in bookmarks.Roots) Walk(node, 0, entries);
            return entries;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void Walk(BookmarkNode node, int level, List<PdfOutlineEntry> entries)
    {
        var page = node is DocumentBookmarkNode document ? document.PageNumber - 1 : 0;

        if (node.Title is { Length: > 0 })
            entries.Add(new PdfOutlineEntry(node.Title.Trim(), Math.Max(0, page), level));

        foreach (var child in node.Children) Walk(child, level + 1, entries);
    }

    private static string Ellipsis(string text, bool before, bool after)
    {
        var cleaned = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return (before ? "…" : "") + cleaned + (after ? "…" : "");
    }
}
