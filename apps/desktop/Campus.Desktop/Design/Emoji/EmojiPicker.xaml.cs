using System.Collections.ObjectModel;
using Campus.Desktop.Design.Icons;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Campus.Desktop.Design.Emoji;

/// <summary>One square in the grid. Display tracks the chosen tone without reloading the grid.</summary>
public sealed partial class EmojiCell(EmojiEntry entry, string sequence, string display)
    : ObservableObject
{
    public EmojiEntry Entry { get; } = entry;
    public string Key => Entry.Key;
    public string Name => Entry.Name;
    public bool HasTones => Entry.Tone != ToneKind.None;

    /// <summary>Code points of the variant being shown. Artwork is looked up by this.</summary>
    [ObservableProperty]
    public partial string Sequence { get; set; } = sequence;

    /// <summary>The characters themselves, used only when no pack has this emoji.</summary>
    [ObservableProperty]
    public partial string Display { get; set; } = display;

    public void Show(string sequence, string display)
    {
        Sequence = sequence;
        Display = display;
    }
}

/// <summary>
/// The emoji picker: every emoji Unicode defines, every skin tone, search, pinning, hand
/// ordering, and a press-and-hold gesture for tones that matches the phone keyboard.
///
/// Glyphs come from the active artwork pack, never from the system emoji font, and there is no
/// fallback to one. See docs/emoji.md for how to build a pack from a font you own.
/// </summary>
public sealed partial class EmojiPicker : UserControl
{
    private static EmojiCatalogue? _catalogue;

    private readonly EmojiPreferences _preferences = EmojiPreferences.Load();
    private readonly ObservableCollection<EmojiCell> _cells = [];
    private readonly DispatcherQueueTimer _holdTimer;

    private string _activeGroup = RecentGroup;
    private EmojiCell? _held;
    private EmojiCell? _previewed;

    private const string RecentGroup = "__recent";
    private const string PinnedGroup = "__pinned";

    /// <summary>Raised when an emoji is chosen, with the exact text to insert.</summary>
    public event EventHandler<string>? EmojiPicked;

    public EmojiPicker()
    {
        InitializeComponent();

        _catalogue ??= EmojiCatalogue.Load(
            Path.Combine(AppContext.BaseDirectory, "Assets", "emoji.dat"));

        Cells.ItemsSource = _cells;

        // Press-and-hold. A timer rather than a gesture recogniser, so the same code path serves
        // mouse, pen and touch, and so the delay matches the platform's own long-press.
        _holdTimer = DispatcherQueue.CreateTimer();
        _holdTimer.Interval = TimeSpan.FromMilliseconds(450);
        _holdTimer.IsRepeating = false;
        _holdTimer.Tick += (_, _) => OpenToneFlyoutForHeld();

        BuildGroupTabs();
        BuildTonePanel();
        UpdateToneButton();
        ShowGroup(_preferences.Recents.Count > 0 ? RecentGroup : FirstRealGroup());

        Loaded += (_, _) => SearchBox.Focus(FocusState.Programmatic);

        EmojiPackStore.Current.ActivePackChanged += OnPackChanged;
        Unloaded += (_, _) => EmojiPackStore.Current.ActivePackChanged -= OnPackChanged;
    }

    private static EmojiCatalogue Catalogue => _catalogue!;

    private void OnPackChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        UpdateToneButton();
        BuildTonePanel();
        ShowGroup(_activeGroup.Length > 0 ? _activeGroup : FirstRealGroup());
    });

    private string FirstRealGroup()
        => Catalogue.Groups.Count > 0 ? Catalogue.Groups[0].Name : RecentGroup;

    // ------------------------------------------------------------------- group tabs

    private void BuildGroupTabs()
    {
        GroupTabs.Children.Clear();

        AddTab(RecentGroup, CampusSymbols.Clock, "Recently used");
        AddTab(PinnedGroup, CampusSymbols.Pin, "Pinned");

        foreach (var group in Catalogue.Groups)
            AddTab(group.Name, GroupSymbol(group.Name), group.Name);
    }

    private void AddTab(string key, string symbol, string tooltip)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Icon"],
            Tag = key,
            Content = new CampusIcon { Symbol = symbol, IconSize = 18 },
        };
        AutomationProperties.SetName(button, tooltip);
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => { SearchBox.Text = string.Empty; ShowGroup(key); };
        GroupTabs.Children.Add(button);
    }

    /// <summary>Maps a Unicode group to one of Campus's own icons rather than to an emoji.</summary>
    private static string GroupSymbol(string group) => group switch
    {
        "Smileys & Emotion" => CampusSymbols.Emoji,
        "People & Body" => CampusSymbols.Person,
        "Animals & Nature" => CampusSymbols.Target,
        "Food & Drink" => CampusSymbols.Collection,
        "Travel & Places" => CampusSymbols.Planner,
        "Activities" => CampusSymbols.Goals,
        "Objects" => CampusSymbols.Files,
        "Symbols" => CampusSymbols.Command,
        "Flags" => CampusSymbols.Flag,
        _ => CampusSymbols.Emoji,
    };

    private void HighlightActiveTab()
    {
        foreach (var child in GroupTabs.Children)
        {
            if (child is not Button button) continue;
            var active = (string?)button.Tag == _activeGroup;
            button.Background = active
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[ThemeTokens.Fill.Tertiary]
                : null;
            if (button.Content is CampusIcon icon)
            {
                icon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                    active ? ThemeTokens.Label.Primary : ThemeTokens.Label.Secondary];
                icon.Weight = active ? IconWeight.Semibold : IconWeight.Regular;
            }
        }
    }

    // ----------------------------------------------------------------------- content

    private void ShowGroup(string key)
    {
        _activeGroup = key;
        HighlightActiveTab();

        var entries = key switch
        {
            RecentGroup => _preferences.Recents
                .Select(Catalogue.Find).Where(e => e is not null).Cast<EmojiEntry>().ToList(),
            PinnedGroup => _preferences.Pinned
                .Select(Catalogue.Find).Where(e => e is not null).Cast<EmojiEntry>().ToList(),
            _ => Sorted(Catalogue.Groups.FirstOrDefault(g => g.Name == key)?.Entries ?? []),
        };

        Fill(entries);

        NoResults.Text = key switch
        {
            RecentGroup => "Nothing used yet. Anything you pick shows up here.",
            PinnedGroup => "Nothing pinned. Pin an emoji to keep it here.",
            _ => "Nothing matches that.",
        };
    }

    /// <summary>Recents and pinned keep their own order; everything else follows the sort mode.</summary>
    private List<EmojiEntry> Sorted(IEnumerable<EmojiEntry> entries) => _preferences.SortMode switch
    {
        EmojiSortMode.Frequency => entries
            .OrderByDescending(e => _preferences.UseCount(e.Key))
            .ThenBy(e => e.Name)
            .ToList(),
        EmojiSortMode.Name => entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        _ => entries.ToList(),
    };

    private void Fill(IReadOnlyList<EmojiEntry> entries)
    {
        var store = EmojiPackStore.Current;

        _cells.Clear();
        foreach (var entry in entries)
        {
            var tone = _preferences.ToneFor(entry.Key);
            var sequence = entry.KeyForTone(tone);

            // A pack built from an older font has none of the emoji Unicode added since. Those
            // are left out rather than shown as squares that will not fill.
            if (!store.Has(sequence))
            {
                // The chosen tone may be missing even though the base emoji is present.
                sequence = entry.Key;
                if (!store.Has(sequence)) continue;
            }

            _cells.Add(new EmojiCell(entry, sequence, entry.ForTone(tone)));
        }

        // With no artwork installed, the grid is not shown at all. Campus will not quietly fall
        // back to the system emoji font — that is the whole reason packs exist.
        var noPack = EmojiPackStore.Current.Active is null;
        var empty = _cells.Count == 0;

        NoPackNotice.Visibility = noPack ? Visibility.Visible : Visibility.Collapsed;
        NoResults.Visibility = !noPack && empty ? Visibility.Visible : Visibility.Collapsed;
        Cells.Visibility = !noPack && !empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            ShowGroup(_activeGroup);
            return;
        }

        _activeGroup = string.Empty;
        HighlightActiveTab();
        NoResults.Text = L.T("nothing.matches.that");
        Fill(Catalogue.Search(query));
    }

    // -------------------------------------------------------------------- selection

    private void OnEmojiClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EmojiCell cell }) return;
        Pick(cell, cell.Display);
    }

    private void Pick(EmojiCell cell, string text)
    {
        _preferences.RecordUse(cell.Key);
        EmojiPicked?.Invoke(this, text);
    }

    private void OnEmojiPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EmojiCell cell }) return;
        ShowPreview(cell);
    }

    private void ShowPreview(EmojiCell cell)
    {
        _previewed = cell;
        PreviewGlyph.Sequence = cell.Sequence;
        PreviewGlyph.Text = cell.Display;
        PreviewName.Text = cell.Name;
        PreviewHint.Text = cell.HasTones
            ? "Press and hold for skin tones"
            : cell.Entry.Subgroup.Replace('-', ' ');

        PinButton.Visibility = Visibility.Visible;
        PinIcon.Variant = _preferences.IsPinned(cell.Key) ? IconVariant.Filled : IconVariant.Outline;
        PinIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            _preferences.IsPinned(cell.Key) ? ThemeTokens.Accent.Primary : ThemeTokens.Label.Secondary];
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (_previewed is null) return;
        _preferences.TogglePin(_previewed.Key);
        ShowPreview(_previewed);
        if (_activeGroup == PinnedGroup) ShowGroup(PinnedGroup);
    }

    // ------------------------------------------------------------ press and hold

    private void OnEmojiPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EmojiCell cell }) return;
        if (!cell.HasTones) return;

        _held = cell;
        _holdTimer.Start();
    }

    private void OnEmojiPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _holdTimer.Stop();
        _held = null;
    }

    private void OnEmojiRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EmojiCell cell } button) return;
        if (!cell.HasTones) return;
        ShowToneFlyout(button, cell);
        e.Handled = true;
    }

    private void OpenToneFlyoutForHeld()
    {
        if (_held is null) return;

        // The container is found by data rather than kept from the pointer event, because the
        // grid virtualises and the button that was pressed may already have been recycled.
        var container = Cells.ContainerFromItem(_held) as GridViewItem;
        if (container?.ContentTemplateRoot is Button button) ShowToneFlyout(button, _held);
        _held = null;
    }

    private void ShowToneFlyout(FrameworkElement anchor, EmojiCell cell)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Top };

        foreach (var (key, text, tone) in cell.Entry.ToneChoices())
        {
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["Button.Plain"],
                Width = 40,
                Height = 40,
                Padding = new Thickness(0),
                MinWidth = 0,
                Content = new CampusEmoji { Sequence = key, Text = text, EmojiSize = 26 },
            };
            AutomationProperties.SetName(button, $"{cell.Name}, {ToneName(tone)}");

            button.Click += (_, _) =>
            {
                // Choosing a tone here also remembers it for this emoji, so the next time it
                // appears in the grid it is already the tone you meant.
                _preferences.SetToneFor(cell.Key, tone);
                cell.Show(key, text);
                flyout.Hide();
                Pick(cell, text);
            };

            panel.Children.Add(button);
        }

        flyout.ShowAt(anchor);
    }

    private static string ToneName(SkinTone tone) => tone switch
    {
        SkinTone.Light => "light skin tone",
        SkinTone.MediumLight => "medium-light skin tone",
        SkinTone.Medium => "medium skin tone",
        SkinTone.MediumDark => "medium-dark skin tone",
        SkinTone.Dark => "dark skin tone",
        _ => "default skin tone",
    };

    // ----------------------------------------------------------------- default tone

    private void BuildTonePanel()
    {
        TonePanel.Children.Clear();

        // The raised hand is the conventional swatch: it shows the tone plainly at small sizes.
        var hand = Catalogue.Find("270B") ?? Catalogue.Find("1F44B");
        if (hand is null) return;

        for (var i = 0; i <= 5; i++)
        {
            var tone = (SkinTone)i;
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["Button.Plain"],
                Width = 40,
                Height = 40,
                Padding = new Thickness(0),
                MinWidth = 0,
                Content = new CampusEmoji
                {
                    Sequence = hand.KeyForTone(tone),
                    Text = hand.ForTone(tone),
                    EmojiSize = 24,
                },
            };
            AutomationProperties.SetName(button, ToneName(tone));

            button.Click += (_, _) =>
            {
                _preferences.DefaultTone = tone;
                UpdateToneButton();
                ToneFlyout.Hide();
                ShowGroup(_activeGroup.Length > 0 ? _activeGroup : FirstRealGroup());
            };

            TonePanel.Children.Add(button);
        }
    }

    private void UpdateToneButton()
    {
        var hand = Catalogue.Find("270B") ?? Catalogue.Find("1F44B");
        if (hand is null) return;

        ToneButtonGlyph.Sequence = hand.KeyForTone(_preferences.DefaultTone);
        ToneButtonGlyph.Text = hand.ForTone(_preferences.DefaultTone);

        var noPack = EmojiPackStore.Current.Active is null;
        ToneButtonPlaceholder.Visibility = noPack ? Visibility.Visible : Visibility.Collapsed;
        ToneButton.IsEnabled = !noPack;
    }

    // ----------------------------------------------------------------------- sorting

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string mode }) return;
        _preferences.SortMode = Enum.Parse<EmojiSortMode>(mode);
        ShowGroup(_activeGroup.Length > 0 ? _activeGroup : FirstRealGroup());
    }

    private void OnClearRecentsClick(object sender, RoutedEventArgs e)
    {
        _preferences.ClearRecents();
        if (_activeGroup == RecentGroup) ShowGroup(RecentGroup);
    }

    public static Visibility ToneMarkVisibility(bool hasTones)
        => hasTones ? Visibility.Visible : Visibility.Collapsed;
}
