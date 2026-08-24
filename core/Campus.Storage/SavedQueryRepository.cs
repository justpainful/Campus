using System.Text.Json;
using Campus.Domain;

namespace Campus.Storage;

/// <summary>A search someone kept. This is what a smart collection actually is.</summary>
public sealed class SavedQuery
{
    public CampusId Id { get; init; } = CampusId.New();
    public string Name { get; set; } = string.Empty;
    public string? IconName { get; set; }
    public CampusQuery Query { get; set; } = new();
    public double SortOrder { get; set; }
    public bool IsPinned { get; set; }
}

/// <summary>
/// Smart collections.
///
/// A saved query is stored as the question, not the answer. "Everything due this week that is not
/// finished" re-runs every time it is opened, so it is right on Sunday and still right on Friday —
/// which a stored list of ids could never be.
/// </summary>
public sealed class SavedQueryRepository(CampusDatabase database)
{
    private readonly CampusDatabase _db = database;

    public async Task<IReadOnlyList<SavedQuery>> AllAsync(CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand(
            "SELECT * FROM saved_queries ORDER BY is_pinned DESC, sort_order, name;");

        var results = new List<SavedQuery>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var iconIndex = reader.GetOrdinal("icon_name");

            results.Add(new SavedQuery
            {
                Id = CampusId.Parse(reader.GetString(reader.GetOrdinal("id"))),
                Name = reader.GetString(reader.GetOrdinal("name")),
                IconName = reader.IsDBNull(iconIndex) ? null : reader.GetString(iconIndex),
                Query = Deserialize(reader.GetString(reader.GetOrdinal("query_json"))),
                SortOrder = reader.GetDouble(reader.GetOrdinal("sort_order")),
                IsPinned = reader.GetInt32(reader.GetOrdinal("is_pinned")) != 0,
            });
        }

        return results;
    }

    public async Task SaveAsync(SavedQuery saved, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            INSERT INTO saved_queries (id, name, icon_name, query_json, sort_order, is_pinned)
            VALUES (@id, @name, @icon, @json, @sort, @pinned)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                icon_name = excluded.icon_name,
                query_json = excluded.query_json,
                sort_order = excluded.sort_order,
                is_pinned = excluded.is_pinned;
            """);

        command.Parameters.AddWithValue("@id", saved.Id.Value);
        command.Parameters.AddWithValue("@name", saved.Name);
        command.Parameters.AddWithValue("@icon", (object?)saved.IconName ?? DBNull.Value);
        command.Parameters.AddWithValue("@json", Serialize(saved.Query));
        command.Parameters.AddWithValue("@sort", saved.SortOrder);
        command.Parameters.AddWithValue("@pinned", saved.IsPinned ? 1 : 0);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(CampusId id, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("DELETE FROM saved_queries WHERE id = @id;");
        command.Parameters.AddWithValue("@id", id.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static string Serialize(CampusQuery query)
        => JsonSerializer.Serialize(query, PayloadSerializer.Options);

    public static CampusQuery Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CampusQuery>(json, PayloadSerializer.Options) ?? new();
        }
        catch (JsonException)
        {
            // A collection whose definition cannot be read should show up empty rather than
            // taking the whole sidebar down with it.
            return new CampusQuery();
        }
    }
}
