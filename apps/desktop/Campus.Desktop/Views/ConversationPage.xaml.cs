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
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Campus.Desktop.Views;

/// <summary>
/// One recorded conversation, and everything said in it.
///
/// The rule that shapes this whole page is that the two sides are written differently, because
/// they were produced differently. What the student typed is what they typed: if they wrote an
/// asterisk, it is an asterisk. What an assistant replied arrives full of markdown, and showing
/// that raw would be showing the plumbing — so it is drawn, headings and lists and code blocks
/// and all, the way it looked where it was read.
///
/// Everything here is a real object. A message is an object with the conversation as its parent,
/// which means an explanation a teacher gave in a corridor is searchable, linkable from a note,
/// exportable and backed up, rather than trapped in a log.
/// </summary>
public sealed partial class ConversationPage : Page
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();
    private readonly ImportService _import = App.GetService<ImportService>();
    private readonly ShellRouter _router = App.GetService<ShellRouter>();

    private CampusObject? _conversation;
    private CampusId _conversationId;
    private ConversationPayload _payload = new();

    /// <summary>Which side the composer is currently speaking as.</summary>
    private Speaker _speaker = Speaker.Me;

    /// <summary>Pictures picked for the message being written, not yet attached to anything.</summary>
    private readonly List<CampusObject> _pending = [];

    /// <summary>Set while the markdown switch is being moved by code rather than by the user.</summary>
    private bool _settingMarkdown;

    public ConversationPage()
    {
        InitializeComponent();
        Loaded += (_, _) => EmojiFlyout.Attach(EmojiButton, Composer);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not CampusId id || !_workspace.IsUnlocked) return;

        _conversationId = id;
        await ReloadAsync();
        await _workspace.Objects.MarkOpenedAsync(id);
    }

    // ------------------------------------------------------------------------ loading

    private async Task ReloadAsync()
    {
        _conversation = await _workspace.Objects.GetAsync(_conversationId);
        if (_conversation is null) return;

        _payload = _conversation.PayloadAs<ConversationPayload>() ?? new ConversationPayload();

        TitleText.Text = _conversation.Title;
        PartyIcon.Symbol = SymbolFor(_payload.ConversationKind);
        CloseButton.Content = _payload.Closed ? "Reopen" : "Close";

        var messages = await _workspace.Objects.QueryAsync(new CampusQuery
        {
            ParentId = _conversationId,
            Sort = SortField.CreatedAt,
            Descending = false,
        });

        Subtitle.Text = string.Join(" · ", new[]
        {
            Describe(_payload),
            messages.Count switch
            {
                0 => "Nothing written down yet",
                1 => "1 message",
                _ => $"{messages.Count} messages",
            },
            _payload.Closed ? "Closed" : "Last " + BoardPage.Ago(
                _payload.LastActivityAt ?? _conversation.CreatedAt),
        });

        BuildSpeakerPicker();
        Composer.IsEnabled = !_payload.Closed;
        Composer.PlaceholderText = _payload.Closed
            ? "This conversation is closed."
            : PlaceholderFor(_speaker);

        Messages.Children.Clear();

        if (messages.Count == 0)
        {
            Messages.Children.Add(Empty());
        }
        else
        {
            // Consecutive messages from the same side are one run under one name, because that
            // is how they were said — three sentences in a row from a teacher is a teacher
            // talking, not three separate events.
            Speaker? previous = null;
            DateTimeOffset? previousAt = null;

            foreach (var message in messages)
            {
                var payload = message.PayloadAs<MessagePayload>() ?? new MessagePayload();
                var sameRun = previous == payload.From
                    && previousAt is { } last
                    && (payload.SentAt - last).TotalMinutes < 30;

                Messages.Children.Add(await BuildMessageAsync(message, payload, sameRun));

                previous = payload.From;
                previousAt = payload.SentAt;
            }
        }

        UpdateMarkdownToggle();
    }

    private FrameworkElement Empty() => new StackPanel
    {
        Spacing = 6,
        Margin = new Thickness(0, 40, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Center,
        Children =
        {
            new TextBlock
            {
                Text = "Nothing written down yet.",
                Style = (Style)Application.Current.Resources["Text.Headline"],
                HorizontalAlignment = HorizontalAlignment.Center,
            },
            new TextBlock
            {
                Text = _payload.ConversationKind == ConversationKind.Assistant
                    ? "Paste what you asked and what came back. Markdown in the answer is drawn."
                    : "Write down what was said while you still remember it.",
                Style = (Style)Application.Current.Resources["Text.Footnote"],
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                TextAlignment = TextAlignment.Center,
            },
        },
    };

    // ------------------------------------------------------------------------ one message

    private async Task<FrameworkElement> BuildMessageAsync(
        CampusObject entity, MessagePayload payload, bool sameRun)
    {
        var mine = payload.From == Speaker.Me;

        var content = new StackPanel { Spacing = 8 };

        if (!sameRun) content.Children.Add(Header(payload, mine));

        if (payload.Body.Trim().Length > 0)
        {
            if (payload.IsMarkdown)
            {
                var markdown = new MarkdownView { Text = payload.Body };
                markdown.LinkInvoked += async (_, url) => await FollowAsync(url);
                content.Children.Add(markdown);
            }
            else
            {
                // Deliberately not markdown. What was typed is shown, wrapped and selectable,
                // with nothing interpreted — a student's own asterisks are their own asterisks.
                content.Children.Add(new TextBlock
                {
                    Text = payload.Body,
                    FontFamily = Font("Theme.Font.Reading"),
                    FontSize = 14.5,
                    LineHeight = 23,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Foreground = Brush(ThemeTokens.Label.Primary),
                });
            }
        }

        if (payload.Attachments.Count > 0)
            content.Children.Add(await PicturesAsync(payload.Attachments));

        // How a message sits is how it is read. An assistant's answer runs the full width like a
        // page of a book, because it is one; everything a person said sits in a bubble on its own
        // side, because a conversation has sides.
        var assistantVoice = !mine && _payload.ConversationKind == ConversationKind.Assistant;

        var bubble = new Border
        {
            Child = content,
            Padding = assistantVoice
                ? new Thickness(0, 6, 0, 10)
                : new Thickness(16, 12, 16, 13),
            Margin = new Thickness(0, sameRun ? 2 : 12, 0, 0),
            CornerRadius = new CornerRadius(14),
            MaxWidth = assistantVoice ? double.PositiveInfinity : 620,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = assistantVoice
                ? null
                : Brush(mine ? ThemeTokens.Accent.Subtle : ThemeTokens.Surface.Primary),
        };

        AutomationProperties.SetName(bubble,
            $"{(mine ? "You" : NameOfOther())}: {payload.Body}");

        bubble.RightTapped += (_, e) =>
        {
            ShowMenu(bubble, entity, payload, e.GetPosition(bubble));
            e.Handled = true;
        };

        return bubble;
    }

    private FrameworkElement Header(MessagePayload payload, bool mine)
    {
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (!mine)
        {
            header.Children.Add(new CampusIcon
            {
                Symbol = SymbolFor(_payload.ConversationKind),
                IconSize = 14,
                Foreground = Brush(ThemeTokens.Label.Tertiary),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        header.Children.Add(new TextBlock
        {
            Text = mine ? "You" : NameOfOther(),
            FontFamily = Font("Theme.Font.Text"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(ThemeTokens.Label.Secondary),
            VerticalAlignment = VerticalAlignment.Center,
        });

        header.Children.Add(new TextBlock
        {
            Text = payload.SentAt.ToLocalTime().ToString("HH:mm · d MMM"),
            Style = (Style)Application.Current.Resources["Text.Caption"],
            VerticalAlignment = VerticalAlignment.Center,
        });

        return header;
    }

    /// <summary>Draws the pictures sent with a message, in the order they were attached.</summary>
    private async Task<FrameworkElement> PicturesAsync(IReadOnlyList<CampusId> ids)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        foreach (var id in ids)
        {
            var file = await _workspace.Objects.GetAsync(id);
            var payload = file?.PayloadAs<FilePayload>();

            var frame = new Border
            {
                Width = 190,
                Height = 140,
                CornerRadius = new CornerRadius(10),
                Background = Brush(ThemeTokens.Fill.Quaternary),
            };

            if (file is null)
            {
                // The picture was deleted from the workspace. The message still happened, so it
                // says so rather than showing an empty square.
                frame.Child = new TextBlock
                {
                    Text = "Picture removed",
                    Style = (Style)Application.Current.Resources["Text.Caption"],
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                strip.Children.Add(frame);
                continue;
            }

            var thumbnail = await _import.LoadThumbnailAsync(payload?.ThumbnailHash, 380);
            if (thumbnail is not null)
            {
                frame.Child = new Image { Source = thumbnail, Stretch = Stretch.UniformToFill };
            }

            ToolTipService.SetToolTip(frame, file.Title);
            AutomationProperties.SetName(frame, file.Title);

            var open = new Button
            {
                Style = (Style)Application.Current.Resources["Button.Plain"],
                Padding = new Thickness(0),
                Content = frame,
            };
            open.Click += (_, _) => _router.Open(file.Id);

            strip.Children.Add(open);
        }

        return strip;
    }

    private void ShowMenu(FrameworkElement anchor, CampusObject entity, MessagePayload payload,
        Windows.Foundation.Point at)
    {
        var menu = new MenuFlyout();

        menu.Items.Add(ObjectCommands.Item("Edit", CampusSymbols.Edit, () => EditAsync(entity)));

        menu.Items.Add(ObjectCommands.Item(
            payload.IsMarkdown ? "Show as plain text" : "Draw as markdown",
            CampusSymbols.Markdown,
            async () =>
            {
                payload.IsMarkdown = !payload.IsMarkdown;
                entity.Payload = payload;
                await _workspace.Objects.SaveAsync(entity);
                await ReloadAsync();
            }));

        menu.Items.Add(ObjectCommands.Item("Copy text", CampusSymbols.Copy, () =>
        {
            App.GetService<SensitiveMode>().Copy(payload.Body, DispatcherQueue);
            Notifications.Show("Copied.");
            return Task.CompletedTask;
        }));

        menu.Items.Add(new MenuFlyoutSeparator());

        var delete = ObjectCommands.Item("Delete message", CampusSymbols.Trash, async () =>
        {
            if (!await ObjectCommands.ConfirmAsync(XamlRoot, "Delete this message?",
                "It moves to the trash. Any pictures sent with it stay in the library.",
                "Delete")) return;

            await _workspace.Objects.TrashAsync(entity.Id);
            await UpdateCountAsync();
            await ReloadAsync();
        });
        delete.Foreground = Brush(ThemeTokens.Destructive.Primary);
        menu.Items.Add(delete);

        menu.ShowAt(anchor, at);
    }

    // ------------------------------------------------------------------------ the composer

    /// <summary>
    /// The two sides, as a pair of segments.
    ///
    /// Built rather than declared because the second one is named after whoever the conversation
    /// is with — "ChatGPT", "Mr Faisal" — and a control that says "Them" would make the student
    /// translate it every time.
    /// </summary>
    private void BuildSpeakerPicker()
    {
        SpeakerPicker.Children.Clear();

        SpeakerPicker.Children.Add(Segment("You", Speaker.Me, first: true));
        SpeakerPicker.Children.Add(Segment(NameOfOther(), Speaker.Them, first: false));
    }

    private ToggleButton Segment(string label, Speaker speaker, bool first)
    {
        var button = new ToggleButton
        {
            Content = new TextBlock { Text = label, Style = (Style)Application.Current.Resources["Text.Caption"] },
            Style = (Style)Application.Current.Resources["Toggle.Chip"],
            IsChecked = _speaker == speaker,
            CornerRadius = first
                ? new CornerRadius(999, 0, 0, 999)
                : new CornerRadius(0, 999, 999, 0),
            Margin = new Thickness(first ? 0 : 1, 0, 0, 0),
        };

        AutomationProperties.SetName(button, $"Speaking as {label}");

        button.Click += (_, _) =>
        {
            _speaker = speaker;
            BuildSpeakerPicker();
            UpdateMarkdownToggle();
            Composer.PlaceholderText = PlaceholderFor(speaker);
            Composer.Focus(FocusState.Programmatic);
        };

        return button;
    }

    /// <summary>
    /// Sets the markdown switch to what this side usually produces.
    ///
    /// An assistant's reply is markdown; a person typing is not. Getting that right by default is
    /// the difference between pasting an answer and having it appear, and pasting an answer and
    /// then having to find a switch. It is still a switch, because the default is a guess.
    /// </summary>
    private void UpdateMarkdownToggle()
    {
        _settingMarkdown = true;
        MarkdownToggle.IsChecked = DefaultMarkdownFor(_speaker);
        _settingMarkdown = false;
    }

    private bool DefaultMarkdownFor(Speaker speaker) =>
        speaker == Speaker.Them && _payload.ConversationKind == ConversationKind.Assistant;

    private void OnMarkdownToggled(object sender, RoutedEventArgs e)
    {
        if (_settingMarkdown) return;
    }

    private string PlaceholderFor(Speaker speaker) => speaker switch
    {
        Speaker.Me => "What you asked…",
        _ when _payload.ConversationKind == ConversationKind.Assistant => "Paste the answer…",
        _ => $"What {NameOfOther()} said…",
    };

    private async Task SendAsync()
    {
        var text = Composer.Text.Trim();
        if (_conversation is null || _payload.Closed) return;
        if (text.Length == 0 && _pending.Count == 0) return;

        var message = new CampusObject
        {
            // Titled by its first line so it reads sensibly in search results and in the trash,
            // without anybody having to name a message.
            Title = FirstLine(text, _pending.Count),
            Kind = ObjectKind.Message,
            ParentId = _conversationId,
            SubjectId = _conversation.SubjectId,
            SourceDeviceId = _workspace.DeviceId,
            Payload = new MessagePayload
            {
                From = _speaker,
                Body = text,
                IsMarkdown = MarkdownToggle.IsChecked == true,
                SentAt = DateTimeOffset.UtcNow,
                Attachments = { },
            },
        };

        var payload = (MessagePayload)message.Payload!;
        foreach (var picture in _pending) payload.Attachments.Add(picture.Id);

        await _workspace.Objects.SaveAsync(message);

        // A [[link]] typed in a message is a real edge, the same as one typed in a note.
        await _workspace.Relations.SyncDerivedLinksAsync(message.Id, text);
        await _workspace.Relations.LinkAsync(message.Id, _conversationId, RelationKind.PartOf);

        foreach (var picture in _pending)
            await _workspace.Relations.LinkAsync(message.Id, picture.Id, RelationKind.Attachment);

        _pending.Clear();
        RefreshAttachments();

        Composer.Text = "";
        await UpdateCountAsync();
        await ReloadAsync();

        Scroller.UpdateLayout();
        Scroller.ChangeView(null, Scroller.ScrollableHeight, null);
    }

    private static string FirstLine(string text, int pictures)
    {
        var line = text.Split('\n')[0].Trim();

        if (line.Length == 0)
            line = pictures switch { 0 => "Message", 1 => "Picture", _ => $"{pictures} pictures" };

        return line.Length > 90 ? line[..90] + "…" : line;
    }

    private async Task UpdateCountAsync()
    {
        if (_conversation is null) return;

        var count = await _workspace.Objects.CountAsync(new CampusQuery { ParentId = _conversationId });

        _payload.MessageCount = count;
        _payload.LastActivityAt = DateTimeOffset.UtcNow;
        _conversation.Payload = _payload;

        await _workspace.Objects.SaveAsync(_conversation);
    }

    // ------------------------------------------------------------------------ pictures

    private async Task AttachAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        var results = await _import.ImportAsync(paths, _conversation?.SubjectId);

        foreach (var result in results)
        {
            if (result.Created is { } created) _pending.Add(created);
            else if (result.Failure is { } why)
                Notifications.Show($"{result.FileName}: {why}", NoticeKind.Error);
        }

        RefreshAttachments();
    }

    /// <summary>Draws what is waiting to be sent, each with a way to take it back off.</summary>
    private void RefreshAttachments()
    {
        Attachments.Items.Clear();
        Attachments.Visibility = _pending.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var picture in _pending)
        {
            var frame = new Border
            {
                Width = 96,
                Height = 72,
                CornerRadius = new CornerRadius(8),
                Background = Brush(ThemeTokens.Fill.Quaternary),
            };

            var hash = picture.PayloadAs<FilePayload>()?.ThumbnailHash;
            _ = LoadInto(frame, hash);

            var remove = new Button
            {
                Style = (Style)Application.Current.Resources["Button.Icon"],
                Content = new CampusIcon
                {
                    Symbol = CampusSymbols.Close,
                    IconSize = 13,
                    Foreground = Brush(ThemeTokens.Label.Primary),
                },
                Width = 24,
                Height = 24,
                MinWidth = 0,
                MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
                Background = Brush(ThemeTokens.Background.Secondary),
                CornerRadius = new CornerRadius(12),
            };

            AutomationProperties.SetName(remove, $"Remove {picture.Title}");

            var current = picture;
            remove.Click += (_, _) =>
            {
                // Off the message, not out of the workspace: it was imported the moment it was
                // picked, and it is a file in the library now whether or not it is sent.
                _pending.Remove(current);
                RefreshAttachments();
            };

            var cell = new Grid();
            cell.Children.Add(frame);
            cell.Children.Add(remove);

            Attachments.Items.Add(cell);
        }
    }

    private async Task LoadInto(Border frame, string? thumbnailHash)
    {
        var image = await _import.LoadThumbnailAsync(thumbnailHash, 200);
        if (image is not null) frame.Child = new Image { Source = image, Stretch = Stretch.UniformToFill };
    }

    // ------------------------------------------------------------------------ actions

    private async Task FollowAsync(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "mailto")
        {
            await Launcher.LaunchUriAsync(uri);
            return;
        }

        var target = await _workspace.Relations.FindByTitleAsync(url.Trim('[', ']'));
        if (target is { } id) _router.Open(id);
        else Notifications.Show($"Nothing here is called “{url}”.", NoticeKind.Warning);
    }

    private async Task EditAsync(CampusObject entity)
    {
        var payload = entity.PayloadAs<MessagePayload>() ?? new MessagePayload();

        var input = new TextBox
        {
            Text = payload.Body,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 260,
            Width = 480,
            Style = (Style)Application.Current.Resources["Input.Text"],
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Edit this message",
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await App.GetService<VersionService>().SnapshotAsync(entity, "before edit");

        payload.Body = input.Text;
        entity.Payload = payload;
        entity.Title = FirstLine(input.Text, payload.Attachments.Count);

        await _workspace.Objects.SaveAsync(entity);
        await _workspace.Relations.SyncDerivedLinksAsync(entity.Id, input.Text);
        await ReloadAsync();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Frame?.CanGoBack == true) Frame.GoBack();
        else Frame?.Navigate(typeof(ConversationsPage));
    }

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendAsync();

    private async void OnComposerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter adds the message; Shift+Enter is a new line. A pasted answer is usually many
        // lines long, and it arrives through paste rather than through this, so Enter is free.
        if (e.Key != VirtualKey.Enter) return;

        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (shift) return;

        e.Handled = true;
        await SendAsync();
    }

    /// <summary>
    /// Catches a picture pasted into the composer.
    ///
    /// A screenshot of a question is on the clipboard far more often than it is in a file, and
    /// making somebody save it to disk first in order to attach it is a step that exists only
    /// because the program could not be bothered.
    /// </summary>
    private void OnComposerPaste(object sender, TextControlPasteEventArgs e)
    {
        var view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
        if (!view.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap)) return;

        e.Handled = true;
        _ = PasteImageAsync(view);
    }

    private async Task PasteImageAsync(Windows.ApplicationModel.DataTransfer.DataPackageView view)
    {
        try
        {
            var reference = await view.GetBitmapAsync();
            using var stream = await reference.OpenReadAsync();

            // Through a real file on disk because that is what the import pipeline takes, and
            // routing it through the pipeline is what makes a pasted screenshot a first-class
            // file — hashed, encrypted, thumbnailed and searchable like any other.
            var path = Path.Combine(Path.GetTempPath(),
                $"campus-paste-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png");

            var bytes = new byte[stream.Size];
            using (var reader = new Windows.Storage.Streams.DataReader(stream))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }

            await File.WriteAllBytesAsync(path, bytes);
            await AttachAsync([path]);

            try { File.Delete(path); } catch (IOException) { /* the copy in the vault is the one that matters */ }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Notifications.Show("That picture could not be read.", NoticeKind.Error);
        }
    }

    private async void OnAttachClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));

        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".heic" })
            picker.FileTypeFilter.Add(extension);

        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;

        await AttachAsync(files.Select(f => f.Path).ToList());
    }

    private async void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_conversation is null) return;

        _payload.Closed = !_payload.Closed;
        _conversation.Payload = _payload;
        _conversation.Status = _payload.Closed ? ObjectStatus.Completed : ObjectStatus.InProgress;

        await _workspace.Objects.SaveAsync(_conversation);
        Notifications.Show(_payload.Closed ? "Conversation closed." : "Conversation reopened.");
        await ReloadAsync();
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (_conversation is null || sender is not FrameworkElement anchor) return;

        var menu = ObjectCommands.Build(_conversation, XamlRoot, ReloadAsync);

        menu.Items.Insert(1, ObjectCommands.Item("Rename the other side", CampusSymbols.Rename,
            async () =>
            {
                var input = new TextBox
                {
                    Text = _payload.With ?? "",
                    PlaceholderText = DefaultNameFor(_payload.ConversationKind),
                    Style = (Style)Application.Current.Resources["Input.Text"],
                    Width = 320,
                };

                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Who is this with?",
                    Content = input,
                    PrimaryButtonText = "Save",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                };

                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

                _payload.With = input.Text.Trim() is { Length: > 0 } name ? name : null;
                _conversation.Payload = _payload;
                await _workspace.Objects.SaveAsync(_conversation);
                await ReloadAsync();
            }));

        menu.ShowAt(anchor);
    }

    // ------------------------------------------------------------------------ naming

    private string NameOfOther() =>
        _payload.With is { Length: > 0 } name ? name : DefaultNameFor(_payload.ConversationKind);

    internal static string DefaultNameFor(ConversationKind kind) => kind switch
    {
        ConversationKind.Teacher => "The teacher",
        ConversationKind.Assistant => "ChatGPT",
        ConversationKind.Classmate => "Classmate",
        _ => "Them",
    };

    internal static string SymbolFor(ConversationKind kind) => kind switch
    {
        ConversationKind.Teacher => CampusSymbols.Teacher,
        ConversationKind.Assistant => CampusSymbols.Assistant,
        ConversationKind.Classmate => CampusSymbols.Person,
        _ => CampusSymbols.Conversation,
    };

    internal static string Describe(ConversationPayload payload) => payload.ConversationKind switch
    {
        ConversationKind.Teacher => "With a teacher",
        ConversationKind.Assistant => "With an assistant",
        ConversationKind.Classmate => "With a classmate",
        _ => "Conversation",
    };

    private static Brush Brush(string token) => (Brush)Application.Current.Resources[token];
    private static FontFamily Font(string key) => (FontFamily)Application.Current.Resources[key];
}
