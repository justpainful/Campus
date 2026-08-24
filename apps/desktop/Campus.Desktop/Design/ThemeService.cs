using System.Runtime.InteropServices;
using Campus.Domain;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
// Campus has its own AccessibilitySettings model, so the platform namespace is imported by
// alias rather than wholesale — importing it would make the bare name ambiguous.
using UISettings = Windows.UI.ViewManagement.UISettings;

namespace Campus.Desktop.Design;

/// <summary>
/// Owns the appearance of the whole application: which mode is active, what the system is
/// currently doing, and what the user has asked for accessibility-wise. Every window registers
/// its root here and is re-themed in place — nothing ever needs a restart.
/// </summary>
public sealed class ThemeService
{
    private readonly List<WeakReference<FrameworkElement>> _roots = [];
    private readonly UISettings _uiSettings = new();
    private DispatcherQueue? _dispatcher;
    private AppearanceMode _appearance = AppearanceMode.System;

    public ThemeService()
    {
        // The system can change theme, contrast, animation and transparency preferences while
        // Campus is open, and each of those re-resolves the theme rather than prompting a
        // restart. Every subscription is guarded: several of these WinRT events are only
        // available to apps that own a CoreWindow, which a desktop app does not, and a missing
        // one should cost live updates rather than the whole app.
        TrySubscribe(() => _uiSettings.ColorValuesChanged += (_, _) => Post(OnSystemColoursChanged));
        TrySubscribe(() => _uiSettings.AdvancedEffectsEnabledChanged += (_, _) => Post(RaiseChanged));
        TrySubscribe(() => _uiSettings.AnimationsEnabledChanged += (_, _) => Post(RaiseChanged));
    }

    /// <summary>Fires whenever the effective theme changes, for code that draws rather than binds.</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>The user's accessibility preferences, merged with what the system reports.</summary>
    public AccessibilitySettings Accessibility { get; private set; } = new();

    /// <summary>System / Light / Dark. System is the default and follows Windows live.</summary>
    public AppearanceMode Appearance
    {
        get => _appearance;
        set
        {
            if (_appearance == value) return;
            _appearance = value;
            ApplyToAllRoots();
            RaiseChanged();
        }
    }

    /// <summary>What the theme actually resolves to right now, with System already followed through.</summary>
    public ApplicationTheme EffectiveTheme => _appearance switch
    {
        AppearanceMode.Light => ApplicationTheme.Light,
        AppearanceMode.Dark => ApplicationTheme.Dark,
        _ => Application.Current.RequestedTheme,
    };

    public bool IsDark => EffectiveTheme == ApplicationTheme.Dark;

    /// <summary>
    /// True when Windows is in a high contrast mode. Read from Win32 on demand rather than from
    /// the WinRT AccessibilitySettings class, whose change event a desktop app cannot subscribe to.
    /// </summary>
    public bool SystemHighContrast => QueryHighContrast();

    /// <summary>True when the system has animations switched off.</summary>
    public bool SystemAnimationsEnabled => TryRead(() => _uiSettings.AnimationsEnabled, true);

    /// <summary>True when the system allows transparency effects.</summary>
    public bool SystemTransparencyEnabled => TryRead(() => _uiSettings.AdvancedEffectsEnabled, true);

    public void Initialise(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    /// <summary>Registers a window root so it follows appearance changes for as long as it lives.</summary>
    public void RegisterRoot(FrameworkElement root)
    {
        Prune();
        _roots.Add(new WeakReference<FrameworkElement>(root));
        Apply(root);
    }

    /// <summary>
    /// Applies the user's stored preferences. Anything the system already forces — high contrast,
    /// animations off — wins over the stored value, because the system preference is the one the
    /// user set for every app.
    /// </summary>
    public void ApplySettings(AccessibilitySettings settings)
    {
        Accessibility = new AccessibilitySettings
        {
            UiScale = settings.UiScale,
            TextScale = settings.TextScale,
            ReduceMotion = settings.ReduceMotion || !SystemAnimationsEnabled,
            ReduceTransparency = settings.ReduceTransparency || !SystemTransparencyEnabled,
            IncreaseContrast = settings.IncreaseContrast || SystemHighContrast,
            LargeHitTargets = settings.LargeHitTargets,
            LargeCursor = settings.LargeCursor,
            AlwaysShowFocusRing = settings.AlwaysShowFocusRing,
            DyslexiaFriendlyReading = settings.DyslexiaFriendlyReading,
            ReadingLineSpacing = settings.ReadingLineSpacing,
            ReadingRuler = settings.ReadingRuler,
        };
        ApplyToAllRoots();
        RaiseChanged();
    }

    private void OnSystemColoursChanged()
    {
        // Turning high contrast on or off also changes the system colours, so this is where a
        // contrast change is noticed without a dedicated event to listen to.
        ApplySettings(Accessibility);
        ApplyToAllRoots();
    }

    private void Apply(FrameworkElement root)
    {
        root.RequestedTheme = _appearance switch
        {
            AppearanceMode.Light => ElementTheme.Light,
            AppearanceMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default, // Default follows Windows, and keeps following it.
        };
    }

    private void ApplyToAllRoots()
    {
        Prune();
        foreach (var reference in _roots)
        {
            if (reference.TryGetTarget(out var root)) Apply(root);
        }
    }

    private void Prune() => _roots.RemoveAll(r => !r.TryGetTarget(out _));

    private void RaiseChanged() => ThemeChanged?.Invoke(this, EventArgs.Empty);

    private void Post(Action action)
    {
        if (_dispatcher is null) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    private static void TrySubscribe(Action subscribe)
    {
        try { subscribe(); }
        catch (COMException) { /* unavailable to this kind of app: costs live updates, not the app */ }
        catch (NotSupportedException) { }
    }

    private static T TryRead<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (COMException) { return fallback; }
        catch (NotSupportedException) { return fallback; }
    }

    // ============================================================ Win32 high contrast

    private const uint SpiGetHighContrast = 0x0042;
    private const uint HcfHighContrastOn = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref HighContrastInfo data, uint update);

    private static bool QueryHighContrast()
    {
        var info = new HighContrastInfo { Size = (uint)Marshal.SizeOf<HighContrastInfo>() };
        return SystemParametersInfo(SpiGetHighContrast, info.Size, ref info, 0)
            && (info.Flags & HcfHighContrastOn) != 0;
    }
}
