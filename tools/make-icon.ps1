# Generates Discern.ico (multi-resolution) with System.Drawing.
#
# Concept: an optical disc whose data area is drawn as a camera iris.
#   optical disc  <-> optical eye, iris blades around the hub
# The glyph is a donut (outer disc minus centre hole), so it stays readable
# on both dark and light backgrounds and keeps a transparent background.
#
# Detail is drawn per size: blades/glints only appear at 48px and up, because
# below that they turn into mud.
#
# Usage:
#   .\tools\make-icon.ps1                 -> Discern\Discern.ico
#   .\tools\make-icon.ps1 -Preview 256    -> also writes a PNG preview

param(
    [string]$Out = (Join-Path $PSScriptRoot '..\Discern\Discern.ico'),
    [int]$Preview = 0,
    [string]$PreviewPath = (Join-Path $PSScriptRoot '..\docs\icon-256.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Accent palette taken from the app (App.xaml): Accent #4FB3FF
$cBright = [Drawing.Color]::FromArgb(255, 0x7C, 0xD6, 0xFF)
$cMid    = [Drawing.Color]::FromArgb(255, 0x3B, 0x96, 0xE6)
$cDeep   = [Drawing.Color]::FromArgb(255, 0x12, 0x45, 0x88)

function New-DiscBitmap {
    param([int]$S)

    $bmp = New-Object Drawing.Bitmap($S, $S, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([Drawing.Color]::Transparent)

    $cx = $S / 2.0
    $cy = $S / 2.0

    # --- geometry (fractions of the canvas) ---
    $rOuter = $S * 0.462      # disc rim
    $rHole  = $S * 0.138      # hub hole  -> this is the "pupil"
    $rIris  = $S * 0.300      # inner data edge / iris ring
    $detail = $S -ge 48

    # --- disc body: donut path so the hole is real transparency ---
    $path = New-Object Drawing.Drawing2D.GraphicsPath -ArgumentList ([Drawing.Drawing2D.FillMode]::Alternate)
    $path.AddEllipse([single]($cx - $rOuter), [single]($cy - $rOuter), [single]($rOuter * 2), [single]($rOuter * 2))
    $path.AddEllipse([single]($cx - $rHole),  [single]($cy - $rHole),  [single]($rHole * 2),  [single]($rHole * 2))

    $gradRect = New-Object Drawing.RectangleF([single]($cx - $rOuter), [single]($cy - $rOuter), [single]($rOuter * 2), [single]($rOuter * 2))
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush($gradRect, $cBright, $cDeep, [single]50)
    $blend = New-Object Drawing.Drawing2D.ColorBlend(3)
    $blend.Colors    = [Drawing.Color[]]@($cBright, $cMid, $cDeep)
    $blend.Positions = [single[]]@(0.0, 0.45, 1.0)
    $brush.InterpolationColors = $blend
    $g.FillPath($brush, $path)

    # --- edge: a dark rim so the shape survives on a white Explorer background ---
    $rimPen = New-Object Drawing.Pen([Drawing.Color]::FromArgb(60, 0x08, 0x2A, 0x52), [single]([Math]::Max(1.0, $S * 0.014)))
    $g.DrawEllipse($rimPen, [single]($cx - $rOuter), [single]($cy - $rOuter), [single]($rOuter * 2), [single]($rOuter * 2))

    # --- iris blades (large sizes only) ---
    if ($detail) {
        $bladePen = New-Object Drawing.Pen([Drawing.Color]::FromArgb(70, 255, 255, 255), [single]([Math]::Max(1.0, $S * 0.014)))
        $count = 12
        for ($i = 0; $i -lt $count; $i++) {
            $a0 = ($i * 360.0 / $count)
            $rad = $a0 * [Math]::PI / 180.0
            $x1 = $cx + [Math]::Cos($rad) * ($rHole + $S * 0.022)
            $y1 = $cy + [Math]::Sin($rad) * ($rHole + $S * 0.022)
            $x2 = $cx + [Math]::Cos($rad) * ($rIris - $S * 0.020)
            $y2 = $cy + [Math]::Sin($rad) * ($rIris - $S * 0.020)
            $g.DrawLine($bladePen, [single]$x1, [single]$y1, [single]$x2, [single]$y2)
        }
    }

    # --- iris ring: the disc's inner data edge (skipped at 16-24, where it muddies) ---
    $irisPen = $null
    if ($S -ge 32) {
        $irisPen = New-Object Drawing.Pen([Drawing.Color]::FromArgb(120, 255, 255, 255), [single]([Math]::Max(1.0, $S * 0.018)))
        $g.DrawEllipse($irisPen, [single]($cx - $rIris), [single]($cy - $rIris), [single]($rIris * 2), [single]($rIris * 2))
    }

    # --- specular arc: the sheen every disc has ---
    $rSpec = ($rOuter + $rIris) / 2.0 + $S * 0.012
    $specPen = New-Object Drawing.Pen([Drawing.Color]::FromArgb(215, 255, 255, 255), [single]($S * 0.052))
    $specPen.StartCap = [Drawing.Drawing2D.LineCap]::Round
    $specPen.EndCap   = [Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($specPen, [single]($cx - $rSpec), [single]($cy - $rSpec), [single]($rSpec * 2), [single]($rSpec * 2), [single]203, [single]62)

    if ($detail) {
        $glintPen = New-Object Drawing.Pen([Drawing.Color]::FromArgb(120, 255, 255, 255), [single]($S * 0.022))
        $glintPen.StartCap = [Drawing.Drawing2D.LineCap]::Round
        $glintPen.EndCap   = [Drawing.Drawing2D.LineCap]::Round
        $g.DrawArc($glintPen, [single]($cx - $rSpec), [single]($cy - $rSpec), [single]($rSpec * 2), [single]($rSpec * 2), [single]25, [single]40)
    }

    $specPen.Dispose(); $rimPen.Dispose(); if ($irisPen) { $irisPen.Dispose() }; $brush.Dispose(); $path.Dispose()
    if ($detail) { $bladePen.Dispose(); $glintPen.Dispose() }
    $g.Dispose()
    return $bmp
}

# ----------------------------------------------------------------- ICO writing

function Get-PngBytes {
    param([Drawing.Bitmap]$Bmp)
    $ms = New-Object IO.MemoryStream
    $Bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return $bytes
}

function Get-DibBytes {
    param([Drawing.Bitmap]$Bmp)

    $S = $Bmp.Width
    $rect = New-Object Drawing.Rectangle 0, 0, $S, $S
    $locked = $Bmp.LockBits($rect, [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $locked.Stride
    $pixels = New-Object byte[] ($stride * $S)
    [Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
    $Bmp.UnlockBits($locked)

    # AND mask: 1 bit per pixel, rows padded to 4 bytes, transparent -> 1
    $maskStride = [int]([Math]::Ceiling($S / 32.0) * 4)
    $mask = New-Object byte[] ($maskStride * $S)

    $out = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter($out)

    # BITMAPINFOHEADER; biHeight is doubled (XOR + AND)
    $w.Write([int]40); $w.Write([int]$S); $w.Write([int]($S * 2))
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]0)
    $w.Write([int]0); $w.Write([int]0)
    $w.Write([int]0); $w.Write([int]0)

    # XOR: bottom-up rows of BGRA (already the in-memory order)
    for ($y = $S - 1; $y -ge 0; $y--) {
        $rowStart = $y * $stride
        for ($x = 0; $x -lt $S; $x++) {
            $i = $rowStart + $x * 4
            $a = $pixels[$i + 3]
            $w.Write($pixels[$i])       # B
            $w.Write($pixels[$i + 1])   # G
            $w.Write($pixels[$i + 2])   # R
            $w.Write($a)                # A
            if ($a -eq 0) {
                $mi = ($S - 1 - $y) * $maskStride + [int]([Math]::Floor($x / 8))
                $mask[$mi] = $mask[$mi] -bor (0x80 -shr ($x % 8))
            }
        }
    }

    $w.Write($mask)
    $w.Flush()
    $bytes = $out.ToArray()
    $w.Dispose(); $out.Dispose()
    return $bytes
}

# ----------------------------------------------------------------- main

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()

foreach ($s in $sizes) {
    $bmp = New-DiscBitmap -S $s
    # PNG for the big frames (smaller file), DIB for the rest (max compatibility)
    # All frames as 32bpp DIB: System.Drawing.Icon (tray icon, ExtractAssociatedIcon)
    # cannot parse PNG-compressed ICO frames, so PNG is not an option here.
    $data = [byte[]](Get-DibBytes -Bmp $bmp)
    $frames += [pscustomobject]@{ Size = $s; Data = $data }
    if ($Preview -eq $s) {
        New-Item -ItemType Directory -Force -Path (Split-Path $PreviewPath) | Out-Null
        $bmp.Save($PreviewPath, [Drawing.Imaging.ImageFormat]::Png)
        Write-Host "preview: $PreviewPath ($s px)"
    }
    $bmp.Dispose()
}

$headerSize = 6 + ($frames.Count * 16)
$offset = $headerSize

$outMs = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter($outMs)
$bw.Write([int16]0)                 # reserved
$bw.Write([int16]1)                 # type = icon
$bw.Write([int16]$frames.Count)

foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $bw.Write([byte]$dim)           # width  (0 means 256)
    $bw.Write([byte]$dim)           # height
    $bw.Write([byte]0)              # palette count
    $bw.Write([byte]0)              # reserved
    $bw.Write([int16]1)             # planes
    $bw.Write([int16]32)            # bit count
    $bw.Write([int]$f.Data.Length)
    $bw.Write([int]$offset)
    $offset += $f.Data.Length
}
foreach ($f in $frames) { $bw.Write([byte[]]$f.Data) }
$bw.Flush()

$outPath = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force -Path (Split-Path $outPath) | Out-Null
[IO.File]::WriteAllBytes($outPath, $outMs.ToArray())
$bw.Dispose(); $outMs.Dispose()

Write-Host "wrote: $outPath  ($((Get-Item $outPath).Length) bytes, $($frames.Count) frames)"
