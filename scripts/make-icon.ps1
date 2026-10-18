# Generates src/VibeDoze/Assets/VibeDoze.ico (multi-size, PNG frames) with System.Drawing.
# Design: indigo squircle, white cup with a ">_" prompt cut into it, steam above.
# Small sizes drop the prompt (≤ 32 px) and the steam (≤ 20 px) so the mark stays crisp.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\VibeDoze\Assets\VibeDoze.ico'))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-RoundPen([System.Drawing.Brush]$brush, [float]$width) {
    $pen = New-Object System.Drawing.Pen $brush, $width
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    return $pen
}

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 100.0, $size / 100.0)

    # Squircle (continuous corners) with a vertical indigo gradient and a soft top sheen.
    $sq = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sq.AddBezier(50, 0, 10, 0, 0, 10, 0, 50)
    $sq.AddBezier(0, 50, 0, 90, 10, 100, 50, 100)
    $sq.AddBezier(50, 100, 90, 100, 100, 90, 100, 50)
    $sq.AddBezier(100, 50, 100, 10, 90, 0, 50, 0)
    $sq.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF 0, 0, 100, 100
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(125, 122, 255)), ([System.Drawing.Color]::FromArgb(75, 63, 217)), 90.0
    $g.FillPath($bg, $sq)
    $sheenRect = New-Object System.Drawing.RectangleF 0, 0, 100, 56
    $sheen = New-Object System.Drawing.Drawing2D.LinearGradientBrush $sheenRect,
        ([System.Drawing.Color]::FromArgb(56, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)), 90.0
    $g.SetClip($sq)
    $g.FillRectangle($sheen, 0, 0, 100, 55)
    $g.ResetClip()

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $cup = New-Object System.Drawing.Drawing2D.GraphicsPath

    if ($size -ge 40) {
        # Full mark.
        $cup.AddLine(23, 46, 63, 46); $cup.AddLine(63, 46, 63, 68)
        $cup.AddBezier(63, 68, 63, 74.6, 57.6, 80, 51, 80); $cup.AddLine(51, 80, 35, 80)
        $cup.AddBezier(35, 80, 28.4, 80, 23, 74.6, 23, 68); $cup.CloseFigure()
        $g.FillPath($white, $cup)
        # ">_" prompt in the background colour (reads as a cut-out).
        $prompt = New-RoundPen $bg 5
        $g.DrawLines($prompt, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 33, 56), (New-Object System.Drawing.PointF 40, 62), (New-Object System.Drawing.PointF 33, 68)))
        $g.DrawLine($prompt, 45, 68, 53, 68)
        $handle = New-Object System.Drawing.Drawing2D.GraphicsPath
        $handle.AddLine(63, 52, 68, 52); $handle.AddArc(61, 52, 14, 14, -90, 180); $handle.AddLine(68, 66, 63, 66)
        $g.DrawPath((New-RoundPen $white 6), $handle)
        $steam = New-Object System.Drawing.Drawing2D.GraphicsPath
        foreach ($x in 36, 50) {
            $steam.StartFigure()
            $steam.AddBezier($x, 39, ($x - 3.5), 35.5, ($x + 3.5), 32.5, $x, 29)
            $steam.AddBezier($x, 29, ($x - 3.5), 25.5, ($x + 3.5), 22.5, $x, 19)
        }
        $g.DrawPath((New-RoundPen $white 5), $steam)
    }
    elseif ($size -ge 24) {
        # Bolder cup, no prompt.
        $cup.AddLine(21, 44, 65, 44); $cup.AddLine(65, 44, 65, 67)
        $cup.AddBezier(65, 67, 65, 74, 59.5, 80, 52, 80); $cup.AddLine(52, 80, 34, 80)
        $cup.AddBezier(34, 80, 26.5, 80, 21, 74, 21, 67); $cup.CloseFigure()
        $g.FillPath($white, $cup)
        $handle = New-Object System.Drawing.Drawing2D.GraphicsPath
        $handle.AddLine(65, 51, 69, 51); $handle.AddArc(61, 51, 16, 16, -90, 180); $handle.AddLine(69, 67, 65, 67)
        $g.DrawPath((New-RoundPen $white 8), $handle)
        $steam = New-Object System.Drawing.Drawing2D.GraphicsPath
        foreach ($x in 35, 51) {
            $steam.StartFigure()
            $steam.AddBezier($x, 37, ($x - 4), 33, ($x + 4), 30, $x, 25.5)
        }
        $g.DrawPath((New-RoundPen $white 7), $steam)
    }
    else {
        # Tiny: just the cup and its handle.
        $cup.AddLine(18, 34, 66, 34); $cup.AddLine(66, 34, 66, 64)
        $cup.AddBezier(66, 64, 66, 73, 59, 80, 50, 80); $cup.AddLine(50, 80, 34, 80)
        $cup.AddBezier(34, 80, 25, 80, 18, 73, 18, 64); $cup.CloseFigure()
        $g.FillPath($white, $cup)
        $handle = New-Object System.Drawing.Drawing2D.GraphicsPath
        $handle.AddLine(66, 44, 72, 44); $handle.AddArc(62, 44, 20, 20, -90, 180); $handle.AddLine(72, 64, 66, 64)
        $g.DrawPath((New-RoundPen $white 10), $handle)
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
