using Campus.Documents;
using Campus.Domain;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Campus.Desktop.Services;

/// <summary>What happened to one file.</summary>
public sealed record ImportResult(
    string FileName,
    CampusObject? Created,
    bool AlreadyHeld,
    string? Failure)
{
    public bool Succeeded => Created is not null;
}

/// <summary>
/// Everything that happens between a file being dropped on Campus and it being part of the
/// workspace:
///
///     identify → hash → store encrypted → read what it can say → thumbnail → index → record
///
/// Identify comes first because it decides what the rest of the steps mean. Hashing comes before
/// storing because the hash is the address, which is what makes importing the same textbook twice
/// cost nothing the second time.
/// </summary>
public sealed class ImportService(WorkspaceService workspace)
{
    private readonly WorkspaceService _workspace = workspace;

    /// <summary>Raised per file so the UI can show progress on a long import.</summary>
    public event EventHandler<ImportResult>? FileImported;

    public async Task<IReadOnlyList<ImportResult>> ImportAsync(
        IEnumerable<string> paths,
        CampusId? subjectId = null,
        IEnumerable<string>? tags = null,
        CancellationToken ct = default)
    {
        var results = new List<ImportResult>();

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var result = await ImportOneAsync(path, subjectId, tags, ct).ConfigureAwait(false);
            results.Add(result);
            FileImported?.Invoke(this, result);
        }

        return results;
    }

    private async Task<ImportResult> ImportOneAsync(
        string path, CampusId? subjectId, IEnumerable<string>? tags, CancellationToken ct)
    {
        var name = Path.GetFileName(path);

        if (!_workspace.IsUnlocked)
            return new ImportResult(name, null, false, "The workspace is locked.");

        if (!File.Exists(path))
            return new ImportResult(name, null, false, "The file is no longer there.");

        try
        {
            var facts = FileInspector.Inspect(path);

            // Reading the document happens before the bytes are stored, while the file is still
            // a plain file on disk. Doing it afterwards would mean decrypting what was just
            // encrypted for no reason.
            DocumentReaders.Enrich(facts, path);

            var stored = await _workspace.Vault.Objects.PutFileAsync(path, ct).ConfigureAwait(false);

            var payload = new FilePayload
            {
                ContentHash = stored.ContentHash,
                OriginalFileName = facts.FileName,
                Extension = facts.Extension,
                MimeType = facts.MimeType,
                Media = facts.Media,
                SizeBytes = stored.SizeBytes,
                PageCount = facts.PageCount,
                PixelWidth = facts.PixelWidth,
                PixelHeight = facts.PixelHeight,
                Duration = facts.Duration,
                TextExtracted = facts.Text is not null,
                ImportedAt = DateTimeOffset.UtcNow,
            };

            payload.ThumbnailHash = await MakeThumbnailAsync(path, facts, ct).ConfigureAwait(false);

            var entity = new CampusObject
            {
                // A title from inside the document beats the file name, which is often a scan
                // number or whatever the teacher's computer called it.
                Title = facts.EmbeddedTitle ?? Path.GetFileNameWithoutExtension(facts.FileName),
                Kind = ObjectKind.File,
                SubjectId = subjectId,
                Source = CaptureSource.Import,
                SourceDeviceId = _workspace.DeviceId,
                Summary = Summarise(facts),
                Payload = payload,
            };

            if (tags is not null) entity.Tags.AddRange(tags);

            await _workspace.Objects.SaveAsync(entity, ct).ConfigureAwait(false);

            // Extracted text is indexed separately from the object's own fields, so searching
            // inside a PDF and searching its title are the same search.
            if (facts.Text is not null)
                await IndexTextAsync(entity, facts.Text, ct).ConfigureAwait(false);

            return new ImportResult(name, entity, stored.AlreadyExisted, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException)
        {
            return new ImportResult(name, null, false, ex.Message);
        }
    }

    private static string? Summarise(FileFacts facts)
    {
        var parts = new List<string>();
        if (facts.PageCount is { } pages) parts.Add($"{pages} page{(pages == 1 ? "" : "s")}");
        if (facts.PixelWidth is { } w && facts.PixelHeight is { } h) parts.Add($"{w}×{h}");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// Adds the document's text to the search index for this object, on top of what the object
    /// itself contributes.
    /// </summary>
    private async Task IndexTextAsync(CampusObject entity, string text, CancellationToken ct)
    {
        await using var command = _workspace.Database.CreateCommand("""
            UPDATE objects_fts SET body = @body WHERE object_id = @id;
            """);
        command.Parameters.AddWithValue("@body", text);
        command.Parameters.AddWithValue("@id", entity.Id.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------- thumbnails

    /// <summary>
    /// Makes a preview and stores it in the vault like everything else, so a thumbnail of a
    /// private document is exactly as private as the document.
    /// </summary>
    private async Task<string?> MakeThumbnailAsync(string path, FileFacts facts, CancellationToken ct)
    {
        try
        {
            var png = facts.Media switch
            {
                MediaKind.Pdf => RenderPdfCover(path),
                MediaKind.Image => await ScaleImageAsync(path, ct).ConfigureAwait(false),
                _ => null,
            };

            if (png is null) return null;

            var stored = await _workspace.Vault.Objects.PutBytesAsync(png, ct).ConfigureAwait(false);
            return stored.ContentHash;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            // A file without a thumbnail is still a perfectly good file.
            return null;
        }
    }

    private static byte[]? RenderPdfCover(string path)
    {
        using var stream = File.OpenRead(path);
        return PdfRenderer.RenderPage(stream, 0, 480);
    }

    /// <summary>
    /// Scales an image down using the platform decoder, which is what gives HEIC support for
    /// free — the format an iPhone actually produces.
    /// </summary>
    private static async Task<byte[]?> ScaleImageAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        using var source = file.AsRandomAccessStream();

        var decoder = await BitmapDecoder.CreateAsync(source);

        const uint target = 480;
        var scale = Math.Min(1.0, target / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateForTranscodingAsync(output, decoder);
        encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale);
        encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale);
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        await encoder.FlushAsync();

        // Read back through a DataReader: the byte-array buffer extensions are not available
        // to a modern .NET target without the legacy WinRT interop shim.
        var bytes = new byte[output.Size];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>Loads a stored thumbnail for display, or null when there is not one.</summary>
    public async Task<BitmapImage?> LoadThumbnailAsync(string? thumbnailHash, int width = 240)
    {
        if (thumbnailHash is null || !_workspace.IsUnlocked) return null;

        try
        {
            var bytes = await _workspace.Vault.Objects.ReadAllBytesAsync(thumbnailHash)
                .ConfigureAwait(true);

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            var image = new BitmapImage { DecodePixelWidth = width };
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException
                                      or InvalidOperationException)
        {
            return null;
        }
    }
}
