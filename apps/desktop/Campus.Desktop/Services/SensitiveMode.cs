using Campus.Domain;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace Campus.Desktop.Services;

/// <summary>
/// Sensitive mode: for a workspace somebody else might sit down in front of.
///
/// It does two things, and it is worth being precise about both, because the honest limits matter
/// more than the feature.
///
/// It clears the clipboard a short time after Campus put something there. Copying a paragraph out
/// of a private note leaves that paragraph in the clipboard until something else replaces it — for
/// hours, readable by every program on the machine and by the clipboard history Windows keeps.
/// Clearing it is not encryption; it is closing a door that was left open.
///
/// It refuses to start a drag that carries a file out of the vault. Dragging a document into a
/// chat window is a decision, and in this mode it becomes a deliberate one: exporting still works,
/// and still says what it does.
///
/// What it cannot do is stop an Administrator reading this process's memory, a screenshot, or a
/// photograph of the screen. Campus says so rather than implying a protection it does not have.
/// </summary>
public sealed class SensitiveMode(WorkspaceSettings settings)
{
    /// <summary>How long something Campus copied is allowed to stay on the clipboard.</summary>
    public static readonly TimeSpan ClipboardLifetime = TimeSpan.FromSeconds(45);

    private readonly WorkspaceSettings _settings = settings;
    private DispatcherQueueTimer? _timer;
    private string? _ours;

    public bool IsOn => _settings.SensitiveMode;

    /// <summary>Raised when something is refused, so the interface can say why.</summary>
    public event EventHandler<string>? Refused;

    /// <summary>
    /// Copies text, and remembers that Campus is what put it there.
    ///
    /// The comparison on clearing matters: if the user has copied something else since, that is
    /// theirs, and wiping it would be Campus reaching outside its own business.
    /// </summary>
    public void Copy(string text, DispatcherQueue dispatcher)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);

        if (!IsOn) return;

        _ours = text;
        _timer?.Stop();

        _timer = dispatcher.CreateTimer();
        _timer.Interval = ClipboardLifetime;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => ClearIfStillOurs();
        _timer.Start();
    }

    /// <summary>Clears the clipboard now, if what is on it is what Campus put there.</summary>
    public void ClearIfStillOurs()
    {
        if (_ours is null) return;

        try
        {
            var current = Clipboard.GetContent();

            // Reading the clipboard can fail while another program holds it open; a failure here
            // means leaving it alone, which is the safe direction for somebody else's data.
            if (current.Contains(StandardDataFormats.Text))
            {
                var text = current.GetTextAsync().AsTask().GetAwaiter().GetResult();
                if (!string.Equals(text, _ours, StringComparison.Ordinal)) { _ours = null; return; }
            }

            Clipboard.Clear();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            // Nothing to do: the clipboard is owned by another program at this instant.
        }
        finally
        {
            _ours = null;
        }
    }

    /// <summary>
    /// Whether a file may leave the vault by being dragged. Returns false in sensitive mode, and
    /// says so, rather than starting a drag that silently does nothing.
    /// </summary>
    public bool MayDragOut()
    {
        if (!IsOn) return true;

        Refused?.Invoke(this,
            "Sensitive mode is on, so files cannot be dragged out of Campus. "
            + "Use Export if you mean to take a copy.");

        return false;
    }

    /// <summary>
    /// Called when the workspace locks. Anything Campus copied is cleared then regardless of the
    /// timer, because locking is the moment somebody walks away from the machine.
    /// </summary>
    public void OnLocked()
    {
        _timer?.Stop();
        if (IsOn) ClearIfStillOurs();
    }
}
