using Campus.Desktop.Design;
using Campus.Desktop.Design.Controls;
using Campus.Desktop.Design.Emoji;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Domain;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Campus.Desktop.Views;

/// <summary>
/// One thread: the question, and every post since.
///
/// Posts are ordinary objects with the thread as their parent, which means a reply is searchable,
/// linkable and exportable like anything else — a good answer written here is not trapped in a
/// chat log. Markdown is rendered, so working through an equation in a reply looks like working
/// through an equation.
/// </summary>
public sealed partial class ThreadPage : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();
    private readonly ShellRouter _router = App.GetService<ShellRouter>();

    private CampusObject? _thread;
    private CampusId _threadId;

    public ThreadPage()
    {
        InitializeComponent();
        Loaded += (_, _) => EmojiFlyout.Attach(EmojiButton, Composer);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not CampusId id || !_workspace.IsUnlocked) return;

        _threadId = id;
        await ReloadAsync();
        await _workspace.Objects.MarkOpenedAsync(id);
    }

    private async Task ReloadAsync()
    {
        _thread = await _workspace.Objects.GetAsync(_threadId);
        if (_thread is null) return;

        var payload = _thread.PayloadAs<ThreadPayload>() ?? new ThreadPayload();
        var resolved = _thread.Status == ObjectStatus.Completed || payload.Resolved;

        TitleText.Text = _thread.Title;
        ResolveButton.Content = resolved ? "Reopen" : "Mark resolved";
        Composer.IsEnabled = !payload.Locked;
        Composer.PlaceholderText = payload.Locked
            ? "This thread is locked."
            : "Add what you worked out…";

        var posts = await _workspace.Objects.QueryAsync(new CampusQuery
        {
            ParentId = _threadId,
            Sort = SortField.CreatedAt,
            Descending = false,
        });

        Subtitle.Text = string.Join(" · ", new[]
        {
            resolved ? "Resolved" : "Open",
            posts.Count switch
            {
                0 => "No replies yet",
                1 => "1 reply",
                _ => $"{posts.Count} replies",
            },
            "Started " + BoardPage.Ago(_thread.CreatedAt),
        });

        Posts.Children.Clear();

        // The opening post is the thread's own body, shown as the first post rather than as a
        // separate header — it is the question, and it is part of the conversation.
        Posts.Children.Add(BuildPost(
            _thread.Title,
            payload.Body ?? "",
            _thread.CreatedAt,
            isOpening: true,
            entity: _thread));

        foreach (var post in posts)
        {
            Posts.Children.Add(BuildPost(
                post.Title,
                post.PayloadAs<NotePayload>()?.Body ?? "",
                post.CreatedAt,
                isOpening: false,
                entity: post));
        }
    }

    private FrameworkElement BuildPost(
        string title, string body, DateTimeOffset when, bool isOpening, CampusObject entity)
    {
        var content = new StackPanel { Spacing = 8 };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        header.Children.Add(new CampusIcon
        {
            Symbol = isOpening ? CampusSymbols.Question : CampusSymbols.Comment,
            IconSize = 15,
            Foreground = Brush(ThemeTokens.Label.Tertiary),
            VerticalAlignment = VerticalAlignment.Center,
        });

        header.Children.Add(new TextBlock
        {
            Text = isOpening ? "The question" : title,
            FontFamily = Font("Theme.Font.Text"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(ThemeTokens.Label.Secondary),
            VerticalAlignment = VerticalAlignment.Center,
        });

        header.Children.Add(new TextBlock
        {
            Text = BoardPage.Ago(when),
            Style = (Style)Application.Current.Resources["Text.Caption"],
            VerticalAlignment = VerticalAlignment.Center,
        });

        content.Children.Add(header);

        if (body.Trim().Length > 0)
        {
            var markdown = new MarkdownView { Text = body };
            markdown.LinkInvoked += async (_, url) => await FollowAsync(url);
            content.Children.Add(markdown);
        }
        else if (isOpening)
        {
            content.Children.Add(new TextBlock
            {
                Text = "No detail was written with the question.",
                Style = (Style)Application.Current.Resources["Text.Footnote"],
            });
        }

        var card = new Border
        {
            Background = Brush(isOpening ? ThemeTokens.Fill.Quaternary : ThemeTokens.Surface.Primary),
            CornerRadius = (CornerRadius)Application.Current.Resources["Theme.Radius.Card"],
            Padding = new Thickness(18, 14, 18, 16),
            Child = content,
        };

        card.RightTapped += (_, e) =>
        {
            var menu = new MenuFlyout();

            menu.Items.Add(ObjectCommands.Item("Edit", CampusSymbols.Edit, () => EditAsync(entity, isOpening)));

            menu.Items.Add(ObjectCommands.Item("Copy text", CampusSymbols.Copy, () =>
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(body);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                Notifications.Show("Copied.");
                return Task.CompletedTask;
            }));

            if (!isOpening)
            {
                menu.Items.Add(new MenuFlyoutSeparator());

                var delete = ObjectCommands.Item("Delete reply", CampusSymbols.Trash, async () =>
                {
                    if (!await ObjectCommands.ConfirmAsync(XamlRoot, "Delete this reply?",
                        "It moves to the trash.", "Delete")) return;

                    await _workspace.Objects.TrashAsync(entity.Id);
                    await UpdateCountAsync();
                    await ReloadAsync();
                });
                delete.Foreground = Brush(ThemeTokens.Destructive.Primary);
                menu.Items.Add(delete);
            }

            menu.ShowAt(card, e.GetPosition(card));
            e.Handled = true;
        };

        return card;
    }

    private async Task FollowAsync(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "mailto")
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
            return;
        }

        var target = await _workspace.Relations.FindByTitleAsync(url.Trim('[', ']'));
        if (target is { } id) _router.Open(id);
        else Notifications.Show($"Nothing here is called “{url}”.", NoticeKind.Warning);
    }

    /// <summary>Edits a post in place. The opening post edits the thread's own body.</summary>
    private async Task EditAsync(CampusObject entity, bool isOpening)
    {
        var current = isOpening
            ? entity.PayloadAs<ThreadPayload>()?.Body ?? ""
            : entity.PayloadAs<NotePayload>()?.Body ?? "";

        var input = new TextBox
        {
            Text = current,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 220,
            Width = 460,
            Style = (Style)Application.Current.Resources["Input.Text"],
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = isOpening ? "Edit the question" : "Edit this reply",
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // Keeping the previous text costs one vault object and makes an edit undoable.
        await App.GetService<VersionService>().SnapshotAsync(entity, "before edit");

        if (isOpening)
        {
            var payload = entity.PayloadAs<ThreadPayload>() ?? new ThreadPayload();
            payload.Body = input.Text;
            entity.Payload = payload;
        }
        else
        {
            var payload = entity.PayloadAs<NotePayload>() ?? new NotePayload();
            payload.Body = input.Text;
            entity.Payload = payload;
        }

        await _workspace.Objects.SaveAsync(entity);
        await _workspace.Relations.SyncDerivedLinksAsync(entity.Id, input.Text);
        await ReloadAsync();
    }

    // ----------------------------------------------------------------------- posting

    private async Task PostAsync()
    {
        var text = Composer.Text.Trim();
        if (text.Length == 0 || _thread is null) return;

        var post = new CampusObject
        {
            // A post's title is the first line of it, so it reads sensibly in search results
            // and in the trash without anyone having to name their replies.
            Title = FirstLine(text),
            Kind = ObjectKind.Note,
            ParentId = _threadId,
            SubjectId = _thread.SubjectId,
            SourceDeviceId = _workspace.DeviceId,
            Payload = new NotePayload { Body = text, NoteKind = NoteKind.Quick },
        };

        await _workspace.Objects.SaveAsync(post);

        // A [[link]] typed in a reply becomes a real edge, the same as one typed in a note.
        await _workspace.Relations.SyncDerivedLinksAsync(post.Id, text);
        await _workspace.Relations.LinkAsync(post.Id, _threadId, RelationKind.PartOf);

        Composer.Text = "";
        await UpdateCountAsync();
        await ReloadAsync();

        Scroller.UpdateLayout();
        Scroller.ChangeView(null, Scroller.ScrollableHeight, null);
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        if (line.Length == 0) line = "Reply";
        return line.Length > 90 ? line[..90] + "…" : line;
    }

    /// <summary>Keeps the thread's reply count and last activity in step with reality.</summary>
    private async Task UpdateCountAsync()
    {
        if (_thread is null) return;

        var count = await _workspace.Objects.CountAsync(new CampusQuery { ParentId = _threadId });

        var payload = _thread.PayloadAs<ThreadPayload>() ?? new ThreadPayload();
        payload.MessageCount = count;
        payload.LastActivityAt = DateTimeOffset.UtcNow;
        _thread.Payload = payload;

        await _workspace.Objects.SaveAsync(_thread);
    }

    // ----------------------------------------------------------------------- actions

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Frame?.CanGoBack == true) Frame.GoBack();
        else if (_thread?.ParentId is { } board) Frame?.Navigate(typeof(BoardPage), board);
    }

    private async void OnSendClick(object sender, RoutedEventArgs e) => await PostAsync();

    private async void OnComposerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter posts; Shift+Enter is a new line. The other way round makes writing a worked
        // solution in a reply painful.
        if (e.Key != VirtualKey.Enter) return;

        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (shift) return;

        e.Handled = true;
        await PostAsync();
    }

    private async void OnResolveClick(object sender, RoutedEventArgs e)
    {
        if (_thread is null) return;

        var payload = _thread.PayloadAs<ThreadPayload>() ?? new ThreadPayload();
        var resolved = _thread.Status == ObjectStatus.Completed || payload.Resolved;

        payload.Resolved = !resolved;
        _thread.Payload = payload;
        _thread.Status = resolved ? ObjectStatus.InProgress : ObjectStatus.Completed;
        _thread.CompletedAt = resolved ? null : DateTimeOffset.UtcNow;

        await _workspace.Objects.SaveAsync(_thread);
        Notifications.Show(resolved ? "Thread reopened." : "Marked resolved.",
            resolved ? NoticeKind.Info : NoticeKind.Success);

        await ReloadAsync();
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (_thread is null || sender is not FrameworkElement anchor) return;

        var payload = _thread.PayloadAs<ThreadPayload>() ?? new ThreadPayload();
        var menu = ObjectCommands.Build(_thread, XamlRoot, ReloadAsync);

        menu.Items.Insert(1, ObjectCommands.Item(
            payload.Locked ? "Unlock thread" : "Lock thread",
            payload.Locked ? CampusSymbols.Unlock : CampusSymbols.Lock,
            async () =>
            {
                payload.Locked = !payload.Locked;
                _thread.Payload = payload;
                await _workspace.Objects.SaveAsync(_thread);
                await ReloadAsync();
            }));

        menu.ShowAt(anchor);
    }

    private static Brush Brush(string token) => (Brush)Application.Current.Resources[token];
    private static FontFamily Font(string key) => (FontFamily)Application.Current.Resources[key];
}
