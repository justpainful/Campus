using Campus.Desktop.Design;
using Campus.Desktop.Design.Controls;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Desktop.ViewModels;
using Campus.Domain;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Campus.Desktop.Views;

/// <summary>
/// The academic profile: who this workspace belongs to and what year it is in.
///
/// This is not decoration. "This term" and "this year" appear in queries all over Campus, and
/// without a profile they would have to be guessed from the calendar — which is wrong for half
/// the world and wrong for everyone in August.
/// </summary>
public sealed partial class ProfilePage : Page
{
    private const string SettingKey = "profile";

    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();
    private AcademicProfile _profile = new();

    public ProfilePage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_workspace.IsUnlocked) return;

        _profile = await _workspace.Settings.GetAsync<AcademicProfile>(SettingKey) ?? new AcademicProfile();

        BuildWho();
        BuildYear();
        BuildRules();
        await BuildStatsAsync();
    }

    private async Task SaveAsync()
    {
        await _workspace.Settings.SetAsync(SettingKey, _profile);
        UpdateSubtitle();
    }

    private void UpdateSubtitle()
    {
        var parts = new List<string>();
        if (_profile.Grade is { Length: > 0 } grade) parts.Add(grade);
        if (_profile.School is { Length: > 0 } school) parts.Add(school);
        parts.Add($"{_profile.AcademicYear} · {TermName(_profile.CurrentTerm)}");

        Subtitle.Text = string.Join(" · ", parts);
    }

    private static string TermName(TermKind term) => term switch
    {
        TermKind.Term1 => "First term",
        TermKind.Term2 => "Second term",
        TermKind.Term3 => "Third term",
        TermKind.Summer => "Summer",
        _ => "Full year",
    };

    // -------------------------------------------------------------------------- who

    private void BuildWho()
    {
        var rows = new StackPanel();

        rows.Children.Add(TextRow("Name", "What Campus calls you on the home page",
            CampusSymbols.Person, _profile.DisplayName,
            value => { _profile.DisplayName = value; _ = SaveAsync(); }, first: true));

        rows.Children.Add(TextRow("School", null, CampusSymbols.Graduation,
            _profile.School ?? "",
            value => { _profile.School = Trimmed(value); _ = SaveAsync(); }));

        rows.Children.Add(TextRow("Grade", "Year or level", CampusSymbols.Subjects,
            _profile.Grade ?? "",
            value => { _profile.Grade = Trimmed(value); _ = SaveAsync(); }));

        rows.Children.Add(TextRow("Section", "Class or group", CampusSymbols.Person,
            _profile.Section ?? "",
            value => { _profile.Section = Trimmed(value); _ = SaveAsync(); }));

        WhoSection.Content = rows;
        UpdateSubtitle();
    }

    private void BuildYear()
    {
        var rows = new StackPanel();

        var year = new NumberBox
        {
            Value = _profile.AcademicYear,
            Minimum = 1900,
            Maximum = 2200,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 140,
        };
        year.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(year.Value)) return;
            _profile.AcademicYear = (int)year.Value;
            _ = SaveAsync();
        };
        AutomationProperties.SetName(year, L.T("academic.year.f0b9"));

        rows.Children.Add(new SettingsRow
        {
            Title = L.T("academic.year.f0b9"),
            Subtitle = "Used to file this year's work apart from last year's",
            Symbol = CampusSymbols.Calendar,
            ShowSeparator = false,
            Content = year,
        });

        var term = new ComboBox { Width = 180 };
        foreach (var value in Enum.GetValues<TermKind>()) term.Items.Add(TermName(value));
        term.SelectedIndex = Array.IndexOf(Enum.GetValues<TermKind>(), _profile.CurrentTerm);
        term.SelectionChanged += (_, _) =>
        {
            if (term.SelectedIndex < 0) return;
            _profile.CurrentTerm = Enum.GetValues<TermKind>()[term.SelectedIndex];
            _ = SaveAsync();
        };
        AutomationProperties.SetName(term, L.T("current.term"));

        rows.Children.Add(new SettingsRow
        {
            Title = L.T("current.term"),
            Subtitle = "What “this term” means in every list",
            Symbol = CampusSymbols.Clock,
            Content = term,
        });

        rows.Children.Add(DateRow("Year starts", _profile.YearStart,
            value => { _profile.YearStart = value; _ = SaveAsync(); }));

        rows.Children.Add(DateRow("Year ends", _profile.YearEnd,
            value => { _profile.YearEnd = value; _ = SaveAsync(); }));

        YearSection.Content = rows;
    }

    private static SettingsRow TextRow(
        string title, string? subtitle, string symbol, string value,
        Action<string> changed, bool first = false)
    {
        var box = new TextBox
        {
            Text = value,
            Width = 240,
            Style = (Style)Application.Current.Resources["Input.Text"],
        };

        // Saved when the field loses focus rather than on every keystroke: a profile is not
        // something anyone types in a hurry, and a write per character is noise in the journal.
        box.LostFocus += (_, _) => changed(box.Text);
        AutomationProperties.SetName(box, title);

        return new SettingsRow
        {
            Title = title,
            Subtitle = subtitle,
            Symbol = symbol,
            ShowSeparator = !first,
            Content = box,
        };
    }

    private static SettingsRow DateRow(
        string title, DateTimeOffset? value, Action<DateTimeOffset?> changed)
    {
        var picker = new CalendarDatePicker
        {
            Date = value,
            PlaceholderText = L.T("not.set"),
            Width = 200,
        };
        picker.DateChanged += (_, args) => changed(args.NewDate);
        AutomationProperties.SetName(picker, title);

        return new SettingsRow
        {
            Title = title,
            Symbol = CampusSymbols.Calendar,
            Content = picker,
        };
    }

    // ------------------------------------------------------------------------ rules

    private void BuildRules()
    {
        RulesList.Children.Clear();

        if (_profile.PersonalRules.Count == 0)
        {
            RulesList.Children.Add(new TextBlock
            {
                Text = L.T("nothing.set.never.leave.revision.to.the.night.60bbbb"),
                Style = (Style)Application.Current.Resources["Text.Footnote"],
            });
            return;
        }

        foreach (var rule in _profile.PersonalRules.ToList())
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new CampusIcon
            {
                Symbol = CampusSymbols.Flag,
                IconSize = 15,
                Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Tertiary],
                VerticalAlignment = VerticalAlignment.Center,
            });

            var text = new TextBlock
            {
                Text = rule,
                Style = (Style)Application.Current.Resources["Text.Body"],
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            var remove = new Button
            {
                Style = (Style)Application.Current.Resources["Button.Icon"],
                Content = new CampusIcon
                {
                    Symbol = CampusSymbols.Close,
                    IconSize = 14,
                    Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Tertiary],
                },
            };
            AutomationProperties.SetName(remove, $"Remove rule: {rule}");
            remove.Click += async (_, _) =>
            {
                _profile.PersonalRules.Remove(rule);
                await SaveAsync();
                BuildRules();
            };
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);

            RulesList.Children.Add(row);
        }
    }

    private async void OnAddRuleClick(object sender, RoutedEventArgs e)
    {
        var rule = await ObjectCommands.AskAsync(
            XamlRoot, "Add a rule", "", "Read the whole question before answering");
        if (rule is null) return;

        _profile.PersonalRules.Add(rule);
        await SaveAsync();
        BuildRules();
    }

    // ------------------------------------------------------------------------ stats

    private async Task BuildStatsAsync()
    {
        Stats.Children.Clear();

        async Task<int> Count(ObjectKind kind)
            => await _workspace.Objects.CountAsync(new CampusQuery { Kinds = { kind } });

        Stats.Children.Add(Stat(await Count(ObjectKind.Subject), "subjects"));
        Stats.Children.Add(Stat(await Count(ObjectKind.File), "files"));
        Stats.Children.Add(Stat(await Count(ObjectKind.Note), "notes"));
        Stats.Children.Add(Stat(await Count(ObjectKind.Assignment), "assignments"));
        Stats.Children.Add(Stat(await Count(ObjectKind.Task), "tasks"));

        // What the vault takes on disk, including every version and thumbnail — the honest
        // number rather than the sum of the files' original sizes.
        var onDisk = await Task.Run(_workspace.Vault.Objects.MeasureOnDisk);
        Stats.Children.Add(TextStat(ObjectItem.FormatSize(onDisk), "encrypted on disk"));
    }

    private static FrameworkElement Stat(int value, string label)
        => TextStat(value.ToString(), label);

    private static FrameworkElement TextStat(string value, string label)
    {
        var stack = new StackPanel { Spacing = 0 };

        stack.Children.Add(new TextBlock
        {
            Text = value,
            FontFamily = (FontFamily)Application.Current.Resources["Theme.Font.Text"],
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Primary],
        });
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["Text.Caption"],
        });

        return stack;
    }

    private static string? Trimmed(string value)
        => value.Trim() is { Length: > 0 } text ? text : null;
}
