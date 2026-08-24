<#
.SYNOPSIS
    Stops Campus, rebuilds it, starts it on a given destination, and captures the window.

.EXAMPLE
    pwsh tools/dev/Shot.ps1 -Destination settings -Out settings.png
    pwsh tools/dev/Shot.ps1 -NoBuild -Destination home
#>

param(
    [string]$Destination = 'home',
    [string]$Out = '',
    [switch]$NoBuild,
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'

$exe = Join-Path $RepoRoot 'apps\desktop\Campus.Desktop\bin\Debug\net10.0-windows10.0.26100.0\win-x64\Campus.exe'
$shots = Join-Path $env:TEMP 'campus-shots'
New-Item -ItemType Directory -Force -Path $shots | Out-Null
if (-not $Out) { $Out = Join-Path $shots "$Destination.png" }

Get-Process -Name Campus -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

if (-not $NoBuild) {
    Push-Location $RepoRoot
    try {
        $output = dotnet build apps/desktop/Campus.Desktop/Campus.Desktop.csproj -c Debug --nologo 2>&1
        $errors = $output | Select-String -Pattern ': error'
        if ($errors) { $errors | Select-Object -First 10; throw 'Build failed.' }
    }
    finally { Pop-Location }
}

$log = Join-Path $env:LOCALAPPDATA 'Campus\logs\startup.log'
Remove-Item $log -ErrorAction SilentlyContinue

Start-Process -FilePath $exe -ArgumentList '--open', $Destination | Out-Null
Start-Sleep -Seconds 6

powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File (Join-Path $RepoRoot 'tools\dev\Capture-Window.ps1') -ProcessName Campus -Out $Out

if (Test-Path $log) {
    Write-Warning 'Errors were logged during startup:'
    Get-Content $log -TotalCount 20
}
