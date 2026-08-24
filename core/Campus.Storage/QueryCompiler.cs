using System.Text;
using Campus.Domain;
using Microsoft.Data.Sqlite;

namespace Campus.Storage;

/// <summary>
/// Turns a <see cref="CampusQuery"/> into SQL. Every virtual collection in Campus — a subject's
/// books, "due this week", the print queue — goes through here, so there is one place where
/// filtering semantics are decided and one place to optimise.
/// </summary>
public static class QueryCompiler
{
    public readonly record struct Compiled(string Sql, IReadOnlyList<SqliteParameter> Parameters);

    public static Compiled Compile(CampusQuery query, DateTimeOffset now, bool countOnly = false)
    {
        var parameters = new List<SqliteParameter>();
        var where = new List<string>();
        var index = 0;

        string Bind(object? value)
        {
            var name = $"@p{index++}";
            parameters.Add(new SqliteParameter(name, value ?? DBNull.Value));
            return name;
        }

        // Trashed objects are hidden everywhere except the Trash view itself, which wants only
        // them — "include the trash" and "show me the trash" are different questions.
        where.Add(query.OnlyTrashed ? "o.deleted_at IS NOT NULL"
            : query.IncludeTrashed ? "1 = 1"
            : "o.deleted_at IS NULL");

        if (query.Kinds.Count > 0)
            where.Add($"o.kind IN ({string.Join(", ", query.Kinds.Select(k => Bind((int)k)))})");

        if (query.SubjectIds.Count > 0)
            where.Add($"o.subject_id IN ({string.Join(", ", query.SubjectIds.Select(s => Bind(s.Value)))})");

        if (query.Statuses.Count > 0)
            where.Add($"o.status IN ({string.Join(", ", query.Statuses.Select(s => Bind((int)s)))})");

        if (query.ParentId is { } parent)
            where.Add($"o.parent_id = {Bind(parent.Value)}");

        if (query.IsFavorite is { } favourite)
            where.Add($"o.is_favorite = {Bind(favourite ? 1 : 0)}");

        if (query.IsPinned is { } pinned)
            where.Add($"o.is_pinned = {Bind(pinned ? 1 : 0)}");

        if (query.IsArchived is { } archived)
            where.Add($"o.is_archived = {Bind(archived ? 1 : 0)}");

        if (query.AcademicYear is { } year)
            where.Add($"o.academic_year = {Bind(year)}");

        if (query.Term is { } term)
            where.Add($"o.term = {Bind((int)term)}");

        AppendRange(query.Due, "o.due_at");
        AppendRange(query.Created, "o.created_at");
        AppendRange(query.Updated, "o.updated_at");

        void AppendRange(DateRange? range, string column)
        {
            if (range is not { } value) return;
            var (from, to) = value.Resolve(now);
            if (from is { } f) where.Add($"{column} >= {Bind(f.ToUnixTimeMilliseconds())}");
            if (to is { } t) where.Add($"{column} < {Bind(t.ToUnixTimeMilliseconds())}");
            // A relative window on a nullable column should exclude rows with no date at all.
            if (from is not null || to is not null) where.Add($"{column} IS NOT NULL");
        }

        // Media kind and print state live inside the JSON payload, which SQLite can read directly.
        if (query.Media.Count > 0)
        {
            var values = string.Join(", ", query.Media.Select(m => Bind((int)m)));
            where.Add($"json_extract(o.payload, '$.media') IN ({values})");
        }

        if (query.PrintState is { } printState)
            where.Add($"json_extract(o.payload, '$.state') = {Bind((int)printState)}");

        foreach (var tag in query.TagsAll)
            where.Add($"EXISTS (SELECT 1 FROM object_tags t WHERE t.object_id = o.id AND t.tag = {Bind(tag)})");

        if (query.TagsAny.Count > 0)
        {
            var values = string.Join(", ", query.TagsAny.Select(Bind));
            where.Add($"EXISTS (SELECT 1 FROM object_tags t WHERE t.object_id = o.id AND t.tag IN ({values}))");
        }

        var sql = new StringBuilder();
        var usesSearch = !string.IsNullOrWhiteSpace(query.Text);

        sql.Append(countOnly ? "SELECT COUNT(*) " : "SELECT o.* ");
        sql.Append("FROM objects o ");

        if (usesSearch)
        {
            sql.Append("JOIN objects_fts f ON f.object_id = o.id ");
            where.Add($"f.objects_fts MATCH {Bind(ToMatchExpression(query.Text!))}");
        }

        sql.Append("WHERE ").Append(string.Join(" AND ", where)).Append(' ');

        if (!countOnly)
        {
            sql.Append("ORDER BY ").Append(OrderBy(query, usesSearch)).Append(' ');
            if (query.Limit is { } limit)
            {
                sql.Append("LIMIT ").Append(Bind(limit)).Append(' ');
                if (query.Offset > 0) sql.Append("OFFSET ").Append(Bind(query.Offset));
            }
        }

        return new Compiled(sql.ToString(), parameters);
    }

    private static string OrderBy(CampusQuery query, bool usesSearch)
    {
        var direction = query.Descending ? "DESC" : "ASC";

        // Pinned things stay at the top of any hand-orderable list, which is what pinning means.
        const string pinnedFirst = "o.is_pinned DESC, ";

        return query.Sort switch
        {
            SortField.Relevance when usesSearch => "f.rank",
            SortField.Relevance => $"o.updated_at {direction}",
            SortField.CreatedAt => $"{pinnedFirst}o.created_at {direction}",
            SortField.DueAt =>
                // Undated items belong after dated ones however the sort runs, not interleaved.
                $"{pinnedFirst}o.due_at IS NULL, o.due_at {direction}",
            SortField.Title => $"{pinnedFirst}o.title COLLATE NOCASE {direction}",
            SortField.Priority => $"{pinnedFirst}o.priority {direction}, o.due_at IS NULL, o.due_at ASC",
            SortField.Manual => $"{pinnedFirst}o.sort_order {direction}",
            SortField.OpenedAt => $"o.opened_at IS NULL, o.opened_at {direction}",
            _ => $"{pinnedFirst}o.updated_at {direction}",
        };
    }

    /// <summary>
    /// Turns what the user typed into an FTS5 expression. Each word becomes a prefix term so
    /// search feels live, and quotes are stripped because FTS5 treats them as phrase syntax and
    /// an unbalanced one is a syntax error rather than a search.
    /// </summary>
    public static string ToMatchExpression(string text)
    {
        var terms = text
            .Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('"', '\'', '*'))
            .Where(t => t.Length > 0)
            .Select(t => $"\"{t.Replace("\"", "\"\"")}\"*");

        var expression = string.Join(" AND ", terms);
        return expression.Length == 0 ? "\"\"" : expression;
    }
}
