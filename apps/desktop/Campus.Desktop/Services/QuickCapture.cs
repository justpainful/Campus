using Campus.Desktop.Design.Emoji;
using Campus.Desktop.Design.Icons;
using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Campus.Desktop.Services;

/// <summary>
/// Capturing something in a hurry. The only required field is the sentence itself; everything
/// else is optional, because a capture that demands a subject and a date is a capture that does
/// not happen during a lesson.
/// </summary>
public static class QuickCapture
{
    private static readonly (ObjectKind Kind, string Label, string Symbol)[] Kinds =
    [
        (ObjectKind.InboxItem, "Inbox", CampusSymbols.Inbox),
        (ObjectKind.Task, "Task", CampusSymbols.Tasks),
        (ObjectKind.Note, "Note", CampusSymbols.Notes),
        (ObjectKind.Assignment, "Assignment", CampusSymbols.Assignments),
        (ObjectKind.Requirement, "Requirement", CampusSymbols.Requirements),
        (ObjectKind.Link, "Link", CampusSymbols.Link),
    ];

    /// <summary>Shows the capture sheet. Returns the saved object, or null if it was cancelled.</summary>
    public static async Task<CampusObject?> ShowAsync(
        XamlRoot root, ObjectKind kind = ObjectKind.InboxItem, string? initialText = null)
    {
        var workspace = App.GetService<WorkspaceService>();
        if (!workspace.IsUnlocked) return null;

        var subjects = await workspace.Objects.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Subject },
            Sort = SortField.Manual,
            Descending = false,
        });

        var body = new StackPanel { Spacing = 12, MinWidth = 420 };

        var text = new TextBox
        {
            PlaceholderText = "What is it?",
            Text = initialText ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 72,
            Style = (Style)Application.Current.Resources["Input.Text"],
        };
        // The text field and the emoji button share a row so the button never pushes the
        // field narrower than it needs to be.
        var textRow = new Grid();
        textRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        textRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        textRow.Children.Add(text);

        var emojiButton = EmojiFlyout.CreateButton(text);
        emojiButton.VerticalAlignment = VerticalAlignment.Bottom;
        emojiButton.Margin = new Thickness(4, 0, 0, 4);
        Grid.SetColumn(emojiButton, 1);
        textRow.Children.Add(emojiButton);

        body.Children.Add(textRow);

        var kindChoice = new ComboBox { MinWidth = 170, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (value, label, _) in Kinds)
            kindChoice.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        kindChoice.SelectedIndex = Math.Max(0, Array.FindIndex(Kinds, k => k.Kind == kind));
        AutomationProperties.SetName(kindChoice, "Kind");

        var subjectChoice = new ComboBox { MinWidth = 170, HorizontalAlignment = HorizontalAlignment.Stretch };
        subjectChoice.Items.Add(new ComboBoxItem { Content = "No subject", Tag = null });
        foreach (var subject in subjects)
            subjectChoice.Items.Add(new ComboBoxItem { Content = subject.Title, Tag = subject.Id });
        subjectChoice.SelectedIndex = 0;
        AutomationProperties.SetName(subjectChoice, "Subject");

        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(kindChoice, 0);
        Grid.SetColumn(subjectChoice, 1);
        row.Children.Add(kindChoice);
        row.Children.Add(subjectChoice);
        body.Children.Add(row);

        var due = new CalendarDatePicker
        {
            PlaceholderText = "No date",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(due, "Due date");
        body.Children.Add(due);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Quick capture",
            Content = body,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        // Focus lands in the text box so typing can start immediately.
        dialog.Opened += (_, _) => text.Focus(FocusState.Programmatic);

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        var title = text.Text.Trim();
        if (title.Length == 0) return null;

        var chosenKind = (ObjectKind)((ComboBoxItem)kindChoice.SelectedItem).Tag;
        var subjectId = ((ComboBoxItem)subjectChoice.SelectedItem).Tag as CampusId?;

        var captured = new CampusObject
        {
            Kind = chosenKind,
            Title = FirstLine(title),
            SubjectId = subjectId,
            DueAt = due.Date,
            Status = chosenKind == ObjectKind.Note ? ObjectStatus.None : ObjectStatus.NotStarted,
            Source = CaptureSource.QuickCapture,
            SourceDeviceId = workspace.DeviceId,
            Payload = BuildPayload(chosenKind, title),
        };

        await workspace.Objects.SaveAsync(captured);
        return captured;
    }

    /// <summary>
    /// The title is the first line; anything after it becomes the body, so pasting a paragraph
    /// keeps its detail instead of turning into a very long title.
    /// </summary>
    private static string FirstLine(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        var line = index < 0 ? text : text[..index];
        return line.Length > 140 ? line[..140].TrimEnd() + "…" : line;
    }

    private static string? Remainder(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        if (index < 0) return null;
        var rest = text[index..].Trim();
        return rest.Length == 0 ? null : rest;
    }

    private static IObjectPayload? BuildPayload(ObjectKind kind, string text) => kind switch
    {
        ObjectKind.InboxItem => new InboxPayload { RawText = text },
        ObjectKind.Note => new NotePayload { Body = Remainder(text) ?? text },
        ObjectKind.Task => new TaskPayload { Notes = Remainder(text) },
        ObjectKind.Assignment => new AssignmentPayload { Instructions = Remainder(text) },
        ObjectKind.Requirement => new RequirementPayload { Action = Remainder(text) },
        ObjectKind.Link => new LinkPayload
        {
            Url = ExtractUrl(text) ?? string.Empty,
            Domain = DomainOf(ExtractUrl(text)),
            Provider = ProviderOf(ExtractUrl(text)),
        },
        _ => null,
    };

    private static string? ExtractUrl(string text)
    {
        foreach (var token in text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Uri.TryCreate(token, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return token;
        }
        return null;
    }

    private static string? DomainOf(string? url)
        => url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    /// <summary>
    /// Recognises the handful of places school links actually come from, so a YouTube
    /// explanation and a Telegram group are not both filed as "a web page".
    /// </summary>
    private static LinkProvider ProviderOf(string? url)
    {
        var host = DomainOf(url)?.ToLowerInvariant();
        if (host is null) return LinkProvider.Generic;

        if (host.Contains("youtube.") || host == "youtu.be") return LinkProvider.YouTube;
        if (host is "t.me" or "telegram.me" || host.EndsWith(".t.me", StringComparison.Ordinal))
            return LinkProvider.Telegram;
        if (host.Contains("classroom.google.")) return LinkProvider.GoogleClassroom;
        if (host.Contains("drive.google.")) return LinkProvider.GoogleDrive;
        if (host.Contains("madrasati")) return LinkProvider.Madrasati;
        return LinkProvider.Generic;
    }
}
