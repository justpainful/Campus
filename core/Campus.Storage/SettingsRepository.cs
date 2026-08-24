using System.Text.Json;

namespace Campus.Storage;

/// <summary>
/// Workspace preferences, kept inside the encrypted database rather than in a settings file.
///
/// That is deliberate: which subjects exist, what the schedule is called, which folders were last
/// open — all of it says something about the person, and none of it belongs in plaintext beside
/// a vault that went to the trouble of hiding the rest.
/// </summary>
public sealed class SettingsRepository(CampusDatabase database)
{
    private readonly CampusDatabase _db = database;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("SELECT value FROM settings WHERE key = @key;");
        command.Parameters.AddWithValue("@key", key);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value as string;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("""
            INSERT INTO settings (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """);
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("DELETE FROM settings WHERE key = @key;");
        command.Parameters.AddWithValue("@key", key);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> AllAsync(CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("SELECT key, value FROM settings;");

        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            all[reader.GetString(0)] = reader.GetString(1);
        return all;
    }

    // ------------------------------------------------------------------- typed access

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var raw = await GetAsync(key, ct).ConfigureAwait(false);
        if (raw is null) return default;

        try
        {
            return JsonSerializer.Deserialize<T>(raw, PayloadSerializer.Options);
        }
        catch (JsonException)
        {
            // A setting written by a newer build, or corrupted, falls back to the default
            // instead of stopping the workspace from opening.
            return default;
        }
    }

    public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
        => SetAsync(key, JsonSerializer.Serialize(value, PayloadSerializer.Options), ct);

    public async Task<bool> GetBoolAsync(string key, bool fallback = false, CancellationToken ct = default)
        => await GetAsync(key, ct).ConfigureAwait(false) switch
        {
            "1" or "true" => true,
            "0" or "false" => false,
            _ => fallback,
        };

    public Task SetBoolAsync(string key, bool value, CancellationToken ct = default)
        => SetAsync(key, value ? "1" : "0", ct);
}
