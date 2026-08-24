using Campus.Domain;

namespace Campus.Documents;

/// <summary>What an import discovered about a file before it went into the vault.</summary>
public sealed class FileFacts
{
    public required string FileName { get; init; }
    public required string Extension { get; init; }
    public required string MimeType { get; init; }
    public required MediaKind Media { get; init; }
    public long SizeBytes { get; init; }

    public int? PageCount { get; set; }
    public int? PixelWidth { get; set; }
    public int? PixelHeight { get; set; }
    public TimeSpan? Duration { get; set; }

    /// <summary>Text worth searching, when the format has any that can be read cheaply.</summary>
    public string? Text { get; set; }

    /// <summary>A title read out of the file itself, which usually beats the file name.</summary>
    public string? EmbeddedTitle { get; set; }
}

/// <summary>
/// Works out what a file actually is.
///
/// The extension is a hint, not an answer: a PDF saved as `.txt` is still a PDF, and a `.docx`
/// that is really a ZIP of something else should not be handed to the Word reader. So the first
/// bytes are read and believed over the name, and the extension only decides between formats
/// that share a container — every Office format is a ZIP, as is EPUB.
/// </summary>
public static class FileInspector
{
    private readonly record struct Signature(byte[] Magic, int Offset, MediaKind Media, string Mime);

    private static readonly Signature[] Signatures =
    [
        new("%PDF"u8.ToArray(), 0, MediaKind.Pdf, "application/pdf"),
        new([0x89, 0x50, 0x4E, 0x47], 0, MediaKind.Image, "image/png"),
        new([0xFF, 0xD8, 0xFF], 0, MediaKind.Image, "image/jpeg"),
        new("GIF8"u8.ToArray(), 0, MediaKind.Image, "image/gif"),
        new("BM"u8.ToArray(), 0, MediaKind.Image, "image/bmp"),
        new([0x49, 0x49, 0x2A, 0x00], 0, MediaKind.Image, "image/tiff"),
        new([0x4D, 0x4D, 0x00, 0x2A], 0, MediaKind.Image, "image/tiff"),
        new("ftypheic"u8.ToArray(), 4, MediaKind.Image, "image/heic"),
        new("ftypheix"u8.ToArray(), 4, MediaKind.Image, "image/heic"),
        new("ftypmif1"u8.ToArray(), 4, MediaKind.Image, "image/heif"),
        new("ftypavif"u8.ToArray(), 4, MediaKind.Image, "image/avif"),
        new("ftypisom"u8.ToArray(), 4, MediaKind.Video, "video/mp4"),
        new("ftypmp4"u8.ToArray(), 4, MediaKind.Video, "video/mp4"),
        new("ftypqt"u8.ToArray(), 4, MediaKind.Video, "video/quicktime"),
        new([0x1A, 0x45, 0xDF, 0xA3], 0, MediaKind.Video, "video/x-matroska"),
        new("ID3"u8.ToArray(), 0, MediaKind.Audio, "audio/mpeg"),
        new("OggS"u8.ToArray(), 0, MediaKind.Audio, "audio/ogg"),
        new("fLaC"u8.ToArray(), 0, MediaKind.Audio, "audio/flac"),
        new("%!PS"u8.ToArray(), 0, MediaKind.Document, "application/postscript"),
        new("{\\rtf"u8.ToArray(), 0, MediaKind.Document, "application/rtf"),
        new([0x7B, 0x5C, 0x72, 0x74], 0, MediaKind.Document, "application/rtf"),
    ];

    /// <summary>Formats that all arrive as a ZIP, so the extension is what tells them apart.</summary>
    private static readonly Dictionary<string, (MediaKind Media, string Mime)> ZipFormats =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".docx"] = (MediaKind.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            [".dotx"] = (MediaKind.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.template"),
            [".xlsx"] = (MediaKind.Spreadsheet, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            [".pptx"] = (MediaKind.Presentation, "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
            [".odt"] = (MediaKind.Document, "application/vnd.oasis.opendocument.text"),
            [".ods"] = (MediaKind.Spreadsheet, "application/vnd.oasis.opendocument.spreadsheet"),
            [".odp"] = (MediaKind.Presentation, "application/vnd.oasis.opendocument.presentation"),
            [".epub"] = (MediaKind.Document, "application/epub+zip"),
            [".zip"] = (MediaKind.Archive, "application/zip"),
        };

    /// <summary>Formats with no signature at all — plain text in one costume or another.</summary>
    private static readonly Dictionary<string, (MediaKind Media, string Mime)> TextFormats =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".md"] = (MediaKind.Markdown, "text/markdown"),
            [".markdown"] = (MediaKind.Markdown, "text/markdown"),
            [".txt"] = (MediaKind.Text, "text/plain"),
            [".csv"] = (MediaKind.Spreadsheet, "text/csv"),
            [".tsv"] = (MediaKind.Spreadsheet, "text/tab-separated-values"),
            [".json"] = (MediaKind.Text, "application/json"),
            [".xml"] = (MediaKind.Text, "application/xml"),
            [".html"] = (MediaKind.Web, "text/html"),
            [".htm"] = (MediaKind.Web, "text/html"),
            [".url"] = (MediaKind.Web, "text/uri-list"),
            [".cs"] = (MediaKind.Text, "text/x-csharp"),
            [".py"] = (MediaKind.Text, "text/x-python"),
            [".js"] = (MediaKind.Text, "text/javascript"),
            [".ts"] = (MediaKind.Text, "text/typescript"),
            [".rtf"] = (MediaKind.Document, "application/rtf"),
            [".doc"] = (MediaKind.Document, "application/msword"),
            [".xls"] = (MediaKind.Spreadsheet, "application/vnd.ms-excel"),
            [".ppt"] = (MediaKind.Presentation, "application/vnd.ms-powerpoint"),
            [".mp4"] = (MediaKind.Video, "video/mp4"),
            [".mov"] = (MediaKind.Video, "video/quicktime"),
            [".avi"] = (MediaKind.Video, "video/x-msvideo"),
            [".webm"] = (MediaKind.Video, "video/webm"),
            [".mp3"] = (MediaKind.Audio, "audio/mpeg"),
            [".m4a"] = (MediaKind.Audio, "audio/mp4"),
            [".wav"] = (MediaKind.Audio, "audio/wav"),
            [".webp"] = (MediaKind.Image, "image/webp"),
            [".svg"] = (MediaKind.Image, "image/svg+xml"),
        };

    public static FileFacts Inspect(string path)
    {
        var info = new FileInfo(path);
        Span<byte> header = stackalloc byte[64];
        var read = 0;

        try
        {
            using var stream = File.OpenRead(path);
            read = stream.Read(header);
        }
        catch (IOException) { }

        return Inspect(info.Name, info.Length, header[..Math.Max(read, 0)]);
    }

    public static FileFacts Inspect(string fileName, long size, ReadOnlySpan<byte> header)
    {
        var extension = Path.GetExtension(fileName);

        var (media, mime) = Identify(extension, header);

        return new FileFacts
        {
            FileName = fileName,
            Extension = extension.ToLowerInvariant(),
            MimeType = mime,
            Media = media,
            SizeBytes = size,
        };
    }

    private static (MediaKind Media, string Mime) Identify(string extension, ReadOnlySpan<byte> header)
    {
        foreach (var signature in Signatures)
        {
            if (Matches(header, signature)) return (signature.Media, signature.Mime);
        }

        // Every Office and OpenDocument format is a ZIP underneath, so the container says
        // nothing and the extension has to decide.
        if (header.Length >= 2 && header[0] == 'P' && header[1] == 'K')
        {
            if (ZipFormats.TryGetValue(extension, out var zip)) return zip;
            return (MediaKind.Archive, "application/zip");
        }

        if (TextFormats.TryGetValue(extension, out var text)) return text;

        // Nothing matched. If it reads as text, treat it as text rather than as a mystery blob:
        // a file the user can read in Notepad should be readable here too.
        return LooksLikeText(header)
            ? (MediaKind.Text, "text/plain")
            : (MediaKind.Unknown, "application/octet-stream");
    }

    private static bool Matches(ReadOnlySpan<byte> header, in Signature signature)
    {
        var end = signature.Offset + signature.Magic.Length;
        if (header.Length < end) return false;
        return header.Slice(signature.Offset, signature.Magic.Length).SequenceEqual(signature.Magic);
    }

    /// <summary>
    /// A crude but effective test: text has no NUL bytes and few control characters. Good enough
    /// to tell a source file from a compiled one, which is all this needs to decide.
    /// </summary>
    private static bool LooksLikeText(ReadOnlySpan<byte> header)
    {
        if (header.Length == 0) return false;

        var suspicious = 0;
        foreach (var b in header)
        {
            if (b == 0) return false;
            if (b < 0x09 || (b > 0x0D && b < 0x20)) suspicious++;
        }
        return suspicious * 10 < header.Length;
    }

    /// <summary>Whether Campus can show this without help from another application.</summary>
    public static bool CanDisplay(MediaKind media) => media is
        MediaKind.Pdf or MediaKind.Image or MediaKind.Video or MediaKind.Audio
        or MediaKind.Text or MediaKind.Markdown or MediaKind.Document
        or MediaKind.Spreadsheet or MediaKind.Presentation or MediaKind.Web;
}
