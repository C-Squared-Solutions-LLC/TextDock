# Draws the TextDock chat-bubble icon and writes Assets\app.ico.
# Frames up to 48px are stored as 32-bit DIBs (every Win32 icon API reads those);
# 64px and up are PNG-compressed to keep the file small.
param([string]$OutFile = (Join-Path $PSScriptRoot '..\Assets\app.ico'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-BubbleBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $size / 256.0
    $g.ScaleTransform($k, $k)

    # Bubble body (rounded rect) plus a tail at the bottom-right. The tail starts inside
    # the body, so filling the two shapes separately leaves no seam.
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $x = 18; $y = 26; $w = 220; $h = 168; $r = 62
    $path.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $tail = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF(202, 170)),
        (New-Object System.Drawing.PointF(220, 238)),
        (New-Object System.Drawing.PointF(130, 186))
    )
    $rect = New-Object System.Drawing.RectangleF(0, 20, 256, 222)
    $top = [System.Drawing.Color]::FromArgb(255, 0x2C, 0xC4, 0xB0)
    $bottom = [System.Drawing.Color]::FromArgb(255, 0x0A, 0x6E, 0x66)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $top, $bottom, [single]90)
    $g.FillPolygon($brush, $tail)
    $g.FillPath($brush, $path)

    # Three dots. Slightly larger at tiny sizes so they survive downsampling.
    $dot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $rad = if ($size -le 24) { 21 } else { 18 }
    $gap = if ($size -le 24) { 50 } else { 47 }
    foreach ($cx in (128 - $gap), 128, (128 + $gap)) {
        $g.FillEllipse($dot, $cx - $rad, 110 - $rad, 2 * $rad, 2 * $rad)
    }

    $dot.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

function ConvertTo-DibBytes([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $pixels = New-Object byte[] ($stride * $s)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $maskRow = [int]([Math]::Floor(($s + 31) / 32) * 4)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER; height is doubled because the AND mask follows the color data.
    $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]($s * $s * 4 + $maskRow * $s))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($row = $s - 1; $row -ge 0; $row--) { $bw.Write($pixels, $row * $stride, $s * 4) }
    $bw.Write((New-Object byte[] ($maskRow * $s)))
    $bw.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) {
    $bmp = New-BubbleBitmap $s
    if ($s -le 48) {
        $frames.Add((ConvertTo-DibBytes $bmp))
    } else {
        $png = New-Object System.IO.MemoryStream
        $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames.Add($png.ToArray())
    }
    $bmp.Dispose()
}

$OutFile = [System.IO.Path]::GetFullPath($OutFile)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($OutFile)) | Out-Null
$fs = [System.IO.File]::Create($OutFile)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$frames[$i].Length); $bw.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Close()
Write-Host "wrote $OutFile ($((Get-Item $OutFile).Length) bytes)"
