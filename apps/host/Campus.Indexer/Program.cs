using System.Text;
using System.Text.Json;
using Campus.Documents;

namespace Campus.Indexer;

/// <summary>
/// Reads one file and says what is in it.
///
/// This is a separate program for one reason: the formats it has to open are the least trustworthy
/// input a personal workspace ever handles. A PDF from a school portal, a slide deck of unknown
/// provenance, a spreadsheet a teacher exported from something ancient — any of those can be
/// malformed enough to take a parser down, and one of them taking Campus down in the middle of an
/// import would be inexcusable.
///
/// Here, the worst case is a process that exits and an import that continues with less metadata.
///
/// It never touches the vault and is never given a key: it is handed a path to a file the user
/// already has, and prints what it found.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: Campus.Indexer <file> [--no-text]");
            return 2;
        }

        var path = args[0];
        var withText = !args.Contains("--no-text");

        if (!File.Exists(path))
        {
            Console.Error.WriteLine("That file is not there.");
            return 3;
        }

        try
        {
            var facts = FileInspector.Inspect(path);
            DocumentReaders.Enrich(facts, path);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                fileName = facts.FileName,
                extension = facts.Extension,
                mimeType = facts.MimeType,
                media = (int)facts.Media,
                sizeBytes = facts.SizeBytes,
                pageCount = facts.PageCount,
                pixelWidth = facts.PixelWidth,
                pixelHeight = facts.PixelHeight,
                duration = facts.Duration?.TotalSeconds,
                embeddedTitle = facts.EmbeddedTitle,
                text = withText ? facts.Text : null,
            }, Json));

            return 0;
        }
        catch (Exception ex)
        {
            // Deliberately catching everything: this process exists to absorb exactly the kind of
            // failure that a narrower catch would let escape.
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
