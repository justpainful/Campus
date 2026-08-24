using System.Text.Json;
using System.Text.Json.Serialization;

namespace Campus.Extensions;

/// <summary>
/// What an extension is allowed to do.
///
/// Permissions are coarse on purpose. A long list of fine-grained switches reads as thorough and
/// behaves as noise: nobody can weigh forty of them, so everybody says yes to all forty. Six that
/// each mean something a person can picture is a consent dialog somebody might actually read.
/// </summary>
[Flags]
public enum ExtensionPermissions
{
    None = 0,

    /// <summary>Read the objects in the workspace — titles, notes, what is due.</summary>
    ReadWorkspace = 1 << 0,

    /// <summary>Create and change objects.</summary>
    WriteWorkspace = 1 << 1,

    /// <summary>Read the bytes of stored files. Strictly more than reading their records.</summary>
    ReadFiles = 1 << 2,

    /// <summary>Add files to the vault.</summary>
    WriteFiles = 1 << 3,

    /// <summary>Reach the network. Campus itself never does; an extension must ask.</summary>
    Network = 1 << 4,

    /// <summary>Read and write outside the workspace, in folders the user picks.</summary>
    FileSystem = 1 << 5,
}

/// <summary>What kind of thing an extension contributes.</summary>
public enum ContributionKind
{
    /// <summary>Something that appears in the command palette.</summary>
    Command = 0,

    /// <summary>A viewer for one or more file types.</summary>
    Viewer = 1,

    /// <summary>A way of bringing content in.</summary>
    Importer = 2,

    /// <summary>A way of taking content out.</summary>
    Exporter = 3,

    /// <summary>A saved query offered as a smart collection.</summary>
    Collection = 4,
}

/// <summary>One thing an extension adds to Campus.</summary>
public sealed record ExtensionContribution
{
    public ContributionKind Kind { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Symbol { get; init; }

    /// <summary>File extensions this applies to, for viewers and importers.</summary>
    public List<string> FileTypes { get; init; } = [];

    /// <summary>A saved query, for collection contributions.</summary>
    public string? Query { get; init; }
}

/// <summary>
/// An extension's declaration of itself.
///
/// The manifest is read before any code is loaded, which is what makes the consent dialog
/// meaningful: Campus knows what an extension wants and who wrote it before deciding whether to
/// start it at all.
/// </summary>
public sealed record ExtensionManifest
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = "1.0.0";
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? Symbol { get; init; }

    /// <summary>Minimum Campus version this expects. Refused rather than crashed if too new.</summary>
    public string? RequiresCampus { get; init; }

    /// <summary>Assembly file to load, relative to the extension's folder.</summary>
    public string? Entry { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExtensionPermissions Permissions { get; init; } = ExtensionPermissions.None;

    public List<ExtensionContribution> Contributes { get; init; } = [];

    /// <summary>True for the extensions Campus ships with, which run in process and are not removable.</summary>
    public bool IsBuiltIn { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ExtensionManifest? Parse(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<ExtensionManifest>(json, Json);
            // An extension without an id cannot be enabled, disabled or removed by name, so it
            // is not an extension.
            return manifest is { Id.Length: > 0, Name.Length: > 0 } ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The permissions, as sentences a person can weigh.</summary>
    public static IEnumerable<string> Describe(ExtensionPermissions permissions)
    {
        if (permissions.HasFlag(ExtensionPermissions.ReadWorkspace))
            yield return "Read your subjects, notes, tasks and what is due";
        if (permissions.HasFlag(ExtensionPermissions.WriteWorkspace))
            yield return "Create and change items in your workspace";
        if (permissions.HasFlag(ExtensionPermissions.ReadFiles))
            yield return "Open the contents of your stored files";
        if (permissions.HasFlag(ExtensionPermissions.WriteFiles))
            yield return "Add files to your vault";
        if (permissions.HasFlag(ExtensionPermissions.Network))
            yield return "Use the internet — Campus itself never does";
        if (permissions.HasFlag(ExtensionPermissions.FileSystem))
            yield return "Read and write files outside Campus, in folders you choose";

        if (permissions == ExtensionPermissions.None)
            yield return "Nothing beyond appearing in the interface";
    }
}
