using System.Security.Cryptography;

namespace Campus.Desktop.Services;

/// <summary>
/// A stable identifier for this machine. Journal entries are attributed to it so a sync between
/// the PC and the phone can tell which side made a change, and so a conflict can name the device
/// it came from rather than saying "somewhere else".
/// </summary>
public static class DeviceIdentity
{
    private static string? _cached;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Campus", "device.id");

    /// <summary>
    /// Reads the id, creating one on first run. Deliberately random rather than derived from
    /// hardware: a machine identifier that survives a reinstall would be a fingerprint, and
    /// Campus has no use for one.
    /// </summary>
    public static string Current()
    {
        if (_cached is not null) return _cached;

        try
        {
            if (File.Exists(FilePath))
            {
                var existing = File.ReadAllText(FilePath).Trim();
                if (existing.Length > 0) return _cached = existing;
            }

            var generated = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, generated);
            return _cached = generated;
        }
        catch (IOException)
        {
            // A machine that cannot keep an id still has to work; the change journal just loses
            // its attribution for this session.
            return _cached = "unknown-device";
        }
        catch (UnauthorizedAccessException)
        {
            return _cached = "unknown-device";
        }
    }

    /// <summary>The name shown to the user and to any device paired with this one.</summary>
    public static string DisplayName => Environment.MachineName;
}
