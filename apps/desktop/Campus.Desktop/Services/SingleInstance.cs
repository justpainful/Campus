using System.IO.Pipes;
using System.Text;

namespace Campus.Desktop.Services;

/// <summary>
/// Keeps Campus to one window, and gives anything else a way to talk to it.
///
/// Two copies of a workspace open at once is not a cosmetic problem: they would hold the same
/// encrypted database, autosave over each other and disagree about what is unlocked. So the second
/// copy hands over what it was asked to do and gets out of the way.
///
/// The same channel is what the background service uses — a named pipe scoped to this user, so
/// nothing another account is running can send anything through it.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\Campus.SingleInstance";
    private const string PipeName = "Campus.Instance";

    private static Mutex? _mutex;
    private static CancellationTokenSource? _listening;

    /// <summary>What another copy of Campus, or the service, asked for.</summary>
    public static event EventHandler<string[]>? MessageReceived;

    /// <summary>
    /// True when this process is the one that should show a window. False means the arguments
    /// have been handed to the window that already exists and this process should exit.
    /// </summary>
    public static bool Claim(string[] arguments)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);

        if (isFirst)
        {
            Listen();
            return true;
        }

        Send(arguments);
        return false;
    }

    /// <summary>Sends a message to the running copy. Returns false when there is not one.</summary>
    public static bool Send(string[] arguments)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.Out, PipeOptions.None);

            // A short wait: either it is there or the caller should start it.
            client.Connect(1500);

            var payload = Encoding.UTF8.GetBytes(string.Join('\n', arguments));
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException
                                      or UnauthorizedAccessException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static void Listen()
    {
        _listening = new CancellationTokenSource();
        var token = _listening.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // A fresh server per connection: a pipe that has been used once cannot be
                    // reused, and holding one open for every possible client is not free.
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(token);

                    var arguments = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (arguments.Length > 0) MessageReceived?.Invoke(null, arguments);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // One failed connection should not stop Campus listening for the next.
                    await Task.Delay(200, CancellationToken.None);
                }
            }
        }, token);
    }

    public static void Release()
    {
        _listening?.Cancel();
        _listening?.Dispose();
        _listening = null;

        if (_mutex is null) return;

        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { /* it was never held */ }

        _mutex.Dispose();
        _mutex = null;
    }
}
