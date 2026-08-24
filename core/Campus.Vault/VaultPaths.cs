namespace Campus.Vault;

/// <summary>
/// Where everything lives on disk. The layout is deliberately opaque: no readable file names,
/// no extensions, no directory that hints at what a file is.
/// </summary>
public sealed class VaultPaths
{
    public VaultPaths(string root)
    {
        Root = Path.GetFullPath(root);
        Objects = Path.Combine(Root, "objects");
        Chunks = Path.Combine(Root, "chunks");
        Thumbnails = Path.Combine(Root, "thumbnails");
        Index = Path.Combine(Root, "index");
        Header = Path.Combine(Root, "vault.header");
        Database = Path.Combine(Root, "workspace.db");
        Backups = Path.Combine(Root, "backups");
        Logs = Path.Combine(Root, "logs");
        Extensions = Path.Combine(Root, "extensions");
        Trash = Path.Combine(Root, "trash");
    }

    public string Root { get; }
    public string Objects { get; }
    public string Chunks { get; }
    public string Thumbnails { get; }
    public string Index { get; }
    public string Header { get; }
    public string Database { get; }
    public string Backups { get; }
    public string Logs { get; }
    public string Extensions { get; }
    public string Trash { get; }

    /// <summary>The default vault location: application data, not the Desktop.</summary>
    public static VaultPaths Default()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new VaultPaths(Path.Combine(local, "Campus", "Vault"));
    }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Objects, Chunks, Thumbnails, Index, Backups, Logs, Extensions, Trash })
            Directory.CreateDirectory(dir);
    }

    public bool Exists => File.Exists(Header);

    /// <summary>Fans a blinded name across two levels so no directory grows unbounded.</summary>
    public string ObjectPath(string blindName)
        => Path.Combine(Objects, blindName[..2], blindName[2..4], blindName);

    public string ThumbnailPath(string blindName)
        => Path.Combine(Thumbnails, blindName[..2], blindName);

    public static void EnsureParent(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }
}
