<#
.SYNOPSIS
    Installs Campus for the current user, or updates an installation in place.

.DESCRIPTION
    Campus keeps its program and its data in two different places, and this only ever writes to
    the first of them:

        program   %LOCALAPPDATA%\Programs\Campus      replaced wholesale on every update
        data      %LOCALAPPDATA%\Campus               never touched by this script

    That separation is the point. Updating is "delete the program, put a new one there", which is
    only safe because the vault, the database, the search index and the settings are somewhere
    else entirely. Run this script again whenever you want the newest build; nothing you have put
    into Campus is at risk from it.

    The new build is published to a staging folder first and only swapped in once it has been
    built successfully, so a failed update leaves the working copy alone rather than half of it.

    No administrator rights are needed and nothing is written outside the user's profile.

.PARAMETER Uninstall
    Removes the program, the shortcuts and the Apps-list entry. Leaves the data alone.

.PARAMETER PurgeData
    Only with -Uninstall, and only if you mean it: also deletes the vault. There is no undo and
    no copy anywhere else.

.EXAMPLE
    pwsh tools/install/Install-Campus.ps1

.EXAMPLE
    pwsh tools/install/Install-Campus.ps1 -Uninstall
#>

[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$PurgeData,
    [switch]$NoShortcuts,
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'

$programDir  = Join-Path $env:LOCALAPPDATA 'Programs\Campus'
$stagingDir  = Join-Path $env:LOCALAPPDATA 'Programs\Campus.staging'
$dataDir     = Join-Path $env:LOCALAPPDATA 'Campus'
$exePath     = Join-Path $programDir 'Campus.exe'
$startMenu   = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Campus.lnk'
$desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Campus.lnk'
$registryKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Campus'

function Write-Step($message) { Write-Host "==> $message" -ForegroundColor Cyan }
function Write-Note($message) { Write-Host "    $message" -ForegroundColor DarkGray }

# Campus holds its own executable open, and so do the three helpers that run beside it. None of
# them can be replaced while running, so they are stopped first — and the vault is closed when
# the window is, so nothing is lost by doing it.
function Stop-Campus {
    $running = Get-Process -Name 'Campus', 'Campus.Service', 'Campus.Indexer', 'Campus.PluginHost' `
        -ErrorAction SilentlyContinue

    if (-not $running) { return }

    Write-Step "Closing Campus"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

function New-Shortcut($path, $target, $description) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $target
    $link.WorkingDirectory = Split-Path $target
    $link.Description = $description
    $link.IconLocation = "$target,0"
    $link.Save()
}

# ---------------------------------------------------------------------------- uninstall

if ($Uninstall) {
    Stop-Campus

    Write-Step "Removing the program"
    Remove-Item -Recurse -Force $programDir -ErrorAction SilentlyContinue
    Remove-Item -Force $startMenu, $desktopLink -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $registryKey -ErrorAction SilentlyContinue

    if ($PurgeData) {
        Write-Host "    Deleting the vault at $dataDir" -ForegroundColor Red
        Remove-Item -Recurse -Force $dataDir -ErrorAction SilentlyContinue
        Write-Host "    Gone. There is no copy." -ForegroundColor Red
    }
    else {
        Write-Note "Your workspace is still at $dataDir"
        Write-Note "Install Campus again and it will be there."
    }

    return
}

# ---------------------------------------------------------------------------- build

$project = Join-Path $RepoRoot 'apps\desktop\Campus.Desktop\Campus.Desktop.csproj'
if (-not (Test-Path $project)) { throw "Cannot find $project" }

$updating = Test-Path $exePath
Write-Step $(if ($updating) { "Updating Campus" } else { "Installing Campus" })

if (Test-Path $stagingDir) { Remove-Item -Recurse -Force $stagingDir }

Write-Step "Building a release copy (this takes a few minutes)"

# Self-contained: the machine needs no .NET installed and no Windows App SDK runtime, and an
# update cannot be broken by something else on the system changing underneath it.
$publish = & dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishDir="$stagingDir\" `
    --nologo 2>&1

if ($LASTEXITCODE -ne 0) {
    $publish | Select-String -Pattern ': error' -SimpleMatch | Select-Object -First 10
    Remove-Item -Recurse -Force $stagingDir -ErrorAction SilentlyContinue
    throw "The build failed. Nothing was changed."
}

if (-not (Test-Path (Join-Path $stagingDir 'Campus.exe'))) {
    Remove-Item -Recurse -Force $stagingDir -ErrorAction SilentlyContinue
    throw "The build produced no Campus.exe. Nothing was changed."
}

# ---------------------------------------------------------------------------- swap

Stop-Campus

Write-Step "Putting it in place"

if (Test-Path $programDir) {
    # Moved aside rather than deleted, so a swap that fails halfway can still be undone.
    $previous = "$programDir.previous"
    if (Test-Path $previous) { Remove-Item -Recurse -Force $previous }
    Move-Item $programDir $previous
}

try {
    Move-Item $stagingDir $programDir
}
catch {
    if (Test-Path "$programDir.previous") { Move-Item "$programDir.previous" $programDir }
    throw
}

Remove-Item -Recurse -Force "$programDir.previous" -ErrorAction SilentlyContinue

# ---------------------------------------------------------------------------- shortcuts

if (-not $NoShortcuts) {
    Write-Step "Shortcuts"
    New-Shortcut $startMenu $exePath 'Campus — Personal Academic Workspace'
    New-Shortcut $desktopLink $exePath 'Campus — Personal Academic Workspace'
}

# ---------------------------------------------------------------------------- apps list

$version = (Get-Item $exePath).VersionInfo.FileVersion
if (-not $version) { $version = '1.0.0.0' }

$size = [math]::Round(
    (Get-ChildItem $programDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)

New-Item -Path $registryKey -Force | Out-Null
Set-ItemProperty $registryKey DisplayName    'Campus'
Set-ItemProperty $registryKey DisplayVersion $version
Set-ItemProperty $registryKey Publisher      'Campus'
Set-ItemProperty $registryKey DisplayIcon    $exePath
Set-ItemProperty $registryKey InstallLocation $programDir
Set-ItemProperty $registryKey EstimatedSize  $size -Type DWord
Set-ItemProperty $registryKey NoModify       1 -Type DWord
Set-ItemProperty $registryKey NoRepair       1 -Type DWord
Set-ItemProperty $registryKey UninstallString `
    "pwsh -NoProfile -File `"$PSCommandPath`" -Uninstall"

# ---------------------------------------------------------------------------- report

Write-Host ""
Write-Step $(if ($updating) { "Updated" } else { "Installed" })
Write-Note "Program   $programDir"
Write-Note "Version   $version"
Write-Note "Data      $dataDir   (untouched by this script)"

if (Test-Path (Join-Path $dataDir 'Vault\vault.header')) {
    $objects = Get-ChildItem (Join-Path $dataDir 'Vault\objects') -Recurse -File `
        -ErrorAction SilentlyContinue
    $bytes = ($objects | Measure-Object Length -Sum).Sum
    Write-Note ("Workspace {0} stored file(s), {1:N1} MB — still there" -f `
        $objects.Count, ($bytes / 1MB))
}
else {
    Write-Note "No workspace yet — Campus will offer to create one."
}

Write-Host ""
Write-Note "Run this script again any time to update. Your workspace is not part of the program."
