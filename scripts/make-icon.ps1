# Generates src/VibeDoze/Assets/VibeDoze.ico (multi-size, PNG frames) with System.Drawing.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\VibeDoze\Assets\VibeDoze.ico'))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [float]$size

    # Rounded square with an indigo → violet gradient.
    $r = $s * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($s - $r * 2 - 1, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($s - $r * 2 - 1, $s - $r * 2 - 1, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $s - $r * 2 - 1, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(79, 70, 229)), ([System.Drawing.Color]::FromArgb(147, 51, 234)), 45.0
    $g.FillPath($grad, $path)

    # Crescent moon.
    $outer = New-Object System.Drawing.Drawing2D.GraphicsPath
    $outer.AddEllipse($s * 0.16, $s * 0.22, $s * 0.58, $s * 0.58)
    $cut = New-Object System.Drawing.Drawing2D.GraphicsPath
    $cut.AddEllipse($s * 0.34, $s * 0.10, $s * 0.52, $s * 0.52)
    $region = New-Object System.Drawing.Region $outer
    $region.Exclude($cut)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillRegion($white, $region)

    # "z z" — only where there's room for it.
    if ($size -ge 32) {
        $zBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 255, 255, 255))
        $font1 = New-Object System.Drawing.Font 'Segoe UI', ([float]($s * 0.22)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $font2 = New-Object System.Drawing.Font 'Segoe UI', ([float]($s * 0.15)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $g.DrawString('z', $font1, $zBrush, $s * 0.60, $s * 0.38)
        $g.DrawString('z', $font2, $zBrush, $s * 0.74, $s * 0.24)
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($size in $sizes) { , (New-Frame $size) }

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f) }
$w.Dispose()
Write-Host "Icono generado: $Out"
