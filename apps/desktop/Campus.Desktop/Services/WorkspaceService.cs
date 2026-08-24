using Campus.Domain;
using Campus.Platform.Windows;
using Campus.Storage;
using Campus.Vault;
using Microsoft.UI.Dispatching;

namespace Campus.Desktop.Services;

/// <summary>
/// The workspace as one thing: the vault, the database opened with its key, and the repositories
/// on top. Locking closes all of it together, so there is no state where the files are locked but
/// the database is still readable.
/// </summary>
public sealed class WorkspaceService : IDisposable
{
    private readonly CampusVault _vault;
    private readonly CampusDatabase _database;
    private readonly WorkspaceSettings _settings;
    private DispatcherQueueTimer? _autoLockTimer;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;

    public WorkspaceService(WorkspaceSettings settings)
    {
        _settings = settings;
        Paths = VaultPaths.Default();
        _vault = new CampusVault(Paths, new WindowsHelloKeyProtector());
        _database = new CampusDatabase(Paths.Database);
        DeviceId = DeviceIdentity.Current();
    }

    public VaultPaths Paths { get; }

    /// <summary>Stable id for this machine, used to attribute journal entries.</summary>
    public string DeviceId { get; }

    public bool IsInitialised => _vault.IsInitialised;
    public bool IsUnlocked => _vault.IsUnlocked && _database.IsOpen;

    /// <summary>Raised on lock and unlock so the shell can swap between the workspace and the lock screen.</summary>
    public event EventHandler<bool>? LockStateChanged;

    public ObjectRepository Objects => _objects
        ?? throw new InvalidOperationException("The workspace is locked.");
    private ObjectRepository? _objects;

    public CampusVault Vault => _vault;
    public CampusDatabase Database => _database;

    public Task<bool> IsHelloAvailableAsync() => _vault.HasPlatformProtectorAsync();

    /// <summary>
    /// Creates the workspace. Returns the recovery key, which is shown once and never stored.
    /// </summary>
    public async Task<string> CreateAsync(CancellationToken ct = default)
    {
        var recoveryKey = await _vault.CreateAsync(ct).ConfigureAwait(false);
        await OpenDatabaseAsync(ct).ConfigureAwait(false);
        await SeedAsync(ct).ConfigureAwait(false);
        RaiseUnlocked();
        return recoveryKey;
    }

    /// <summary>Unlocks with Windows Hello.</summary>
    public async Task<UnlockOutcome> UnlockAsync(CancellationToken ct = default)
    {
        var outcome = await _vault.UnlockAsync("Unlock Campus", ct).ConfigureAwait(false);
        if (outcome != UnlockOutcome.Success) return outcome;

        await OpenDatabaseAsync(ct).ConfigureAwait(false);
        RaiseUnlocked();
        return outcome;
    }

    /// <summary>Unlocks with the recovery key, for a new machine or a lost Hello enrolment.</summary>
    public async Task<UnlockOutcome> UnlockWithRecoveryKeyAsync(string key, CancellationToken ct = default)
    {
        var outcome = await _vault.UnlockWithRecoveryKeyAsync(key, ct).ConfigureAwait(false);
        if (outcome != UnlockOutcome.Success) return outcome;

        await OpenDatabaseAsync(ct).ConfigureAwait(false);
        RaiseUnlocked();
        return outcome;
    }

    /// <summary>Enrols Windows Hello against an already-unlocked vault.</summary>
    public Task<bool> EnrolHelloAsync(CancellationToken ct = default)
        => _vault.EnrolPlatformProtectorAsync(ct);

    /// <summary>
    /// Closes the database and zeroes every key. After this the workspace is exactly as
    /// unreadable as it is when Campus is not running.
    /// </summary>
    public void Lock()
    {
        if (!IsUnlocked) return;

        _objects = null;
        _database.CloseAsync().GetAwaiter().GetResult();
        _vault.Lock();
        _autoLockTimer?.Stop();

        LockStateChanged?.Invoke(this, false);
    }

    /// <summary>
    /// Called on any real user interaction. Auto-lock measures idleness from here rather than
    /// from a fixed interval, so a lock never lands in the middle of typing.
    /// </summary>
    public void NoteActivity() => _lastActivity = DateTimeOffset.UtcNow;

    /// <summary>Starts the auto-lock watchdog on the UI thread.</summary>
    public void StartAutoLock(DispatcherQueue dispatcher)
    {
        _autoLockTimer?.Stop();

        if (_settings.AutoLock == AutoLockPolicy.Never) return;

        _autoLockTimer = dispatcher.CreateTimer();
        _autoLockTimer.Interval = TimeSpan.FromSeconds(20);
        _autoLockTimer.IsRepeating = true;
        _autoLockTimer.Tick += (_, _) =>
        {
            if (!IsUnlocked) return;
            var idleFor = DateTimeOffset.UtcNow - _lastActivity;
            if (idleFor >= TimeSpan.FromMinutes((int)_settings.AutoLock)) Lock();
        };
        _autoLockTimer.Start();
    }

    private async Task OpenDatabaseAsync(CancellationToken ct)
    {
        await _database.OpenAsync(_vault.Keys, ct).ConfigureAwait(false);
        _objects = new ObjectRepository(_database, DeviceId);
    }

    private void RaiseUnlocked()
    {
        _lastActivity = DateTimeOffset.UtcNow;
        LockStateChanged?.Invoke(this, true);
    }

    /// <summary>
    /// Puts the minimum in place for a workspace to make sense on first run: the subjects the
    /// user actually studies. Everything else starts empty on purpose — Campus should not invent
    /// content and then make the user delete it.
    /// </summary>
    private async Task SeedAsync(CancellationToken ct)
    {
        string[] subjects =
            ["English", "Mathematics", "Physics", "Chemistry", "Biology", "Environmental Science"];
        string[] accents = ["Blue", "Indigo", "Teal", "Green", "Orange", "Graphite"];

        for (var i = 0; i < subjects.Length; i++)
        {
            await Objects.SaveAsync(new CampusObject
            {
                Kind = ObjectKind.Subject,
                Title = subjects[i],
                SortOrder = i,
                AcademicYear = DateTimeOffset.Now.Year,
                Payload = new SubjectPayload { AccentName = accents[i], SortOrder = i },
            }, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _autoLockTimer?.Stop();
        _database.Dispose();
        _vault.Dispose();
    }
}
