using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Campus.Domain;

namespace Campus.Desktop.Services;

/// <summary>What an export produced.</summary>
public sealed record ExportResult(int Objects, int Files, long Bytes, string Path);

public enum ExportShape
{
    /// <summary>A folder of markdown and the original files, readable without Campus.</summary>
    Readable = 0,

    /// <summary>Everything, including ids and payloads, for moving a workspace or restoring it.</summary>
    Complete = 1,
}

/// <summary>
/// Takes the workspace out.
///
/// This exists because a workspace nobody can leave is a trap, not a vault. Encryption is only
/// reassuring if the door opens from the inside: an export must be readable years from now, on a
/// machine that has never heard of Campus, without this program.
///
/// So the readable shape is a folder of markdown files and the original documents, named after
/// their titles and arranged by subject — openable in anything. The complete shape adds a JSON
/// record of every field for restoring the workspace as it was. Both are plaintext by definition,
/// which is why exporting is always an explicit act and always says so.
/// </summary>
public sealed class ExportService(WorkspaceService workspace)
{
    private readonly WorkspaceService _workspace = workspace;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new CampusIdJsonConverter(), new CampusIdJsonConverter.Nullable() },
    };

    public event EventHandler<string>? Progress;

    /// <summary>Writes everything the query matches into a folder.</summary>
    public async Task<ExportResult> ExportAsync(
        CampusQuery query,
        string destinationFolder,
        ExportShape shape = ExportShape.Readable,
        CancellationToken ct = default)
    {
        if (!_workspace.IsUnlocked)
            throw new InvalidOperationException("The workspace is locked.");

        Directory.CreateDirectory(destinationFolder);

        var objects = await _workspace.Objects.QueryAsync(query, ct).ConfigureAwait(false);
        var subjects = await LoadSubjectNamesAsync(ct).ConfigureAwait(false);

        var fileCount = 0;
        long bytes = 0;

        foreach (var entity in objects)
        {
            ct.ThrowIfCancellationRequested();
            Progress?.Invoke(this, entity.Title);

            // Objects are grouped by subject, because that is the shelf they came off. An object
            // with no subject goes to the top level rather than into a folder called "null".
            var folder = entity.SubjectId is { } subjectId && subjects.TryGetValue(subjectId, out var name)
                ? Path.Combine(destinationFolder, Safe(name))
                : destinationFolder;

            Directory.CreateDirectory(folder);

            if (entity.PayloadAs<FilePayload>() is { } file)
            {
                var target = Unique(folder, Safe(entity.Title), Path.GetExtension(file.OriginalFileName));

                try
                {
                    await _workspace.Vault.Objects
                        .ExportAsync(file.ContentHash, target, ct).ConfigureAwait(false);

                    bytes += file.SizeBytes;
                    fileCount++;
                }
                catch (Exception ex) when (ex is IOException or FileNotFoundException)
                {
                    // A record whose bytes are missing still deserves its metadata written out,
                    // so what was lost is visible rather than silently absent.
                }
            }

            var markdown = ToMarkdown(entity, subjects);
            var notePath = Unique(folder, Safe(entity.Title), ".md");
            await File.WriteAllTextAsync(notePath, markdown, Encoding.UTF8, ct).ConfigureAwait(false);
            bytes += markdown.Length;
        }

        if (shape == ExportShape.Complete)
        {
            var manifest = Path.Combine(destinationFolder, "campus-export.json");
            await using var stream = File.Create(manifest);
            await JsonSerializer.SerializeAsync(stream, new
            {
                exportedAt = DateTimeOffset.UtcNow,
                device = _workspace.DeviceId,
                count = objects.Count,
                objects,
            }, Json, ct).ConfigureAwait(false);
        }

        await WriteReadmeAsync(destinationFolder, objects.Count, fileCount, ct).ConfigureAwait(false);

        return new ExportResult(objects.Count, fileCount, bytes, destinationFolder);
    }

    /// <summary>
    /// The same export, zipped. Convenient for moving a term's work to another machine — and
    /// plainly not encrypted, which the readme inside says in as many words.
    /// </summary>
    public async Task<ExportResult> ExportZipAsync(
        CampusQuery query,
        string destinationZip,
        ExportShape shape = ExportShape.Readable,
        CancellationToken ct = default)
    {
        var staging = Path.Combine(Path.GetTempPath(), "Campus", "export-" + CampusId.New().Value);

        try
        {
            var result = await ExportAsync(query, staging, shape, ct).ConfigureAwait(false);

            if (File.Exists(destinationZip)) File.Delete(destinationZip);
            ZipFile.CreateFromDirectory(staging, destinationZip, CompressionLevel.Optimal, false);

            return result with { Path = destinationZip };
        }
        finally
        {
            // The staging copy is plaintext; it does not get to outlive the zip.
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (IOException) { /* the temp folder is cleaned by Windows eventually */ }
        }
    }

    /// <summary>Everything, in the shape that can be read back in.</summary>
    public Task<ExportResult> ExportEverythingAsync(
        string destination, ExportShape shape = ExportShape.Complete, CancellationToken ct = default)
        => destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? ExportZipAsync(new CampusQuery { IncludeTrashed = false }, destination, shape, ct)
            : ExportAsync(new CampusQuery { IncludeTrashed = false }, destination, shape, ct);

    // ------------------------------------------------------------------- formatting

    /// <summary>
    /// One object as markdown with a small front-matter block. Front matter rather than prose,
    /// because the due date of an assignment should still be machine-readable after it leaves.
    /// </summary>
    private static string ToMarkdown(CampusObject entity, IReadOnlyDictionary<CampusId, string> subjects)
    {
        var text = new StringBuilder();

        text.AppendLine("---");
        text.Append("title: ").AppendLine(Quote(entity.Title));
        text.Append("kind: ").AppendLine(entity.Kind.ToString());
        text.Append("created: ").AppendLine(entity.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        text.Append("updated: ").AppendLine(entity.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));

        if (entity.SubjectId is { } subject && subjects.TryGetValue(subject, out var name))
            text.Append("subject: ").AppendLine(Quote(name));
        if (entity.DueAt is { } due)
            text.Append("due: ").AppendLine(due.ToString("O", CultureInfo.InvariantCulture));
        if (entity.Status != ObjectStatus.None)
            text.Append("status: ").AppendLine(entity.Status.ToString());
        if (entity.Tags.Count > 0)
            text.Append("tags: [").Append(string.Join(", ", entity.Tags)).AppendLine("]");

        text.AppendLine("---").AppendLine();
        text.Append("# ").AppendLine(entity.Title).AppendLine();

        if (entity.Summary is { Length: > 0 } summary) text.AppendLine(summary).AppendLine();

        switch (entity.Payload)
        {
            case NotePayload note:
                text.AppendLine(note.Body);
                break;

            case LessonPayload lesson:
                if (lesson.Unit is { Length: > 0 } unit) text.Append("**Unit:** ").AppendLine(unit);
                text.AppendLine(lesson.Body ?? "");
                break;

            case AssignmentPayload assignment:
                if (assignment.Teacher is { Length: > 0 } teacher)
                    text.Append("**Teacher:** ").AppendLine(teacher);
                if (assignment.Points is { } points)
                    text.Append("**Points:** ").AppendLine(points.ToString(CultureInfo.InvariantCulture));
                text.AppendLine().AppendLine(assignment.Instructions ?? "");
                break;

            case TaskPayload task:
                if (task.Notes is { Length: > 0 } notes) text.AppendLine(notes).AppendLine();
                foreach (var item in task.Checklist)
                    text.Append("- [").Append(item.Done ? 'x' : ' ').Append("] ").AppendLine(item.Text);
                break;

            case GoalPayload goal:
                if (goal.Detail is { Length: > 0 } detail) text.AppendLine(detail).AppendLine();
                foreach (var step in goal.Steps)
                    text.Append("- [").Append(step.Done ? 'x' : ' ').Append("] ").AppendLine(step.Text);
                break;

            case LinkPayload link:
                text.Append('<').Append(link.Url).AppendLine(">");
                if (link.Description is { Length: > 0 } description) text.AppendLine().AppendLine(description);
                break;

            case FilePayload file:
                text.Append("The file is saved beside this note as `")
                    .Append(Safe(entity.Title)).Append(Path.GetExtension(file.OriginalFileName))
                    .AppendLine("`.");
                break;

            case ThreadPayload thread:
                text.AppendLine(thread.Body ?? "");
                break;

            case ExamPayload exam:
                if (exam.ScheduledAt is { } when)
                    text.Append("**When:** ").AppendLine(when.ToString("f", CultureInfo.InvariantCulture));
                if (exam.Scope is { Length: > 0 } scope) text.Append("**Scope:** ").AppendLine(scope);
                break;
        }

        return text.ToString();
    }

    private static async Task WriteReadmeAsync(string folder, int objects, int files, CancellationToken ct)
    {
        var text = $"""
            # Campus export

            {objects} item{(objects == 1 ? "" : "s")}, including {files} file{(files == 1 ? "" : "s")},
            written on {DateTimeOffset.Now:f}.

            Every item is a markdown file with a small block of details at the top. Files kept in
            the workspace are saved beside their notes under their own titles. Folders are subjects.

            This copy is NOT encrypted. The workspace it came from is; this is a plain folder that
            anyone with access to it can read. Keep it somewhere you would be comfortable keeping
            the original documents.
            """;

        await File.WriteAllTextAsync(Path.Combine(folder, "README.md"), text, Encoding.UTF8, ct)
            .ConfigureAwait(false);
    }

    private async Task<Dictionary<CampusId, string>> LoadSubjectNamesAsync(CancellationToken ct)
    {
        var subjects = await _workspace.Objects
            .QueryAsync(new CampusQuery { Kinds = { ObjectKind.Subject } }, ct)
            .ConfigureAwait(false);

        return subjects.ToDictionary(s => s.Id, s => s.Title);
    }

    // --------------------------------------------------------------------- file names

    /// <summary>
    /// Makes a title safe to be a file name on Windows, and short enough that the path built
    /// from it still fits. A title is not a file name and never has been.
    /// </summary>
    private static string Safe(string title)
    {
        var cleaned = new StringBuilder(title.Length);

        foreach (var c in title.Trim())
        {
            cleaned.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '-' : c);
        }

        var name = cleaned.ToString().TrimEnd('.', ' ');
        if (name.Length == 0) name = "untitled";
        if (name.Length > 80) name = name[..80].TrimEnd();

        // Windows still refuses these names, extension or not.
        var reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "LPT1" };
        return reserved.Contains(name, StringComparer.OrdinalIgnoreCase) ? name + "-" : name;
    }

    /// <summary>Two objects can share a title; their files cannot share a path.</summary>
    private static string Unique(string folder, string name, string extension)
    {
        var candidate = Path.Combine(folder, name + extension);
        var counter = 2;

        while (File.Exists(candidate))
        {
            candidate = Path.Combine(folder, $"{name} ({counter}){extension}");
            counter++;
        }

        return candidate;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
