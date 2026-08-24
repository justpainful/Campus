using System.Text;
using Campus.Domain;

namespace Campus.Storage;

/// <summary>One search result: the object, a piece of the text that matched, and how well.</summary>
public sealed record SearchHit(CampusObject Object, string Snippet, double Rank);

/// <summary>
/// Search across everything readable.
///
/// The point of storing extracted document text is this: searching for a phrase should find the
/// page of the textbook it appears on, not just the file called "textbook". So the index covers
/// titles, summaries, note bodies, extracted document text and tags, and a hit brings back the
/// sentence it matched in — a list of titles is a much worse answer than a list of sentences.
/// </summary>
public sealed class SearchRepository(CampusDatabase database)
{
    private readonly CampusDatabase _db = database;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string text,
        IReadOnlyList<ObjectKind>? kinds = null,
        CampusId? subjectId = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        var match = ToMatchExpression(text);
        if (match is null) return [];

        var filters = new StringBuilder();
        if (kinds is { Count: > 0 })
            filters.Append(" AND o.kind IN (").AppendJoin(", ", kinds.Select(k => (int)k)).Append(')');
        if (subjectId is not null)
            filters.Append(" AND o.subject_id = @subject");

        // bm25 ranks by relevance and returns smaller numbers for better matches; the column
        // weights say a hit in the title matters more than one buried in a hundred-page PDF.
        await using var command = _db.CreateCommand($"""
            SELECT o.*,
                   snippet(objects_fts, -1, '', '', '…', 14) AS snip,
                   bm25(objects_fts, 0.0, 12.0, 6.0, 1.0, 4.0) AS rank
            FROM objects_fts f
            JOIN objects o ON o.id = f.object_id
            WHERE f.objects_fts MATCH @q
              AND o.deleted_at IS NULL{filters}
            ORDER BY rank
            LIMIT @limit;
            """);

        command.Parameters.AddWithValue("@q", match);
        command.Parameters.AddWithValue("@limit", limit);
        if (subjectId is { } subject) command.Parameters.AddWithValue("@subject", subject.Value);

        var results = new List<SearchHit>();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var snippet = reader.GetString(reader.GetOrdinal("snip")).Trim();
            results.Add(new SearchHit(
                ObjectRepository.Read(reader),
                snippet,
                reader.GetDouble(reader.GetOrdinal("rank"))));
        }

        return results;
    }

    /// <summary>How many things match, without fetching them.</summary>
    public async Task<int> CountAsync(string text, CancellationToken ct = default)
    {
        var match = ToMatchExpression(text);
        if (match is null) return 0;

        await using var command = _db.CreateCommand("""
            SELECT COUNT(*)
            FROM objects_fts f
            JOIN objects o ON o.id = f.object_id
            WHERE f.objects_fts MATCH @q AND o.deleted_at IS NULL;
            """);
        command.Parameters.AddWithValue("@q", match);

        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    /// <summary>
    /// Turns what someone typed into something FTS5 will accept.
    ///
    /// FTS5's query language has operators — AND, OR, NOT, NEAR, colons, asterisks, quotes — and
    /// a syntax error in it throws rather than returning nothing. People type apostrophes and
    /// hyphens without meaning any of that, so every word is quoted as a literal and the last one
    /// gets a prefix asterisk, which is what makes results appear while still typing.
    /// </summary>
    public static string? ToMatchExpression(string text)
    {
        var words = text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.Trim('"'))
            .Where(w => w.Length > 0)
            .ToList();

        if (words.Count == 0) return null;

        var expression = new StringBuilder();

        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0) expression.Append(" AND ");

            var quoted = '"' + words[i].Replace("\"", "\"\"") + '"';
            expression.Append(quoted);

            // Only the word still being typed is treated as a prefix. Doing it to all of them
            // makes "the" match everything and drowns the real terms.
            if (i == words.Count - 1 && words[i].Length >= 2) expression.Append('*');
        }

        return expression.ToString();
    }
}
