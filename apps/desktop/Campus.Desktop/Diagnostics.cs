namespace Campus.Desktop;

/// <summary>
/// A startup log. XAML failures arrive as a stowed exception with no managed stack attached, so
/// the only reliable way to see what went wrong is to write it down as it happens.
/// </summary>
public static class Diagnostics
{
    private static readonly Lock Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Campus", "logs", "startup.log");

    public static void Log(string stage, Exception? exception)
    {
        if (exception is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath,
                    $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {stage}: {exception.GetType().FullName}: "
                    + $"{exception.Message}{Environment.NewLine}{exception.StackTrace}{Environment.NewLine}"
                    + $"{new string('-', 60)}{Environment.NewLine}");
            }
        }
        catch (IOException) { /* logging must never be the thing that crashes the app */ }
        catch (UnauthorizedAccessException) { }
    }

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
