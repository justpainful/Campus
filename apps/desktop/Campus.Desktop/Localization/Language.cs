using System.Globalization;
using Microsoft.UI.Xaml;

namespace Campus.Desktop.Localization;

/// <summary>
/// The interface's language, and everything that follows from choosing one.
///
/// Three things change together and none of them is optional. The words, obviously. The direction
/// the interface runs in, because an Arabic interface laid out left to right is not an Arabic
/// interface — the back button has to point the other way, the sidebar has to be on the right, and
/// a list of names has to start where the eye starts. And the typography, because the faces that
/// carry English well have no Arabic in them at all, and the fallback that Windows substitutes is
/// not a decision anybody made.
///
/// Strings live in plain dictionaries rather than in .resw files. Campus runs unpackaged, where
/// the resource loader is a good deal more trouble than a lookup, and a table in source can be
/// checked at build time for a key that exists in one language and not the other — which is the
/// failure that actually happens.
/// </summary>
public sealed class Language
{
    /// <summary>The languages the interface is translated into.</summary>
    public static readonly IReadOnlyList<LanguageOption> Available =
    [
        new("en", "English", "English", RightToLeft: false),
        new("ar", "Arabic", "العربية", RightToLeft: true),
    ];

    private readonly List<WeakReference<FrameworkElement>> _roots = [];
    private string _code = "en";

    /// <summary>The one instance. Set up by the app at startup, before any page is built.</summary>
    public static Language Current { get; } = new();

    /// <summary>The chosen language's code, as stored in the workspace settings.</summary>
    public string Code => _code;

    public bool IsRightToLeft => Option.RightToLeft;

    public LanguageOption Option =>
        Available.FirstOrDefault(l => l.Code == _code) ?? Available[0];

    /// <summary>Raised after the language changes, so the shell can rebuild what is on screen.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Chooses a language. Everything already on screen is rebuilt by the shell in response,
    /// because a translated string is read when a page is built rather than watched.
    /// </summary>
    public void Use(string? code)
    {
        var wanted = Available.FirstOrDefault(
            l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))?.Code ?? "en";

        if (wanted == _code) return;

        _code = wanted;
        ApplyCulture();
        ArabicTypography.Apply(IsRightToLeft);
        ApplyToAllRoots();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the language without announcing it, for the one call made during startup.</summary>
    public void Initialise(string? code)
    {
        _code = Available.FirstOrDefault(
            l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))?.Code ?? "en";

        ApplyCulture();
        ArabicTypography.Apply(IsRightToLeft);
    }

    /// <summary>
    /// Where the choice is kept: a file beside the workspace rather than inside it.
    ///
    /// Everything else the user decides lives in the encrypted database, and should. This cannot,
    /// because the first screen a person sees is the one asking them to unlock that database, and
    /// showing it in English to somebody who chose Arabic — every time, before they can do
    /// anything about it — is not a workspace secret worth keeping. Which language somebody reads
    /// is not what the vault is protecting.
    /// </summary>
    private static string PreferencePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Campus", "language");

    public static string? Load()
    {
        try
        {
            return File.Exists(PreferencePath) ? File.ReadAllText(PreferencePath).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A language that cannot be read is English, which is what it would have been anyway.
            return null;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, _code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth a message. The interface is already in the right language; it is only
            // the next launch that will have forgotten.
        }
    }

    /// <summary>
    /// The translation for a key, or the key itself if there is none.
    ///
    /// A missing translation shows the key rather than an empty label or an exception: a screen
    /// with `settings.appearance.title` on it is obviously wrong and takes a minute to fix, and a
    /// screen with a blank where a heading should be is a bug report six months later.
    /// </summary>
    public string this[string key] => Get(key);

    public static string Get(string key)
    {
        var table = Current._code == "ar" ? Strings.Arabic : Strings.English;

        if (table.TryGetValue(key, out var value)) return value;
        if (Strings.English.TryGetValue(key, out var english)) return english;

        return key;
    }

    /// <summary>A translation with <c>{0}</c>-style holes filled in.</summary>
    public static string Get(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    // ------------------------------------------------------------------ direction and culture

    /// <summary>Registers a root so it follows the language. Mirrors how the theme is applied.</summary>
    public void Register(FrameworkElement root)
    {
        Prune();
        if (_roots.Any(r => r.TryGetTarget(out var existing) && ReferenceEquals(existing, root)))
            return;

        _roots.Add(new WeakReference<FrameworkElement>(root));
        Apply(root);
    }

    private void Apply(FrameworkElement root) =>
        root.FlowDirection = IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    private void ApplyToAllRoots()
    {
        Prune();
        foreach (var reference in _roots)
        {
            if (reference.TryGetTarget(out var root)) Apply(root);
        }
    }

    private void Prune() => _roots.RemoveAll(r => !r.TryGetTarget(out _));

    /// <summary>
    /// Sets the thread's culture, which is what makes a date, a number and a sort order Arabic
    /// rather than merely the labels around them.
    ///
    /// The calendar stays Gregorian. Saudi schools run on it, the timetable in a student's hand is
    /// printed on it, and a planner that quietly disagreed with the paper on the wall would be
    /// worse than useless — a Hijri view is a feature to add beside this, not a substitution to
    /// make underneath it.
    /// </summary>
    private void ApplyCulture()
    {
        var culture = _code == "ar"
            ? new CultureInfo("ar-SA")
            : new CultureInfo("en-GB");

        if (_code == "ar")
        {
            culture = (CultureInfo)culture.Clone();
            culture.DateTimeFormat.Calendar = new GregorianCalendar();
            // Western digits: every worksheet, calculator and exam paper the student will put
            // beside this screen uses them, and a page that disagrees costs a double-take.
            culture.NumberFormat.DigitSubstitution = DigitShapes.None;
        }

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}

/// <summary>One language the interface is available in.</summary>
/// <param name="Code">The two-letter code stored in the workspace settings.</param>
/// <param name="EnglishName">What it is called in English, for logs and for the settings list.</param>
/// <param name="NativeName">What it is called in itself, which is what the chooser shows.</param>
/// <param name="RightToLeft">Whether the interface runs the other way.</param>
public sealed record LanguageOption(
    string Code, string EnglishName, string NativeName, bool RightToLeft);

/// <summary>
/// Counting, in a language that does not count the way English does.
///
/// English has two forms and Arabic has six, and they are not a stylistic nicety: "4 رسالة" is
/// not a slightly awkward plural, it is wrong in the way "4 message" is wrong. Worse, the form
/// for 3–10 differs from the form for 11 and up, so a single "plural" string cannot cover both.
///
/// A counted string is therefore several keys sharing a stem — <c>message.one</c>,
/// <c>message.two</c>, <c>message.few</c>, <c>message.many</c>, <c>message.other</c> — and this
/// picks between them. English fills in only <c>one</c> and <c>other</c>; the lookup falls back
/// through them, so a language with two forms costs two entries and a language with six costs
/// six, which is the right way round.
/// </summary>
public static class Plural
{
    public static string Of(string stem, int count)
    {
        foreach (var form in Forms(count))
        {
            var key = stem + "." + form;
            if (Strings.English.ContainsKey(key)) return Language.Get(key, count);
        }

        return Language.Get(stem + ".other", count);
    }

    /// <summary>
    /// The forms to try, best first.
    ///
    /// The rules are Arabic's, which subsume English's: a language with fewer forms simply never
    /// has entries for the ones it does not use, and the search falls through to "other".
    /// </summary>
    private static IEnumerable<string> Forms(int count)
    {
        if (count == 0) yield return "zero";
        if (count == 1) yield return "one";
        if (count == 2) yield return "two";

        var hundreds = count % 100;
        if (hundreds is >= 3 and <= 10) yield return "few";
        if (hundreds is >= 11 and <= 99) yield return "many";

        yield return "other";
    }
}
