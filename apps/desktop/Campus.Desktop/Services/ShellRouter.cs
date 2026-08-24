using Campus.Domain;

namespace Campus.Desktop.Services;

/// <summary>Where the shell has been asked to go.</summary>
public sealed record NavigationRequest
{
    /// <summary>An object to open, or null when the request names a destination instead.</summary>
    public CampusId? ObjectId { get; init; }

    /// <summary>A shell destination id, for requests that name a list rather than an object.</summary>
    public string? Destination { get; init; }

    /// <summary>True when the caller asked for this to open beside what is already open.</summary>
    public bool InNewTab { get; init; }

    /// <summary>Text to hand the page — a search to run, a page to jump to.</summary>
    public string? Argument { get; init; }
}

/// <summary>
/// One place that knows how to open things.
///
/// Without it, every page that can link to another page would need a reference to the window, and
/// "open this object" would be implemented slightly differently in each of them — which is exactly
/// how a link in a note ends up behaving differently from the same link in a search result. Pages
/// ask; the shell answers.
/// </summary>
public sealed class ShellRouter
{
    /// <summary>Raised on the UI thread by whoever owns the frame.</summary>
    public event EventHandler<NavigationRequest>? NavigationRequested;

    public void Open(CampusId id, bool inNewTab = false)
        => NavigationRequested?.Invoke(this, new NavigationRequest
        {
            ObjectId = id,
            InNewTab = inNewTab,
        });

    public void GoTo(string destination, string? argument = null, bool inNewTab = false)
        => NavigationRequested?.Invoke(this, new NavigationRequest
        {
            Destination = destination,
            Argument = argument,
            InNewTab = inNewTab,
        });

    /// <summary>Opens the search destination with a query already typed in.</summary>
    public void Search(string text) => GoTo(Shell.ShellDestinations.Search, text);
}
