using System.Text;
using Campus.Documents;
using Campus.Vault;
using Xunit;

namespace Campus.Core.Tests;

/// <summary>
/// The reading path, end to end: a real PDF, stored encrypted, opened again through the vault and
/// handed to the readers exactly as a viewer would hand it to them.
///
/// These exist because the interesting failures in this path are not about parsing. They are about
/// what the vault hands back — whether it can be seeked, whether it starts where the reader expects
/// it to — and those only show up when the stream is the vault's rather than a file's.
/// </summary>
public sealed class DocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "campus-doc-tests-" + Guid.NewGuid().ToString("N"));

    private static byte[] SamplePdf() => TestPdf.Create(
        "Fields and forces",
        [
            "The electric field E at a point is the force per unit positive charge placed there.",
            "A charge q in a field E feels a force F = qE, opposite to E when q is negative.",
        ]);

    [Fact]
    public void APdfCanBeReadFromAPlainFile()
    {
        var path = Path.Combine(_root, "sample.pdf");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(path, SamplePdf());

        using var stream = File.OpenRead(path);

        Assert.Equal(2, PdfRenderer.PageCount(stream));

        var size = PdfRenderer.PageSize(stream, 0);
        Assert.NotNull(size);
        Assert.True(size!.Value.Height > size.Value.Width, "A4 is taller than it is wide.");

        var text = PdfText.Extract(stream);
        Assert.NotNull(text);
        Assert.Contains("electric field", text!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APdfStoredInTheVaultIsStillReadable()
    {
        var vault = new CampusVault(new VaultPaths(_root), null);
        await vault.CreateAsync();

        var stored = await vault.Objects.PutBytesAsync(SamplePdf());

        using var stream = vault.Objects.OpenRead(stored.ContentHash);

        // The readers need to seek. A vault stream that cannot is the difference between a
        // textbook that opens and one that throws on the first page.
        Assert.True(stream.CanSeek, "A vault object must be seekable.");
        Assert.Equal(2, PdfRenderer.PageCount(stream));

        // Reading twice from the same stream is what a viewer actually does: count, then measure,
        // then render, then search — all without reopening.
        Assert.NotNull(PdfRenderer.PageSize(stream, 1));
        Assert.Equal(2, PdfRenderer.PageCount(stream));

        var matches = PdfText.Search(stream, "positive charge");
        Assert.NotEmpty(matches);
        Assert.Equal(0, matches[0].PageIndex);

        vault.Dispose();
    }

    [Fact]
    public void SearchingInsideAPdfFindsThePageAndNotJustTheFile()
    {
        using var stream = new MemoryStream(SamplePdf());

        var matches = PdfText.Search(stream, "negative");

        Assert.Single(matches);
        Assert.Equal(1, matches[0].PageIndex);
        Assert.Contains("negative", matches[0].Context, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhatAFileIsComesFromItsBytesRatherThanItsName()
    {
        Directory.CreateDirectory(_root);

        // A PDF saved under the wrong extension is still a PDF, and treating it as text would
        // hand it to the wrong reader.
        var misnamed = Path.Combine(_root, "lecture.txt");
        File.WriteAllBytes(misnamed, SamplePdf());

        var facts = FileInspector.Inspect(misnamed);

        Assert.Equal(Domain.MediaKind.Pdf, facts.Media);
        Assert.Equal("application/pdf", facts.MimeType);
    }

    [Fact]
    public void AMarkdownFileTakesItsTitleFromItsFirstHeading()
    {
        Directory.CreateDirectory(_root);

        var path = Path.Combine(_root, "notes.md");
        File.WriteAllText(path, "# Bonding\n\nIonic bonds **give**; covalent bonds share.\n");

        var facts = FileInspector.Inspect(path);
        DocumentReaders.Enrich(facts, path);

        Assert.Equal("Bonding", facts.EmbeddedTitle);
        Assert.Contains("covalent", facts.Text!, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* the temp folder is cleaned by the system */ }
    }
}

/// <summary>
/// Writes a small, genuinely valid PDF for the tests to read.
///
/// Deliberately hand-written: a test that reads a PDF produced by the same library that reads it
/// proves the two agree, not that either is right.
/// </summary>
internal static class TestPdf
{
    public static byte[] Create(string title, IReadOnlyList<string> pages)
    {
        var output = new MemoryStream();
        var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        var offsets = new List<long>();

        void Write(string text) => writer.Write(Encoding.ASCII.GetBytes(text));

        void Object(int number, string body)
        {
            offsets.Add(output.Position);
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.4\n");
        writer.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        var pageIds = Enumerable.Range(0, pages.Count).Select(i => 4 + i * 2).ToList();

        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, $"<< /Type /Pages /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] "
                + $"/Count {pages.Count} >>");
        Object(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        for (var i = 0; i < pages.Count; i++)
        {
            var pageId = pageIds[i];
            var contentId = pageId + 1;

            Object(pageId,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] "
                + "/Resources << /Font << /F1 3 0 R >> >> "
                + $"/Contents {contentId} 0 R >>");

            var heading = i == 0 ? title : $"{title} page {i + 1}";
            var content =
                $"BT\n/F1 22 Tf\n72 760 Td\n({Escape(heading)}) Tj\nET\n"
                + $"BT\n/F1 12 Tf\n72 710 Td\n({Escape(pages[i])}) Tj\nET\n";

            var bytes = Encoding.ASCII.GetBytes(content);

            offsets.Add(output.Position);
            Write($"{contentId} 0 obj\n<< /Length {bytes.Length} >>\nstream\n");
            writer.Write(bytes);
            Write("\nendstream\nendobj\n");
        }

        var xref = output.Position;
        var count = offsets.Count + 1;

        Write($"xref\n0 {count}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        writer.Flush();
        return output.ToArray();
    }

    private static string Escape(string text) => text
        .Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
