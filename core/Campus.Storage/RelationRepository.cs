using System.Text.RegularExpressions;
using Campus.Domain;
using Microsoft.Data.Sqlite;

namespace Campus.Storage;

/// <summary>One end of a link, with enough of the other object to draw a row for it.</summary>
public sealed record RelatedObject(
    Relation Relation,
    CampusId OtherId,
    string Title,
    ObjectKind Kind,
    bool IsIncoming);

/// <summary>
/// Links between objects, and the backlinks that come free with them.
///
/// An edge is stored once, in the direction it was made. "What links here" is the same table read
/// the other way round, which is why a backlink can never be stale or one-sided: there is only one
/// row, and deleting it removes both views of it.
/// </summary>
public sealed partial class RelationRepository(CampusDatabase database)
{
    private readonly CampusDatabase _db = database;

    // ------------------------------------------------------------------------ reads

    /// <summary>Everything this object points at.</summary>
    public Task<IReadOnlyList<RelatedObject>> OutgoingAsync(
        CampusId id, CancellationToken ct = default) => ReadAsync(id, incoming: false, ct);

    /// <summary>Everything that points at this object — its backlinks.</summary>
    public Task<IReadOnlyList<RelatedObject>> IncomingAsync(
        CampusId id, CancellationToken ct = default) => ReadAsync(id, incoming: true, ct);

    public async Task<IReadOnlyList<RelatedObject>> AllAsync(
        CampusId id, CancellationToken ct = default)
    {
        var outgoing = await OutgoingAsync(id, ct).ConfigureAwait(false);
        var incoming = await IncomingAsync(id, ct).ConfigureAwait(false);
        return [.. outgoing, .. incoming];
    }

    private async Task<IReadOnlyList<RelatedObject>> ReadAsync(
        CampusId id, bool incoming, CancellationToken ct)
    {
        var (mine, theirs) = incoming ? ("to_id", "from_id") : ("from_id", "to_id");

        await using var command = _db.CreateCommand($"""
            SELECT r.id, r.from_id, r.to_id, r.kind, r.label, r.created_at, r.is_derived,
                   o.id AS other_id, o.title AS other_title, o.kind AS other_kind
            FROM relations r
            JOIN objects o ON o.id = r.{theirs}
            WHERE r.{mine} = @id AND o.deleted_at IS NULL
            ORDER BY o.title;
            """);
        command.Parameters.AddWithValue("@id", id.Value);

        var results = new List<RelatedObject>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new RelatedObject(
                ReadRelation(reader),
                CampusId.Parse(reader.GetString(reader.GetOrdinal("other_id"))),
                reader.GetString(reader.GetOrdinal("other_title")),
                (ObjectKind)reader.GetInt32(reader.GetOrdinal("other_kind")),
                incoming));
        }

        return results;
    }

    public async Task<int> CountAsync(CampusId id, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT COUNT(*) FROM relations WHERE from_id = @id OR to_id = @id;
            """);
        command.Parameters.AddWithValue("@id", id.Value);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    // ----------------------------------------------------------------------- writes

    /// <summary>
    /// Creates a link. Making the same link twice is not an error and does not create a second
    /// row — the unique index on (from, to, kind) makes that a property of the schema rather
    /// than something every caller has to remember.
    /// </summary>
    public async Task<Relation> LinkAsync(
        CampusId from, CampusId to, RelationKind kind = RelationKind.Related,
        string? label = null, bool isDerived = false, CancellationToken ct = default)
    {
        var relation = new Relation
        {
            FromId = from,
            ToId = to,
            Kind = kind,
            Label = label,
            IsDerived = isDerived,
        };

        await using var command = _db.CreateCommand("""
            INSERT INTO relations (id, from_id, to_id, kind, label, created_at, is_derived)
            VALUES (@id, @from, @to, @kind, @label, @created, @derived)
            ON CONFLICT(from_id, to_id, kind) DO UPDATE SET label = excluded.label;
            """);

        command.Parameters.AddWithValue("@id", relation.Id.Value);
        command.Parameters.AddWithValue("@from", from.Value);
        command.Parameters.AddWithValue("@to", to.Value);
        command.Parameters.AddWithValue("@kind", (int)kind);
        command.Parameters.AddWithValue("@label", (object?)label ?? DBNull.Value);
        command.Parameters.AddWithValue("@created", relation.CreatedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@derived", isDerived ? 1 : 0);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return relation;
    }

    public async Task UnlinkAsync(CampusId relationId, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("DELETE FROM relations WHERE id = @id;");
        command.Parameters.AddWithValue("@id", relationId.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ wiki links

    [GeneratedRegex(@"\[\[([^\]\|]+)(?:\|[^\]]*)?\]\]", RegexOptions.Compiled)]
    private static partial Regex WikiLink();

    /// <summary>
    /// Rebuilds the links a note's text implies. Typing [[Trigonometry]] in a note should link
    /// to the object called Trigonometry without a separate step, and deleting the text should
    /// remove the link — so derived edges are replaced wholesale rather than added to. Edges made
    /// by hand are left alone, because the person meant those.
    /// </summary>
    public async Task<int> SyncDerivedLinksAsync(
        CampusId from, string? body, CancellationToken ct = default)
    {
        await using (var clear = _db.CreateCommand(
            "DELETE FROM relations WHERE from_id = @id AND is_derived = 1;"))
        {
            clear.Parameters.AddWithValue("@id", from.Value);
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(body)) return 0;

        var made = 0;
        foreach (var name in WikiLink().Matches(body)
            .Select(m => m.Groups[1].Value.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var target = await FindByTitleAsync(name, ct).ConfigureAwait(false);
            if (target is null || target.Value == from) continue;

            await LinkAsync(from, target.Value, RelationKind.Reference, name, isDerived: true, ct)
                .ConfigureAwait(false);
            made++;
        }

        return made;
    }

    /// <summary>Finds an object by exact title, which is what a wiki link names.</summary>
    public async Task<CampusId?> FindByTitleAsync(string title, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            SELECT id FROM objects
            WHERE title = @title COLLATE NOCASE AND deleted_at IS NULL
            ORDER BY updated_at DESC
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("@title", title);

        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is string id ? CampusId.Parse(id) : null;
    }

    private static Relation ReadRelation(SqliteDataReader reader) => new()
    {
        Id = CampusId.Parse(reader.GetString(reader.GetOrdinal("id"))),
        FromId = CampusId.Parse(reader.GetString(reader.GetOrdinal("from_id"))),
        ToId = CampusId.Parse(reader.GetString(reader.GetOrdinal("to_id"))),
        Kind = (RelationKind)reader.GetInt32(reader.GetOrdinal("kind")),
        Label = reader.IsDBNull(reader.GetOrdinal("label"))
            ? null
            : reader.GetString(reader.GetOrdinal("label")),
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(
            reader.GetInt64(reader.GetOrdinal("created_at"))),
        IsDerived = reader.GetInt32(reader.GetOrdinal("is_derived")) != 0,
    };
}
