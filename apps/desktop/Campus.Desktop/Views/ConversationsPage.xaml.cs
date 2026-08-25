using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Domain;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Campus.Desktop.Views;

/// <summary>
/// Every recorded conversation, most recently touched first.
///
/// Deliberately not grouped by who or by subject. What a student wants from this list is the one
/// they were reading yesterday, and any arrangement cleverer than that puts it further away.
/// </summary>
public sealed partial class ConversationsPage : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();

    public ConversationsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        Rows.Children.Clear();
        if (!_workspace.IsUnlocked) return;

        var conversations = await _workspace.Objects.QueryAsync(new CampusQuery
        {
            Kinds = { ObjectKind.Conversation },
            Sort = SortField.UpdatedAt,
            Descending = true,
        });

        EmptyState.Visibility = conversations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var open = conversations.Count(c =>
            c.PayloadAs<ConversationPayload>()?.Closed != true);

        Summary.Text = conversations.Count == 0
            ? L.T("conversations.none")
            : Plural.Of("conversation.count", conversations.Count)
              + " · " + L.T("still.open", open);

        foreach (var conversation in conversations)
        {
            var messages = await _workspace.Objects.CountAsync(new CampusQuery
            {
                ParentId = conversation.Id,
            });

            Rows.Children.Add(BuildRow(conversation, messages));
        }
    }

    private FrameworkElement BuildRow(CampusObject conversation, int messages)
    {
        var payload = conversation.PayloadAs<ConversationPayload>() ?? new ConversationPayload();

        var row = new Grid { ColumnSpacing = 14 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = Brush(ThemeTokens.Fill.Quaternary),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new CampusIcon
            {
                Symbol = ConversationPage.SymbolFor(payload.ConversationKind),
                IconSize = 19,
                Foreground = Brush(ThemeTokens.Label.Secondary),
            },
        });

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock
        {
            Text = conversation.Title,
            Style = (Style)Application.Current.Resources["Text.Headline"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (payload.Closed)
        {
            titleRow.Children.Add(new Border
            {
                Background = Brush(ThemeTokens.Fill.Quaternary),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 1, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = L.T("closed"),
                    Style = (Style)Application.Current.Resources["Text.Caption"],
                },
            });
        }

        text.Children.Add(titleRow);

        var name = payload.With is { Length: > 0 } with
            ? with
            : ConversationPage.DefaultNameFor(payload.ConversationKind);

        text.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", new[]
            {
                name,
                L.T("last.when", BoardPage.Ago(payload.LastActivityAt ?? conversation.UpdatedAt)),
            }),
            Style = (Style)Application.Current.Resources["Text.Footnote"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });

        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var counts = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        counts.Children.Add(new TextBlock
        {
            Text = messages == 0 ? "—" : messages.ToString(),
            FontFamily = Font("Theme.Font.Text"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = Brush(ThemeTokens.Label.Secondary),
        });
        counts.Children.Add(new TextBlock
        {
            Text = Plural.Of("message.word", messages),
            Style = (Style)Application.Current.Resources["Text.Caption"],
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        Grid.SetColumn(counts, 2);
        row.Children.Add(counts);

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Plain"],
            Background = Brush(ThemeTokens.Surface.Primary),
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.Card"],
            Padding = new Thickness(16, 12, 18, 12),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = row,
            Opacity = payload.Closed ? 0.62 : 1,
        };

        AutomationProperties.SetName(button,
            L.T("conversation.row.spoken", conversation.Title, name, messages));
        button.Click += (_, _) => Frame?.Navigate(typeof(ConversationPage), conversation.Id);
        button.RightTapped += (_, e) =>
        {
            ObjectCommands.Build(conversation, XamlRoot, ReloadAsync)
                .ShowAt(button, e.GetPosition(button));
            e.Handled = true;
        };

        return button;
    }

    // ------------------------------------------------------------------------ starting one

    private async void OnNewTeacherClick(object sender, RoutedEventArgs e)
        => await StartAsync(ConversationKind.Teacher);

    private async void OnNewAssistantClick(object sender, RoutedEventArgs e)
        => await StartAsync(ConversationKind.Assistant);

    /// <summary>
    /// Starts one and opens it.
    ///
    /// Two questions and no further ceremony: what it is about, and who it was with. Anything
    /// more asked up front is asked at the exact moment the student is trying to write something
    /// down before they forget it.
    /// </summary>
    private async Task StartAsync(ConversationKind kind)
    {
        if (!_workspace.IsUnlocked) return;

        var title = await ObjectCommands.AskAsync(
            XamlRoot,
            L.T(kind == ConversationKind.Assistant ? "what.did.you.ask.about" : "what.was.it.about"),
            "",
            L.T(kind == ConversationKind.Assistant ? "example.redox" : "example.test.moved"));

        if (title is null) return;

        var with = await ObjectCommands.AskAsync(
            XamlRoot,
            L.T("who.was.it.with"),
            ConversationPage.DefaultNameFor(kind),
            ConversationPage.DefaultNameFor(kind));

        var conversation = new CampusObject
        {
            Kind = ObjectKind.Conversation,
            Title = title,
            SourceDeviceId = _workspace.DeviceId,
            Payload = new ConversationPayload
            {
                ConversationKind = kind,
                With = string.IsNullOrWhiteSpace(with) ? null : with!.Trim(),
                LastActivityAt = DateTimeOffset.UtcNow,
            },
        };

        await _workspace.Objects.SaveAsync(conversation);
        Frame?.Navigate(typeof(ConversationPage), conversation.Id);
    }

    private static Brush Brush(string token) => (Brush)Application.Current.Resources[token];
    private static FontFamily Font(string key) => (FontFamily)Application.Current.Resources[key];
}
