using System.Diagnostics;
using System.Text.Json;
using Campus.Documents;
using Campus.Domain;

namespace Campus.Desktop.Services;

/// <summary>
/// Works out what a file is, out of process when it can.
///
/// The formats an import has to open are the least trustworthy input Campus ever handles: a PDF
/// from a school portal, a deck of unknown provenance, a spreadsheet exported from something
/// ancient. Any of those can be malformed enough to take a parser down with it, and losing the
/// application mid-import would be inexcusable — so the parsing happens in a separate program
/// whose death costs a page count rather than the session.
///
/// If that program is missing, the same code runs here instead. A missing helper should mean less
/// isolation, never a broken import.
/// </summary>
public static class FileFactsReader
{
    private static readonly string IndexerPath =
        Path.Combine(AppContext.BaseDirectory, "indexer", "Campus.Indexer.exe");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<FileFacts> ReadAsync(string path, CancellationToken ct = default)
    {
        if (File.Exists(IndexerPath) && await RunIndexerAsync(path, ct) is { } facts) return facts;

        var local = FileInspector.Inspect(path);
        DocumentReaders.Enrich(local, path);
        return local;
    }

    private static async Task<FileFacts?> RunIndexerAsync(string path, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = IndexerPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(path);

        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;

            var output = await process.StandardOutput.ReadToEndAsync(ct);

            // A parser stuck in a loop on a malformed file must not hold up an import for ever.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }

            if (process.ExitCode != 0 || output.Trim().Length == 0) return null;

            return Parse(output);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception
                                      or InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    private static FileFacts? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        string? Text(string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        int? Number(string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : null;

        if (Text("fileName") is not { } fileName) return null;

        var facts = new FileFacts
        {
            FileName = fileName,
            Extension = Text("extension") ?? "",
            MimeType = Text("mimeType") ?? "application/octet-stream",
            Media = (MediaKind)(Number("media") ?? 0),
            SizeBytes = root.TryGetProperty("sizeBytes", out var size) ? size.GetInt64() : 0,
            PageCount = Number("pageCount"),
            PixelWidth = Number("pixelWidth"),
            PixelHeight = Number("pixelHeight"),
            EmbeddedTitle = Text("embeddedTitle"),
            Text = Text("text"),
        };

        if (root.TryGetProperty("duration", out var duration)
            && duration.ValueKind == JsonValueKind.Number)
        {
            facts.Duration = TimeSpan.FromSeconds(duration.GetDouble());
        }

        return facts;
    }
}
