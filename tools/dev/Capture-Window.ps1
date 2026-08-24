<#
.SYNOPSIS
    Captures a running window to a PNG, for checking the UI without a packaged install.

.DESCRIPTION
    Uses PrintWindow with PW_RENDERFULLCONTENT so it captures the composed frame of a WinUI
    window rather than a blank surface. Run under Windows PowerShell 5.1, which has
    System.Drawing available without an extra package.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/dev/Capture-Window.ps1 -ProcessName Campus -Out shot.png
#>

param(
    [string]$ProcessName = 'Campus',
    [string]$Out = 'window.png'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class WindowCapture
{
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    // The window rectangle including the drop shadow is bigger than the visible frame; the
    // extended frame bounds is what the user actually sees.
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);

    public const int ExtendedFrameBounds = 9;
    public const uint RenderFullContent = 2;
}
'@

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { throw "No window found for process '$ProcessName'." }

$hwnd = $proc.MainWindowHandle

# Give the window a predictable size so screenshots are comparable between runs.
[void][WindowCapture]::ShowWindow($hwnd, 9)          # SW_RESTORE
[void][WindowCapture]::MoveWindow($hwnd, 60, 40, 1480, 940, $true)
[void][WindowCapture]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 900

$rect = New-Object WindowCapture+Rect
[void][WindowCapture]::DwmGetWindowAttribute(
    $hwnd, [WindowCapture]::ExtendedFrameBounds, [ref]$rect,
    [System.Runtime.InteropServices.Marshal]::SizeOf($rect))

$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
if ($w -le 0 -or $h -le 0) { throw "Window has no visible bounds." }

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
try {
    # PrintWindow can come back empty for composed windows, so fall back to grabbing the
    # screen region the window occupies.
    $hdc = $g.GetHdc()
    $ok = [WindowCapture]::PrintWindow($hwnd, $hdc, [WindowCapture]::RenderFullContent)
    $g.ReleaseHdc($hdc)

    if (-not $ok) {
        $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
    }
}
finally { $g.Dispose() }

$full = [System.IO.Path]::GetFullPath($Out)
$bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $full ($w x $h)"
