using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Campus.Platform.Windows;

/// <summary>
/// The places Windows expects an application to register itself: the Start menu, the desktop, the
/// list of things that run at sign-in, and the table that decides which program opens which file.
///
/// Everything here is written under HKEY_CURRENT_USER and the user's own folders. Campus never
/// asks for administrator rights, so it never writes anywhere that would need them — which also
/// means uninstalling it is deleting a folder rather than trusting an uninstaller.
/// </summary>
public static class WindowsIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "CampusService";
    private const string ProgId = "Campus.Workspace";
    private const string Extension = ".campus";

    /// <summary>
    /// Registers the background service to start at sign-in — the service, never the window.
    /// An application that reopens itself every time you log in is one you end up uninstalling.
    /// </summary>
    public static bool SetRunAtStartup(string servicePath, bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return false;

            if (enabled) key.SetValue(RunValue, $"\"{servicePath}\" --background");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                      or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static bool RunsAtStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Teaches Windows what a .campus file is, so a backup or an exported workspace has the right
    /// icon and opens in the right program rather than being an unknown blob.
    /// </summary>
    public static bool RegisterFileType(string applicationPath, string iconPath)
    {
        try
        {
            using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                progId.SetValue("", "Campus workspace");

                using var icon = progId.CreateSubKey("DefaultIcon");
                icon.SetValue("", $"\"{iconPath}\",0");

                using var command = progId.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{applicationPath}\" \"%1\"");
            }

            using (var extension = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{Extension}"))
            {
                extension.SetValue("", ProgId);
            }

            // Explorer caches associations aggressively; without this the change is not visible
            // until the next sign-in, which reads as the setting not having worked.
            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                      or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static bool IsFileTypeRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Extension}");
            return key?.GetValue("") as string == ProgId;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Removes everything this class wrote. Uninstalling should leave nothing behind.</summary>
    public static void Unregister()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(RunValue, throwOnMissingValue: false);

            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{Extension}", throwOnMissingSubKey: false);

            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                      or System.Security.SecurityException)
        {
            // Nothing here is worth failing over; the folder can still be deleted.
        }
    }

    // ------------------------------------------------------------------------ shortcuts

    /// <summary>
    /// Makes a shortcut. Written through the shell's own interface rather than by hand, because a
    /// .lnk is a structured binary format and a hand-written one is a shortcut that mostly works.
    /// </summary>
    public static bool CreateShortcut(
        string shortcutPath, string targetPath, string? description = null, string? iconPath = null)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();

            link.SetPath(targetPath);
            link.SetWorkingDirectory(Path.GetDirectoryName(targetPath) ?? "");
            if (description is not null) link.SetDescription(description);
            if (iconPath is not null) link.SetIconLocation(iconPath, 0);

            ((IPersistFile)link).Save(shortcutPath, true);
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static string DesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Campus.lnk");

    public static string StartMenuShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Campus.lnk");

    // --------------------------------------------------------------------------- interop

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport,
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file,
            int maxPath, IntPtr findData, int flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder arguments, int maxArguments);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath,
            int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, int reserved);
        void Resolve(IntPtr window, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport,
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder fileName);
    }
}
