using System.Text.Json;

namespace Campus.Desktop.Design.Emoji;

/// <summary>
/// What the picker remembers between sessions: the tone you prefer, the ones you use, the ones
/// you pinned, and the order you put them in.
///
/// Tone is remembered per emoji as well as globally, because a person who sets a default tone
/// still occasionally picks a different one deliberately, and having that choice reset every
/// time is the single most irritating thing an emoji picker can do.
/// </summary>
public sealed class EmojiPreferences
{
    private const int MaxRecents = 48;

    private sealed class Snapshot
    {
        public int DefaultTone { get; set; }
        public List<string> Recents { get; set; } = [];
        public List<string> Pinned { get; set; } = [];
        public Dictionary<string, int> Frequency { get; set; } = [];
        public Dictionary<string, int> ToneByEmoji { get; set; } = [];
        public int SortMode { get; set; }
        public string? PackId { get; set; }
    }

    private Snapshot _state = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Campus", "emoji-preferences.json");

    /// <summary>The tone applied to any emoji with no choice of its own.</summary>
    public SkinTone DefaultTone
    {
        get => (SkinTone)_state.DefaultTone;
        set { _state.DefaultTone = (int)value; Save(); }
    }

    public EmojiSortMode SortMode
    {
        get => (EmojiSortMode)_state.SortMode;
        set { _state.SortMode = (int)value; Save(); }
    }

    /// <summary>The artwork pack to draw with. Null means "whichever has the widest coverage".</summary>
    public string? PackId
    {
        get => _state.PackId;
        set { _state.PackId = value; Save(); }
    }

    public IReadOnlyList<string> Recents => _state.Recents;
    public IReadOnlyList<string> Pinned => _state.Pinned;

    /// <summary>The tone chosen for one emoji, falling back to the default.</summary>
    public SkinTone ToneFor(string key)
        => _state.ToneByEmoji.TryGetValue(key, out var tone) ? (SkinTone)tone : DefaultTone;

    public void SetToneFor(string key, SkinTone tone)
    {
        if (tone == DefaultTone) _state.ToneByEmoji.Remove(key);
        else _state.ToneByEmoji[key] = (int)tone;
        Save();
    }

    public bool IsPinned(string key) => _state.Pinned.Contains(key);

    public void TogglePin(string key)
    {
        if (!_state.Pinned.Remove(key)) _state.Pinned.Add(key);
        Save();
    }

    /// <summary>Moves a pinned emoji within the pinned row, for hand-ordering.</summary>
    public void MovePinned(string key, int toIndex)
    {
        var from = _state.Pinned.IndexOf(key);
        if (from < 0) return;

        _state.Pinned.RemoveAt(from);
        _state.Pinned.Insert(Math.Clamp(toIndex, 0, _state.Pinned.Count), key);
        Save();
    }

    /// <summary>Records a use. Drives both the recent row and the frequency ordering.</summary>
    public void RecordUse(string key)
    {
        _state.Recents.Remove(key);
        _state.Recents.Insert(0, key);
        if (_state.Recents.Count > MaxRecents) _state.Recents.RemoveRange(MaxRecents, _state.Recents.Count - MaxRecents);

        _state.Frequency[key] = _state.Frequency.GetValueOrDefault(key) + 1;
        Save();
    }

    public int UseCount(string key) => _state.Frequency.GetValueOrDefault(key);

    public void ClearRecents()
    {
        _state.Recents.Clear();
        Save();
    }

    public static EmojiPreferences Load()
    {
        var preferences = new EmojiPreferences();
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                preferences._state = JsonSerializer.Deserialize<Snapshot>(json) ?? new Snapshot();
            }
        }
        catch (IOException) { }
        catch (JsonException) { }        // a corrupt file resets preferences, never blocks the app
        catch (UnauthorizedAccessException) { }
        return preferences;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_state));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public enum EmojiSortMode
{
    /// <summary>The order Unicode defines, which is what every other picker uses.</summary>
    Unicode = 0,
    /// <summary>Most used first, within each group.</summary>
    Frequency = 1,
    /// <summary>Alphabetical by name, within each group.</summary>
    Name = 2,
}
