using Microsoft.UI.Xaml.Controls;

namespace Campus.Desktop.Views;

public sealed partial class PlaceholderPage : Page
{
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["Home"] = "Today's classes, what is due, what you were last reading, and everything waiting to be printed.",
        ["Inbox"] = "Anything captured in a hurry lands here until you decide what it actually is.",
        ["Subjects"] = "Every subject, with its own books, lessons, assignments and requirements.",
        ["Library"] = "Textbooks, solved books, references and explanations, kept together and searchable inside.",
        ["Notes"] = "Quick notes, lesson notes, daily notes and the scratchpad.",
        ["Assignments"] = "What was set, when it is due, who set it, and whether it has been handed in.",
        ["Tasks"] = "Today, upcoming, overdue and someday — with checklists and reminders.",
        ["Requirements"] = "The things you have to bring or prepare, before the day you need them.",
        ["Planner"] = "Day, week, month and term, with classes, exams and deadlines on one timeline.",
        ["Print Center"] = "A queue of what still needs printing, a record of what already was, and the page counts.",
        ["Links"] = "YouTube explanations, Telegram groups, school portals — saved with their titles and thumbnails.",
        ["Boards"] = "Each subject as a board of threads: a lesson, an assignment, a question you are still stuck on.",
        ["Search"] = "One search across file contents, notes, annotations, captions and metadata.",
        ["Sync"] = "Pair your iPhone once, then everything captured there arrives here.",
        ["Extensions"] = "Viewers, importers and tools, each running outside the app and asking for only what it needs.",
        ["Profile"] = "Your school year, subjects, goals and how you like to study.",
    };

    public PlaceholderPage(string title, string symbol)
    {
        InitializeComponent();
        TitleText.Text = title;
        Glyph.Symbol = symbol;
        DescriptionText.Text = Descriptions.TryGetValue(title, out var description)
            ? description
            : "This part of Campus is not built yet.";
    }
}
