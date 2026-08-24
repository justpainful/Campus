using System.Text.Json;
using System.Text.Json.Serialization;
using Campus.Domain;

namespace Campus.Storage;

/// <summary>
/// Reads and writes the kind-specific half of a <see cref="CampusObject"/>. The object's own
/// Kind column decides which payload type to expect, so the JSON does not need a discriminator
/// and stays readable.
/// </summary>
public static class PayloadSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string? Serialize(IObjectPayload? payload)
        => payload is null ? null : JsonSerializer.Serialize(payload, payload.GetType(), Options);

    public static IObjectPayload? Deserialize(ObjectKind kind, string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        var type = TypeFor(kind);
        return type is null ? null : JsonSerializer.Deserialize(json, type, Options) as IObjectPayload;
    }

    public static Type? TypeFor(ObjectKind kind) => kind switch
    {
        ObjectKind.File => typeof(FilePayload),
        ObjectKind.Note => typeof(NotePayload),
        ObjectKind.Task => typeof(TaskPayload),
        ObjectKind.Assignment => typeof(AssignmentPayload),
        ObjectKind.Requirement => typeof(RequirementPayload),
        ObjectKind.Link => typeof(LinkPayload),
        ObjectKind.Book => typeof(BookPayload),
        ObjectKind.Lesson => typeof(LessonPayload),
        ObjectKind.Exam => typeof(ExamPayload),
        ObjectKind.PrintJob => typeof(PrintJobPayload),
        ObjectKind.Goal => typeof(GoalPayload),
        ObjectKind.Board => typeof(BoardPayload),
        ObjectKind.Thread => typeof(ThreadPayload),
        ObjectKind.Collection => typeof(CollectionPayload),
        ObjectKind.Event => typeof(EventPayload),
        ObjectKind.Person => typeof(PersonPayload),
        ObjectKind.InboxItem => typeof(InboxPayload),
        ObjectKind.Subject => typeof(SubjectPayload),
        _ => null,
    };

    public static string SerializeMetadata(IDictionary<string, string> metadata)
        => metadata.Count == 0 ? "{}" : JsonSerializer.Serialize(metadata, Options);

    public static Dictionary<string, string> DeserializeMetadata(string? json)
        => string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options) ?? [];

    /// <summary>Serialises a whole object for the change journal and for conflict comparison.</summary>
    public static string SerializeSnapshot(CampusObject entity) => JsonSerializer.Serialize(new
    {
        id = entity.Id.Value,
        kind = (int)entity.Kind,
        title = entity.Title,
        summary = entity.Summary,
        subjectId = entity.SubjectId?.Value,
        parentId = entity.ParentId?.Value,
        status = (int)entity.Status,
        priority = (int)entity.Priority,
        createdAt = entity.CreatedAt,
        updatedAt = entity.UpdatedAt,
        dueAt = entity.DueAt,
        completedAt = entity.CompletedAt,
        deletedAt = entity.DeletedAt,
        isFavorite = entity.IsFavorite,
        isPinned = entity.IsPinned,
        isArchived = entity.IsArchived,
        academicYear = entity.AcademicYear,
        term = entity.Term is null ? (int?)null : (int)entity.Term,
        sortOrder = entity.SortOrder,
        tags = entity.Tags,
        metadata = entity.Metadata,
        payload = entity.Payload,
    }, Options);
}
