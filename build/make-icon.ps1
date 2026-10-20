# Draws the Freevy mark (design/logo/freevy-icon.svg: an F made of a text cursor, a text line and a
# line still being typed, on a turquoise rounded square) at every size Windows asks for, and writes
# them into one .ico with PNG entries. Up to 48 px every shape is snapped to whole pixels, so the
# tray icon stays crisp. -Preview also writes the PNGs next to the .ico for checking.
# Usage: powershell -NoProfile -File build\make-icon.ps1 [-Out src\Freevy.App\Assets\freevy.ico] [-Preview <dir>]
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\src\Freevy.App\Assets\freevy.ico'),
    [string]$Preview = ''
)

Add-Type -AssemblyName System.Drawing

# Geometry in a 100 x 100 box, the same as the SVG.
$tileRadius = 24
$shapes = @(
    @{ X = 24; Y = 21; W = 12; H = 58 },   # stem: the text cursor
    @{ X = 43; Y = 21; W = 33; H = 12 },   # top arm: a line of text
    @{ X = 43; Y = 44; W = 19; H = 12 },   # short arm: the line being typed
    @{ X = 65.5; Y = 44.5; W = 11; H = 11; Dot = $true }   # caret dot
)

function New-Capsule([single]$x, [single]$y, [single]$w, [single]$h) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [math]::Min($w, $h)
    if ($w -ge $h) {
        $path.AddArc($x, $y, $d, $d, 90, 180)
        $path.AddArc($x + $w - $d, $y, $d, $d, 270, 180)
    } else {
        $path.AddArc($x, $y, $d, $d, 180, 180)
        $path.AddArc($x, $y + $h - $d, $d, $d, 0, 180)
    }
    $path.CloseFigure()
    $path
}

function New-Tile([single]$size, [single]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

function Draw-Icon([int]$size) {
    $scale = $size / 100.0
    $snap = $size -le 48
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(54, 222, 205))), (New-Tile $size ([math]::Max(2, $tileRadius * $scale))))

    $ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(12, 34, 38))
    $placed = @()
    foreach ($s in $shapes) {
        $x0 = $s.X * $scale; $y0 = $s.Y * $scale; $x1 = ($s.X + $s.W) * $scale; $y1 = ($s.Y + $s.H) * $scale
        if ($snap) {
            $x0 = [math]::Round($x0); $y0 = [math]::Round($y0); $x1 = [math]::Round($x1); $y1 = [math]::Round($y1)
            if ($s.Dot) {
                # Keep a visible gap after the short arm and keep the dot round.
                $arm = $placed[2]
                $x0 = [math]::Max($x0, $arm.X1 + [math]::Max(1, [math]::Round(2 * $scale)))
                $side = [math]::Max(2, $arm.Y1 - $arm.Y0)
                $y0 = $arm.Y0; $y1 = $arm.Y0 + $side; $x1 = $x0 + $side
            }
            if ($x1 - $x0 -lt 1) { $x1 = $x0 + 1 }
            if ($y1 - $y0 -lt 1) { $y1 = $y0 + 1 }
        }
        $placed += [pscustomobject]@{ X0 = $x0; Y0 = $y0; X1 = $x1; Y1 = $y1 }
        $g.FillPath($ink, (New-Capsule $x0 $y0 ($x1 - $x0) ($y1 - $y0)))
    }
    $g.Dispose()
    $bitmap
}

# Classic 32-bit DIB entry (what every Windows API reads), used for the small sizes.
function Get-DibBytes([System.Drawing.Bitmap]$bitmap) {
    $size = $bitmap.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$size); $w.Write([int32]($size * 2))
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0)
    $w.Write([uint32]($size * $size * 4)); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bitmap.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    # AND mask: all zero (alpha carries the transparency), rows padded to 4 bytes.
    $maskRow = [math]::Ceiling($size / 32) * 4
    $w.Write((New-Object byte[] ($maskRow * $size)))
    $w.Flush()
    $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bitmap = Draw-Icon $size
    $stream = New-Object System.IO.MemoryStream
    if ($size -le 64) {
        $bytes = Get-DibBytes $bitmap
        $stream.Write($bytes, 0, $bytes.Length)
    } else {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    if ($Preview) {
        New-Item -ItemType Directory -Force $Preview | Out-Null
        $bitmap.Save((Join-Path $Preview "freevy-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $bitmap.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$file = [System.IO.File]::Create($Out)
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$image.Bytes.Length); $writer.Write([uint32]$offset)
    $offset += $image.Bytes.Length
}
foreach ($image in $images) { $writer.Write($image.Bytes) }
$writer.Dispose()
Write-Output "Wrote $Out ($($images.Count) sizes)"
