# Draws the Freevy mark (lowercase f on a turquoise rounded square, as AppIcon.cs does at runtime)
# at every size Windows asks for and writes them into one .ico with PNG entries.
# Usage: powershell -NoProfile -File build\make-icon.ps1 [-Out src\Freevy.App\Assets\freevy.ico]
param([string]$Out = (Join-Path $PSScriptRoot '..\src\Freevy.App\Assets\freevy.ico'))

Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    $radius = [single]($size * 0.28)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $radius, $radius, 180, 90)
    $path.AddArc($size - $radius - 1, 0, $radius, $radius, 270, 90)
    $path.AddArc($size - $radius - 1, $size - $radius - 1, $radius, $radius, 0, 90)
    $path.AddArc(0, $size - $radius - 1, $radius, $radius, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(54, 222, 205))), $path)

    $font = New-Object System.Drawing.Font 'Cascadia Code', ([single]($size * 0.52)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = 'Center'
    $format.LineAlignment = 'Center'
    $rect = New-Object System.Drawing.RectangleF 0, ([single]($size * 0.02)), $size, $size
    $g.DrawString('f', $font, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(12, 34, 38))), $rect, $format)
    $g.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
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
