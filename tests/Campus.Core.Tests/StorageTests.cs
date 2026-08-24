using System.Text;
using Campus.Domain;
using Campus.Storage;
using Campus.Vault;
using Xunit;

namespace Campus.Core.Tests;

/// <summary>
/// Exercises the workspace database against a real encrypted file: real SQLCipher, real FTS5,
/// real JSON payloads.
/// </summary>
public sealed class StorageTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "campus-tests", Guid.NewGuid().ToString("N"));
    private CampusVault _vault = null!;
    private CampusDatabase _database = null!;
    private ObjectRepository _objects = null!;

    public async Task InitializeAsync()
    {
        _vault = new CampusVault(new VaultPaths(_root));
        await _vault.CreateAsync();

        _database = new CampusDatabase(_vault.Paths.Database);
        await _database.OpenAsync(_vault.Keys);
        _objects = new ObjectRepository(_database, "test-device");
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        _vault.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static CampusObject NewTask(string title, DateTimeOffset? due = null) => new()
    {
        Kind = ObjectKind.Task,
        Title = title,
        DueAt = due,
        Payload = new TaskPayload(),
    };

    [Fact]
    public async Task Schema_is_created_at_the_latest_version()
    {
        var version = await _database.ScalarAsync<long>("PRAGMA user_version;");
        Assert.Equal(Migrations.LatestVersion, (int)version);
    }

    [Fact]
    public async Task The_database_file_is_encrypted_on_disk()
    {
        await _objects.SaveAsync(NewTask("Chemistry homework"));
        await _database.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);");

        // The connection still holds the file, so it is read with sharing rather than closed —
        // the point of the test is what is on disk while the database is live.
        byte[] bytes;
        await using (var stream = new FileStream(
            _vault.Paths.Database, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes);
        }
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("Chemistry homework", text, StringComparison.Ordinal);
        // A plain SQLite file starts with this header; an encrypted one must not.
        Assert.DoesNotContain("SQLite format 3", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_saved_object_round_trips_with_its_payload()
    {
        var assignment = new CampusObject
        {
            Kind = ObjectKind.Assignment,
            Title = "Physics Homework - Chapter 2",
            DueAt = DateTimeOffset.UtcNow.AddDays(3),
            Priority = Priority.High,
            Payload = new AssignmentPayload { Teacher = "Mr Ali", Points = 20, Instructions = "Questions 1-14" },
        };
        assignment.Tags.Add("homework");
        assignment.Tags.Add("exam-source");

        await _objects.SaveAsync(assignment);
        var loaded = await _objects.GetAsync(assignment.Id);

        Assert.NotNull(loaded);
        Assert.Equal(assignment.Title, loaded.Title);
        Assert.Equal(Priority.High, loaded.Priority);
        Assert.Equal(["exam-source", "homework"], loaded.Tags.Order().ToArray());

        var payload = loaded.PayloadAs<AssignmentPayload>();
        Assert.NotNull(payload);
        Assert.Equal("Mr Ali", payload.Teacher);
        Assert.Equal(20, payload.Points);
    }

    [Fact]
    public async Task Queries_filter_by_kind_and_subject()
    {
        var english = new CampusObject { Kind = ObjectKind.Subject, Title = "English", Payload = new SubjectPayload() };
        await _objects.SaveAsync(english);

        var book = new CampusObject
        {
            Kind = ObjectKind.Book, Title = "MegaGoal 1", SubjectId = english.Id, Payload = new BookPayload(),
        };
        await _objects.SaveAsync(book);
        await _objects.SaveAsync(NewTask("Unrelated task"));

        var results = await _objects.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Book },
            SubjectIds = { english.Id },
        });

        Assert.Single(results);
        Assert.Equal("MegaGoal 1", results[0].Title);
    }

    [Fact]
    public async Task Relative_date_windows_are_resolved_at_query_time()
    {
        await _objects.SaveAsync(NewTask("Due today", DateTimeOffset.Now.AddHours(2)));
        await _objects.SaveAsync(NewTask("Due next month", DateTimeOffset.Now.AddDays(40)));
        await _objects.SaveAsync(NewTask("No date at all"));

        var soon = await _objects.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Task },
            Due = DateRange.Of(RelativeWindow.Next7Days),
        });

        Assert.Single(soon);
        Assert.Equal("Due today", soon[0].Title);
    }

    [Fact]
    public async Task Overdue_finds_only_past_dates()
    {
        await _objects.SaveAsync(NewTask("Late", DateTimeOffset.Now.AddDays(-2)));
        await _objects.SaveAsync(NewTask("Upcoming", DateTimeOffset.Now.AddDays(2)));

        var overdue = await _objects.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Task },
            Due = DateRange.Of(RelativeWindow.Overdue),
        });

        Assert.Single(overdue);
        Assert.Equal("Late", overdue[0].Title);
    }

    [Fact]
    public async Task Search_finds_text_inside_a_note_body()
    {
        var note = new CampusObject
        {
            Kind = ObjectKind.Note,
            Title = "Unit 1 notes",
            Payload = new NotePayload { Body = "The present perfect is used for actions with present relevance." },
        };
        await _objects.SaveAsync(note);
        await _objects.SaveAsync(NewTask("Something else entirely"));

        var hits = await _objects.QueryAsync(new CampusQuery { Text = "present perfect" });

        Assert.Single(hits);
        Assert.Equal(note.Id, hits[0].Id);
    }

    [Fact]
    public async Task Search_matches_partial_words_as_you_type()
    {
        await _objects.SaveAsync(new CampusObject
        {
            Kind = ObjectKind.Book, Title = "MegaGoal 1", Payload = new BookPayload(),
        });

        var hits = await _objects.QueryAsync(new CampusQuery { Text = "mega" });

        Assert.Single(hits);
    }

    [Fact]
    public async Task Trashed_objects_disappear_from_queries_and_from_search()
    {
        var task = NewTask("Buy a ruler");
        await _objects.SaveAsync(task);
        await _objects.TrashAsync(task.Id);

        Assert.Empty(await _objects.QueryAsync(new CampusQuery { Kinds = { ObjectKind.Task } }));
        Assert.Empty(await _objects.QueryAsync(new CampusQuery { Text = "ruler" }));

        var inTrash = await _objects.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Task }, IncludeTrashed = true,
        });
        Assert.Single(inTrash);
    }

    [Fact]
    public async Task Restoring_brings_an_object_back_to_search()
    {
        var task = NewTask("Buy a ruler");
        await _objects.SaveAsync(task);
        await _objects.TrashAsync(task.Id);
        await _objects.RestoreAsync(task.Id);

        Assert.Single(await _objects.QueryAsync(new CampusQuery { Text = "ruler" }));
    }

    [Fact]
    public async Task Emptying_the_trash_reports_bytes_nothing_references_any_more()
    {
        const string sharedHash = "aaaa1111";

        var kept = new CampusObject
        {
            Kind = ObjectKind.File, Title = "Kept", Payload = new FilePayload { ContentHash = sharedHash },
        };
        var alsoUsingTheSameBytes = new CampusObject
        {
            Kind = ObjectKind.File, Title = "Duplicate", Payload = new FilePayload { ContentHash = sharedHash },
        };
        var onlyCopy = new CampusObject
        {
            Kind = ObjectKind.File, Title = "Only copy", Payload = new FilePayload { ContentHash = "bbbb2222" },
        };

        await _objects.SaveAsync(kept);
        await _objects.SaveAsync(alsoUsingTheSameBytes);
        await _objects.SaveAsync(onlyCopy);

        await _objects.TrashAsync(alsoUsingTheSameBytes.Id);
        await _objects.TrashAsync(onlyCopy.Id);

        var orphaned = await _objects.EmptyTrashAsync();

        // The shared hash is still referenced by the object that was kept, so it must survive.
        Assert.Equal(["bbbb2222"], orphaned);
    }

    [Fact]
    public async Task Every_write_appends_to_the_change_journal()
    {
        var task = NewTask("Journal me");
        await _objects.SaveAsync(task);
        task.Title = "Journal me, edited";
        await _objects.SaveAsync(task);
        await _objects.TrashAsync(task.Id);

        var count = await _database.ScalarAsync<long>(
            $"SELECT COUNT(*) FROM journal WHERE entity_id = '{task.Id.Value}';");

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task Pinned_objects_sort_above_the_rest()
    {
        var ordinary = NewTask("Ordinary");
        var pinned = NewTask("Pinned");
        pinned.IsPinned = true;

        await _objects.SaveAsync(pinned);
        await Task.Delay(5);
        await _objects.SaveAsync(ordinary);   // saved later, so it would otherwise sort first

        var results = await _objects.QueryAsync(new CampusQuery { Kinds = { ObjectKind.Task } });

        Assert.Equal("Pinned", results[0].Title);
    }

    [Fact]
    public async Task Undated_items_sort_after_dated_ones_in_either_direction()
    {
        await _objects.SaveAsync(NewTask("No date"));
        await _objects.SaveAsync(NewTask("Has a date", DateTimeOffset.Now.AddDays(1)));

        var ascending = await _objects.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Task }, Sort = SortField.DueAt, Descending = false,
        });
        var descending = await _objects.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Task }, Sort = SortField.DueAt, Descending = true,
        });

        Assert.Equal("Has a date", ascending[0].Title);
        Assert.Equal("Has a date", descending[0].Title);
    }

    [Fact]
    public async Task Counting_matches_what_the_query_returns()
    {
        for (var i = 0; i < 7; i++) await _objects.SaveAsync(NewTask($"Task {i}"));

        var query = new CampusQuery { Kinds = { ObjectKind.Task } };

        Assert.Equal(7, await _objects.CountAsync(query));
        Assert.Equal(7, (await _objects.QueryAsync(query)).Count);
    }

    [Fact]
    public async Task A_wrong_key_cannot_open_the_database()
    {
        await _objects.SaveAsync(NewTask("Secret"));
        await _database.CloseAsync();

        // A second vault at a different location has a different master key entirely.
        var otherRoot = Path.Combine(_root, "other");
        using var otherVault = new CampusVault(new VaultPaths(otherRoot));
        await otherVault.CreateAsync();

        await using var wrong = new CampusDatabase(_vault.Paths.Database);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.OpenAsync(otherVault.Keys));
    }
}
