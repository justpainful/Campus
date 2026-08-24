using Campus.Desktop.Design;
using Campus.Desktop.Design.Icons;
using Campus.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Campus.Desktop.Views;

public sealed partial class ThemeGalleryPage : Page
{
    private readonly ThemeService _theme = App.GetService<ThemeService>();
    private bool _loading = true;

    public ThemeGalleryPage()
    {
        InitializeComponent();

        BuildSwatches(BackgroundSwatches,
            ("Background.Primary", ThemeTokens.Background.Primary),
            ("Background.Secondary", ThemeTokens.Background.Secondary),
            ("Background.Tertiary", ThemeTokens.Background.Tertiary));

        BuildSwatches(GroupedSwatches,
            ("GroupedBackground.Primary", ThemeTokens.GroupedBackground.Primary),
            ("GroupedBackground.Secondary", ThemeTokens.GroupedBackground.Secondary),
            ("GroupedBackground.Tertiary", ThemeTokens.GroupedBackground.Tertiary));

        BuildSwatches(SurfaceSwatches,
            ("Surface.Primary", ThemeTokens.Surface.Primary),
            ("Surface.Secondary", ThemeTokens.Surface.Secondary),
            ("Surface.Tertiary", ThemeTokens.Surface.Tertiary),
            ("Surface.Elevated", ThemeTokens.Surface.Elevated));

        BuildSwatches(FillSwatches,
            ("Fill.Primary", ThemeTokens.Fill.Primary),
            ("Fill.Secondary", ThemeTokens.Fill.Secondary),
            ("Fill.Tertiary", ThemeTokens.Fill.Tertiary),
            ("Fill.Quaternary", ThemeTokens.Fill.Quaternary));

        BuildSwatches(StateSwatches,
            ("Accent.Primary", ThemeTokens.Accent.Primary),
            ("Accent.Hover", ThemeTokens.Accent.Hover),
            ("Accent.Pressed", ThemeTokens.Accent.Pressed),
            ("Destructive.Primary", ThemeTokens.Destructive.Primary),
            ("Warning.Primary", ThemeTokens.Warning.Primary),
            ("Success.Primary", ThemeTokens.Success.Primary),
            ("Info.Primary", ThemeTokens.Info.Primary),
            ("Selected.Fill", ThemeTokens.State.SelectedFill),
            ("Disabled.Fill", ThemeTokens.State.DisabledFill));

        BuildSwatches(SubjectSwatches, ThemeTokens.Subject.All
            .Select(token => (ThemeTokens.Subject.ToName(token), token)).ToArray());

        BuildIconGrid();

        (_theme.Appearance switch
        {
            AppearanceMode.Light => ModeLight,
            AppearanceMode.Dark => ModeDark,
            _ => ModeSystem,
        }).IsChecked = true;

        Loaded += (_, _) => { _loading = false; };
    }

    /// <summary>
    /// One row per role: a filled chip, the role name, and the value it currently resolves to.
    /// Showing the resolved value is what makes the gallery useful for review rather than
    /// merely decorative.
    /// </summary>
    private void BuildSwatches(Panel host, params (string Name, string Token)[] roles)
    {
        host.Children.Clear();

        foreach (var (name, token) in roles)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var chip = new Border
            {
                Width = 56,
                Height = 30,
                CornerRadius = new CornerRadius(7),
                Background = (Brush)Application.Current.Resources[token],
                // A hairline keeps a chip visible when its role happens to match the page behind it.
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources[ThemeTokens.Separator.Standard],
            };
            Grid.SetColumn(chip, 0);
            row.Children.Add(chip);

            var label = new TextBlock
            {
                Text = name,
                FontFamily = (FontFamily)Application.Current.Resources["Theme.Font.Mono"],
                FontSize = (double)Application.Current.Resources["Theme.Text.Callout"],
                Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Primary],
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            var value = new TextBlock
            {
                Text = Describe(token),
                FontFamily = (FontFamily)Application.Current.Resources["Theme.Font.Mono"],
                FontSize = (double)Application.Current.Resources["Theme.Text.Caption1"],
                Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Tertiary],
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(value, 2);
            row.Children.Add(value);

            host.Children.Add(row);
        }
    }

    private static string Describe(string token)
    {
        if (Application.Current.Resources[token] is not SolidColorBrush brush) return string.Empty;
        var c = brush.Color;
        return c.A == 255
            ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
            : $"#{c.R:X2}{c.G:X2}{c.B:X2} · {c.A / 255d:P0}";
    }

    private void BuildIconGrid()
    {
        foreach (var symbol in IconRegistry.AllSymbols)
        {
            var tile = new Border
            {
                Width = 84,
                Height = 68,
                CornerRadius = new CornerRadius(8),
                Background = (Brush)Application.Current.Resources[ThemeTokens.Fill.Quaternary],
            };

            var stack = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            stack.Children.Add(new CampusIcon
            {
                Symbol = symbol,
                IconSize = 22,
                Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Primary],
            });
            stack.Children.Add(new TextBlock
            {
                Text = symbol,
                FontSize = 9,
                Foreground = (Brush)Application.Current.Resources[ThemeTokens.Label.Tertiary],
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 78,
                TextWrapping = TextWrapping.NoWrap,
            });

            tile.Child = stack;
            ToolTipService.SetToolTip(tile, symbol);
            IconGrid.Items.Add(tile);
        }
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not RadioButton { Tag: string tag }) return;

        _theme.Appearance = tag switch
        {
            "Light" => AppearanceMode.Light,
            "Dark" => AppearanceMode.Dark,
            _ => AppearanceMode.System,
        };

        // The chips read their colours once, so re-resolve them after the theme flips.
        DispatcherQueue.TryEnqueue(RefreshResolvedValues);
    }

    private void RefreshResolvedValues()
    {
        BuildSwatches(BackgroundSwatches,
            ("Background.Primary", ThemeTokens.Background.Primary),
            ("Background.Secondary", ThemeTokens.Background.Secondary),
            ("Background.Tertiary", ThemeTokens.Background.Tertiary));
        BuildSwatches(GroupedSwatches,
            ("GroupedBackground.Primary", ThemeTokens.GroupedBackground.Primary),
            ("GroupedBackground.Secondary", ThemeTokens.GroupedBackground.Secondary),
            ("GroupedBackground.Tertiary", ThemeTokens.GroupedBackground.Tertiary));
        BuildSwatches(SurfaceSwatches,
            ("Surface.Primary", ThemeTokens.Surface.Primary),
            ("Surface.Secondary", ThemeTokens.Surface.Secondary),
            ("Surface.Tertiary", ThemeTokens.Surface.Tertiary),
            ("Surface.Elevated", ThemeTokens.Surface.Elevated));
        BuildSwatches(FillSwatches,
            ("Fill.Primary", ThemeTokens.Fill.Primary),
            ("Fill.Secondary", ThemeTokens.Fill.Secondary),
            ("Fill.Tertiary", ThemeTokens.Fill.Tertiary),
            ("Fill.Quaternary", ThemeTokens.Fill.Quaternary));
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Frame?.CanGoBack == true) Frame.GoBack();
    }
}
