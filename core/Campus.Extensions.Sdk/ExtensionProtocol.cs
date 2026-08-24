using System.Text.Json;

namespace Campus.Extensions;

/// <summary>
/// What Campus and a plugin host say to each other.
///
/// One line of JSON per message, over the host process's standard input and output. Deliberately
/// the dullest possible transport: no sockets to secure, no port to collide, no serialiser that
/// can be talked into constructing a type, and a channel that dies exactly when the process does.
/// </summary>
public sealed record ExtensionMessage
{
    /// <summary>What is being asked or answered.</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>Matches a reply to its request.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The request or reply body, as raw JSON.</summary>
    public string? Payload { get; init; }

    /// <summary>Set on a reply that failed. Null means it worked.</summary>
    public string? Error { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static ExtensionMessage? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<ExtensionMessage>(line, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The methods that cross the boundary.</summary>
public static class ExtensionMethods
{
    /// <summary>Campus → host: load this extension and report what it contributes.</summary>
    public const string Load = "load";

    /// <summary>Campus → host: run a command the extension contributed.</summary>
    public const string Invoke = "invoke";

    /// <summary>Campus → host: shut down cleanly.</summary>
    public const string Shutdown = "shutdown";

    /// <summary>Host → Campus: the extension is asking for something it has permission for.</summary>
    public const string Request = "request";

    /// <summary>Host → Campus: show the user a message.</summary>
    public const string Notify = "notify";

    /// <summary>Either direction: the answer to a request.</summary>
    public const string Reply = "reply";
}

/// <summary>
/// The surface an extension sees. Everything an extension can do goes through here, which is
/// what makes the permission model enforceable rather than advisory: there is no other door.
/// </summary>
public interface ICampusExtension
{
    /// <summary>Called once, after the host has loaded the assembly.</summary>
    Task ActivateAsync(IExtensionContext context, CancellationToken ct = default);

    /// <summary>Runs one of the commands the manifest declared.</summary>
    Task InvokeAsync(string commandId, string? payload, CancellationToken ct = default);

    /// <summary>Called before the host exits. Best-effort; the host will not wait long.</summary>
    Task DeactivateAsync(CancellationToken ct = default);
}

/// <summary>What an extension is handed when it starts.</summary>
public interface IExtensionContext
{
    ExtensionManifest Manifest { get; }

    /// <summary>A folder of its own, inside the workspace, for whatever it needs to keep.</summary>
    string StorageDirectory { get; }

    /// <summary>Shows the user a message. The only way an extension can speak to them directly.</summary>
    Task NotifyAsync(string message, CancellationToken ct = default);

    /// <summary>
    /// Asks Campus for something — a query, an object, a file. Refused, with an error, when the
    /// manifest did not ask for the permission it would need.
    /// </summary>
    Task<string?> RequestAsync(string method, string? payload, CancellationToken ct = default);
}
