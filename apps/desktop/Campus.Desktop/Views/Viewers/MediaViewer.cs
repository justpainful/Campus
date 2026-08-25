using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Desktop.Services;
using Campus.Documents;
using Campus.Domain;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Campus.Desktop.Views.Viewers;

/// <summary>
/// Plays a recording — a lecture capture, a voice memo, a video a lecturer shared.
///
/// The important part for study is not the player, it is the timestamp: a note taken at 41:20 is
/// worth far more than the same note taken with no reference to where in the two-hour recording
/// it belongs. Taking one is a single click, and it makes a note that links back here.
/// </summary>
public sealed class MediaViewer : Grid, IContentViewer
{
    private readonly WorkspaceService _workspace = App.GetService<WorkspaceService>();
    private readonly MediaPlayerElement _player = new();
    private readonly TextBlock _position = ViewerChrome.ToolLabel();
    private readonly DispatcherQueueTimer _ticker;

    private MediaPlayer? _media;
    private CampusObject? _entity;
    private Stream? _content;

    public MediaViewer()
    {
        Background = ViewerChrome.Brush(ThemeTokens.Background.Secondary);

        _player.AreTransportControlsEnabled = true;
        _player.HorizontalAlignment = HorizontalAlignment.Stretch;
        _player.VerticalAlignment = VerticalAlignment.Stretch;
        AutomationProperties.SetName(_player, L.T("player"));

        Children.Add(_player);

        _ticker = DispatcherQueue.CreateTimer();
        _ticker.Interval = TimeSpan.FromMilliseconds(250);
        _ticker.Tick += (_, _) => UpdatePosition();

        Unloaded += (_, _) => Stop();
    }

    public Task LoadAsync(Stream content, CampusObject entity, FilePayload payload)
    {
        _entity = entity;
        _content = content;

        _media = new MediaPlayer
        {
            // The file is never written to disk in the clear: the player reads the decrypted
            // stream directly, and a seek in the player becomes a seek in the vault.
            Source = MediaSource.CreateFromStream(
                content.AsRandomAccessStream(),
                payload.MimeType.Length > 0 ? payload.MimeType : "video/mp4"),
        };

        _player.SetMediaPlayer(_media);
        _ticker.Start();

        // An audio file has nothing to show, so the space carries the file's own artwork slot
        // rather than a black rectangle.
        if (payload.Media == MediaKind.Audio) ShowAudioBackdrop(entity.Title);

        return Task.CompletedTask;
    }

    private void ShowAudioBackdrop(string title)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 16,
            Margin = new Thickness(0, 0, 0, 80),
        };

        panel.Children.Add(new CampusIcon
        {
            Symbol = CampusSymbols.Audio,
            IconSize = 72,
            Weight = IconWeight.Light,
            Foreground = ViewerChrome.Brush(ThemeTokens.Label.Quaternary),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["Text.Headline"],
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 420,
            TextWrapping = TextWrapping.Wrap,
        });

        Children.Insert(0, panel);
    }

    // ------------------------------------------------------------------------- tools

    public IEnumerable<FrameworkElement> BuildTools()
    {
        yield return _position;

        yield return ViewerChrome.ToolMenu(CampusSymbols.Speed, "Playback speed",
        [
            ("0.5×", () => SetRate(0.5)),
            ("0.75×", () => SetRate(0.75)),
            ("Normal", () => SetRate(1.0)),
            ("1.25×", () => SetRate(1.25)),
            ("1.5×", () => SetRate(1.5)),
            ("2×", () => SetRate(2.0)),
        ], "Normal");

        yield return ViewerChrome.ToolButton(CampusSymbols.SkipBackward, "Back 10 seconds",
            () => Nudge(-10));
        yield return ViewerChrome.ToolButton(CampusSymbols.SkipForward, "Forward 10 seconds",
            () => Nudge(10));
        yield return ViewerChrome.ToolButton(CampusSymbols.Comment, "Note at this moment",
            () => _ = NoteHereAsync());
    }

    private void SetRate(double rate)
    {
        if (_media is not null) _media.PlaybackRate = rate;
    }

    private void Nudge(int seconds)
    {
        if (_media?.PlaybackSession is not { } session) return;

        var target = session.Position + TimeSpan.FromSeconds(seconds);
        session.Position = target < TimeSpan.Zero ? TimeSpan.Zero
            : target > session.NaturalDuration ? session.NaturalDuration
            : target;
    }

    /// <summary>
    /// Pins a note to the current moment.
    ///
    /// Playback pauses first, because a note typed while the lecture keeps going is a note about
    /// something three slides back. The note is an annotation on the recording rather than a
    /// separate object, so it travels with the file and shows up on its timeline.
    /// </summary>
    private async Task NoteHereAsync()
    {
        if (_entity is null || _media?.PlaybackSession is not { } session) return;

        _media.Pause();
        var at = session.Position;

        var input = new TextBox
        {
            PlaceholderText = L.T("what.happens.here"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 120,
            Style = (Style)Application.Current.Resources["Input.Text"],
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Note at {Format(at)}",
            Content = input,
            PrimaryButtonText = L.T("add"),
            CloseButtonText = L.T("cancel"),
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ActualTheme,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (input.Text.Trim().Length == 0) return;

        await _workspace.Annotations.SaveAsync(new Annotation
        {
            ObjectId = _entity.Id,
            Kind = AnnotationKind.TimestampNote,
            Position = at,
            Text = input.Text.Trim(),
        });

        await _workspace.History.RecordAsync(_entity.Id, "annotated", $"note at {Format(at)}");
        Notifications.Show($"Note added at {Format(at)}", NoticeKind.Success);
    }

    private void UpdatePosition()
    {
        if (_media?.PlaybackSession is not { } session) return;

        var duration = session.NaturalDuration;
        _position.Text = duration > TimeSpan.Zero
            ? $"{Format(session.Position)} / {Format(duration)}"
            : Format(session.Position);
    }

    private static string Format(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
        : $"{time.Minutes}:{time.Seconds:00}";

    private void Stop()
    {
        _ticker.Stop();
        _player.SetMediaPlayer(null);

        // The player holds the decrypted stream open; closing it in order matters, because
        // disposing the stream first leaves the player reading from nothing.
        _media?.Dispose();
        _media = null;
        _content = null;
    }
}
