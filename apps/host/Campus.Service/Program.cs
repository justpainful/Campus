using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Campus.Service;

/// <summary>
/// The part of Campus that is allowed to be running when Campus is not.
///
/// It does two things, and deliberately only two. It holds the quick-capture shortcut, so a
/// thought can be caught in the two seconds before it is gone, whether or not the window is open.
/// And it watches one folder, so anything dropped there — a scan, a share from the phone, a file
/// saved from a browser — is waiting inside the workspace next time it is opened.
///
/// What it does not do matters more. It never opens the vault, never holds a key, never touches
/// the database, and never talks to a network. It cannot: it has no way to unlock anything. The
/// most it knows is that a file exists at a path, which is what the file system already knew.
///
/// Idle cost is a message loop and a directory watch — no timers, no polling, no work between
/// the moments something actually happens.
/// </summary>
public static class Program
{
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_ALT = 0x0001;
    private const int VK_N = 0x4E;
    private const int HotkeyId = 0xC0DE;

    private const string MutexName = @"Local\Campus.Service";

    private static string DropFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Campus", "drop");

    private static string QueueFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Campus", "pending-import.txt");

    [STAThread]
    public static int Main(string[] args)
    {
        // One service, however many times sign-in decides to start it.
        using var mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst) return 0;

        if (args.Contains("--stop"))
        {
            // Nothing to stop beyond this process; another copy will exit on the mutex above.
            return 0;
        }

        Directory.CreateDirectory(DropFolder);

        using var window = new MessageWindow();
        using var watcher = WatchDropFolder();

        window.HotkeyPressed += () => Send(["--capture"]);

        if (!RegisterHotKey(window.Handle, HotkeyId, MOD_CONTROL | MOD_ALT, VK_N))
        {
            // Something else owns Ctrl+Alt+N. The drop folder still works, and taking the
            // shortcut from whatever has it is not this program's business.
            Log("The quick-capture shortcut is already taken by another application.");
        }

        window.Run();

        UnregisterHotKey(window.Handle, HotkeyId);
        return 0;
    }

    /// <summary>
    /// Anything appearing in the drop folder is offered to Campus. If Campus is not running it
    /// is written to a queue instead, and picked up the next time it opens — which is the whole
    /// point of a folder you can drop things into while the app is closed.
    /// </summary>
    private static FileSystemWatcher WatchDropFolder()
    {
        var watcher = new FileSystemWatcher(DropFolder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };

        watcher.Created += (_, e) => Offer(e.FullPath);
        watcher.Renamed += (_, e) => Offer(e.FullPath);

        return watcher;
    }

    private static void Offer(string path)
    {
        // A file that has only just appeared is often still being written. Waiting for it to be
        // openable is cruder than watching the copy finish, and it is right far more often.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var probe = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                break;
            }
            catch (IOException)
            {
                Thread.Sleep(250);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException)
            {
                return;
            }
        }

        if (!Send(["--import", path])) Queue(path);
    }

    private static void Queue(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(QueueFile)!);
            File.AppendAllText(QueueFile, path + Environment.NewLine, Encoding.UTF8);
        }
        catch (IOException ex)
        {
            Log(ex.Message);
        }
    }

    /// <summary>Hands a message to Campus if it is open. False means it is not.</summary>
    private static bool Send(string[] arguments)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", "Campus.Instance", PipeDirection.Out, PipeOptions.None);

            client.Connect(1200);
            var payload = Encoding.UTF8.GetBytes(string.Join('\n', arguments));
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException
                                      or UnauthorizedAccessException)
        {
            // Not running. For a capture that means starting it; for a file it means queueing.
            return arguments is ["--capture"] && Launch("--capture");
        }
    }

    private static bool Launch(params string[] arguments)
    {
        var app = Path.Combine(AppContext.BaseDirectory, "..", "Campus.exe");
        if (!File.Exists(app)) app = Path.Combine(AppContext.BaseDirectory, "Campus.exe");
        if (!File.Exists(app)) return false;

        try
        {
            var start = new ProcessStartInfo { FileName = app, UseShellExecute = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void Log(string message)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Campus", "service.log");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:u}  {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // A log that cannot be written is not worth crashing a background process over.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, int modifiers, int key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    /// <summary>
    /// A window with no pixels. Windows will only deliver a hotkey to a window, so there has to
    /// be one — but it is message-only, so it never appears, never takes focus and costs nothing.
    /// </summary>
    private sealed class MessageWindow : IDisposable
    {
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private readonly WndProc _handler;
        private readonly IntPtr _class;
        private bool _running = true;

        public MessageWindow()
        {
            _handler = Handle_;

            var wndClass = new WNDCLASS
            {
                lpszClassName = "CampusServiceMessageWindow",
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_handler),
                hInstance = GetModuleHandle(null),
            };

            _class = (IntPtr)RegisterClass(ref wndClass);

            Handle = CreateWindowEx(
                WS_EX_TOOLWINDOW, wndClass.lpszClassName, "Campus", 0, 0, 0, 0, 0,
                // HWND_MESSAGE: a window that exists only to receive messages.
                new IntPtr(-3), IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
        }

        public IntPtr Handle { get; }

        public event Action? HotkeyPressed;

        public void Run()
        {
            while (_running && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }

        private IntPtr Handle_(IntPtr window, int message, IntPtr wParam, IntPtr lParam)
        {
            switch (message)
            {
                case WM_HOTKEY:
                    HotkeyPressed?.Invoke();
                    return IntPtr.Zero;

                case 0x0002: // WM_DESTROY
                    _running = false;
                    PostQuitMessage(0);
                    return IntPtr.Zero;

                default:
                    return DefWindowProc(window, message, wParam, lParam);
            }
        }

        public void Dispose()
        {
            _running = false;
            if (Handle != IntPtr.Zero) DestroyWindow(Handle);
            if (_class != IntPtr.Zero) UnregisterClass("CampusServiceMessageWindow", GetModuleHandle(null));
        }

        private delegate IntPtr WndProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam;
            public IntPtr lParam;
            public int time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClass(ref WNDCLASS wndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool UnregisterClass(string className, IntPtr instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetMessage(out MSG message, IntPtr window, uint first, uint last);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DispatchMessage(ref MSG message);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int exitCode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? moduleName);
    }
}
