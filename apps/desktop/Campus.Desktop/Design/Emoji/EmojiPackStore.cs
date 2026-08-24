using System.Text.Json;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Campus.Desktop.Design.Emoji;

/// <summary>What a pack says about itself, written by tools/emoji/build_pack.py.</summary>
public sealed class EmojiPackManifest
{
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public int Count { get; set; }
    public int Missing { get; set; }
    public List<string> Sizes { get; set; } = [];
    public string License { get; set; } = string.Empty;
}

public sealed class EmojiPack
{
    public required string Id { get; init; }
    public required string Directory { get; init; }
    public required EmojiPackManifest Manifest { get; init; }
    /// <summary>True for a pack the user installed, as opposed to one that shipped with Campus.</summary>
    public bool IsUserInstalled { get; init; }

    public string DisplayName => Manifest.Source.Length > 0 ? Manifest.Source : Id;
}

/// <summary>
/// Finds and serves emoji artwork.
///
/// Campus draws emoji as images rather than as text in a system font. That is the whole point:
/// with a font, the emoji you see are whatever the operating system happens to ship, and on
/// Windows that means Segoe UI Emoji whether you want it or not. With a pack, the emoji you see
/// are the ones in the pack you chose.
/// </summary>
public sealed class EmojiPackStore
{
    private readonly Dictionary<string, BitmapImage?> _cache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private static readonly Lazy<EmojiPackStore> Instance = new(() => new EmojiPackStore());
    public static EmojiPackStore Current => Instance.Value;

    /// <summary>Packs that shipped with the application.</summary>
    private static string BuiltInRoot =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "emoji-packs");

    /// <summary>
    /// Where a pack the user built goes. Kept outside the install directory so it survives an
    /// update, and so installing one never needs administrator rights.
    /// </summary>
    public static string UserRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Campus", "emoji-packs");

    public IReadOnlyList<EmojiPack> Packs { get; private set; } = [];

    /// <summary>The pack currently drawing. Null when none is installed.</summary>
    public EmojiPack? Active { get; private set; }

    /// <summary>Raised when the active pack changes, so open pickers can redraw.</summary>
    public event EventHandler? ActivePackChanged;

    private EmojiPackStore()
    {
        Refresh();
        Select(EmojiPreferences.Load().PackId);
    }

    /// <summary>Rescans both pack directories.</summary>
    public void Refresh()
    {
        var packs = new List<EmojiPack>();
        packs.AddRange(Discover(BuiltInRoot, userInstalled: false));
        packs.AddRange(Discover(UserRoot, userInstalled: true));

        Packs = packs;

        // A pack that has gone missing must not leave the app rendering nothing.
        if (Active is not null && !packs.Any(p => p.Id == Active.Id)) Select(null);
    }

    private static IEnumerable<EmojiPack> Discover(string root, bool userInstalled)
    {
        if (!Directory.Exists(root)) yield break;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var manifestPath = Path.Combine(directory, "pack.json");
            if (!File.Exists(manifestPath)) continue;

            EmojiPackManifest? manifest = null;
            try
            {
                manifest = JsonSerializer.Deserialize<EmojiPackManifest>(
                    File.ReadAllText(manifestPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException) { }
            catch (IOException) { }

            if (manifest is null) continue;

            yield return new EmojiPack
            {
                Id = Path.GetFileName(directory),
                Directory = directory,
                Manifest = manifest,
                IsUserInstalled = userInstalled,
            };
        }
    }

    /// <summary>
    /// Chooses a pack. Passing null, or a name that is not installed, falls back to whichever
    /// pack has the widest coverage — the point being that there is always artwork if any exists.
    /// </summary>
    public void Select(string? packId)
    {
        var chosen = packId is null
            ? null
            : Packs.FirstOrDefault(p => string.Equals(p.Id, packId, StringComparison.OrdinalIgnoreCase));

        chosen ??= Packs.OrderByDescending(p => p.Manifest.Count).FirstOrDefault();

        if (ReferenceEquals(chosen, Active)) return;

        Active = chosen;
        lock (_gate) _cache.Clear();
        ActivePackChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>`1F44B 1F3FB` becomes `1f44b-1f3fb.png`, the naming every emoji pack uses.</summary>
    public static string FileNameFor(string codePoints)
        => string.Join('-', codePoints.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.ToLowerInvariant())) + ".png";

    /// <summary>
    /// The artwork for one sequence, or null when the active pack does not have it.
    ///
    /// Images are decoded once at the size they will be drawn and then kept, because a grid of
    /// two thousand cells scrolling at sixty frames a second cannot afford to decode anything.
    /// </summary>
    public BitmapImage? Image(string codePoints, int decodeWidth = 72)
    {
        if (Active is null) return null;

        var key = $"{Active.Id}/{codePoints}/{decodeWidth}";
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }

        var path = Path.Combine(Active.Directory, FileNameFor(codePoints));
        BitmapImage? image = null;

        if (File.Exists(path))
        {
            image = new BitmapImage
            {
                // Set before the source, or it is ignored and the full-size bitmap is kept.
                DecodePixelWidth = decodeWidth,
                DecodePixelType = DecodePixelType.Logical,
                UriSource = new Uri(path),
            };
        }

        lock (_gate) _cache[key] = image;
        return image;
    }

    public bool Has(string codePoints)
        => Active is not null && File.Exists(Path.Combine(Active.Directory, FileNameFor(codePoints)));

    /// <summary>
    /// Copies a pack directory into the user's pack folder. The source is whatever
    /// tools/emoji/build_pack.py produced, including from a font the user owns.
    /// </summary>
    public async Task<EmojiPack?> InstallAsync(string sourceDirectory, CancellationToken ct = default)
    {
        var manifestPath = Path.Combine(sourceDirectory, "pack.json");
        if (!File.Exists(manifestPath)) return null;

        var id = Path.GetFileName(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var target = Path.Combine(UserRoot, id);

        await Task.Run(() =>
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(sourceDirectory))
            {
                ct.ThrowIfCancellationRequested();
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
        }, ct).ConfigureAwait(false);

        Refresh();
        return Packs.FirstOrDefault(p => p.Id == id && p.IsUserInstalled);
    }

    /// <summary>Removes a pack the user installed. Built-in packs are left alone.</summary>
    public bool Remove(string packId)
    {
        var pack = Packs.FirstOrDefault(p => p.Id == packId && p.IsUserInstalled);
        if (pack is null) return false;

        try { Directory.Delete(pack.Directory, recursive: true); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        Refresh();
        Select(null);
        return true;
    }
}
