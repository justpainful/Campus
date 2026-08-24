using System.Text;

namespace Campus.Desktop.Design.Emoji;

/// <summary>How many skin tones an emoji takes.</summary>
public enum ToneKind
{
    None = 0,
    /// <summary>One person: five variants.</summary>
    Single = 1,
    /// <summary>Two people: twenty-five combinations.</summary>
    Dual = 2,
}

/// <summary>The five Fitzpatrick modifiers, in the order Apple and Unicode present them.</summary>
public enum SkinTone
{
    Default = 0,
    Light = 1,
    MediumLight = 2,
    Medium = 3,
    MediumDark = 4,
    Dark = 5,
}

/// <summary>One emoji, with its tone variants attached rather than scattered.</summary>
public sealed class EmojiEntry
{
    public required string Key { get; init; }          // base code points, space separated
    public required string Text { get; init; }         // the base emoji itself
    public required string Name { get; init; }
    public required string Group { get; init; }
    public required string Subgroup { get; init; }
    public ToneKind Tone { get; init; }

    /// <summary>Rendered variants, in Unicode order. Empty when the emoji takes no tone.</summary>
    public IReadOnlyList<string> Variants { get; init; } = [];

    /// <summary>Extra words this emoji should be findable by.</summary>
    public string Aliases { get; set; } = string.Empty;

    /// <summary>Lowercase search haystack, built once.</summary>
    public string SearchText { get; init; } = string.Empty;

    /// <summary>
    /// The variant for a tone. For dual-tone emoji this returns the combination where both
    /// people share the tone, which is what a single tone choice means.
    /// </summary>
    public string ForTone(SkinTone tone)
    {
        if (tone == SkinTone.Default || Variants.Count == 0) return Text;

        var index = (int)tone - 1;
        return Tone switch
        {
            ToneKind.Single when index < Variants.Count => Variants[index],
            // Dual variants are laid out as a 5×5 grid in Unicode order, so the matching pair
            // sits on the diagonal.
            ToneKind.Dual when (index * 5) + index < Variants.Count => Variants[(index * 5) + index],
            _ => Text,
        };
    }

    /// <summary>All variants for the tone picker, with the untoned form first.</summary>
    public IReadOnlyList<string> ToneChoices()
    {
        if (Tone == ToneKind.None) return [Text];

        var choices = new List<string>(6) { Text };
        for (var i = 1; i <= 5; i++) choices.Add(ForTone((SkinTone)i));
        return choices;
    }
}

public sealed class EmojiGroup
{
    public required string Name { get; init; }
    public required string Symbol { get; init; }
    public List<EmojiEntry> Entries { get; } = [];
}

/// <summary>
/// The emoji catalogue, loaded from the generated data file.
///
/// Every emoji Unicode defines is here, with every skin-tone variant — including the twenty-five
/// combinations that two-person emoji actually have, rather than the five that a naive reading of
/// the data would give.
/// </summary>
public sealed class EmojiCatalogue
{
    private readonly List<EmojiEntry> _all = [];
    private readonly Dictionary<string, EmojiEntry> _byKey = new(StringComparer.Ordinal);

    public IReadOnlyList<EmojiGroup> Groups { get; private set; } = [];
    public IReadOnlyList<EmojiEntry> All => _all;
    public string UnicodeVersion { get; private set; } = "unknown";

    /// <summary>Icons for the group tabs, in the order the groups appear in the data.</summary>
    private static readonly Dictionary<string, string> GroupSymbols = new(StringComparer.Ordinal)
    {
        ["Smileys & Emotion"] = "emoji",
        ["People & Body"] = "person",
        ["Animals & Nature"] = "leaf",
        ["Food & Drink"] = "cup",
        ["Travel & Places"] = "globe",
        ["Activities"] = "ball",
        ["Objects"] = "lightbulb",
        ["Symbols"] = "symbols",
        ["Flags"] = "flag",
        ["Component"] = "palette",
    };

    public static EmojiCatalogue Load(string path)
    {
        var catalogue = new EmojiCatalogue();
        if (!File.Exists(path)) return catalogue;

        var groups = new List<EmojiGroup>();
        EmojiGroup? group = null;
        var subgroup = string.Empty;
        var pendingAliases = new List<(string Key, string Aliases)>();

        foreach (var line in File.ReadLines(path))
        {
            if (line.Length < 2) continue;
            var parts = line.Split('\t');

            switch (parts[0])
            {
                case "V":
                    catalogue.UnicodeVersion = parts[1];
                    break;

                case "G":
                    // The Component group holds the skin-tone modifiers themselves, which are
                    // not emoji anyone picks.
                    if (parts[1] == "Component") { group = null; break; }
                    group = new EmojiGroup
                    {
                        Name = parts[1],
                        Symbol = GroupSymbols.GetValueOrDefault(parts[1], "emoji"),
                    };
                    groups.Add(group);
                    break;

                case "S":
                    subgroup = parts[1];
                    break;

                case "E":
                    if (group is null || parts.Length < 4) break;
                    var entry = Build(parts, group.Name, subgroup);
                    group.Entries.Add(entry);
                    catalogue._all.Add(entry);
                    catalogue._byKey[entry.Key] = entry;
                    break;

                case "A":
                    if (parts.Length >= 3) pendingAliases.Add((parts[1], parts[2]));
                    break;
            }
        }

        // Aliases are listed after the entries, so they are applied once everything is loaded.
        foreach (var (key, aliases) in pendingAliases)
        {
            if (catalogue._byKey.TryGetValue(key, out var target)) target.Aliases = aliases;
        }

        catalogue.Groups = groups;
        return catalogue;
    }

    private static EmojiEntry Build(string[] parts, string group, string subgroup)
    {
        var key = parts[1];
        var name = parts[2];
        var tone = (ToneKind)int.Parse(parts[3]);
        var variants = parts.Length > 4 && parts[4].Length > 0
            ? parts[4].Split('|').Select(ToText).ToArray()
            : [];

        // Subgroup names are hyphenated in the source ("face-smiling"); split them so a search
        // for "smiling" matches.
        var haystack = $"{name} {subgroup.Replace('-', ' ')} {group}".ToLowerInvariant();

        return new EmojiEntry
        {
            Key = key,
            Text = ToText(key),
            Name = name,
            Group = group,
            Subgroup = subgroup,
            Tone = tone,
            Variants = variants,
            SearchText = haystack,
        };
    }

    /// <summary>Turns "1F44B 1F3FB" into the characters it represents.</summary>
    private static string ToText(string codePoints)
    {
        var builder = new StringBuilder(8);
        foreach (var part in codePoints.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            builder.Append(char.ConvertFromUtf32(Convert.ToInt32(part, 16)));
        return builder.ToString();
    }

    public EmojiEntry? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>
    /// Ranks emoji against a query. A name that starts with the term beats one that contains it,
    /// and an alias match beats a group match, so typing "fire" reaches the flame before
    /// "fire engine" and "firecracker".
    /// </summary>
    public IReadOnlyList<EmojiEntry> Search(string query, int limit = 300)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var term = query.Trim().ToLowerInvariant();
        var results = new List<(EmojiEntry Entry, int Score)>();

        foreach (var entry in _all)
        {
            var score = 0;
            var name = entry.Name.AsSpan();

            if (name.StartsWith(term, StringComparison.Ordinal)) score = 100;
            else if (entry.Aliases.Length > 0 && MatchesWord(entry.Aliases, term)) score = 90;
            else if (MatchesWord(entry.Name, term)) score = 70;
            else if (entry.SearchText.Contains(term, StringComparison.Ordinal)) score = 30;

            if (score > 0) results.Add((entry, score));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Entry.Name.Length)
            .Take(limit)
            .Select(r => r.Entry)
            .ToList();
    }

    /// <summary>True when the term starts a word in the text, rather than landing mid-word.</summary>
    private static bool MatchesWord(string text, string term)
    {
        var span = text.AsSpan();
        var index = 0;
        while (index < span.Length)
        {
            var found = span[index..].IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            var absolute = index + found;
            if (absolute == 0 || !char.IsLetterOrDigit(span[absolute - 1])) return true;
            index = absolute + 1;
        }
        return false;
    }
}
