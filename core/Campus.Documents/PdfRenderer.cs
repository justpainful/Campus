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
            return PDFtoImage.Conversion.GetPageCount(pdf);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException)
        {
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
                output, pdf, page: pageIndex,
                options: new PDFtoImage.RenderOptions(Width: width));
            return output.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                      or FormatException or ArgumentOutOfRangeException)
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
            var size = PDFtoImage.Conversion.GetPageSize(pdf, pageIndex);
            return (size.Width, size.Height);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                      or FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
