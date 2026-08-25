using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Domain;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Shell;

/// <summary>One thing open in the workspace.</summary>
public sealed class WorkspaceTab
{
    /// <summary>A destination id, or the object's id — whichever this tab is showing.</summary>
    public required string Key { get; init; }

    public required string Title { get; set; }
    public required string Symbol { get; set; }
    public required Type PageType { get; init; }
    public object? Parameter { get; init; }

    /// <summary>
    /// A preview tab is the one that gets replaced when the next thing is opened. Clicking
    /// through a list should not leave twenty tabs behind; editing something should keep it.
    /// </summary>
    public bool IsPreview { get; set; }
}

/// <summary>
/// The tab strip.
///
/// Modelled on the editor rather than the browser: one preview tab that keeps being replaced as
/// you look through a list, and tabs that stay only once you have done something in them. That is
/// what stops a morning's reading from leaving forty tabs open.
/// </summary>
public sealed class WorkspaceTabs
{
    private readonly List<WorkspaceTab> _tabs = [];
    private readonly ItemsControl _strip;
    private readonly FrameworkElement _container;

    public WorkspaceTabs(ItemsControl strip, FrameworkElement container)
    {
        _strip = strip;
        _container = container;
    }

    public IReadOnlyList<WorkspaceTab> Tabs => _tabs;
    public WorkspaceTab? Active { get; private set; }

    /// <summary>Raised when a tab is chosen, so the shell can put its page in the frame.</summary>
    public event EventHandler<WorkspaceTab>? Activated;

    /// <summary>Raised when the last tab closes and the workspace has nothing to show.</summary>
    public event EventHandler? Emptied;

    /// <summary>
    /// Opens something. An existing tab for the same thing is reused rather than duplicated;
    /// otherwise it either replaces the preview tab or becomes a tab of its own.
    /// </summary>
    public void Open(WorkspaceTab tab, bool pinned = false)
    {
        var existing = _tabs.FirstOrDefault(t => t.Key == tab.Key);

        if (existing is not null)
        {
            if (pinned) existing.IsPreview = false;
            Activate(existing);
            return;
        }

        if (!pinned)
        {
            var preview = _tabs.FirstOrDefault(t => t.IsPreview);
            if (preview is not null) _tabs.Remove(preview);
            tab.IsPreview = true;
        }

        _tabs.Add(tab);
        Activate(tab);
    }

    public void Activate(WorkspaceTab tab)
    {
        Active = tab;
        Render();
        Activated?.Invoke(this, tab);
    }

    /// <summary>Marks the current tab as one worth keeping. Called when it is edited.</summary>
    public void PinActive()
    {
        if (Active is null || !Active.IsPreview) return;
        Active.IsPreview = false;
        Render();
    }

    public void Close(WorkspaceTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;

        _tabs.RemoveAt(index);

        if (Active != tab)
        {
            Render();
            return;
        }

        if (_tabs.Count == 0)
        {
            Active = null;
            Render();
            Emptied?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Closing the active tab moves to its neighbour, the way every editor does.
        Activate(_tabs[Math.Clamp(index, 0, _tabs.Count - 1)]);
    }

    public void CloseActive()
    {
        if (Active is not null) Close(Active);
    }

    public void CloseOthers()
    {
        if (Active is null) return;

        _tabs.RemoveAll(t => t != Active);
        Render();
    }

    public void CloseAll()
    {
        _tabs.Clear();
        Active = null;
        Render();
        Emptied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves to the next or previous tab, wrapping round.</summary>
    public void Cycle(int direction)
    {
        if (_tabs.Count < 2 || Active is null) return;

        var index = _tabs.IndexOf(Active);
        var next = ((index + direction) % _tabs.Count + _tabs.Count) % _tabs.Count;
        Activate(_tabs[next]);
    }

    /// <summary>Renames a tab whose object has been renamed underneath it.</summary>
    public void Rename(string key, string title)
    {
        var tab = _tabs.FirstOrDefault(t => t.Key == key);
        if (tab is null || tab.Title == title) return;

        tab.Title = title;
        Render();
    }

    // ------------------------------------------------------------------------ drawing

    private void Render()
    {
        _strip.Items.Clear();
        _container.Visibility = _tabs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var tab in _tabs) _strip.Items.Add(Build(tab));
    }

    private FrameworkElement Build(WorkspaceTab tab)
    {
        var isActive = tab == Active;

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
        };

        content.Children.Add(new CampusIcon
        {
            Symbol = tab.Symbol,
            IconSize = 15,
            Foreground = Brush(isActive ? ThemeTokens.Label.Primary : ThemeTokens.Label.Tertiary),
            VerticalAlignment = VerticalAlignment.Center,
        });

        content.Children.Add(new TextBlock
        {
            Text = tab.Title,
            FontFamily = Font("Theme.Font.Text"),
            FontSize = 12.5,
            // A preview tab is italic, exactly as an editor shows one — the same signal, so it
            // needs no explaining.
            FontStyle = tab.IsPreview ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Brush(isActive ? ThemeTokens.Label.Primary : ThemeTokens.Label.Secondary),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxWidth = 150,
        });

        var close = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Plain"],
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new CampusIcon
            {
                Symbol = CampusSymbols.Close,
                IconSize = 11,
                Foreground = Brush(ThemeTokens.Label.Tertiary),
            },
        };
        AutomationProperties.SetName(close, $"Close {tab.Title}");
        close.Click += (_, _) => Close(tab);
        content.Children.Add(close);

        var container = new Grid();

        container.Children.Add(new Border
        {
            Background = Brush(isActive ? ThemeTokens.Background.Primary : ThemeTokens.Background.Secondary),
        });

        // The active tab is marked with a rail on top rather than a border all the way round,
        // which would turn a row of tabs into a row of boxes.
        if (isActive)
        {
            container.Children.Add(new Border
            {
                Height = 2,
                VerticalAlignment = VerticalAlignment.Top,
                Background = Brush(ThemeTokens.Accent.Primary),
            });
        }

        content.Margin = new Thickness(14, 0, 8, 0);
        container.Children.Add(content);

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["Button.Plain"],
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Content = container,
            MinWidth = 0,
            Height = (double)Application.Current.Resources["Theme.Size.TabHeight"],
        };

        AutomationProperties.SetName(button, tab.Title);
        button.Click += (_, _) => Activate(tab);

        // Double-clicking a preview tab keeps it, which is the gesture people already know.
        button.DoubleTapped += (_, _) =>
        {
            tab.IsPreview = false;
            Render();
        };

        button.RightTapped += (_, e) => ShowMenu(button, tab, e);
        return button;
    }

    private void ShowMenu(FrameworkElement anchor, WorkspaceTab tab, RightTappedRoutedEventArgs e)
    {
        var menu = new MenuFlyout();

        var close = new MenuFlyoutItem { Text = L.T("close") };
        close.Click += (_, _) => Close(tab);
        menu.Items.Add(close);

        var others = new MenuFlyoutItem { Text = L.T("close.others") };
        others.Click += (_, _) => { Activate(tab); CloseOthers(); };
        menu.Items.Add(others);

        var all = new MenuFlyoutItem { Text = L.T("close.all") };
        all.Click += (_, _) => CloseAll();
        menu.Items.Add(all);

        if (tab.IsPreview)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var keep = new MenuFlyoutItem { Text = L.T("keep.open") };
            keep.Click += (_, _) => { tab.IsPreview = false; Render(); };
            menu.Items.Add(keep);
        }

        menu.ShowAt(anchor, e.GetPosition(anchor));
        e.Handled = true;
    }

    private static Brush Brush(string token) => (Brush)Application.Current.Resources[token];
    private static FontFamily Font(string key) => (FontFamily)Application.Current.Resources[key];
}
