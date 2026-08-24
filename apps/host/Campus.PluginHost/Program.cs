using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Campus.Extensions;

namespace Campus.PluginHost;

/// <summary>
/// Runs one extension, in its own process.
///
/// This exists so that an extension cannot take Campus down with it. A plugin that loops forever,
/// corrupts its own heap or throws on a background thread kills this process and nothing else —
/// the workspace stays open, the vault stays unlocked, and the user is told which extension
/// stopped working rather than losing what they were writing.
///
/// It also means an extension never shares an address space with the decryption keys.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Standard output is the channel; anything else an extension prints must not corrupt it.
        var channel = Console.Out;
        Console.SetOut(Console.Error);

        var manifestPath = Argument(args, "--manifest");
        var storage = Argument(args, "--storage") ?? Path.GetTempPath();

        if (manifestPath is null || !File.Exists(manifestPath))
        {
            await WriteAsync(channel, new ExtensionMessage
            {
                Method = ExtensionMethods.Reply,
                Error = "No manifest was given.",
            });
            return 2;
        }

        var manifest = ExtensionManifest.Parse(await File.ReadAllTextAsync(manifestPath));
        if (manifest is null)
        {
            await WriteAsync(channel, new ExtensionMessage
            {
                Method = ExtensionMethods.Reply,
                Error = "That manifest could not be read.",
            });
            return 2;
        }

        var host = new Host(manifest, Path.GetDirectoryName(manifestPath)!, storage, channel);

        try
        {
            return await host.RunAsync();
        }
        catch (Exception ex)
        {
            // A crash here is the extension's crash, reported rather than swallowed.
            await WriteAsync(channel, new ExtensionMessage
            {
                Method = ExtensionMethods.Reply,
                Error = $"{manifest.Name} stopped: {ex.Message}",
            });
            return 1;
        }
    }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    internal static async Task WriteAsync(TextWriter channel, ExtensionMessage message)
    {
        await channel.WriteLineAsync(message.Serialize());
        await channel.FlushAsync();
    }
}

/// <summary>The loop: read a line, do the thing, write a line.</summary>
internal sealed class Host(
    ExtensionManifest manifest, string folder, string storage, TextWriter channel)
    : IExtensionContext
{
    private ICampusExtension? _extension;
    private AssemblyLoadContext? _context;
    private readonly Dictionary<string, TaskCompletionSource<string?>> _pending = new(StringComparer.Ordinal);

    public ExtensionManifest Manifest { get; } = manifest;
    public string StorageDirectory { get; } = storage;

    public async Task<int> RunAsync()
    {
        Directory.CreateDirectory(StorageDirectory);

        while (await Console.In.ReadLineAsync() is { } line)
        {
            var message = ExtensionMessage.Parse(line);
            if (message is null) continue;

            switch (message.Method)
            {
                case ExtensionMethods.Load:
                    await LoadAsync(message.Id);
                    break;

                case ExtensionMethods.Invoke:
                    await InvokeAsync(message);
                    break;

                case ExtensionMethods.Reply:
                    // An answer to something this process asked for.
                    if (_pending.Remove(message.Id, out var waiting))
                        waiting.TrySetResult(message.Error is null ? message.Payload : null);
                    break;

                case ExtensionMethods.Shutdown:
                    await ShutdownAsync();
                    return 0;
            }
        }

        await ShutdownAsync();
        return 0;
    }

    private async Task LoadAsync(string id)
    {
        try
        {
            if (Manifest.Entry is not { Length: > 0 } entry)
            {
                // An extension with no assembly is a declaration only — templates, collections
                // and commands that Campus itself carries out. That is a valid extension.
                await Program.WriteAsync(channel, new ExtensionMessage
                {
                    Method = ExtensionMethods.Reply,
                    Id = id,
                    Payload = JsonSerializer.Serialize(Manifest.Contributes, ExtensionManifest.Json),
                });
                return;
            }

            var assemblyPath = Path.Combine(folder, entry);
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException($"{entry} is missing from the extension.");

            // A collectible context of its own: the extension's dependencies cannot collide with
            // the host's, and unloading it is possible if this ever needs to reload one.
            _context = new AssemblyLoadContext(Manifest.Id, isCollectible: true);
            var assembly = _context.LoadFromAssemblyPath(assemblyPath);

            var type = assembly.GetTypes()
                .FirstOrDefault(t => typeof(ICampusExtension).IsAssignableFrom(t)
                                     && t is { IsAbstract: false, IsInterface: false })
                ?? throw new TypeLoadException("Nothing in that assembly is a Campus extension.");

            _extension = (ICampusExtension?)Activator.CreateInstance(type)
                ?? throw new TypeLoadException("That extension could not be created.");

            await _extension.ActivateAsync(this);

            await Program.WriteAsync(channel, new ExtensionMessage
            {
                Method = ExtensionMethods.Reply,
                Id = id,
                Payload = JsonSerializer.Serialize(Manifest.Contributes, ExtensionManifest.Json),
            });
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException
                                      or TypeLoadException or ReflectionTypeLoadException
                                      or MissingMethodException or InvalidOperationException)
        {
            await Program.WriteAsync(channel, new ExtensionMessage
            {
                Method = ExtensionMethods.Reply,
                Id = id,
                Error = ex.Message,
            });
        }
    }

    private async Task InvokeAsync(ExtensionMessage message)
    {
        if (_extension is null)
        {
            await Program.WriteAsync(channel, new ExtensionMessage
            {
                Method = ExtensionMethods.Reply,
                Id = message.Id,
                Error = "That extension has not been loaded.",
            });
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _extension.InvokeAsync(message.Payload ?? "", null, timeout.Token);

            await Program.WriteAsync(channel, new ExtensionMessage
            {
                Method = ExtensionMethods.Reply,
                Id = message.Id,
            });
        }
        catch (Exception ex)
        {
            // Whatever an extension throws is its problem to report, not Campus's to crash on.
            await Program.WriteAsync(channel, new ExtensionMessage
            {
                Method = ExtensionMethods.Reply,
                Id = message.Id,
                Error = ex.Message,
            });
        }
    }

    private async Task ShutdownAsync()
    {
        if (_extension is null) return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _extension.DeactivateAsync(timeout.Token);
        }
        catch (Exception)
        {
            // Shutting down is best-effort. The process is about to end regardless.
        }

        _context?.Unload();
    }

    // ------------------------------------------------------------- the extension's view

    public Task NotifyAsync(string message, CancellationToken ct = default)
        => Program.WriteAsync(channel, new ExtensionMessage
        {
            Method = ExtensionMethods.Notify,
            Payload = JsonSerializer.Serialize(message, ExtensionManifest.Json),
        });

    public async Task<string?> RequestAsync(
        string method, string? payload, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var waiting = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiting;

        await Program.WriteAsync(channel, new ExtensionMessage
        {
            Method = ExtensionMethods.Request,
            Id = id,
            Payload = JsonSerializer.Serialize(new { method, payload }, ExtensionManifest.Json),
        });

        // An extension that asks something and is never answered must not wait forever.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        timeout.Token.Register(() => waiting.TrySetResult(null));

        return await waiting.Task;
    }
}
