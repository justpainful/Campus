using Campus.Domain;
using Microsoft.Data.Sqlite;

namespace Campus.Storage;

/// <summary>
/// Highlights, ink, comments, bookmarks and timestamp notes.
///
/// Annotations are stored beside the document rather than inside it. Writing into the PDF would
/// change its bytes, and the bytes are the document's identity in the vault — the same textbook
/// imported on two devices would stop being the same object the moment one of them highlighted a
/// paragraph. Keeping them separate also means annotating a file Campus can only read, never write.
/// </summary>
public sealed class AnnotationRepository(CampusDatabase database)
{
    private readonly CampusDatabase _db = database;

    public async Task<IReadOnlyList<Annotation>> ForObjectAsync(
        CampusId objectId, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT * FROM annotations
            WHERE object_id = @id
            ORDER BY page, position, created_at;
            """);
        command.Parameters.AddWithValue("@id", objectId.Value);
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Annotation>> ForPageAsync(
        CampusId objectId, int page, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT * FROM annotations
            WHERE object_id = @id AND page = @page
            ORDER BY created_at;
            """);
        command.Parameters.AddWithValue("@id", objectId.Value);
        command.Parameters.AddWithValue("@page", page);
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    /// <summary>Everything annotated recently, for the review lists.</summary>
    public async Task<IReadOnlyList<(Annotation Annotation, string Title)>> RecentAsync(
        int limit = 50, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT a.*, o.title AS owner_title
            FROM annotations a
            JOIN objects o ON o.id = a.object_id
            WHERE o.deleted_at IS NULL
            ORDER BY a.updated_at DESC
            LIMIT @limit;
            """);
        command.Parameters.AddWithValue("@limit", limit);

        var results = new List<(Annotation, string)>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add((Read(reader), reader.GetString(reader.GetOrdinal("owner_title"))));
        }
        return results;
    }

    public async Task SaveAsync(Annotation annotation, CancellationToken ct = default)
    {
        annotation.UpdatedAt = DateTimeOffset.UtcNow;

        await using var command = _db.CreateCommand("""
            INSERT INTO annotations (
                id, object_id, kind, page, position, x, y, width, height,
                geometry, text, color_name, created_at, updated_at)
            VALUES (
                @id, @object, @kind, @page, @position, @x, @y, @width, @height,
                @geometry, @text, @color, @created, @updated)
            ON CONFLICT(id) DO UPDATE SET
                kind = excluded.kind,
                page = excluded.page,
                position = excluded.position,
                x = excluded.x, y = excluded.y,
                width = excluded.width, height = excluded.height,
                geometry = excluded.geometry,
                text = excluded.text,
                color_name = excluded.color_name,
                updated_at = excluded.updated_at;
            """);

        command.Parameters.AddWithValue("@id", annotation.Id.Value);
        command.Parameters.AddWithValue("@object", annotation.ObjectId.Value);
        command.Parameters.AddWithValue("@kind", (int)annotation.Kind);
        command.Parameters.AddWithValue("@page", (object?)annotation.Page ?? DBNull.Value);
        command.Parameters.AddWithValue("@position",
            annotation.Position is { } p ? (long)p.TotalMilliseconds : DBNull.Value);
        command.Parameters.AddWithValue("@x", (object?)annotation.X ?? DBNull.Value);
        command.Parameters.AddWithValue("@y", (object?)annotation.Y ?? DBNull.Value);
        command.Parameters.AddWithValue("@width", (object?)annotation.Width ?? DBNull.Value);
        command.Parameters.AddWithValue("@height", (object?)annotation.Height ?? DBNull.Value);
        command.Parameters.AddWithValue("@geometry", (object?)annotation.Geometry ?? DBNull.Value);
        command.Parameters.AddWithValue("@text", (object?)annotation.Text ?? DBNull.Value);
        command.Parameters.AddWithValue("@color", (object?)annotation.ColorName ?? DBNull.Value);
        command.Parameters.AddWithValue("@created", annotation.CreatedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@updated", annotation.UpdatedAt.ToUnixTimeMilliseconds());

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(CampusId id, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("DELETE FROM annotations WHERE id = @id;");
        command.Parameters.AddWithValue("@id", id.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CampusId objectId, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand(
            "SELECT COUNT(*) FROM annotations WHERE object_id = @id;");
        command.Parameters.AddWithValue("@id", objectId.Value);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    private static async Task<IReadOnlyList<Annotation>> ReadAllAsync(
        SqliteCommand command, CancellationToken ct)
    {
        var results = new List<Annotation>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) results.Add(Read(reader));
        return results;
    }

    private static Annotation Read(SqliteDataReader reader)
    {
        double? Real(string column)
        {
            var index = reader.GetOrdinal(column);
            return reader.IsDBNull(index) ? null : reader.GetDouble(index);
        }

        string? Text(string column)
        {
            var index = reader.GetOrdinal(column);
            return reader.IsDBNull(index) ? null : reader.GetString(index);
        }

        var pageIndex = reader.GetOrdinal("page");
        var positionIndex = reader.GetOrdinal("position");

        return new Annotation
        {
            Id = CampusId.Parse(reader.GetString(reader.GetOrdinal("id"))),
            ObjectId = CampusId.Parse(reader.GetString(reader.GetOrdinal("object_id"))),
            Kind = (AnnotationKind)reader.GetInt32(reader.GetOrdinal("kind")),
            Page = reader.IsDBNull(pageIndex) ? null : reader.GetInt32(pageIndex),
            Position = reader.IsDBNull(positionIndex)
                ? null
                : TimeSpan.FromMilliseconds(reader.GetInt64(positionIndex)),
            X = Real("x"),
            Y = Real("y"),
            Width = Real("width"),
            Height = Real("height"),
            Geometry = Text("geometry"),
            Text = Text("text"),
            ColorName = Text("color_name"),
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                reader.GetInt64(reader.GetOrdinal("created_at"))),
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                reader.GetInt64(reader.GetOrdinal("updated_at"))),
        };
    }
}
