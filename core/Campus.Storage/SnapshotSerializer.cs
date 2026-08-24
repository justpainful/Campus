using System.Text.Json;
using Campus.Domain;

namespace Campus.Storage;

/// <summary>
/// Reads back the whole-object snapshots the journal writes.
///
/// A snapshot is what makes replay possible: a peer does not need to understand the schema, only
/// to hand back the state an object was in. Reading it by hand rather than through a DTO keeps
/// the writer free to add fields without every older build refusing to parse them.
/// </summary>
public static class SnapshotSerializer
{
    public static CampusObject? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!CampusId.TryParse(Text(root, "id"), out var id)) return null;

            var kind = (ObjectKind)(Number(root, "kind") ?? 0);

            var entity = new CampusObject
            {
                Id = id,
                Kind = kind,
                Title = Text(root, "title") ?? string.Empty,
                Summary = Text(root, "summary"),
                SubjectId = Id(root, "subjectId"),
                ParentId = Id(root, "parentId"),
                Status = (ObjectStatus)(Number(root, "status") ?? 0),
                Priority = (Priority)(Number(root, "priority") ?? 0),
                CreatedAt = Time(root, "createdAt") ?? DateTimeOffset.UtcNow,
                UpdatedAt = Time(root, "updatedAt") ?? DateTimeOffset.UtcNow,
                DueAt = Time(root, "dueAt"),
                CompletedAt = Time(root, "completedAt"),
                DeletedAt = Time(root, "deletedAt"),
                IsFavorite = Flag(root, "isFavorite"),
                IsPinned = Flag(root, "isPinned"),
                IsArchived = Flag(root, "isArchived"),
                AcademicYear = Number(root, "academicYear"),
                SortOrder = Real(root, "sortOrder") ?? 0,
            };

            if (Number(root, "term") is { } term) entity.Term = (TermKind)term;

            if (root.TryGetProperty("payload", out var payload)
                && payload.ValueKind == JsonValueKind.Object)
            {
                entity.Payload = PayloadSerializer.Deserialize(kind, payload.GetRawText());
            }

            if (root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tags.EnumerateArray())
                {
                    if (tag.GetString() is { Length: > 0 } value) entity.Tags.Add(value);
                }
            }

            if (root.TryGetProperty("metadata", out var metadata)
                && metadata.ValueKind == JsonValueKind.Object)
            {
                foreach (var pair in metadata.EnumerateObject())
                {
                    if (pair.Value.GetString() is { } value) entity.Metadata[pair.Name] = value;
                }
            }

            return entity;
        }
        catch (JsonException)
        {
            // A snapshot that cannot be read is skipped rather than stopping the whole sync;
            // the object it described will come again in a later bundle.
            return null;
        }
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static double? Real(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static bool Flag(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Time(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && value.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : null;

    private static CampusId? Id(JsonElement root, string name)
        => CampusId.TryParse(Text(root, name), out var id) ? id : null;
}
