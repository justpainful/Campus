using Campus.Desktop.Design;
using Campus.Desktop.Services;
using Campus.Desktop.Shell;
using Campus.Desktop.ViewModels;
using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Campus.Desktop.Views;

/// <summary>
/// The landing page: what is happening today, what is coming, what is waiting to be sorted, and
/// what you were last reading. Counts link to the list that explains them, so nothing here is a
/// dead end.
/// </summary>
public sealed partial class HomePage : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();
    private readonly Dictionary<string, string> _subjectNames = new(StringComparer.Ordinal);

    public HomePage()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;

        AttachRowClick(UpcomingList);
        AttachRowClick(InboxList);
        AttachRowClick(ContinueList);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ShowGreeting();
        await LoadAsync();
    }

    private void ShowGreeting()
    {
        var now = DateTimeOffset.Now;
        GreetingText.Text = now.Hour switch
        {
            < 5 => "Still up",
            < 12 => "Good morning",
            < 17 => "Good afternoon",
            < 22 => "Good evening",
            _ => "Good evening",
        };
        DateText.Text = now.ToString("dddd, d MMMM");
    }

    /// <summary>Stacks the two columns when the window is too narrow to hold them side by side.</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width > 1060;
        UpcomingColumn.Width = wide ? 480 : double.NaN;
        InboxColumn.Width = wide ? 480 : double.NaN;
        UpcomingColumn.HorizontalAlignment = wide ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        InboxColumn.HorizontalAlignment = wide ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
    }

    private async Task LoadAsync()
    {
        if (!_workspace.IsUnlocked) return;
        var repository = _workspace.Objects;

        var subjects = await repository.QueryAsync(new CampusQuery { Kinds = { ObjectKind.Subject } });
        _subjectNames.Clear();
        foreach (var subject in subjects) _subjectNames[subject.Id.Value] = subject.Title;

        // Counts first: they are what the page leads with, and they are cheap.
        var dueToday = await repository.CountAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Task, ObjectKind.Assignment },
            Due = DateRange.Of(RelativeWindow.Today),
            Statuses = { ObjectStatus.None, ObjectStatus.NotStarted, ObjectStatus.InProgress },
        });

        var overdue = await repository.CountAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Task, ObjectKind.Assignment, ObjectKind.Requirement },
            Due = DateRange.Of(RelativeWindow.Overdue),
            Statuses = { ObjectStatus.None, ObjectStatus.NotStarted, ObjectStatus.InProgress },
        });

        var requirements = await repository.CountAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Requirement },
            Statuses = { ObjectStatus.None, ObjectStatus.NotStarted, ObjectStatus.InProgress },
        });

        var printJobs = await repository.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.PrintJob },
            PrintState = PrintState.ToPrint,
        });

        DueTodayCount.Text = dueToday.ToString();
        OverdueCount.Text = overdue.ToString();
        RequirementCount.Text = requirements.ToString();
        PrintCount.Text = printJobs.Count.ToString();

        // The print card says how many pages as well as how many jobs, because the page count is
        // what decides whether this is a five-minute errand or a trip to the shop.
        var pages = printJobs.Sum(j => j.PayloadAs<PrintJobPayload>()?.Pages ?? 0);
        PrintCaption.Text = pages > 0
            ? $"waiting to print · {pages} page{(pages == 1 ? "" : "s")}"
            : "waiting to print";

        // Overdue is only worth colouring red when there is something in it.
        OverdueCount.Foreground = (Brush)Application.Current.Resources[
            overdue > 0 ? ThemeTokens.Destructive.Primary : ThemeTokens.Label.Primary];

        await FillAsync(UpcomingList, UpcomingEmpty, new CampusQuery
        {
            Kinds = { ObjectKind.Assignment, ObjectKind.Task, ObjectKind.Requirement, ObjectKind.Exam },
            Due = DateRange.Absolute(DateTimeOffset.Now, DateTimeOffset.Now.AddDays(14)),
            Statuses = { ObjectStatus.None, ObjectStatus.NotStarted, ObjectStatus.InProgress },
            Sort = SortField.DueAt,
            Descending = false,
            Limit = 6,
        });

        await FillAsync(InboxList, InboxEmpty, new CampusQuery
        {
            Kinds = { ObjectKind.InboxItem },
            Sort = SortField.CreatedAt,
            Limit = 5,
        });

        await FillAsync(ContinueList, ContinueEmpty, new CampusQuery
        {
            Sort = SortField.OpenedAt,
            Limit = 4,
        });
    }

    /// <summary>Makes a Home row open its object, the same as a row in any list.</summary>
    private void AttachRowClick(ItemsControl list)
    {
        list.Tapped += (sender, args) =>
        {
            if (args.OriginalSource is not FrameworkElement { DataContext: ObjectItem item }) return;
            Frame?.Navigate(typeof(ObjectDetailPage), item.Id);
        };
    }

    private async Task FillAsync(ItemsControl list, UIElement empty, CampusQuery query)
    {
        var results = await _workspace.Objects.QueryAsync(query);

        // OpenedAt sorts nulls last, so anything never opened is trimmed rather than padding
        // the Continue list with things the user has not touched.
        if (query.Sort == SortField.OpenedAt)
            results = results.Where(r => r.OpenedAt is not null).ToList();

        var items = results.Select(model =>
        {
            var item = new ObjectItem(model);
            if (model.SubjectId is { } id && _subjectNames.TryGetValue(id.Value, out var name))
                item.SubjectName = name;
            return item;
        }).ToList();

        list.ItemsSource = items;
        list.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // --------------------------------------------------------------------- navigation

    private void Go(string destination)
        => (App.MainWindow as MainWindow)?.NavigateTo(destination);

    private void OnTasksClick(object sender, RoutedEventArgs e) => Go(ShellDestinations.Tasks);
    private void OnRequirementsClick(object sender, RoutedEventArgs e) => Go(ShellDestinations.Requirements);
    private void OnPrintClick(object sender, RoutedEventArgs e) => Go(ShellDestinations.PrintCenter);
    private void OnAssignmentsClick(object sender, RoutedEventArgs e) => Go(ShellDestinations.Assignments);
    private void OnInboxClick(object sender, RoutedEventArgs e) => Go(ShellDestinations.Inbox);

    private async void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string kindName }) return;
        if (!Enum.TryParse<ObjectKind>(kindName, out var kind)) return;

        await QuickCapture.ShowAsync(XamlRoot, kind);
        await LoadAsync();
    }

    // ------------------------------------------------- template helper functions

    public static Visibility TextVisibility(string? text)
        => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Brush RoleBrush(string token) => (Brush)Application.Current.Resources[token];
}
