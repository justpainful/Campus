<#
.SYNOPSIS
    Renders the wordmark and the full lockup to PNG so the letterforms can be checked by eye.

.DESCRIPTION
    Uses the same WPF geometry engine the icon exporter uses, reading the generated path data
    rather than any second copy of it.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [int]$Width = 1200
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$OutDir = Join-Path $RepoRoot 'brand/generated'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Push-Location $RepoRoot
try {
    python tools/brand/wordmark.py --emit | Out-Null
    python tools/brand/logo.py --emit | Out-Null
} finally { Pop-Location }

$metrics = @{}
Get-Content (Join-Path $OutDir 'wordmark.metrics') | ForEach-Object {
    $parts = $_.Split('=')
    if ($parts.Count -eq 2) { $metrics[$parts[0]] = [double]$parts[1] }
}

$wordPaths = Get-Content (Join-Path $OutDir 'wordmark.paths')
$markPath = Get-Content (Join-Path $OutDir 'mark.path') -Raw

$White = [System.Windows.Media.Brushes]::White

function Save-Visual {
    param([System.Windows.Media.DrawingVisual]$Visual, [int]$W, [int]$H, [string]$Path)
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
        $W, $H, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($Visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $stream = [System.IO.File]::Create($Path)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

function New-Transform {
    param([double]$Scale, [double]$Dx, [double]$Dy)
    $group = New-Object System.Windows.Media.TransformGroup
    $group.Children.Add((New-Object System.Windows.Media.ScaleTransform($Scale, $Scale)))
    $group.Children.Add((New-Object System.Windows.Media.TranslateTransform($Dx, $Dy)))
    return $group
}

function New-StrokePen {
    param([double]$Thickness)
    $pen = New-Object System.Windows.Media.Pen($White, $Thickness)
    $pen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $pen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $pen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    return $pen
}

# ------------------------------------------------------------------ wordmark only
$pad = 30.0
$boxW = $metrics['width'] + (2 * $pad)
$boxH = $metrics['height'] + $pad + $metrics['ascentMargin']
$scale = $Width / $boxW
$outH = [int][math]::Round($boxH * $scale)
$pen = New-StrokePen -Thickness ($metrics['stroke'] * $scale)

$visual = New-Object System.Windows.Media.DrawingVisual
$ctx = $visual.RenderOpen()
try {
    $ctx.DrawRectangle(
        [System.Windows.Media.Brushes]::Black, $null,
        (New-Object System.Windows.Rect(0, 0, $Width, $outH)))
    foreach ($p in $wordPaths) {
        if ([string]::IsNullOrWhiteSpace($p)) { continue }
        $g = [System.Windows.Media.Geometry]::Parse($p).Clone()
        $g.Transform = New-Transform -Scale $scale -Dx ($pad * $scale) -Dy ($metrics['ascentMargin'] * $scale)
        $ctx.DrawGeometry($null, $pen, $g)
    }
} finally { $ctx.Close() }
Save-Visual -Visual $visual -W $Width -H $outH -Path (Join-Path $OutDir 'wordmark-preview.png')

# ------------------------------------------------------- horizontal lockup (mark + wordmark)
$markGrid = 48.0
$markTarget = 134.0
$markScale = $markTarget / $markGrid
$gapUnits = 34.0

$lockW = ($markGrid * $markScale) + $gapUnits + $metrics['width'] + (2 * $pad)
$lockH = $markTarget + (2 * $pad)
$lockScale = $Width / $lockW
$lockOutH = [int][math]::Round($lockH * $lockScale)
$lockPen = New-StrokePen -Thickness ($metrics['stroke'] * $lockScale)

$visual2 = New-Object System.Windows.Media.DrawingVisual
$ctx2 = $visual2.RenderOpen()
try {
    $ctx2.DrawRectangle(
        [System.Windows.Media.Brushes]::Black, $null,
        (New-Object System.Windows.Rect(0, 0, $Width, $lockOutH)))

    $markGeometry = [System.Windows.Media.Geometry]::Parse($markPath).Clone()
    $markGeometry.Transform = New-Transform -Scale ($markScale * $lockScale) `
        -Dx ($pad * $lockScale) -Dy ($pad * $lockScale)
    $ctx2.DrawGeometry($White, $null, $markGeometry)

    $wordX = $pad + ($markGrid * $markScale) + $gapUnits
    $wordY = $pad + (($markTarget - $metrics['height']) / 2) + 6

    foreach ($p in $wordPaths) {
        if ([string]::IsNullOrWhiteSpace($p)) { continue }
        $g = [System.Windows.Media.Geometry]::Parse($p).Clone()
        $g.Transform = New-Transform -Scale $lockScale -Dx ($wordX * $lockScale) -Dy ($wordY * $lockScale)
        $ctx2.DrawGeometry($null, $lockPen, $g)
    }
} finally { $ctx2.Close() }
Save-Visual -Visual $visual2 -W $Width -H $lockOutH -Path (Join-Path $OutDir 'lockup-preview.png')

Write-Host "wrote wordmark-preview.png and lockup-preview.png to $OutDir"
