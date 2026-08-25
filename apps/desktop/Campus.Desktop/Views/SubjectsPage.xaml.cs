using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Domain;
using Campus.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Campus.Desktop.Views;

/// <summary>
/// The subjects, and what is going on inside each one.
///
/// A card is not decoration here: what a student needs to know about a subject at a glance is
/// what is due, what is unread and when it next meets — so that is what the card says, read live
/// rather than stored. Opening one goes to everything that belongs to it.
/// </summary>
public sealed partial class SubjectsPage : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();

    public SubjectsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        Cards.Children.Clear();

        if (!_workspace.IsUnlocked)
        {
            EmptyState.Visibility = Visibility.Visible;
            return;
        }

        var subjects = await _workspace.Objects.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Subject },
            Sort = SortField.Manual,
            Descending = false,
        });

        EmptyState.Visibility = subjects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Subtitle.Text = subjects.Count switch
        {
            0 => "Nothing set up yet",
            1 => "1 subject",
            _ => $"{subjects.Count} subjects",
        };

        var slots = await _workspace.Schedule.AllAsync();

        foreach (var subject in subjects)
        {
            var counts = await CountsAsync(subject.Id);
            var next = NextMeeting(slots.Where(s => s.SubjectId == subject.Id).ToList());
            Cards.Children.Add(BuildCard(subject, counts, next));
        }
    }

    private sealed record SubjectCounts(int Due, int Files, int Notes, int Lessons);

    /// <summary>
    /// Four counts, four small queries. Counting in SQL rather than fetching rows to measure
    /// them keeps a page of six subjects from reading a term's worth of objects.
    /// </summary>
    private async Task<SubjectCounts> CountsAsync(CampusId subjectId)
    {
        async Task<int> Count(ObjectKind kind, Action<CampusQuery>? refine = null)
        {
            var query = new CampusQuery { Kinds = { kind }, SubjectIds = { subjectId } };
            refine?.Invoke(query);
            return await _workspace.Objects.CountAsync(query);
        }

        return new SubjectCounts(
            await Count(ObjectKind.Assignment, q =>
            {
                q.Statuses.Add(ObjectStatus.NotStarted);
                q.Statuses.Add(ObjectStatus.InProgress);
            }),
            await Count(ObjectKind.File),
            await Count(ObjectKind.Note),
            await Count(ObjectKind.Lesson));
    }

    /// <summary>
    /// When this subject next meets, counting from now. Wraps into next week when the last
    /// lesson of the week has already been and gone.
    /// </summary>
    private static string? NextMeeting(IReadOnlyList<ScheduleSlot> slots)
    {
        if (slots.Count == 0) return null;

        var now = DateTimeOffset.Now;
        var minutesNow = now.Hour * 60 + now.Minute;

        var best = slots
            .Select(slot =>
            {
                var days = ((int)slot.Day - (int)now.DayOfWeek + 7) % 7;
                // A slot earlier today has already happened, so it belongs to next week.
                if (days == 0 && slot.EndMinutes() <= minutesNow) days = 7;
                return (slot, offset: days * 1440 + slot.StartMinutes() - (days == 0 ? minutesNow : 0));
            })
            .OrderBy(x => x.offset)
            .First();

        // Parenthesised because `switch` binds tighter than `%`.
        var daysAway = ((int)best.slot.Day - (int)now.DayOfWeek + 7) % 7;
        var when = daysAway switch
        {
            0 => "today",
            1 => "tomorrow",
            _ => best.slot.Day.ToString(),
        };

        var room = best.slot.Room is { Length: > 0 } r ? $" · {r}" : "";
        return $"Next {when} at {best.slot.Start.ToString("HH:mm")}{room}";
    }

    // -------------------------------------------------------------------------- card

    private FrameworkElement BuildCard(CampusObject subject, SubjectCounts counts, string? next)
    {
        var payload = subject.PayloadAs<SubjectPayload>();
        var accent = (Brush)Application.Current.Resources[
            ThemeTokens.Subject.FromName(payload?.AccentName)];

        var body = new StackPanel { Spacing = 12 };

        // The accent belongs to the subject, so it is used as identity — a stripe and an icon —
        // and never as the colour of a control that does something.
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        header.Children.Add(new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.M"],
            Background = accent,
            Child = new CampusIcon
            {
                Symbol = payload?.IconName ?? CampusSymbols.Subjects,
                IconSize = 22,
                Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.OnAccent],
            },
        });

        var titles = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = subject.Title,
            Style = (Style)Application.Current.Resources["Text.Headline"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var detail = string.Join(" · ", new[] { payload?.Teacher, payload?.Room, payload?.Code }
            .Where(v => !string.IsNullOrWhiteSpace(v)));
        if (detail.Length > 0)
        {
            titles.Children.Add(new TextBlock
            {
                Text = detail,
                Style = (Style)Application.Current.Resources["Text.Footnote"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        header.Children.Add(titles);
        body.Children.Add(header);

        var stats = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        stats.Children.Add(Stat(counts.Due, "due", counts.Due > 0
            ? ThemeTokens.Warning.Primary
            : ThemeTokens.Label.Tertiary));
        stats.Children.Add(Stat(counts.Files, "files", ThemeTokens.Label.Tertiary));
        stats.Children.Add(Stat(counts.Notes, "notes", ThemeTokens.Label.Tertiary));
        stats.Children.Add(Stat(counts.Lessons, "lessons", ThemeTokens.Label.Tertiary));
        body.Children.Add(stats);

        if (next is not null)
        {
            var when = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            when.Children.Add(new CampusIcon
            {
                Symbol = CampusSymbols.Clock,
                IconSize = 14,
                Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Tertiary],
                VerticalAlignment = VerticalAlignment.Center,
            });
            when.Children.Add(new TextBlock
            {
                Text = next,
                Style = (Style)Application.Current.Resources["Text.Footnote"],
                VerticalAlignment = VerticalAlignment.Center,
            });
            body.Children.Add(when);
        }

        var card = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Plain"],
            Width = 280,
            Padding = new Thickness(16),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = (Brush)Application.Current.Resources[ThemeTokens.Surface.Primary],
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.Card"],
            Content = body,
        };

        AutomationProperties.SetName(card, subject.Title);
        card.Click += (_, _) => Frame?.Navigate(typeof(SubjectPage), subject.Id);
        card.RightTapped += (_, e) => ShowMenu(card, subject, e);

        return card;
    }

    private static FrameworkElement Stat(int value, string label, string token)
    {
        var stack = new StackPanel { Spacing = 0 };

        stack.Children.Add(new TextBlock
        {
            Text = value.ToString(),
            FontFamily = (FontFamily)Application.Current.Resources["Theme.Font.Text"],
            FontSize = 19,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources[
                value > 0 ? token : ThemeTokens.Label.Quaternary],
        });
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["Text.Caption"],
        });

        return stack;
    }

    private void ShowMenu(FrameworkElement anchor, CampusObject subject, RightTappedRoutedEventArgs e)
    {
        var menu = new MenuFlyout();

        menu.Items.Add(ObjectCommands.Item(L.T("open"), CampusSymbols.OpenExternal, () =>
        {
            Frame?.Navigate(typeof(SubjectPage), subject.Id);
            return Task.CompletedTask;
        }));

        menu.Items.Add(ObjectCommands.Item(L.T("rename"), CampusSymbols.Rename, async () =>
        {
            var title = await ObjectCommands.AskAsync(XamlRoot, L.T("rename.subject"), subject.Title);
            if (title is null) return;

            subject.Title = title;
            await _workspace.Objects.SaveAsync(subject);
            await ReloadAsync();
        }));

        menu.Items.Add(ColourSubmenu(subject));

        menu.Items.Add(new MenuFlyoutSeparator());

        var remove = ObjectCommands.Item(L.T("move.to.trash"), CampusSymbols.Trash, async () =>
        {
            if (!await ObjectCommands.ConfirmAsync(XamlRoot, L.T("move.this.subject.to.the.trash"),
                $"Everything filed under “{subject.Title}” stays where it is, but it will no "
                + "longer have a subject.", "Move to trash")) return;

            await _workspace.Objects.TrashAsync(subject.Id);
            await ReloadAsync();
        });
        remove.Foreground = (Brush)Application.Current.Resources[ThemeTokens.Destructive.Primary];
        menu.Items.Add(remove);

        menu.ShowAt(anchor, e.GetPosition(anchor));
        e.Handled = true;
    }

    /// <summary>
    /// The subject's colour, chosen from the named palette. A subject stores the name of an
    /// accent, never a colour value, so the same subject looks right in both themes.
    /// </summary>
    private MenuFlyoutSubItem ColourSubmenu(CampusObject subject)
    {
        var submenu = new MenuFlyoutSubItem { Text = L.T("colour") };

        foreach (var token in ThemeTokens.Subject.All)
        {
            var name = ThemeTokens.Subject.ToName(token);
            var item = new MenuFlyoutItem
            {
                Text = name,
                Icon = new IconSourceElement
                {
                    IconSource = new PathIconSource
                    {
                        Data = IconRegistry.Resolve(CampusSymbols.Circle, IconVariant.Filled).Geometry,
                        Foreground = (Brush)Application.Current.Resources[token],
                    },
                },
            };

            item.Click += async (_, _) =>
            {
                var payload = subject.PayloadAs<SubjectPayload>() ?? new SubjectPayload();
                payload.AccentName = name;
                subject.Payload = payload;
                await _workspace.Objects.SaveAsync(subject);
                await ReloadAsync();
            };

            submenu.Items.Add(item);
        }

        return submenu;
    }

    private async void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (!_workspace.IsUnlocked) return;

        var title = await ObjectCommands.AskAsync(XamlRoot, L.T("new.subject"), "", "Physics");
        if (title is null) return;

        var count = await _workspace.Objects.CountAsync(new CampusQuery { Kinds = { ObjectKind.Subject } });

        await _workspace.Objects.SaveAsync(new CampusObject
        {
            Kind = ObjectKind.Subject,
            Title = title,
            SortOrder = count,
            AcademicYear = DateTimeOffset.Now.Year,
            SourceDeviceId = _workspace.DeviceId,
            Payload = new SubjectPayload
            {
                // Colours are handed out in order so a new subject never arrives looking like
                // one that already exists.
                AccentName = ThemeTokens.Subject.ToName(
                    ThemeTokens.Subject.All[count % ThemeTokens.Subject.All.Length]),
                SortOrder = count,
            },
        });

        await ReloadAsync();
    }
}
