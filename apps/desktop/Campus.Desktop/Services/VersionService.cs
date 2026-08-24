using System.Text;
using Campus.Domain;
using Campus.Storage;

namespace Campus.Desktop.Services;

/// <summary>
/// Keeps what content used to be.
///
/// Only for things that can be lost by overwriting — a note's body, a lesson's text — and only
/// when the text has actually changed since the last snapshot. Autosave fires every few hundred
/// milliseconds while someone types; without that check a single paragraph would leave a hundred
/// identical versions behind.
///
/// Snapshots go into the vault like any other content, addressed by their hash, so two versions
/// that happen to be identical cost one copy between them.
/// </summary>
public sealed class VersionService(WorkspaceService workspace)
{
    private readonly WorkspaceService _workspace = workspace;

    /// <summary>How many versions of one object are kept before the oldest start to go.</summary>
    public int Keep { get; set; } = 30;

    /// <summary>
    /// Records the object's current content as a version, unless it is the same text the last
    /// version already holds. Returns the version number, or null when nothing was recorded.
    /// </summary>
    public async Task<int?> SnapshotAsync(
        CampusObject entity, string? label = null, CancellationToken ct = default)
    {
        if (!_workspace.IsUnlocked) return null;

        var text = ContentOf(entity);
        if (text is null || text.Trim().Length == 0) return null;

        var bytes = Encoding.UTF8.GetBytes(text);
        var stored = await _workspace.Vault.Objects.PutBytesAsync(bytes, ct).ConfigureAwait(false);

        var existing = await _workspace.History.VersionsAsync(entity.Id, ct).ConfigureAwait(false);
        if (existing.Count > 0 && existing[0].ContentHash == stored.ContentHash) return null;

        var number = await _workspace.History
            .AddVersionAsync(entity.Id, stored.ContentHash, bytes.LongLength, label, ct)
            .ConfigureAwait(false);

        // Pruning returns what nothing references any more; the vault reclaims exactly that.
        foreach (var hash in await _workspace.History.PruneAsync(entity.Id, Keep, ct).ConfigureAwait(false))
        {
            try { _workspace.Vault.Objects.Delete(hash); }
            catch (IOException) { /* it will be caught by the next vacuum */ }
        }

        return number;
    }

    public Task<IReadOnlyList<ObjectVersion>> ListAsync(CampusId id, CancellationToken ct = default)
        => _workspace.History.VersionsAsync(id, ct);

    /// <summary>Reads back what a version holds, without changing anything.</summary>
    public async Task<string?> ReadAsync(ObjectVersion version, CancellationToken ct = default)
    {
        try
        {
            var bytes = await _workspace.Vault.Objects
                .ReadAllBytesAsync(version.ContentHash, ct).ConfigureAwait(false);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts an older version back. The text being replaced is snapshotted first, so restoring is
    /// itself undoable — otherwise "restore" would be a way to lose work rather than recover it.
    /// </summary>
    public async Task<bool> RestoreAsync(
        CampusObject entity, ObjectVersion version, CancellationToken ct = default)
    {
        var text = await ReadAsync(version, ct).ConfigureAwait(false);
        if (text is null) return false;

        await SnapshotAsync(entity, "before restore", ct).ConfigureAwait(false);

        if (!ApplyContent(entity, text)) return false;

        await _workspace.Objects.SaveAsync(entity, ct).ConfigureAwait(false);
        await _workspace.History
            .RecordAsync(entity.Id, "restored", $"version {version.VersionNumber}", ct)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>The part of an object that is worth keeping old copies of.</summary>
    private static string? ContentOf(CampusObject entity) => entity.Payload switch
    {
        NotePayload note => note.Body,
        LessonPayload lesson => lesson.Body,
        ThreadPayload thread => thread.Body,
        AssignmentPayload assignment => assignment.Instructions,
        TaskPayload task => task.Notes,
        _ => null,
    };

    private static bool ApplyContent(CampusObject entity, string text)
    {
        switch (entity.Payload)
        {
            case NotePayload note: note.Body = text; return true;
            case LessonPayload lesson: lesson.Body = text; return true;
            case ThreadPayload thread: thread.Body = text; return true;
            case AssignmentPayload assignment: assignment.Instructions = text; return true;
            case TaskPayload task: task.Notes = text; return true;
            default: return false;
        }
    }
}
