using Campus.Domain;

namespace Campus.Desktop.Services;

/// <summary>
/// A throwaway workspace for development and screenshots, created with <c>--dev-workspace</c>.
///
/// It lives in its own directory, never touches the real vault, keeps its recovery key in a file
/// beside it so it can be reopened without a prompt, and fills itself with sample content. All of
/// that is unacceptable for a real workspace, which is why the whole thing is compiled out of
/// release builds rather than merely hidden behind a flag.
/// </summary>
public static class DeveloperWorkspace
{
#if DEBUG
    public const string Argument = "--dev-workspace";

    public static bool Requested =>
        Environment.GetCommandLineArgs().Any(a => string.Equals(a, Argument, StringComparison.Ordinal));

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Campus", "DevWorkspace");

    private static string RecoveryKeyPath => Path.Combine(Root, "dev-recovery-key.txt");

    /// <summary>Opens the development workspace, creating and populating it on first use.</summary>
    public static async Task PrepareAsync(WorkspaceService workspace, CancellationToken ct = default)
    {
        if (!workspace.IsInitialised)
        {
            var recoveryKey = await workspace.CreateAsync(ct).ConfigureAwait(false);
            Directory.CreateDirectory(Root);
            await File.WriteAllTextAsync(RecoveryKeyPath, recoveryKey, ct).ConfigureAwait(false);
            await PopulateAsync(workspace, ct).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(RecoveryKeyPath)) return;

        var stored = await File.ReadAllTextAsync(RecoveryKeyPath, ct).ConfigureAwait(false);
        await workspace.UnlockWithRecoveryKeyAsync(stored.Trim(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sample content that looks like a real school week, so layouts are exercised against
    /// realistic titles and dates rather than "Test 1".
    /// </summary>
    private static async Task PopulateAsync(WorkspaceService workspace, CancellationToken ct)
    {
        var repository = workspace.Objects;

        var subjects = await repository.QueryAsync(
            new CampusQuery { Kinds = { ObjectKind.Subject } }, ct).ConfigureAwait(false);
        CampusId Subject(string name) =>
            subjects.FirstOrDefault(s => s.Title == name)?.Id ?? subjects[0].Id;

        var today = DateTimeOffset.Now.Date;
        DateTimeOffset Day(int offset, int hour = 8) =>
            new DateTimeOffset(today, DateTimeOffset.Now.Offset).AddDays(offset).AddHours(hour);

        async Task Add(CampusObject entity) =>
            await repository.SaveAsync(entity, ct).ConfigureAwait(false);

        await Add(new CampusObject
        {
            Kind = ObjectKind.Assignment,
            Title = "English Workbook, page 220",
            SubjectId = Subject("English"),
            DueAt = Day(1, 8),
            Status = ObjectStatus.InProgress,
            Priority = Priority.High,
            Payload = new AssignmentPayload { Teacher = "Mr Salem", Points = 10, Instructions = "Exercises A to C" },
            Tags = { "homework" },
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.Assignment,
            Title = "Chemistry Homework, Chapter 2",
            SubjectId = Subject("Chemistry"),
            DueAt = Day(2, 8),
            Status = ObjectStatus.NotStarted,
            Payload = new AssignmentPayload { Teacher = "Mr Nasser", Points = 15 },
            Tags = { "homework" },
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.Assignment,
            Title = "Biology worksheet",
            SubjectId = Subject("Biology"),
            DueAt = Day(-2, 8),
            Status = ObjectStatus.NotStarted,
            Payload = new AssignmentPayload { Teacher = "Ms Huda", Points = 5 },
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.Task,
            Title = "Review Connect vocabulary",
            SubjectId = Subject("English"),
            DueAt = Day(0, 17),
            Priority = Priority.Normal,
            Payload = new TaskPayload
            {
                Checklist =
                {
                    new ChecklistItem { Text = "Read Connect", Done = true },
                    new ChecklistItem { Text = "Complete exercise A", Done = true },
                    new ChecklistItem { Text = "Review vocabulary" },
                },
            },
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.Task,
            Title = "Ask about the physics mark",
            SubjectId = Subject("Physics"),
            DueAt = Day(0, 9),
            Priority = Priority.Urgent,
            Payload = new TaskPayload(),
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.Task,
            Title = "Redo the graph from Monday",
            SubjectId = Subject("Mathematics"),
            DueAt = Day(3),
            Payload = new TaskPayload(),
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.Requirement,
            Title = "Bring the chemistry notebook",
            SubjectId = Subject("Chemistry"),
            DueAt = Day(2, 7),
            Status = ObjectStatus.NotStarted,
            Payload = new RequirementPayload { Action = "Bring", Teacher = "Mr Nasser" },
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.Requirement,
            Title = "Print the biology worksheet",
            SubjectId = Subject("Biology"),
            DueAt = Day(1, 7),
            Payload = new RequirementPayload { Action = "Print", RequiresPrinting = true },
            Tags = { "print" },
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.Book,
            Title = "MegaGoal 1",
            SubjectId = Subject("English"),
            Status = ObjectStatus.InProgress,
            OpenedAt = DateTimeOffset.UtcNow.AddHours(-3),
            Payload = new BookPayload { Author = "McGraw Hill", TotalPages = 280, CurrentPage = 220 },
            Tags = { "textbook", "exam-source" },
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.Book,
            Title = "MegaGoal 1 — Solved",
            SubjectId = Subject("English"),
            Payload = new BookPayload { IsSolutionBook = true, TotalPages = 190 },
            Tags = { "solved" },
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.Note,
            Title = "Unit 1 — Big Changes",
            SubjectId = Subject("English"),
            Payload = new NotePayload
            {
                NoteKind = NoteKind.Lesson,
                Body = "The present perfect connects a past action to now. "
                     + "Use it for experience, change over time and unfinished actions.",
            },
            IsPinned = true,
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.Note,
            Title = "Chemistry chapter 2 summary",
            SubjectId = Subject("Chemistry"),
            Payload = new NotePayload { Body = "Ionic bonds transfer electrons; covalent bonds share them." },
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.Link,
            Title = "Present perfect explained",
            SubjectId = Subject("English"),
            Payload = new LinkPayload
            {
                Url = "https://www.youtube.com/watch?v=example",
                Provider = LinkProvider.YouTube,
                Domain = "youtube.com",
                Description = "12 minute explanation with examples",
            },
            IsPinned = true,
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.Link,
            Title = "Chemistry group",
            SubjectId = Subject("Chemistry"),
            Payload = new LinkPayload { Url = "https://t.me/example", Provider = LinkProvider.Telegram, Domain = "t.me" },
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.PrintJob,
            Title = "Biology worksheet",
            SubjectId = Subject("Biology"),
            Payload = new PrintJobPayload { Pages = 4, State = PrintState.ToPrint, DoubleSided = true },
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.PrintJob,
            Title = "English homework",
            SubjectId = Subject("English"),
            Payload = new PrintJobPayload { Pages = 2, State = PrintState.ToPrint },
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.InboxItem,
            Title = "Chemistry teacher said bring the notebook Wednesday",
            Source = CaptureSource.Phone,
            Payload = new InboxPayload { RawText = "استاذ الكيمياء قال نجيب الدفتر الاربعاء" },
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.InboxItem,
            Title = "Check whether the physics test moved",
            Source = CaptureSource.QuickCapture,
            Payload = new InboxPayload(),
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.Exam,
            Title = "Mathematics exam",
            SubjectId = Subject("Mathematics"),
            DueAt = Day(8, 8),
            Payload = new ExamPayload { ScheduledAt = Day(8, 8), Scope = "Chapters 1 to 4", MaxScore = 40 },
        });
    }
#else
    public const string Argument = "--dev-workspace";
    public static bool Requested => false;
    public static string Root => string.Empty;
    public static Task PrepareAsync(WorkspaceService workspace, CancellationToken ct = default)
        => Task.CompletedTask;
#endif
}
