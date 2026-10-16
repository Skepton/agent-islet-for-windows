# Draws the app icon (an orange island pill hanging over a dark tile) and writes it as a multi-size .ico.
# Original artwork, drawn in code so the look can be tuned here. Colours match src/Island.App/Theme.xaml.
# Usage: powershell -File scripts\make-icon.ps1
# Output: src\Island.App\Assets\island.ico and docs\images\icon-preview.png

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$icoPath = Join-Path $root 'src\Island.App\Assets\island.ico'
$previewPath = Join-Path $root 'docs\images\icon-preview.png'
New-Item -ItemType Directory -Force (Split-Path $icoPath), (Split-Path $previewPath) | Out-Null

function New-RoundedRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [double]$size

    $orange = [System.Drawing.Color]::FromArgb(255, 0xE5, 0x8C, 0x36)
    $black = [System.Drawing.Color]::FromArgb(255, 0x0B, 0x0B, 0x0D)

    # Orange tile: visible on light and dark taskbars alike.
    $tile = New-RoundedRect ($s * 0.03) ($s * 0.03) ($s * 0.94) ($s * 0.94) ($s * 0.24)
    $g.FillPath((New-Object System.Drawing.SolidBrush $orange), $tile)

    # The island: a black shape that hangs from the top edge of the tile, flat on top, rounded below.
    $g.SetClip($tile)
    $island = New-RoundedRect ($s * 0.17) ($s * -0.30) ($s * 0.66) ($s * 0.98) ($s * 0.24)
    $g.FillPath((New-Object System.Drawing.SolidBrush $black), $island)
    $g.ResetClip()

    # Inside it: the status dot and a line of text.
    $dot = $s * 0.15
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $orange), $s * 0.28, $s * 0.31, $dot, $dot)
    $line = New-RoundedRect ($s * 0.50) ($s * 0.345) ($s * 0.23) ($s * 0.08) ($s * 0.04)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xE8, 0xE8, 0xEC))), $line)

    $g.Dispose()
    return $bmp
}

function Png-Bytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($size in $sizes) {
    $bmp = Draw-Icon $size
    [pscustomobject]@{ Size = $size; Bytes = (Png-Bytes $bmp) }
    $bmp.Dispose()
}

# ICO container: header, one directory entry per image, then the PNG images (supported since Windows Vista).
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dim = if ($image.Size -ge 256) { 0 } else { $image.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$image.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $image.Bytes.Length
}
foreach ($image in $images) { $w.Write($image.Bytes) }
$w.Flush()
[IO.File]::WriteAllBytes($icoPath, $out.ToArray())

# Preview: the 256 px icon next to the small sizes enlarged (nearest neighbour) on dark and light backgrounds.
$preview = New-Object System.Drawing.Bitmap 700, 300
$pg = [System.Drawing.Graphics]::FromImage($preview)
$pg.Clear([System.Drawing.Color]::FromArgb(255, 0x20, 0x20, 0x24))
$pg.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xF3, 0xF3, 0xF3))), 350, 0, 350, 300)
$big = Draw-Icon 256
$pg.DrawImage($big, 20, 22, 256, 256)
$pg.InterpolationMode = 'NearestNeighbor'
$pg.PixelOffsetMode = 'Half'
$x = 300
foreach ($size in 16, 24, 32) {
    $small = Draw-Icon $size
    $pg.DrawImage($small, $x, 40, $size * 4, $size * 4)
    $pg.DrawImage($small, $x + 70, 200, $size, $size)
    $x += $size * 4 + 8
    $small.Dispose()
}
$preview.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
"wrote $icoPath ($($images.Count) sizes) and $previewPath"
