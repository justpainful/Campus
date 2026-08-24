using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Campus.Extensions;

namespace Campus.Desktop.Services;

/// <summary>An extension as Campus knows it: what it declares, and what has been decided about it.</summary>
public sealed class InstalledExtension
{
    public required ExtensionManifest Manifest { get; init; }

    /// <summary>Where it lives. Empty for the ones built into Campus.</summary>
    public string Folder { get; init; } = string.Empty;

    public bool IsEnabled { get; set; }

    /// <summary>True once the user has agreed to what it asked for.</summary>
    public bool IsGranted { get; set; }

    /// <summary>What went wrong the last time it was started, if anything.</summary>
    public string? Failure { get; set; }

    public bool IsRunning { get; internal set; }
}

/// <summary>
/// Finding, permitting and running extensions.
///
/// Three rules shape all of this. An extension declares what it wants before any of its code is
/// loaded, so consent can be informed. Consent is asked once, in plain sentences, and remembered.
/// And an extension runs in a process of its own, so the worst it can do is stop working — the
/// workspace it was extending stays open and the keys it never had stay where they are.
/// </summary>
public sealed class ExtensionService(WorkspaceService workspace)
{
    private const string EnabledKey = "extensions.enabled";
    private const string GrantedKey = "extensions.granted";

    private readonly WorkspaceService _workspace = workspace;
    private readonly Dictionary<string, Process> _running = new(StringComparer.Ordinal);
    private readonly List<InstalledExtension> _extensions = [];

    /// <summary>Raised when an extension starts, stops, or has something to say.</summary>
    public event EventHandler<string>? Message;

    public IReadOnlyList<InstalledExtension> Extensions => _extensions;

    /// <summary>Where installed extensions live, beside the vault rather than in Program Files.</summary>
    public string Root => Path.Combine(
        Path.GetDirectoryName(_workspace.Paths.Database) ?? AppContext.BaseDirectory,
        "extensions");

    // ------------------------------------------------------------------------ loading

    public async Task RefreshAsync()
    {
        _extensions.Clear();

        var enabled = await ReadSetAsync(EnabledKey);
        var granted = await ReadSetAsync(GrantedKey);

        foreach (var manifest in BuiltIn.All)
        {
            _extensions.Add(new InstalledExtension
            {
                Manifest = manifest,
                IsEnabled = !enabled.Contains("!" + manifest.Id),
                IsGranted = true,
            });
        }

        if (!Directory.Exists(Root)) return;

        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            var path = Path.Combine(folder, "extension.json");
            if (!File.Exists(path)) continue;

            var manifest = ExtensionManifest.Parse(await File.ReadAllTextAsync(path));
            if (manifest is null)
            {
                Message?.Invoke(this, $"{Path.GetFileName(folder)} has an unreadable manifest.");
                continue;
            }

            _extensions.Add(new InstalledExtension
            {
                Manifest = manifest,
                Folder = folder,
                IsEnabled = enabled.Contains(manifest.Id),
                IsGranted = granted.Contains(manifest.Id),
            });
        }
    }

    private async Task<HashSet<string>> ReadSetAsync(string key)
    {
        var stored = await _workspace.Settings.GetAsync<List<string>>(key);
        return new HashSet<string>(stored ?? [], StringComparer.Ordinal);
    }

    private async Task WriteSetAsync(string key, IEnumerable<string> values)
        => await _workspace.Settings.SetAsync(key, values.Distinct(StringComparer.Ordinal).ToList());

    // ------------------------------------------------------------------- installation

    /// <summary>
    /// Installs from a folder or a .campusx file, which is a zip of the same folder. The manifest
    /// is read before anything is copied, so a package that is not an extension never lands.
    /// </summary>
    public async Task<InstalledExtension?> InstallAsync(string path, CancellationToken ct = default)
    {
        var staging = Path.Combine(Path.GetTempPath(), "campus-extension-" + Guid.NewGuid().ToString("N"));

        try
        {
            if (File.Exists(path))
            {
                Directory.CreateDirectory(staging);
                await Task.Run(() => ZipFile.ExtractToDirectory(path, staging, overwriteFiles: true), ct);
            }
            else if (Directory.Exists(path))
            {
                staging = path;
            }
            else
            {
                return null;
            }

            var manifestPath = Path.Combine(staging, "extension.json");
            if (!File.Exists(manifestPath))
            {
                Message?.Invoke(this, "That package has no extension.json.");
                return null;
            }

            var manifest = ExtensionManifest.Parse(await File.ReadAllTextAsync(manifestPath, ct));
            if (manifest is null)
            {
                Message?.Invoke(this, "That extension's manifest could not be read.");
                return null;
            }

            if (BuiltIn.All.Any(b => b.Id == manifest.Id))
            {
                Message?.Invoke(this, $"{manifest.Name} is built into Campus already.");
                return null;
            }

            var destination = Path.Combine(Root, Safe(manifest.Id));
            Directory.CreateDirectory(Root);

            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            await Task.Run(() => CopyTree(staging, destination), ct);

            var installed = new InstalledExtension
            {
                Manifest = manifest,
                Folder = destination,
                IsEnabled = false,
                IsGranted = false,
            };

            _extensions.Add(installed);
            return installed;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
                                      or UnauthorizedAccessException)
        {
            Message?.Invoke(this, $"That extension could not be installed: {ex.Message}");
            return null;
        }
        finally
        {
            if (staging != path && Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { /* the temp folder is cleaned by Windows */ }
            }
        }
    }

    public async Task UninstallAsync(InstalledExtension extension)
    {
        if (extension.Manifest.IsBuiltIn) return;

        await StopAsync(extension);

        if (Directory.Exists(extension.Folder))
        {
            try { Directory.Delete(extension.Folder, recursive: true); }
            catch (IOException ex) { Message?.Invoke(this, ex.Message); return; }
        }

        _extensions.Remove(extension);

        await WriteSetAsync(EnabledKey,
            _extensions.Where(e => e.IsEnabled && !e.Manifest.IsBuiltIn).Select(e => e.Manifest.Id));
        await WriteSetAsync(GrantedKey,
            _extensions.Where(e => e.IsGranted && !e.Manifest.IsBuiltIn).Select(e => e.Manifest.Id));
    }

    // --------------------------------------------------------------------- permissions

    /// <summary>Records that the user agreed to what an extension asked for.</summary>
    public async Task GrantAsync(InstalledExtension extension)
    {
        extension.IsGranted = true;
        await WriteSetAsync(GrantedKey,
            _extensions.Where(e => e.IsGranted && !e.Manifest.IsBuiltIn).Select(e => e.Manifest.Id));
    }

    public async Task SetEnabledAsync(InstalledExtension extension, bool enabled)
    {
        extension.IsEnabled = enabled;

        if (extension.Manifest.IsBuiltIn)
        {
            // Built-ins are on unless explicitly turned off, so the stored list records the
            // exceptions rather than the norm.
            var disabled = _extensions
                .Where(e => e.Manifest.IsBuiltIn && !e.IsEnabled)
                .Select(e => "!" + e.Manifest.Id);

            await WriteSetAsync(EnabledKey, disabled);
        }
        else
        {
            await WriteSetAsync(EnabledKey,
                _extensions.Where(e => e.IsEnabled && !e.Manifest.IsBuiltIn).Select(e => e.Manifest.Id));

            if (enabled) await StartAsync(extension);
            else await StopAsync(extension);
        }
    }

    // ------------------------------------------------------------------------ running

    /// <summary>
    /// Starts an extension in its own process. Nothing about the workspace is handed to it: the
    /// host talks to Campus over a pipe, and every request it makes is checked against what the
    /// manifest asked for.
    /// </summary>
    public async Task<bool> StartAsync(InstalledExtension extension)
    {
        if (extension.Manifest.IsBuiltIn || _running.ContainsKey(extension.Manifest.Id)) return true;
        if (!extension.IsGranted) return false;

        var host = Path.Combine(AppContext.BaseDirectory, "pluginhost", "Campus.PluginHost.exe");
        if (!File.Exists(host))
        {
            extension.Failure = "The plugin host is missing from this build.";
            return false;
        }

        var storage = Path.Combine(Root, Safe(extension.Manifest.Id), "storage");

        var start = new ProcessStartInfo
        {
            FileName = host,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        start.ArgumentList.Add("--manifest");
        start.ArgumentList.Add(Path.Combine(extension.Folder, "extension.json"));
        start.ArgumentList.Add("--storage");
        start.ArgumentList.Add(storage);

        try
        {
            var process = Process.Start(start);
            if (process is null) return false;

            _running[extension.Manifest.Id] = process;
            extension.IsRunning = true;
            extension.Failure = null;

            // Anything the host writes is read on a thread of its own, so a chatty extension
            // cannot block the UI by filling a pipe nobody is draining.
            _ = Task.Run(() => PumpAsync(extension, process));

            await SendAsync(process, new ExtensionMessage
            {
                Method = ExtensionMethods.Load,
                Id = Guid.NewGuid().ToString("N"),
            });

            return true;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            extension.Failure = ex.Message;
            return false;
        }
    }

    public async Task StopAsync(InstalledExtension extension)
    {
        if (!_running.Remove(extension.Manifest.Id, out var process)) return;

        extension.IsRunning = false;

        try
        {
            await SendAsync(process, new ExtensionMessage { Method = ExtensionMethods.Shutdown });

            // A polite request, then the door. An extension does not get to refuse to stop.
            if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>Runs one of an extension's commands.</summary>
    public async Task InvokeAsync(InstalledExtension extension, string commandId)
    {
        if (!_running.TryGetValue(extension.Manifest.Id, out var process))
        {
            if (!await StartAsync(extension)) return;
            process = _running[extension.Manifest.Id];
        }

        await SendAsync(process, new ExtensionMessage
        {
            Method = ExtensionMethods.Invoke,
            Id = Guid.NewGuid().ToString("N"),
            Payload = commandId,
        });
    }

    private static async Task SendAsync(Process process, ExtensionMessage message)
    {
        await process.StandardInput.WriteLineAsync(message.Serialize());
        await process.StandardInput.FlushAsync();
    }

    private async Task PumpAsync(InstalledExtension extension, Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                var message = ExtensionMessage.Parse(line);
                if (message is null) continue;

                if (message.Error is { Length: > 0 } error)
                {
                    extension.Failure = error;
                    Message?.Invoke(this, $"{extension.Manifest.Name}: {error}");
                    continue;
                }

                switch (message.Method)
                {
                    case ExtensionMethods.Notify when message.Payload is { } payload:
                        var text = JsonSerializer.Deserialize<string>(payload, ExtensionManifest.Json);
                        if (text is { Length: > 0 })
                            Message?.Invoke(this, $"{extension.Manifest.Name}: {text}");
                        break;

                    case ExtensionMethods.Request:
                        await AnswerAsync(extension, process, message);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                      or InvalidOperationException)
        {
            // The host went away. That is what the isolation is for.
        }
        finally
        {
            extension.IsRunning = false;
            _running.Remove(extension.Manifest.Id);

            if (process.HasExited && process.ExitCode != 0)
            {
                extension.Failure ??= $"It stopped unexpectedly (code {process.ExitCode}).";
                Message?.Invoke(this, $"{extension.Manifest.Name} stopped working.");
            }
        }
    }

    /// <summary>
    /// Answers a request from an extension — but only one its manifest asked permission for.
    /// This is where the permission model stops being a promise and starts being a check.
    /// </summary>
    private async Task AnswerAsync(
        InstalledExtension extension, Process process, ExtensionMessage message)
    {
        string? payload = null;
        string? error = null;

        try
        {
            using var document = JsonDocument.Parse(message.Payload ?? "{}");
            var method = document.RootElement.TryGetProperty("method", out var m)
                ? m.GetString() ?? ""
                : "";

            var permissions = extension.Manifest.Permissions;

            switch (method)
            {
                case "workspace.count" when permissions.HasFlag(ExtensionPermissions.ReadWorkspace):
                    var count = await _workspace.Objects.CountAsync(new Domain.CampusQuery());
                    payload = JsonSerializer.Serialize(count, ExtensionManifest.Json);
                    break;

                case "workspace.search" when permissions.HasFlag(ExtensionPermissions.ReadWorkspace):
                    var text = document.RootElement.TryGetProperty("payload", out var q)
                        ? q.GetString() ?? ""
                        : "";
                    var hits = await _workspace.Search.SearchAsync(text, limit: 20);
                    payload = JsonSerializer.Serialize(
                        hits.Select(h => new { id = h.Object.Id.Value, title = h.Object.Title }),
                        ExtensionManifest.Json);
                    break;

                case "workspace.count" or "workspace.search":
                    error = "That extension did not ask for permission to read the workspace.";
                    break;

                default:
                    error = $"Campus does not answer “{method}”.";
                    break;
            }
        }
        catch (JsonException)
        {
            error = "That request could not be read.";
        }

        await SendAsync(process, new ExtensionMessage
        {
            Method = ExtensionMethods.Reply,
            Id = message.Id,
            Payload = payload,
            Error = error,
        });
    }

    public async Task StopAllAsync()
    {
        foreach (var extension in _extensions.Where(e => e.IsRunning).ToList())
            await StopAsync(extension);
    }

    // ------------------------------------------------------------------------ helpers

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        foreach (var folder in Directory.EnumerateDirectories(source))
            CopyTree(folder, Path.Combine(destination, Path.GetFileName(folder)));
    }

    private static string Safe(string id)
    {
        var cleaned = id.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray();
        return cleaned.Length > 0 ? new string(cleaned) : "extension";
    }
}

/// <summary>
/// The extensions Campus ships with.
///
/// They are declared the same way third-party ones are, and appear in the same list, so the
/// extension model is not a second-class path bolted on beside the real features — it is how the
/// real features describe themselves. They run in process because they are Campus.
/// </summary>
public static class BuiltIn
{
    public static readonly IReadOnlyList<ExtensionManifest> All =
    [
        Make("campus.pdf", "PDF", "Reads PDFs: pages, zoom, search, highlights and notes.",
            "file.pdf", [".pdf"]),
        Make("campus.images", "Images", "Shows pictures, including the HEIC an iPhone produces.",
            "file.image", [".png", ".jpg", ".jpeg", ".heic", ".webp", ".gif", ".bmp", ".tiff"]),
        Make("campus.media", "Audio and video", "Plays recordings, with notes pinned to the moment.",
            "file.video", [".mp4", ".mov", ".mkv", ".webm", ".m4a", ".mp3", ".wav"]),
        Make("campus.markdown", "Markdown", "Reads and writes markdown, with links between notes.",
            "file.markdown", [".md", ".markdown"]),
        Make("campus.office", "Word, PowerPoint and Excel", "Reads Office documents without Office.",
            "file.doc", [".docx", ".pptx", ".xlsx", ".csv"]),
        Make("campus.text", "Text and code", "Numbered lines, wrapping, and find.",
            "file.code", [".txt", ".log", ".json", ".xml", ".cs", ".py", ".js", ".html", ".css"]),
        Make("campus.links", "Links", "Keeps web links with their titles rather than as raw URLs.",
            "links", []),
        Make("campus.print", "Print centre", "Queues what has to be printed and remembers what was.",
            "print", []),
    ];

    private static ExtensionManifest Make(
        string id, string name, string description, string symbol, string[] fileTypes) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        Symbol = symbol,
        Author = "Campus",
        Version = "1.0.0",
        IsBuiltIn = true,
        Permissions = ExtensionPermissions.ReadWorkspace
                    | ExtensionPermissions.WriteWorkspace
                    | ExtensionPermissions.ReadFiles,
        Contributes = fileTypes.Length == 0
            ? []
            : [new ExtensionContribution
            {
                Kind = ContributionKind.Viewer,
                Id = id + ".viewer",
                Title = name,
                Symbol = symbol,
                FileTypes = [.. fileTypes],
            }],
    };
}
