using System.Security.Cryptography;
using System.Text;
using Campus.Vault;
using Xunit;

namespace Campus.Core.Tests;

/// <summary>
/// A vault is only worth having if the guarantees hold, so these exercise the real thing:
/// a temporary vault on disk, real AES-GCM, real recovery keys.
/// </summary>
public sealed class VaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "campus-tests", Guid.NewGuid().ToString("N"));

    private CampusVault NewVault() => new(new VaultPaths(_root));

    [Fact]
    public async Task Create_produces_a_usable_recovery_key_and_leaves_the_vault_unlocked()
    {
        using var vault = NewVault();
        var recovery = await vault.CreateAsync();

        Assert.True(RecoveryKey.IsWellFormed(recovery));
        Assert.True(vault.IsUnlocked);
        Assert.True(vault.IsInitialised);
    }

    [Fact]
    public async Task Recovery_key_reopens_the_vault_after_locking()
    {
        using var vault = NewVault();
        var recovery = await vault.CreateAsync();
        vault.Lock();
        Assert.False(vault.IsUnlocked);

        var outcome = await vault.UnlockWithRecoveryKeyAsync(recovery);

        Assert.Equal(UnlockOutcome.Success, outcome);
        Assert.True(vault.IsUnlocked);
    }

    [Fact]
    public async Task Recovery_key_survives_formatting_differences()
    {
        using var vault = NewVault();
        var recovery = await vault.CreateAsync();
        vault.Lock();

        // Lowercase, spaces instead of hyphens, and the O/0 and I/1 confusions people actually make.
        var mistyped = recovery.Replace("-", " ").ToLowerInvariant()
            .Replace('0', 'O').Replace('1', 'l');

        Assert.Equal(UnlockOutcome.Success, await vault.UnlockWithRecoveryKeyAsync(mistyped));
    }

    [Fact]
    public async Task A_wrong_recovery_key_is_rejected_and_leaves_the_vault_locked()
    {
        using var vault = NewVault();
        await vault.CreateAsync();
        vault.Lock();

        var outcome = await vault.UnlockWithRecoveryKeyAsync(RecoveryKey.Generate());

        Assert.Equal(UnlockOutcome.VerificationFailed, outcome);
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task Files_round_trip_byte_for_byte()
    {
        using var vault = NewVault();
        await vault.CreateAsync();

        // Deliberately not a multiple of the 1 MiB chunk size, so the tail chunk is exercised.
        var payload = RandomNumberGenerator.GetBytes((3 * 1024 * 1024) + 12_345);
        var source = Path.Combine(_root, "input.bin");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(source, payload);

        var put = await vault.Objects.PutFileAsync(source);
        var readBack = await vault.Objects.ReadAllBytesAsync(put.ContentHash);

        Assert.Equal(payload, readBack);
        Assert.Equal(payload.LongLength, put.SizeBytes);
        Assert.False(put.AlreadyExisted);
    }

    [Fact]
    public async Task Storing_the_same_bytes_twice_stores_them_once()
    {
        using var vault = NewVault();
        await vault.CreateAsync();

        var payload = Encoding.UTF8.GetBytes("MegaGoal 1, imported twice.");
        var first = await vault.Objects.PutBytesAsync(payload);
        var second = await vault.Objects.PutBytesAsync(payload);

        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
    }

    [Fact]
    public async Task Seeking_reads_the_right_bytes_without_reading_from_the_start()
    {
        using var vault = NewVault();
        await vault.CreateAsync();

        var payload = RandomNumberGenerator.GetBytes(5 * 1024 * 1024);
        var put = await vault.Objects.PutBytesAsync(payload);

        await using var stream = vault.Objects.OpenRead(put.ContentHash);
        stream.Seek(4_000_000, SeekOrigin.Begin);
        var window = new byte[1024];
        await stream.ReadExactlyAsync(window);

        Assert.Equal(payload.AsSpan(4_000_000, 1024).ToArray(), window);
    }

    [Fact]
    public async Task Nothing_readable_is_written_to_disk()
    {
        using var vault = NewVault();
        await vault.CreateAsync();

        const string secret = "Chemistry homework is due Wednesday";
        var put = await vault.Objects.PutBytesAsync(Encoding.UTF8.GetBytes(secret));

        var everyByteOnDisk = Directory
            .EnumerateFiles(vault.Paths.Objects, "*", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllBytes)
            .ToArray();

        Assert.DoesNotContain(Encoding.UTF8.GetString(everyByteOnDisk), secret);

        // The plaintext hash must not appear as a file name either.
        var names = Directory.EnumerateFiles(vault.Paths.Objects, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName);
        Assert.DoesNotContain(put.ContentHash, names);
    }

    [Fact]
    public async Task Tampering_with_an_object_is_detected()
    {
        using var vault = NewVault();
        await vault.CreateAsync();
        var put = await vault.Objects.PutBytesAsync(Encoding.UTF8.GetBytes("original content"));

        var path = Directory.EnumerateFiles(vault.Paths.Objects, "*", SearchOption.AllDirectories).Single();
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 0xFF; // flip a bit in the ciphertext
        await File.WriteAllBytesAsync(path, bytes);

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            () => vault.Objects.ReadAllBytesAsync(put.ContentHash));
        Assert.False(await vault.Objects.VerifyAsync(put.ContentHash));
    }

    [Fact]
    public async Task Locking_makes_content_unreachable()
    {
        using var vault = NewVault();
        await vault.CreateAsync();
        var put = await vault.Objects.PutBytesAsync(Encoding.UTF8.GetBytes("locked away"));

        vault.Lock();

        Assert.Throws<InvalidOperationException>(() => vault.Objects.OpenRead(put.ContentHash));
    }

    [Fact]
    public async Task Export_writes_the_original_bytes_back_out()
    {
        using var vault = NewVault();
        await vault.CreateAsync();
        var payload = RandomNumberGenerator.GetBytes(200_000);
        var put = await vault.Objects.PutBytesAsync(payload);

        var destination = Path.Combine(_root, "exported.bin");
        await vault.Objects.ExportAsync(put.ContentHash, destination);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a locked temp file is not a test failure */ }
    }
}
