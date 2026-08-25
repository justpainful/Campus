using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Campus.Sync;

/// <summary>
/// Just enough of Apple's XML property list format to talk to usbmux.
///
/// There is no plist reader in .NET and no reason to take a dependency for this: the messages
/// exchanged with usbmux are four keys long, and the replies are a dictionary of dictionaries.
/// A hundred lines here is a smaller liability than a package that has to be kept current for the
/// rest of the program's life.
///
/// Deliberately partial. Dates, reals and nested arrays-of-arrays are not implemented because
/// usbmux does not send them; anything unrecognised is read as its text rather than guessed at,
/// so an unexpected reply degrades to something readable instead of throwing.
/// </summary>
public static class PropertyList
{
    private static readonly XmlWriterSettings WriterSettings = new()
    {
        Indent = false,
        OmitXmlDeclaration = false,
        Encoding = new UTF8Encoding(false),
    };

    /// <summary>Writes a flat dictionary of strings, integers and booleans.</summary>
    public static byte[] Write(IReadOnlyDictionary<string, object> values)
    {
        var dict = new XElement("dict");

        foreach (var (key, value) in values)
        {
            dict.Add(new XElement("key", key));
            dict.Add(Element(value));
        }

        var document = new XDocument(
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN",
                "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), dict));

        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, WriterSettings)) document.Save(writer);

        return buffer.ToArray();
    }

    private static XElement Element(object value) => value switch
    {
        string text => new XElement("string", text),
        int number => new XElement("integer", number.ToString(CultureInfo.InvariantCulture)),
        long number => new XElement("integer", number.ToString(CultureInfo.InvariantCulture)),
        bool flag => new XElement(flag ? "true" : "false"),
        byte[] bytes => new XElement("data", Convert.ToBase64String(bytes)),
        _ => new XElement("string", value?.ToString() ?? string.Empty),
    };

    /// <summary>
    /// Reads a plist into dictionaries, lists and primitives.
    ///
    /// The DTD reference in Apple's plists points at apple.com, and an XML reader that resolves it
    /// would make a network request in the middle of a USB conversation — so the resolver is
    /// switched off explicitly rather than left to the default.
    /// </summary>
    public static object? Read(byte[] payload)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };

        using var stream = new MemoryStream(payload);
        using var reader = XmlReader.Create(stream, settings);

        var document = XDocument.Load(reader);
        var root = document.Root?.Elements().FirstOrDefault();

        return root is null ? null : Parse(root);
    }

    private static object? Parse(XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "dict":
            {
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                string? key = null;

                foreach (var child in element.Elements())
                {
                    if (child.Name.LocalName == "key") { key = child.Value; continue; }
                    if (key is null) continue;

                    result[key] = Parse(child);
                    key = null;
                }

                return result;
            }

            case "array":
                return element.Elements().Select(Parse).ToList();

            case "integer":
                return long.TryParse(element.Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var number) ? number : 0L;

            case "true": return true;
            case "false": return false;
            case "data": return Convert.FromBase64String(element.Value);

            default: return element.Value;
        }
    }

    // ------------------------------------------------------------------ reading a reply

    public static Dictionary<string, object?>? AsDictionary(object? value)
        => value as Dictionary<string, object?>;

    public static string? String(object? node, string key)
        => AsDictionary(node)?.GetValueOrDefault(key) as string;

    public static long? Integer(object? node, string key)
        => AsDictionary(node)?.GetValueOrDefault(key) as long?;

    public static List<object?>? Array(object? node, string key)
        => AsDictionary(node)?.GetValueOrDefault(key) as List<object?>;
}
