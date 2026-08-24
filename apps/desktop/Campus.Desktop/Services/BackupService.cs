using System.IO.Compression;
using System.Text.Json;
using Campus.Domain;

namespace Campus.Desktop.Services;

/// <summary>What a backup file says about itself, in the clear.</summary>
public sealed record BackupManifest
{
    public int FormatVersion { get; init; } = 1;
    public string VaultId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Device { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public int ObjectCount { get; init; }
}

/// <summary>One backup found on disk.</summary>
public sealed record BackupFile(string Path, DateTimeOffset CreatedAt, long SizeBytes);

/// <summary>
/// Backing the workspace up, and putting it back.
///
/// A backup is a copy of the vault exactly as it sits on disk — which means it is already
/// encrypted, and Campus never has to decrypt anything to make one. That has a consequence worth
/// stating plainly: a backup is worthless without the recovery key. There is no way around that
/// and no back door, which is the same property that makes the workspace worth having.
///
/// Restoring never writes over a workspace that is open. It lands beside it, and swapping is
/// something a person does deliberately.
/// </summary>
public sealed class BackupService(WorkspaceService workspace)
{
    private const string ManifestEntry = "backup.json";
    private const string LastRunKey = "backup.lastRun";

    private readonly WorkspaceService _workspace = workspace;

    public event EventHandler<string>? Progress;

    /// <summary>Where backups go when nobody has said otherwise.</summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Campus Backups");

    /// <summary>
    /// Writes a backup. The database is copied through a checkpoint first, so a backup taken
    /// while Campus is open is a coherent database rather than one missing its write-ahead log.
    /// </summary>
    public async Task<BackupFile?> CreateAsync(string? folder = null, CancellationToken ct = default)
    {
        if (!_workspace.IsUnlocked)
            throw new InvalidOperationException("The workspace is locked.");

        folder ??= DefaultFolder;
        Directory.CreateDirectory(folder);

        var name = $"campus-{DateTime.Now:yyyy-MM-dd-HHmm}.campusbackup";
        var path = Path.Combine(folder, name);

        Progress?.Invoke(this, "Settling the database");

        // WAL pages that have not been folded back into the file would otherwise be missing from
        // the copy, and a database missing them is a database that will not open.
        await _workspace.Database.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);

        var root = Path.GetDirectoryName(_workspace.Paths.Database)!;
        var files = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Progress?.Invoke(this, $"Copying {files.Count} files");

        try
        {
            await using (var output = File.Create(path))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                long total = 0;

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();

                    var relative = Path.GetRelativePath(root, file);

                    try
                    {
                        // Vault objects are already encrypted and already compressed in effect;
                        // compressing them again costs time and saves nothing.
                        var entry = archive.CreateEntry("vault/" + relative.Replace('\\', '/'),
                            CompressionLevel.NoCompression);

                        await using var source = new FileStream(
                            file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        await using var target = entry.Open();
                        await source.CopyToAsync(target, ct);

                        total += source.Length;
                    }
                    catch (IOException)
                    {
                        // A file being written at this instant is skipped rather than aborting
                        // the whole backup; the next one will pick it up.
                    }
                }

                var manifest = new BackupManifest
                {
                    VaultId = _workspace.Vault.Keys.VaultId,
                    Device = Environment.MachineName,
                    SizeBytes = total,
                    ObjectCount = files.Count,
                };

                var manifestEntry = archive.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                await using var writer = new StreamWriter(manifestEntry.Open());
                await writer.WriteAsync(JsonSerializer.Serialize(manifest,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception)
        {
            // A half-written backup is worse than none: it looks like a backup.
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
            throw;
        }

        await _workspace.Settings.SetAsync(LastRunKey, DateTimeOffset.UtcNow, ct);

        var info = new FileInfo(path);
        Progress?.Invoke(this, "Done");

        await PruneAsync(folder, ct);
        return new BackupFile(path, DateTimeOffset.Now, info.Length);
    }

    /// <summary>What backups are sitting in a folder, newest first.</summary>
    public static IReadOnlyList<BackupFile> List(string? folder = null)
    {
        folder ??= DefaultFolder;
        if (!Directory.Exists(folder)) return [];

        return Directory
            .EnumerateFiles(folder, "*.campusbackup")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new BackupFile(f.FullName, f.LastWriteTime, f.Length))
            .ToList();
    }

    public static async Task<BackupManifest?> ReadManifestAsync(
        string path, CancellationToken ct = default)
    {
        try
        {
            await using var file = File.OpenRead(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);

            var entry = archive.GetEntry(ManifestEntry);
            if (entry is null) return null;

            await using var stream = entry.Open();
            return await JsonSerializer.DeserializeAsync<BackupManifest>(stream, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Unpacks a backup into a folder of its own. It is deliberately not put where the live
    /// workspace is: replacing a workspace is a decision, not a side effect of clicking Restore.
    /// </summary>
    public static async Task<string?> RestoreAsync(
        string backupPath, string destinationFolder, CancellationToken ct = default)
    {
        var manifest = await ReadManifestAsync(backupPath, ct);
        if (manifest is null) return null;

        var target = Path.Combine(destinationFolder,
            $"campus-restored-{manifest.CreatedAt.ToLocalTime():yyyy-MM-dd-HHmm}");

        Directory.CreateDirectory(target);

        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(backupPath);

            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();

                if (!entry.FullName.StartsWith("vault/", StringComparison.Ordinal)) continue;

                var relative = entry.FullName["vault/".Length..];
                if (relative.Length == 0) continue;

                var path = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));

                // A zip entry that escapes its own folder is either a bug or an attack; either
                // way it does not get to write outside the destination.
                var full = Path.GetFullPath(path);
                if (!full.StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                entry.ExtractToFile(full, overwrite: true);
            }
        }, ct);

        return target;
    }

    // ------------------------------------------------------------------------ schedule

    /// <summary>
    /// Runs a backup if one is due. Called at startup rather than on a timer: a workspace that is
    /// not open cannot be backed up anyway, and a background process that wakes up to touch an
    /// encrypted vault is exactly the sort of thing this app should not have.
    /// </summary>
    public async Task<BackupFile?> RunIfDueAsync(
        BackupSettings settings, CancellationToken ct = default)
    {
        if (!settings.Automatic || settings.Cadence == BackupCadence.Manual) return null;
        if (!_workspace.IsUnlocked) return null;

        var last = await _workspace.Settings.GetAsync<DateTimeOffset?>(LastRunKey, ct);

        var due = settings.Cadence switch
        {
            BackupCadence.Daily => last is null || DateTimeOffset.UtcNow - last > TimeSpan.FromDays(1),
            BackupCadence.Weekly => last is null || DateTimeOffset.UtcNow - last > TimeSpan.FromDays(7),
            _ => false,
        };

        if (!due) return null;

        try
        {
            return await CreateAsync(settings.Destination, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidOperationException)
        {
            Progress?.Invoke(this, $"The scheduled backup did not run: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Applies the retention rule: some recent ones, fewer older ones, a handful of old ones.
    /// Keeping every backup forever fills a disk; keeping only the newest means a mistake made
    /// last week is already backed up over.
    /// </summary>
    public async Task PruneAsync(string? folder = null, CancellationToken ct = default)
    {
        var settings = App.GetService<WorkspaceSettings>().Backup;
        var backups = List(folder);
        if (backups.Count == 0) return;

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void KeepNewestPer(Func<DateTimeOffset, object> bucket, int count)
        {
            foreach (var group in backups.GroupBy(b => bucket(b.CreatedAt)).Take(count))
            {
                var newest = group.MaxBy(b => b.CreatedAt);
                if (newest is not null) keep.Add(newest.Path);
            }
        }

        KeepNewestPer(d => d.Date, settings.KeepDaily);
        KeepNewestPer(d => (d.Year, System.Globalization.ISOWeek.GetWeekOfYear(d.DateTime)), settings.KeepWeekly);
        KeepNewestPer(d => (d.Year, d.Month), settings.KeepMonthly);

        foreach (var backup in backups.Where(b => !keep.Contains(b.Path)))
        {
            ct.ThrowIfCancellationRequested();

            try { File.Delete(backup.Path); }
            catch (IOException) { /* it will be caught by the next prune */ }
        }

        await Task.CompletedTask;
    }
}
