namespace Campus.Storage;

/// <summary>
/// Schema versioning. Each migration runs once, in order, inside a transaction, and the version
/// is recorded in SQLite's own user_version so the schema and its version can never disagree.
/// </summary>
public static class Migrations
{
    private static readonly (int Version, string Sql)[] Steps =
    [
        (1, """
            -- Every first-class thing in Campus lives in one table. Kind-specific data goes into
            -- the JSON payload, which keeps one index, one query path and one search path serving
            -- books, tasks, notes and print jobs alike.
            CREATE TABLE objects (
                id            TEXT PRIMARY KEY,
                kind          INTEGER NOT NULL,
                title         TEXT NOT NULL DEFAULT '',
                summary       TEXT,
                subject_id    TEXT REFERENCES objects(id) ON DELETE SET NULL,
                parent_id     TEXT REFERENCES objects(id) ON DELETE CASCADE,
                status        INTEGER NOT NULL DEFAULT 0,
                priority      INTEGER NOT NULL DEFAULT 0,
                created_at    INTEGER NOT NULL,
                updated_at    INTEGER NOT NULL,
                due_at        INTEGER,
                completed_at  INTEGER,
                deleted_at    INTEGER,
                opened_at     INTEGER,
                is_favorite   INTEGER NOT NULL DEFAULT 0,
                is_pinned     INTEGER NOT NULL DEFAULT 0,
                is_archived   INTEGER NOT NULL DEFAULT 0,
                academic_year INTEGER,
                term          INTEGER,
                source        INTEGER NOT NULL DEFAULT 0,
                source_device TEXT,
                sort_order    REAL NOT NULL DEFAULT 0,
                payload       TEXT,
                metadata      TEXT
            );

            CREATE INDEX ix_objects_kind        ON objects(kind, deleted_at);
            CREATE INDEX ix_objects_subject     ON objects(subject_id, kind);
            CREATE INDEX ix_objects_parent      ON objects(parent_id);
            CREATE INDEX ix_objects_due         ON objects(due_at) WHERE due_at IS NOT NULL;
            CREATE INDEX ix_objects_updated     ON objects(updated_at DESC);
            CREATE INDEX ix_objects_opened      ON objects(opened_at DESC) WHERE opened_at IS NOT NULL;
            CREATE INDEX ix_objects_trash       ON objects(deleted_at) WHERE deleted_at IS NOT NULL;

            CREATE TABLE tags (
                name        TEXT PRIMARY KEY,
                color_name  TEXT,
                description TEXT,
                is_pinned   INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE object_tags (
                object_id TEXT NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
                tag       TEXT NOT NULL,
                PRIMARY KEY (object_id, tag)
            );
            CREATE INDEX ix_object_tags_tag ON object_tags(tag);

            -- Edges are stored once and read in both directions; a backlink is the reverse read.
            CREATE TABLE relations (
                id         TEXT PRIMARY KEY,
                from_id    TEXT NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
                to_id      TEXT NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
                kind       INTEGER NOT NULL DEFAULT 0,
                label      TEXT,
                created_at INTEGER NOT NULL,
                is_derived INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX ux_relations_edge ON relations(from_id, to_id, kind);
            CREATE INDEX ix_relations_to ON relations(to_id);

            CREATE TABLE annotations (
                id         TEXT PRIMARY KEY,
                object_id  TEXT NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
                kind       INTEGER NOT NULL,
                page       INTEGER,
                position   INTEGER,
                x          REAL,
                y          REAL,
                width      REAL,
                height     REAL,
                geometry   TEXT,
                text       TEXT,
                color_name TEXT,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            );
            CREATE INDEX ix_annotations_object ON annotations(object_id, page);

            CREATE TABLE history (
                id        TEXT PRIMARY KEY,
                object_id TEXT NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
                action    TEXT NOT NULL,
                detail    TEXT,
                at        INTEGER NOT NULL,
                device_id TEXT
            );
            CREATE INDEX ix_history_object ON history(object_id, at DESC);

            CREATE TABLE versions (
                id             TEXT PRIMARY KEY,
                object_id      TEXT NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
                version_number INTEGER NOT NULL,
                content_hash   TEXT NOT NULL,
                size_bytes     INTEGER NOT NULL,
                created_at     INTEGER NOT NULL,
                label          TEXT
            );
            CREATE UNIQUE INDEX ux_versions ON versions(object_id, version_number);

            -- Append-only change log. Sync ships the range a peer has not seen rather than
            -- comparing whole workspaces.
            CREATE TABLE journal (
                seq          INTEGER PRIMARY KEY AUTOINCREMENT,
                operation    INTEGER NOT NULL,
                entity_type  TEXT NOT NULL,
                entity_id    TEXT NOT NULL,
                at           INTEGER NOT NULL,
                device_id    TEXT NOT NULL,
                snapshot     TEXT,
                content_hash TEXT
            );
            CREATE INDEX ix_journal_entity ON journal(entity_id, seq DESC);

            CREATE TABLE devices (
                device_id       TEXT PRIMARY KEY,
                display_name    TEXT NOT NULL,
                platform        INTEGER NOT NULL,
                public_key      TEXT NOT NULL,
                paired_at       INTEGER NOT NULL,
                last_seen_at    INTEGER,
                last_ack_seq    INTEGER NOT NULL DEFAULT 0,
                trusted         INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE conflicts (
                id              TEXT PRIMARY KEY,
                entity_id       TEXT NOT NULL,
                entity_type     TEXT NOT NULL,
                local_snapshot  TEXT NOT NULL,
                remote_snapshot TEXT NOT NULL,
                local_updated   INTEGER NOT NULL,
                remote_updated  INTEGER NOT NULL,
                remote_device   TEXT NOT NULL,
                detected_at     INTEGER NOT NULL,
                resolution      INTEGER
            );

            CREATE TABLE schedule_slots (
                id            TEXT PRIMARY KEY,
                subject_id    TEXT NOT NULL REFERENCES objects(id) ON DELETE CASCADE,
                day           INTEGER NOT NULL,
                start_minutes INTEGER NOT NULL,
                end_minutes   INTEGER NOT NULL,
                room          TEXT,
                term          INTEGER,
                academic_year INTEGER
            );
            CREATE INDEX ix_schedule_day ON schedule_slots(day, start_minutes);

            CREATE TABLE settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            -- Saved searches, which is what a smart collection is.
            CREATE TABLE saved_queries (
                id          TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                icon_name   TEXT,
                query_json  TEXT NOT NULL,
                sort_order  REAL NOT NULL DEFAULT 0,
                is_pinned   INTEGER NOT NULL DEFAULT 0
            );
            """),

        (2, """
            -- Full-text search over everything readable: titles, summaries, note bodies, the
            -- text extracted from documents, annotation text and link descriptions.
            --
            -- Deliberately not a contentless table. Contentless FTS5 stores no copy of the text
            -- and refuses DELETE, which would leave trashed objects findable in search. Carrying
            -- our own copy costs disk that the encrypted database already accounts for.
            CREATE VIRTUAL TABLE objects_fts USING fts5(
                object_id UNINDEXED,
                title,
                summary,
                body,
                tags
            );
            """),

        (3, """
            -- Pairing a phone establishes a shared secret rather than a public key: the two
            -- devices are the only parties, and there is no third one to verify a signature for.
            -- The column is separate from public_key so that neither is ever mistaken for the
            -- other. It is only ever written to a database that is already encrypted.
            ALTER TABLE devices ADD COLUMN shared_key TEXT;
            """),
    ];

    public static int LatestVersion => Steps[^1].Version;

    public static async Task ApplyAsync(CampusDatabase database, CancellationToken ct = default)
    {
        var current = await database.ScalarAsync<long>("PRAGMA user_version;", ct).ConfigureAwait(false);

        foreach (var (version, sql) in Steps)
        {
            if (version <= current) continue;

            await database.InTransactionAsync(async () =>
            {
                await database.ExecuteAsync(sql, ct).ConfigureAwait(false);
                // PRAGMA cannot be parameterised, and the value is an int from this file.
                await database.ExecuteAsync($"PRAGMA user_version = {version};", ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
    }
}
