using Campus.Documents;
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

        // Adds one message. The times run backwards from now so the runs group the way they
        // would in life: two sentences from a teacher a minute apart are one run, an answer an
        // hour later is not.
        async Task AddMessage(
            CampusObject conversation, Speaker from, string body, int minutesAgo,
            bool markdown = false)
        {
            var at = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);
            var line = body.Split('\n')[0].Trim();

            await Add(new CampusObject
            {
                Kind = ObjectKind.Message,
                Title = line.Length > 90 ? line[..90] + "…" : line,
                ParentId = conversation.Id,
                SubjectId = conversation.SubjectId,
                CreatedAt = at,
                Payload = new MessagePayload
                {
                    From = from,
                    Body = body,
                    IsMarkdown = markdown,
                    SentAt = at,
                },
            });
        }

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

        // ---- a board with a thread and an answer, because a forum with nothing in it shows
        // nothing about whether a forum works.
        var board = new CampusObject
        {
            Kind = ObjectKind.Board,
            Title = "Physics questions",
            SubjectId = Subject("Physics"),
            Payload = new BoardPayload { Description = "Things worth writing down once" },
        };
        await Add(board);

        var thread = new CampusObject
        {
            Kind = ObjectKind.Thread,
            Title = "Why does the sign flip when the charge is negative?",
            ParentId = board.Id,
            SubjectId = Subject("Physics"),
            Status = ObjectStatus.InProgress,
            Payload = new ThreadPayload
            {
                Body = "The force comes out the other way and I keep losing the minus sign "
                     + "halfway through. Where exactly does it come from?",
                MessageCount = 1,
                LastActivityAt = DateTimeOffset.UtcNow,
            },
        };
        await Add(thread);

        await Add(new CampusObject
        {
            Kind = ObjectKind.Note,
            Title = "It is in the definition of the field",
            ParentId = thread.Id,
            SubjectId = Subject("Physics"),
            Payload = new NotePayload
            {
                Body = "**E** is defined as force per unit *positive* charge.\n\n"
                     + "So for a negative charge the force is opposite to **E** — the minus sign "
                     + "is in `q`, not in the field.\n\n"
                     + "- Positive charge: force along the field\n"
                     + "- Negative charge: force against it\n",
            },
        });

        // ---- two recorded conversations: one with a teacher, one with an assistant. Both are
        // here because the whole point of the feature is that the two sides are drawn
        // differently, and one of each is the only way to see whether that is true.
        var corridor = new CampusObject
        {
            Kind = ObjectKind.Conversation,
            Title = "Whether the physics test moved",
            SubjectId = Subject("Physics"),
            Payload = new ConversationPayload
            {
                ConversationKind = ConversationKind.Teacher,
                With = "Mr Faisal",
                MessageCount = 4,
                LastActivityAt = DateTimeOffset.UtcNow.AddHours(-3),
            },
        };
        await Add(corridor);

        await AddMessage(corridor, Speaker.Me,
            "Sir, is the test still on Sunday?", 200);
        await AddMessage(corridor, Speaker.Them,
            "It moved to Tuesday. The lab is being used on Sunday.", 198);
        await AddMessage(corridor, Speaker.Them,
            "Chapters 4 and 5 only. Nothing from 6.", 197);
        await AddMessage(corridor, Speaker.Me,
            "Does that include the derivations?", 190);

        var asked = new CampusObject
        {
            Kind = ObjectKind.Conversation,
            Title = "Balancing redox equations",
            SubjectId = Subject("Chemistry"),
            Payload = new ConversationPayload
            {
                ConversationKind = ConversationKind.Assistant,
                With = "ChatGPT",
                MessageCount = 3,
                LastActivityAt = DateTimeOffset.UtcNow.AddMinutes(-40),
            },
        };
        await Add(asked);

        await AddMessage(asked, Speaker.Me,
            "How do I balance a redox equation in acidic solution? I keep getting the electrons "
            + "wrong.", 60);

        // Joined lines rather than a raw string literal, because this whole file sits inside
        // `#if DEBUG` and a release build still scans the region for preprocessor directives —
        // where a line starting with `#` is one, string or not. A markdown heading is such a line.
        await AddMessage(asked, Speaker.Them, string.Join('\n',
        [
            "## The half-reaction method",
            "",
            "Split the reaction into two halves, balance each one on its own, then put them back "
            + "together. The order matters:",
            "",
            "1. Balance every atom **except** oxygen and hydrogen.",
            "2. Balance oxygen by adding `H2O`.",
            "3. Balance hydrogen by adding `H+`.",
            "4. Balance charge by adding electrons.",
            "",
            "### Worked example",
            "",
            "For the oxidation half:",
            "",
            "```text",
            "Fe2+  ->  Fe3+  +  e-",
            "```",
            "",
            "And the reduction half, which is where the water and the protons appear:",
            "",
            "```text",
            "MnO4-  +  8 H+  +  5 e-  ->  Mn2+  +  4 H2O",
            "```",
            "",
            "| Step | What you add | Why |",
            "|------|--------------|-----|",
            "| 2    | Water        | Oxygen has nowhere else to go |",
            "| 3    | Protons      | The solution is acidic |",
            "| 4    | Electrons    | The charges have to match |",
            "",
            "> The electrons have to cancel exactly when the halves are added. If they do not, "
            + "multiply one half through until they do.",
            "",
            "So multiply the iron half by five and add:",
            "",
            "```text",
            "5 Fe2+  +  MnO4-  +  8 H+  ->  5 Fe3+  +  Mn2+  +  4 H2O",
            "```",
            "",
            "- [x] Atoms balanced",
            "- [x] Charge balanced",
            "- [ ] Try it on dichromate",
        ]), 58, markdown: true);

        await AddMessage(asked, Speaker.Me, "And in basic solution?", 40);

        // ---- goals, which are the slow things
        await Add(new CampusObject
        {
            Kind = ObjectKind.Goal,
            Title = "Finish the physics textbook before the exam",
            SubjectId = Subject("Physics"),
            Status = ObjectStatus.InProgress,
            Payload = new GoalPayload
            {
                Detail = "Two chapters a week is enough",
                TargetDate = Day(30),
                Progress = 0.4,
                Steps =
                {
                    new ChecklistItem { Text = "Chapter 1 — motion", Done = true, SortOrder = 0 },
                    new ChecklistItem { Text = "Chapter 2 — forces", Done = true, SortOrder = 1 },
                    new ChecklistItem { Text = "Chapter 3 — energy", SortOrder = 2 },
                    new ChecklistItem { Text = "Chapter 4 — fields", SortOrder = 3 },
                    new ChecklistItem { Text = "Past papers", SortOrder = 4 },
                },
            },
        });

        await Add(new CampusObject
        {
            Kind = ObjectKind.Goal,
            Title = "Stop leaving revision to the night before",
            Status = ObjectStatus.InProgress,
            Payload = new GoalPayload { Detail = "One hour a day, whatever else happens", Progress = 0.2 },
        });

        // ---- a timetable, so the planner has lessons as well as deadlines
        var timetable = new (string Subject, DayOfWeek Day, int Hour, int Minute, string Room)[]
        {
            ("Mathematics", DayOfWeek.Sunday, 8, 0, "B2"),
            ("English", DayOfWeek.Sunday, 9, 0, "A1"),
            ("Physics", DayOfWeek.Monday, 8, 0, "Lab 1"),
            ("Chemistry", DayOfWeek.Monday, 10, 0, "Lab 2"),
            ("Biology", DayOfWeek.Tuesday, 8, 0, "Lab 3"),
            ("Mathematics", DayOfWeek.Tuesday, 11, 0, "B2"),
            ("English", DayOfWeek.Wednesday, 8, 0, "A1"),
            ("Physics", DayOfWeek.Wednesday, 9, 0, "Lab 1"),
            ("Environmental Science", DayOfWeek.Thursday, 8, 0, "A4"),
            ("Chemistry", DayOfWeek.Thursday, 10, 0, "Lab 2"),
        };

        foreach (var slot in timetable)
        {
            await workspace.Schedule.SaveAsync(new ScheduleSlot
            {
                SubjectId = Subject(slot.Subject),
                Day = slot.Day,
                Start = new TimeOnly(slot.Hour, slot.Minute),
                End = new TimeOnly(slot.Hour, slot.Minute).AddMinutes(45),
                Room = slot.Room,
                AcademicYear = DateTimeOffset.Now.Year,
            }, ct).ConfigureAwait(false);
        }

        await ImportSampleFilesAsync(workspace, Subject, ct).ConfigureAwait(false);

        await Add(new CampusObject
        {
            Kind = ObjectKind.Exam,
            Title = "Mathematics exam",
            SubjectId = Subject("Mathematics"),
            DueAt = Day(8, 8),
            Payload = new ExamPayload { ScheduledAt = Day(8, 8), Scope = "Chapters 1 to 4", MaxScore = 40 },
        });
    }

    /// <summary>
    /// Puts real files through the real import pipeline — identify, hash, encrypt, extract text,
    /// thumbnail, index. A sample workspace whose files were faked would prove nothing about the
    /// part of Campus most likely to be broken.
    /// </summary>
    private static async Task ImportSampleFilesAsync(
        WorkspaceService workspace, Func<string, CampusId> subject, CancellationToken ct)
    {
        var staging = Path.Combine(Path.GetTempPath(), "campus-sample-files");
        Directory.CreateDirectory(staging);

        var pdf = Path.Combine(staging, "Physics — fields and forces.pdf");
        await File.WriteAllBytesAsync(pdf, SamplePdf.Create(
            "Fields and forces",
            [
                "The electric field E at a point is defined as the force per unit positive charge "
                + "placed at that point. It is a vector, and it points away from positive charge "
                + "and towards negative charge.",

                "A charge q placed in a field E experiences a force F = qE. When q is negative the "
                + "force is opposite to the field, which is where the sign that everyone loses "
                + "actually comes from.",

                "Work done moving a charge between two points does not depend on the path taken. "
                + "That is what makes potential a useful idea at all.",
            ]), ct).ConfigureAwait(false);

        // Written as joined lines rather than as a raw string literal: everything in this file
        // sits inside `#if DEBUG`, and in a release build the compiler scans that region for
        // preprocessor directives — where a line beginning with `#` is one, string or not.
        var notes = Path.Combine(staging, "Chemistry — bonding.md");
        await File.WriteAllTextAsync(notes, string.Join('\n',
        [
            "# Bonding",
            "",
            "Two ways atoms end up sharing the electrons they need.",
            "",
            "## Ionic",
            "",
            "One atom **gives** an electron to another. Metals and non-metals.",
            "",
            "- Sodium gives, chlorine takes",
            "- The result is two ions that attract each other",
            "",
            "## Covalent",
            "",
            "Both atoms **share** a pair. Non-metals with non-metals.",
            "",
            "> A double bond is two shared pairs, not one stronger pair.",
            "",
            "| Bond     | Electrons | Between               |",
            "|----------|-----------|-----------------------|",
            "| Ionic    | Given     | Metal + non-metal     |",
            "| Covalent | Shared    | Non-metal + non-metal |",
            "",
            "- [x] Chapter read",
            "- [ ] Questions 1 to 12",
        ]), ct).ConfigureAwait(false);

        var vocabulary = Path.Combine(staging, "English — Connect vocabulary.txt");
        await File.WriteAllTextAsync(vocabulary, string.Join(Environment.NewLine,
        [
            "Unit 4 vocabulary",
            "",
            "acquire      to get something, often over time",
            "consequence  what follows from something else",
            "deliberate   done on purpose",
            "essential    cannot be done without",
            "gradual      happening slowly, in stages",
            "reluctant    unwilling, but doing it anyway",
            "sufficient   enough",
        ]), ct).ConfigureAwait(false);

        // A picture, made by rendering a page of the sample PDF: a photograph of a board is what
        // a student actually attaches to a message, and writing a PNG encoder to produce one
        // would be a lot of work to end up with a coloured rectangle.
        var photo = Path.Combine(staging, "Board — the reduction half.png");
        await using (var source = File.OpenRead(pdf))
        {
            var png = PdfRenderer.RenderPage(source, 1, 900);
            if (png is not null) await File.WriteAllBytesAsync(photo, png, ct).ConfigureAwait(false);
        }

        var import = App.GetService<ImportService>();

        await import.ImportAsync([pdf], subject("Physics"), ["textbook"], ct).ConfigureAwait(false);
        await import.ImportAsync([notes], subject("Chemistry"), ["notes"], ct).ConfigureAwait(false);
        await import.ImportAsync([vocabulary], subject("English"), ["vocabulary"], ct).ConfigureAwait(false);

        if (File.Exists(photo))
        {
            var imported = await import.ImportAsync([photo], subject("Chemistry"), ["board"], ct)
                .ConfigureAwait(false);

            if (imported.FirstOrDefault()?.Created is { } picture)
                await AttachToLastMessageAsync(workspace, picture, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Hangs a picture on the first message in the assistant conversation.
    ///
    /// Done here rather than when the message was written because the file has to exist in the
    /// workspace first, and importing it is the last thing that happens.
    /// </summary>
    private static async Task AttachToLastMessageAsync(
        WorkspaceService workspace, CampusObject picture, CancellationToken ct)
    {
        var messages = await workspace.Objects.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Message },
            Sort = SortField.CreatedAt,
            Descending = false,
        }, ct).ConfigureAwait(false);

        var last = messages.FirstOrDefault(m =>
            m.PayloadAs<MessagePayload>() is { From: Speaker.Me, IsMarkdown: false }
            && m.Title.StartsWith("How do I balance", StringComparison.Ordinal));
        if (last is null) return;

        var payload = last.PayloadAs<MessagePayload>()!;
        payload.Attachments.Add(picture.Id);
        last.Payload = payload;

        await workspace.Objects.SaveAsync(last, ct).ConfigureAwait(false);
        await workspace.Relations
            .LinkAsync(last.Id, picture.Id, RelationKind.Attachment, ct: ct)
            .ConfigureAwait(false);
    }

#else
    public const string Argument = "--dev-workspace";
    public static bool Requested => false;
    public static string Root => string.Empty;
    public static Task PrepareAsync(WorkspaceService workspace, CancellationToken ct = default)
        => Task.CompletedTask;
#endif
}
