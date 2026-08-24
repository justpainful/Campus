using Campus.Domain;
using Microsoft.Data.Sqlite;

namespace Campus.Storage;

/// <summary>
/// Reads and writes <see cref="CampusObject"/>s. Every write also updates the tag index, the
/// full-text index and the change journal, so those three can never drift out of step with the
/// objects table.
/// </summary>
public sealed class ObjectRepository(CampusDatabase database, string deviceId)
{
    private readonly CampusDatabase _db = database;
    private readonly string _deviceId = deviceId;

    // ------------------------------------------------------------------------ reads

    public async Task<CampusObject?> GetAsync(CampusId id, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("SELECT * FROM objects WHERE id = @id;");
        command.Parameters.AddWithValue("@id", id.Value);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        var entity = Read(reader);
        entity.Tags.AddRange(await GetTagsAsync(id, ct).ConfigureAwait(false));
        return entity;
    }

    public async Task<IReadOnlyList<CampusObject>> QueryAsync(
        CampusQuery query, CancellationToken ct = default)
    {
        var compiled = QueryCompiler.Compile(query, DateTimeOffset.Now);

        await using var command = _db.CreateCommand(compiled.Sql);
        foreach (var parameter in compiled.Parameters) command.Parameters.Add(parameter);

        var results = new List<CampusObject>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) results.Add(Read(reader));
        }

        // Tags are fetched for the whole page at once rather than per row.
        await FillTagsAsync(results, ct).ConfigureAwait(false);
        return results;
    }

    public async Task<int> CountAsync(CampusQuery query, CancellationToken ct = default)
    {
        var compiled = QueryCompiler.Compile(query, DateTimeOffset.Now, countOnly: true);
        await using var command = _db.CreateCommand(compiled.Sql);
        foreach (var parameter in compiled.Parameters) command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    // ----------------------------------------------------------------------- writes

    /// <summary>
    /// The one statement that writes an object. Shared by local saves and by changes arriving
    /// from another device, so the two can never drift apart in what they persist.
    /// </summary>
    private const string UpsertSql = """
                INSERT INTO objects (
                    id, kind, title, summary, subject_id, parent_id, status, priority,
                    created_at, updated_at, due_at, completed_at, deleted_at, opened_at,
                    is_favorite, is_pinned, is_archived, academic_year, term,
                    source, source_device, sort_order, payload, metadata)
                VALUES (
                    @id, @kind, @title, @summary, @subject, @parent, @status, @priority,
                    @created, @updated, @due, @completed, @deleted, @opened,
                    @favorite, @pinned, @archived, @year, @term,
                    @source, @device, @sort, @payload, @metadata)
                ON CONFLICT(id) DO UPDATE SET
                    kind = excluded.kind,
                    title = excluded.title,
                    summary = excluded.summary,
                    subject_id = excluded.subject_id,
                    parent_id = excluded.parent_id,
                    status = excluded.status,
                    priority = excluded.priority,
                    updated_at = excluded.updated_at,
                    due_at = excluded.due_at,
                    completed_at = excluded.completed_at,
                    deleted_at = excluded.deleted_at,
                    opened_at = excluded.opened_at,
                    is_favorite = excluded.is_favorite,
                    is_pinned = excluded.is_pinned,
                    is_archived = excluded.is_archived,
                    academic_year = excluded.academic_year,
                    term = excluded.term,
                    source = excluded.source,
                    source_device = excluded.source_device,
                    sort_order = excluded.sort_order,
                    payload = excluded.payload,
                    metadata = excluded.metadata;
        """;

    /// <summary>Inserts or updates an object, keeping tags, search and the journal in step.</summary>
    public async Task SaveAsync(CampusObject entity, CancellationToken ct = default)
    {
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.InTransactionAsync(async () =>
        {
            var existed = await ExistsAsync(entity.Id, ct).ConfigureAwait(false);

            await using (var command = _db.CreateCommand(UpsertSql))
            {
                Bind(command, entity);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await ReplaceTagsAsync(entity, ct).ConfigureAwait(false);
            await IndexAsync(entity, ct).ConfigureAwait(false);
            await AppendJournalAsync(
                existed ? ChangeOperation.Update : ChangeOperation.Create, entity, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>What happened when a change from another device was applied.</summary>
    public enum ApplyOutcome
    {
        /// <summary>The object was new here, or the incoming version was newer.</summary>
        Applied,

        /// <summary>Our copy was already newer, so nothing changed.</summary>
        Ignored,

        /// <summary>Both sides changed since they last agreed. The caller decides.</summary>
        Conflicted,
    }

    /// <summary>
    /// Applies a change that arrived from another device.
    ///
    /// Last-writer-wins on the timestamp, with one exception that matters: if both sides changed
    /// the same object since they last spoke, that is reported as a conflict rather than resolved
    /// silently. Losing a paragraph you wrote on the train because the laptop happened to save a
    /// second later is exactly the failure Campus must not have.
    ///
    /// The journal entry keeps the originating device's name, which is what stops the change
    /// being sent back to the device it came from.
    /// </summary>
    public async Task<ApplyOutcome> ApplyRemoteAsync(
        CampusObject incoming,
        string fromDeviceId,
        DateTimeOffset lastAgreedAt,
        ChangeOperation operation = ChangeOperation.Update,
        CancellationToken ct = default)
    {
        var existing = await GetAsync(incoming.Id, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            if (existing.UpdatedAt > incoming.UpdatedAt && existing.UpdatedAt > lastAgreedAt)
                return ApplyOutcome.Conflicted;

            if (existing.UpdatedAt >= incoming.UpdatedAt) return ApplyOutcome.Ignored;
        }

        await _db.InTransactionAsync(async () =>
        {
            if (operation == ChangeOperation.Delete)
            {
                await RemoveFromIndexAsync(incoming.Id, ct).ConfigureAwait(false);

                await using var delete = _db.CreateCommand("DELETE FROM objects WHERE id = @id;");
                delete.Parameters.AddWithValue("@id", incoming.Id.Value);
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await using (var command = _db.CreateCommand(UpsertSql))
                {
                    Bind(command, incoming);
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await ReplaceTagsAsync(incoming, ct).ConfigureAwait(false);

                if (incoming.DeletedAt is null) await IndexAsync(incoming, ct).ConfigureAwait(false);
                else await RemoveFromIndexAsync(incoming.Id, ct).ConfigureAwait(false);
            }

            await using var journal = _db.CreateCommand("""
                INSERT INTO journal (operation, entity_type, entity_id, at, device_id, snapshot, content_hash)
                VALUES (@op, 'object', @id, @at, @device, @snapshot, @hash);
                """);

            journal.Parameters.AddWithValue("@op", (int)operation);
            journal.Parameters.AddWithValue("@id", incoming.Id.Value);
            journal.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            journal.Parameters.AddWithValue("@device", fromDeviceId);
            journal.Parameters.AddWithValue("@snapshot", PayloadSerializer.SerializeSnapshot(incoming));
            journal.Parameters.AddWithValue("@hash",
                (object?)(incoming.PayloadAs<FilePayload>()?.ContentHash) ?? DBNull.Value);

            await journal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return ApplyOutcome.Applied;
    }

    /// <summary>Moves an object to the trash. Nothing is destroyed until it is emptied.</summary>
    public async Task TrashAsync(CampusId id, CancellationToken ct = default)
    {
        var entity = await GetAsync(id, ct).ConfigureAwait(false);
        if (entity is null) return;

        entity.DeletedAt = DateTimeOffset.UtcNow;
        await _db.InTransactionAsync(async () =>
        {
            await SetTimestampAsync(id, "deleted_at", entity.DeletedAt, ct).ConfigureAwait(false);
            await RemoveFromIndexAsync(id, ct).ConfigureAwait(false);
            await AppendJournalAsync(ChangeOperation.Trash, entity, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task RestoreAsync(CampusId id, CancellationToken ct = default)
    {
        var entity = await GetAsync(id, ct).ConfigureAwait(false);
        if (entity is null) return;

        entity.DeletedAt = null;
        await _db.InTransactionAsync(async () =>
        {
            await SetTimestampAsync(id, "deleted_at", null, ct).ConfigureAwait(false);
            await IndexAsync(entity, ct).ConfigureAwait(false);
            await AppendJournalAsync(ChangeOperation.Restore, entity, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes an object for good. The caller is responsible for deciding whether the vault
    /// bytes it referenced are now unreferenced — content addressing means they may not be.
    /// </summary>
    public async Task DeleteForeverAsync(CampusId id, CancellationToken ct = default)
    {
        var entity = await GetAsync(id, ct).ConfigureAwait(false);
        if (entity is null) return;

        await _db.InTransactionAsync(async () =>
        {
            await AppendJournalAsync(ChangeOperation.Delete, entity, ct).ConfigureAwait(false);
            await RemoveFromIndexAsync(id, ct).ConfigureAwait(false);
            await using var command = _db.CreateCommand("DELETE FROM objects WHERE id = @id;");
            command.Parameters.AddWithValue("@id", id.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Records that the user just opened something, for the Recent and Continue lists.</summary>
    public Task MarkOpenedAsync(CampusId id, CancellationToken ct = default)
        => SetTimestampAsync(id, "opened_at", DateTimeOffset.UtcNow, ct);

    public async Task SetFlagAsync(CampusId id, string column, bool value, CancellationToken ct = default)
    {
        if (column is not ("is_favorite" or "is_pinned" or "is_archived"))
            throw new ArgumentException($"'{column}' is not a flag column.", nameof(column));

        await using var command = _db.CreateCommand(
            $"UPDATE objects SET {column} = @value, updated_at = @now WHERE id = @id;");
        command.Parameters.AddWithValue("@value", value ? 1 : 0);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@id", id.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Empties the trash, returning the content hashes that no object references any more.</summary>
    public async Task<IReadOnlyList<string>> EmptyTrashAsync(CancellationToken ct = default)
    {
        var orphaned = new List<string>();

        await _db.InTransactionAsync(async () =>
        {
            await using (var command = _db.CreateCommand("""
                SELECT DISTINCT json_extract(payload, '$.contentHash') AS hash
                FROM objects
                WHERE deleted_at IS NOT NULL
                  AND json_extract(payload, '$.contentHash') IS NOT NULL
                  AND json_extract(payload, '$.contentHash') NOT IN (
                      SELECT json_extract(payload, '$.contentHash')
                      FROM objects
                      WHERE deleted_at IS NULL
                        AND json_extract(payload, '$.contentHash') IS NOT NULL);
                """))
            {
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(0)) orphaned.Add(reader.GetString(0));
                }
            }

            await _db.ExecuteAsync("""
                DELETE FROM objects_fts WHERE object_id IN
                    (SELECT id FROM objects WHERE deleted_at IS NOT NULL);
                DELETE FROM objects WHERE deleted_at IS NOT NULL;
                """, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return orphaned;
    }

    // ------------------------------------------------------------------------- tags

    public async Task<IReadOnlyList<string>> GetTagsAsync(CampusId id, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand(
            "SELECT tag FROM object_tags WHERE object_id = @id ORDER BY tag;");
        command.Parameters.AddWithValue("@id", id.Value);

        var tags = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) tags.Add(reader.GetString(0));
        return tags;
    }

    private async Task FillTagsAsync(List<CampusObject> objects, CancellationToken ct)
    {
        if (objects.Count == 0) return;

        var ids = string.Join(", ", objects.Select((_, i) => $"@i{i}"));
        await using var command = _db.CreateCommand(
            $"SELECT object_id, tag FROM object_tags WHERE object_id IN ({ids});");
        for (var i = 0; i < objects.Count; i++)
            command.Parameters.AddWithValue($"@i{i}", objects[i].Id.Value);

        var byId = objects.ToDictionary(o => o.Id.Value, StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (byId.TryGetValue(reader.GetString(0), out var entity))
                entity.Tags.Add(reader.GetString(1));
        }
    }

    private async Task ReplaceTagsAsync(CampusObject entity, CancellationToken ct)
    {
        await using (var clear = _db.CreateCommand("DELETE FROM object_tags WHERE object_id = @id;"))
        {
            clear.Parameters.AddWithValue("@id", entity.Id.Value);
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var tag in entity.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalised = tag.Trim().TrimStart('#');
            if (normalised.Length == 0) continue;

            await using var insert = _db.CreateCommand("""
                INSERT OR IGNORE INTO tags (name) VALUES (@tag);
                INSERT OR IGNORE INTO object_tags (object_id, tag) VALUES (@id, @tag);
                """);
            insert.Parameters.AddWithValue("@tag", normalised);
            insert.Parameters.AddWithValue("@id", entity.Id.Value);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ search index

    /// <summary>Adds or replaces an object's row in the full-text index.</summary>
    public async Task IndexAsync(CampusObject entity, CancellationToken ct = default)
    {
        await RemoveFromIndexAsync(entity.Id, ct).ConfigureAwait(false);

        await using var insert = _db.CreateCommand("""
            INSERT INTO objects_fts (object_id, title, summary, body, tags)
            VALUES (@id, @title, @summary, @body, @tags);
            """);
        insert.Parameters.AddWithValue("@title", entity.Title);
        insert.Parameters.AddWithValue("@summary", (object?)entity.Summary ?? string.Empty);
        insert.Parameters.AddWithValue("@body", SearchableBody(entity));
        insert.Parameters.AddWithValue("@tags", string.Join(' ', entity.Tags));
        insert.Parameters.AddWithValue("@id", entity.Id.Value);
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task RemoveFromIndexAsync(CampusId id, CancellationToken ct)
    {
        await using var command = _db.CreateCommand(
            "DELETE FROM objects_fts WHERE object_id = @id;");
        command.Parameters.AddWithValue("@id", id.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The text worth searching inside an object. Extracted document text is added separately by
    /// the indexer, which runs out of process.
    /// </summary>
    private static string SearchableBody(CampusObject entity) => entity.Payload switch
    {
        NotePayload note => note.Body,
        LessonPayload lesson => $"{lesson.Unit} {lesson.Body}",
        AssignmentPayload assignment => $"{assignment.Teacher} {assignment.Instructions}",
        RequirementPayload requirement => $"{requirement.Action} {requirement.Teacher}",
        LinkPayload link => $"{link.Url} {link.Description}",
        TaskPayload task => $"{task.Notes} {string.Join(' ', task.Checklist.Select(c => c.Text))}",
        ThreadPayload thread => thread.Body ?? string.Empty,
        FilePayload file => file.OriginalFileName,
        BookPayload book => $"{book.Author} {book.Edition}",
        InboxPayload inbox => inbox.RawText ?? string.Empty,
        GoalPayload goal => $"{goal.Detail} {string.Join(' ', goal.Steps.Select(s => s.Text))}",
        _ => string.Empty,
    };

    // ---------------------------------------------------------------------- journal

    private async Task AppendJournalAsync(
        ChangeOperation operation, CampusObject entity, CancellationToken ct)
    {
        await using var command = _db.CreateCommand("""
            INSERT INTO journal (operation, entity_type, entity_id, at, device_id, snapshot, content_hash)
            VALUES (@op, 'object', @id, @at, @device, @snapshot, @hash);
            """);
        command.Parameters.AddWithValue("@op", (int)operation);
        command.Parameters.AddWithValue("@id", entity.Id.Value);
        command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@device", _deviceId);
        command.Parameters.AddWithValue("@snapshot", PayloadSerializer.SerializeSnapshot(entity));
        command.Parameters.AddWithValue("@hash",
            (object?)(entity.PayloadAs<FilePayload>()?.ContentHash) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------------- helpers

    private async Task<bool> ExistsAsync(CampusId id, CancellationToken ct)
    {
        await using var command = _db.CreateCommand("SELECT 1 FROM objects WHERE id = @id;");
        command.Parameters.AddWithValue("@id", id.Value);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private async Task SetTimestampAsync(
        CampusId id, string column, DateTimeOffset? value, CancellationToken ct)
    {
        if (column is not ("deleted_at" or "opened_at" or "completed_at"))
            throw new ArgumentException($"'{column}' is not a timestamp column.", nameof(column));

        await using var command = _db.CreateCommand(
            $"UPDATE objects SET {column} = @value, updated_at = @now WHERE id = @id;");
        command.Parameters.AddWithValue("@value",
            value is null ? DBNull.Value : value.Value.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@id", id.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void Bind(SqliteCommand command, CampusObject entity)
    {
        command.Parameters.AddWithValue("@id", entity.Id.Value);
        command.Parameters.AddWithValue("@kind", (int)entity.Kind);
        command.Parameters.AddWithValue("@title", entity.Title);
        command.Parameters.AddWithValue("@summary", (object?)entity.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("@subject", (object?)entity.SubjectId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("@parent", (object?)entity.ParentId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", (int)entity.Status);
        command.Parameters.AddWithValue("@priority", (int)entity.Priority);
        command.Parameters.AddWithValue("@created", entity.CreatedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@updated", entity.UpdatedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@due", Time(entity.DueAt));
        command.Parameters.AddWithValue("@completed", Time(entity.CompletedAt));
        command.Parameters.AddWithValue("@deleted", Time(entity.DeletedAt));
        command.Parameters.AddWithValue("@opened", Time(entity.OpenedAt));
        command.Parameters.AddWithValue("@favorite", entity.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("@pinned", entity.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("@archived", entity.IsArchived ? 1 : 0);
        command.Parameters.AddWithValue("@year", (object?)entity.AcademicYear ?? DBNull.Value);
        command.Parameters.AddWithValue("@term",
            entity.Term is null ? DBNull.Value : (int)entity.Term.Value);
        command.Parameters.AddWithValue("@source", (int)entity.Source);
        command.Parameters.AddWithValue("@device", (object?)entity.SourceDeviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("@sort", entity.SortOrder);
        command.Parameters.AddWithValue("@payload",
            (object?)PayloadSerializer.Serialize(entity.Payload) ?? DBNull.Value);
        command.Parameters.AddWithValue("@metadata",
            PayloadSerializer.SerializeMetadata(entity.Metadata));

        static object Time(DateTimeOffset? value)
            => value is null ? DBNull.Value : value.Value.ToUnixTimeMilliseconds();
    }

    /// <summary>Reads one row of the objects table. Shared with the search repository.</summary>
    internal static CampusObject Read(SqliteDataReader reader)
    {
        var kind = (ObjectKind)reader.GetInt32(reader.GetOrdinal("kind"));
        var entity = new CampusObject
        {
            Id = CampusId.Parse(reader.GetString(reader.GetOrdinal("id"))),
            Kind = kind,
            Title = reader.GetString(reader.GetOrdinal("title")),
            Summary = GetNullableString(reader, "summary"),
            SubjectId = GetNullableId(reader, "subject_id"),
            ParentId = GetNullableId(reader, "parent_id"),
            Status = (ObjectStatus)reader.GetInt32(reader.GetOrdinal("status")),
            Priority = (Priority)reader.GetInt32(reader.GetOrdinal("priority")),
            CreatedAt = GetTime(reader, "created_at") ?? DateTimeOffset.UnixEpoch,
            UpdatedAt = GetTime(reader, "updated_at") ?? DateTimeOffset.UnixEpoch,
            DueAt = GetTime(reader, "due_at"),
            CompletedAt = GetTime(reader, "completed_at"),
            DeletedAt = GetTime(reader, "deleted_at"),
            OpenedAt = GetTime(reader, "opened_at"),
            IsFavorite = reader.GetInt32(reader.GetOrdinal("is_favorite")) != 0,
            IsPinned = reader.GetInt32(reader.GetOrdinal("is_pinned")) != 0,
            IsArchived = reader.GetInt32(reader.GetOrdinal("is_archived")) != 0,
            AcademicYear = GetNullableInt(reader, "academic_year"),
            Source = (CaptureSource)reader.GetInt32(reader.GetOrdinal("source")),
            SourceDeviceId = GetNullableString(reader, "source_device"),
            SortOrder = reader.GetDouble(reader.GetOrdinal("sort_order")),
        };

        var term = GetNullableInt(reader, "term");
        if (term is { } t) entity.Term = (TermKind)t;

        entity.Payload = PayloadSerializer.Deserialize(kind, GetNullableString(reader, "payload"));

        foreach (var pair in PayloadSerializer.DeserializeMetadata(GetNullableString(reader, "metadata")))
            entity.Metadata[pair.Key] = pair.Value;

        return entity;
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var index = reader.GetOrdinal(column);
        return reader.IsDBNull(index) ? null : reader.GetString(index);
    }

    private static int? GetNullableInt(SqliteDataReader reader, string column)
    {
        var index = reader.GetOrdinal(column);
        return reader.IsDBNull(index) ? null : reader.GetInt32(index);
    }

    private static CampusId? GetNullableId(SqliteDataReader reader, string column)
    {
        var value = GetNullableString(reader, column);
        return value is null ? null : CampusId.Parse(value);
    }

    private static DateTimeOffset? GetTime(SqliteDataReader reader, string column)
    {
        var index = reader.GetOrdinal(column);
        return reader.IsDBNull(index)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(index));
    }
}
