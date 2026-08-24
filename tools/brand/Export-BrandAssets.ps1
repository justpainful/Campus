<#
.SYNOPSIS
    Renders the Campus brand geometry to PNG and ICO at every size the app and the shell need.

.DESCRIPTION
    The mark is defined once, as path data, in tools/brand/logo.py. This script is the only
    rasteriser: it parses that same path with WPF's geometry engine and renders it, so the
    exported PNGs and the shapes drawn live in the app can never drift apart.

    Produces:
        brand/generated/png/mark-<size>.png            transparent, white mark
        brand/generated/png/icon-square-<size>.png     black square, white mark
        brand/generated/png/icon-rounded-<size>.png    black rounded square, white mark
        brand/generated/png/file-icon-<size>.png       .campus document icon
        brand/generated/Campus.ico                     multi-resolution app icon
        brand/generated/CampusFile.ico                 multi-resolution document icon

.EXAMPLE
    pwsh tools/brand/Export-BrandAssets.ps1
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [int[]]$Sizes = @(16, 32, 64, 128, 256, 512, 1024)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$GridSize = 48.0
$OutDir = Join-Path $RepoRoot 'brand/generated'
$PngDir = Join-Path $OutDir 'png'
New-Item -ItemType Directory -Force -Path $PngDir | Out-Null

# Regenerate the geometry so the export can never lag behind the definition.
Push-Location $RepoRoot
try { python tools/brand/logo.py --emit | Out-Null } finally { Pop-Location }

$markPath = Get-Content (Join-Path $OutDir 'mark.path') -Raw
if ([string]::IsNullOrWhiteSpace($markPath)) { throw 'mark.path is empty — run tools/brand/logo.py first.' }

$White = [System.Windows.Media.Brushes]::White
$Black = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0, 0, 0))
$Black.Freeze()
# The page fold on the document icon, a step lighter than the body so it reads as a fold.
$FoldBrush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x3A, 0x3A, 0x3C))
$FoldBrush.Freeze()

function New-Bitmap {
    param([System.Windows.Media.DrawingVisual]$Visual, [int]$Size)
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
        $Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($Visual)
    return $bmp
}

function Save-Png {
    param([System.Windows.Media.Imaging.BitmapSource]$Bitmap, [string]$Path)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
    $stream = [System.IO.File]::Create($Path)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

function Render-Mark {
    <#
        Draws the mark, optionally on a plate. Inset is the proportion of the plate the mark
        occupies — Apple-style icons leave generous breathing room, so the default is 0.62.
    #>
    param(
        [int]$Size,
        [System.Windows.Media.Brush]$Foreground,
        [System.Windows.Media.Brush]$Plate = $null,
        [double]$PlateRadius = 0,
        [double]$Inset = 0.62
    )

    $visual = New-Object System.Windows.Media.DrawingVisual
    $ctx = $visual.RenderOpen()
    try {
        if ($null -ne $Plate) {
            $rect = New-Object System.Windows.Rect(0, 0, $Size, $Size)
            $ctx.DrawRoundedRectangle($Plate, $null, $rect, $PlateRadius, $PlateRadius)
        }

        $geometry = [System.Windows.Media.Geometry]::Parse($markPath).Clone()
        $scale = ($Size * $Inset) / $GridSize
        $offset = ($Size - ($GridSize * $scale)) / 2.0

        $transform = New-Object System.Windows.Media.TransformGroup
        $transform.Children.Add((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))
        $transform.Children.Add((New-Object System.Windows.Media.TranslateTransform($offset, $offset)))
        $geometry.Transform = $transform

        $ctx.DrawGeometry($Foreground, $null, $geometry)
    }
    finally { $ctx.Close() }

    return New-Bitmap -Visual $visual -Size $Size
}

function Render-FileIcon {
    <# The .campus document icon: a page with a folded corner and the mark centred on it. #>
    param([int]$Size)

    $visual = New-Object System.Windows.Media.DrawingVisual
    $ctx = $visual.RenderOpen()
    try {
        $u = $Size / 48.0            # work on the same 48-unit grid as the mark
        $left = 7 * $u
        $right = 41 * $u
        $top = 3 * $u
        $bottom = 45 * $u
        $foldSize = 11 * $u
        $radius = 3 * $u

        $page = New-Object System.Windows.Media.PathGeometry
        $figure = New-Object System.Windows.Media.PathFigure
        $figure.StartPoint = New-Object System.Windows.Point(($left + $radius), $top)
        $figure.IsClosed = $true
        $figure.IsFilled = $true

        $add = {
            param($x, $y)
            $figure.Segments.Add((New-Object System.Windows.Media.LineSegment(
                (New-Object System.Windows.Point($x, $y)), $false)))
        }

        & $add ($right - $foldSize) $top
        & $add $right ($top + $foldSize)
        & $add $right ($bottom - $radius)
        $figure.Segments.Add((New-Object System.Windows.Media.ArcSegment(
            (New-Object System.Windows.Point(($right - $radius), $bottom)),
            (New-Object System.Windows.Size($radius, $radius)), 0,
            $false, [System.Windows.Media.SweepDirection]::Clockwise, $false)))
        & $add ($left + $radius) $bottom
        $figure.Segments.Add((New-Object System.Windows.Media.ArcSegment(
            (New-Object System.Windows.Point($left, ($bottom - $radius))),
            (New-Object System.Windows.Size($radius, $radius)), 0,
            $false, [System.Windows.Media.SweepDirection]::Clockwise, $false)))
        & $add $left ($top + $radius)
        $figure.Segments.Add((New-Object System.Windows.Media.ArcSegment(
            (New-Object System.Windows.Point(($left + $radius), $top)),
            (New-Object System.Windows.Size($radius, $radius)), 0,
            $false, [System.Windows.Media.SweepDirection]::Clockwise, $false)))

        $page.Figures.Add($figure)
        $ctx.DrawGeometry($Black, $null, $page)

        # The folded corner.
        $foldGeometry = New-Object System.Windows.Media.PathGeometry
        $foldFigure = New-Object System.Windows.Media.PathFigure
        $foldFigure.StartPoint = New-Object System.Windows.Point(($right - $foldSize), $top)
        $foldFigure.IsClosed = $true
        $foldFigure.IsFilled = $true
        $foldFigure.Segments.Add((New-Object System.Windows.Media.LineSegment(
            (New-Object System.Windows.Point(($right - $foldSize), ($top + $foldSize))), $false)))
        $foldFigure.Segments.Add((New-Object System.Windows.Media.LineSegment(
            (New-Object System.Windows.Point($right, ($top + $foldSize))), $false)))
        $foldGeometry.Figures.Add($foldFigure)
        $ctx.DrawGeometry($FoldBrush, $null, $foldGeometry)

        # The mark, sized to sit inside the page rather than the whole canvas.
        $geometry = [System.Windows.Media.Geometry]::Parse($markPath).Clone()
        $scale = ($Size * 0.42) / $GridSize
        $offsetX = ($Size - ($GridSize * $scale)) / 2.0
        $offsetY = $offsetX + ($Size * 0.02)

        $transform = New-Object System.Windows.Media.TransformGroup
        $transform.Children.Add((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))
        $transform.Children.Add((New-Object System.Windows.Media.TranslateTransform($offsetX, $offsetY)))
        $geometry.Transform = $transform
        $ctx.DrawGeometry($White, $null, $geometry)
    }
    finally { $ctx.Close() }

    return New-Bitmap -Visual $visual -Size $Size
}

function Get-DibBytes {
    <#
        Converts a rendered bitmap into the DIB form an .ico frame uses: a BITMAPINFOHEADER
        whose height is doubled to account for the mask, bottom-up BGRA rows, then a 1bpp AND
        mask. PNG-compressed frames are legal in modern Windows but the C# compiler's Win32
        resource writer will not read them, so DIB it is.
    #>
    param([System.Windows.Media.Imaging.BitmapSource]$Bitmap)

    $w = $Bitmap.PixelWidth
    $h = $Bitmap.PixelHeight
    $stride = $w * 4
    $pixels = New-Object byte[] ($stride * $h)
    $Bitmap.CopyPixels($pixels, $stride, 0)

    $maskStride = [int](([math]::Floor(($w + 31) / 32)) * 4)
    $maskSize = $maskStride * $h

    $memory = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($memory)
    try {
        $writer.Write([uint32]40)          # biSize
        $writer.Write([int32]$w)           # biWidth
        $writer.Write([int32]($h * 2))     # biHeight — image plus mask
        $writer.Write([uint16]1)           # biPlanes
        $writer.Write([uint16]32)          # biBitCount
        $writer.Write([uint32]0)           # biCompression = BI_RGB
        $writer.Write([uint32]($stride * $h + $maskSize))
        $writer.Write([int32]0)            # biXPelsPerMeter
        $writer.Write([int32]0)            # biYPelsPerMeter
        $writer.Write([uint32]0)           # biClrUsed
        $writer.Write([uint32]0)           # biClrImportant

        # Colour rows, bottom-up.
        for ($y = $h - 1; $y -ge 0; $y--) {
            $writer.Write($pixels, $y * $stride, $stride)
        }

        # The AND mask is unused for 32-bit frames, but the bytes must still be there.
        $writer.Write((New-Object byte[] $maskSize))

        $writer.Flush()
        return ,$memory.ToArray()
    }
    finally { $writer.Dispose() }
}

function Save-Ico {
    <# Writes a multi-resolution .ico from DIB frames. #>
    param([hashtable]$Frames, [string]$Path)

    $entries = @($Frames.Keys | Sort-Object)
    $stream = [System.IO.File]::Create($Path)
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([uint16]0)                 # reserved
        $writer.Write([uint16]1)                 # type: icon
        $writer.Write([uint16]$entries.Count)

        $offset = 6 + (16 * $entries.Count)
        foreach ($size in $entries) {
            $bytes = [byte[]]$Frames[$size]
            # 256 is encoded as 0 in the directory, which is how the format expresses it.
            $dimension = [byte]$(if ($size -ge 256) { 0 } else { $size })
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0)               # palette count
            $writer.Write([byte]0)               # reserved
            $writer.Write([uint16]1)             # colour planes
            $writer.Write([uint16]32)            # bits per pixel
            $writer.Write([uint32]$bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $bytes.Length
        }
        foreach ($size in $entries) { $writer.Write([byte[]]$Frames[$size]) }
    }
    finally { $writer.Dispose() }
}

function Get-PngBytes {
    param([System.Windows.Media.Imaging.BitmapSource]$Bitmap)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
    $memory = New-Object System.IO.MemoryStream
    try { $encoder.Save($memory); return ,$memory.ToArray() } finally { $memory.Dispose() }
}

Write-Host 'Rendering brand assets...'

$appFrames = @{}
$fileFrames = @{}

foreach ($size in $Sizes) {
    # Plain mark, no plate — for in-app use over any surface.
    Save-Png (Render-Mark -Size $size -Foreground $White -Inset 0.84) `
        (Join-Path $PngDir "mark-$size.png")

    # Square app icon.
    $square = Render-Mark -Size $size -Foreground $White -Plate $Black -PlateRadius 0
    Save-Png $square (Join-Path $PngDir "icon-square-$size.png")

    # Rounded app icon. The radius follows the Apple squircle proportion of about 22%.
    $rounded = Render-Mark -Size $size -Foreground $White -Plate $Black -PlateRadius ($size * 0.22)
    Save-Png $rounded (Join-Path $PngDir "icon-rounded-$size.png")

    # Document icon for .campus files.
    $document = Render-FileIcon -Size $size
    Save-Png $document (Join-Path $PngDir "file-icon-$size.png")

    if ($size -le 256) {
        $appFrames[$size] = Get-DibBytes $rounded
        $fileFrames[$size] = Get-DibBytes $document
    }

    Write-Host "  $($size)px"
}

Save-Ico -Frames $appFrames -Path (Join-Path $OutDir 'Campus.ico')
Save-Ico -Frames $fileFrames -Path (Join-Path $OutDir 'CampusFile.ico')

# The app icon the desktop project compiles in.
$assets = Join-Path $RepoRoot 'apps/desktop/Campus.Desktop/Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
Copy-Item (Join-Path $OutDir 'Campus.ico') (Join-Path $assets 'Campus.ico') -Force

Write-Host "Done. Assets in $OutDir"
