namespace Campus.Domain;

/// <summary>Every first-class thing in Campus is a CampusObject with one of these kinds.</summary>
public enum ObjectKind
{
    Unknown = 0,
    InboxItem = 1,
    File = 2,
    Note = 3,
    Task = 4,
    Assignment = 5,
    Requirement = 6,
    Link = 7,
    Book = 8,
    Lesson = 9,
    Exam = 10,
    Project = 11,
    Topic = 12,
    Question = 13,
    Collection = 14,
    Board = 15,
    Thread = 16,
    PrintJob = 17,
    Goal = 18,
    Subject = 19,
    Event = 20,
    Person = 21,
    Conversation = 22,
    Message = 23,
}

/// <summary>Who a recorded conversation was with. Decides how the other side's words are drawn.</summary>
public enum ConversationKind
{
    /// <summary>A teacher, in a lesson or afterwards. Written down so it is not lost by Thursday.</summary>
    Teacher = 0,

    /// <summary>ChatGPT or another assistant. Its answers arrive as markdown and are drawn as markdown.</summary>
    Assistant = 1,

    /// <summary>A classmate, a study group, anyone else who explained something.</summary>
    Classmate = 2,

    Other = 3,
}

/// <summary>Which side of a conversation a message came from.</summary>
public enum Speaker
{
    /// <summary>The person whose workspace this is.</summary>
    Me = 0,

    /// <summary>Whoever the conversation is with.</summary>
    Them = 1,
}

/// <summary>Workflow state shared across kinds. Not every kind uses every value.</summary>
public enum ObjectStatus
{
    None = 0,
    NotStarted = 1,
    InProgress = 2,
    Completed = 3,
    Blocked = 4,
    Waiting = 5,
    Cancelled = 6,
    Archived = 7,
}

public enum Priority
{
    None = 0,
    Low = 1,
    Normal = 2,
    High = 3,
    Urgent = 4,
}

/// <summary>Broad content family, decided by the import pipeline from magic bytes + extension.</summary>
public enum MediaKind
{
    Unknown = 0,
    Document = 1,
    Pdf = 2,
    Image = 3,
    Video = 4,
    Audio = 5,
    Spreadsheet = 6,
    Presentation = 7,
    Text = 8,
    Markdown = 9,
    Archive = 10,
    Web = 11,
}

public enum LinkProvider
{
    Generic = 0,
    YouTube = 1,
    Telegram = 2,
    GoogleClassroom = 3,
    GoogleDrive = 4,
    Madrasati = 5,
    SchoolPortal = 6,
}

public enum PrintState
{
    ToPrint = 0,
    Printed = 1,
    Archived = 2,
}

public enum PrintColorMode
{
    BlackAndWhite = 0,
    Color = 1,
}

public enum RelationKind
{
    Related = 0,
    Reference = 1,
    Attachment = 2,
    Solution = 3,
    Source = 4,
    Explains = 5,
    PartOf = 6,
    Supersedes = 7,
    Backlink = 8,
}

public enum TermKind
{
    Term1 = 1,
    Term2 = 2,
    Term3 = 3,
    Summer = 4,
    FullYear = 5,
}

public enum NoteKind
{
    Quick = 0,
    Lesson = 1,
    Daily = 2,
    Pinned = 3,
    Scratchpad = 4,
}

public enum CaptureSource
{
    Desktop = 0,
    QuickCapture = 1,
    Import = 2,
    Phone = 3,
    ShareExtension = 4,
    Scanner = 5,
    Extension = 6,
}
