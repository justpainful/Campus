using Microsoft.UI.Xaml.Markup;

namespace Campus.Desktop.Localization;

/// <summary>
/// The markup extension that puts a translated string into XAML: <c>Text="{l:T home.title}"</c>.
///
/// It resolves once, when the page is built, rather than binding. Campus already rebuilds what is
/// on screen when the theme changes, and a language change goes through the same path — so a
/// binding would buy a notification nobody needs at the cost of a property-changed hook on every
/// label in the program.
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class T : MarkupExtension
{
    public T() { }

    public T(string key) => Key = key;

    /// <summary>The key to look up. Positional, so the markup reads as the string it stands for.</summary>
    public string Key { get; set; } = string.Empty;

    protected override object ProvideValue() => Language.Get(Key);
}

/// <summary>
/// The short name for a translated string in C#: <c>L.T("delete")</c>.
///
/// Short on purpose. It appears a few hundred times, in the middle of expressions that are
/// already doing something else, and a long name at every one of those turns the code that builds
/// a menu into code about translation.
/// </summary>
public static class L
{
    public static string T(string key) => Language.Get(key);

    public static string T(string key, params object?[] arguments) => Language.Get(key, arguments);
}
