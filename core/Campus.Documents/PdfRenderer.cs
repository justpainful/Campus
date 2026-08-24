namespace Campus.Documents;

/// <summary>
/// Renders PDF pages to images.
///
/// Pages are rendered on demand at the size they will be shown rather than all at once: a
/// three-hundred-page textbook opened at page one should cost one page of work, not three
/// hundred.
/// </summary>
public static class PdfRenderer
{
    /// <summary>How many pages the document has, or zero if it cannot be read.</summary>
    public static int PageCount(Stream pdf)
    {
        try
        {
            pdf.Position = 0;

            // leaveOpen is false by default, which closes the caller's stream. A viewer counts,
            // measures and then renders from one stream, so closing it after the first call would
            // break every page after the first.
            return PDFtoImage.Conversion.GetPageCount(pdf, leaveOpen: true);
        }
        catch (Exception)
        {
            // Broad on purpose: a document that cannot be counted has zero readable pages, and
            // saying so is always better than taking the application down with it.
            return 0;
        }
    }

    /// <summary>
    /// Renders one page as PNG bytes at the requested width. Returns null when the page cannot
    /// be rendered, which a viewer shows as a blank page rather than treating as a crash.
    /// </summary>
    public static byte[]? RenderPage(Stream pdf, int pageIndex, int width)
    {
        try
        {
            pdf.Position = 0;
            using var output = new MemoryStream();
            PDFtoImage.Conversion.SavePng(
                output, pdf, page: pageIndex, leaveOpen: true,
                options: new PDFtoImage.RenderOptions(Width: width));
            return output.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The aspect ratio of a page, so a viewer can size its placeholder before rendering.</summary>
    public static (double Width, double Height)? PageSize(Stream pdf, int pageIndex)
    {
        try
        {
            pdf.Position = 0;
            var size = PDFtoImage.Conversion.GetPageSize(pdf, pageIndex, leaveOpen: true);
            return (size.Width, size.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
