<#
.SYNOPSIS
    Generates src/OftScrubber/Assets/app.ico: a multi-size Windows icon drawn with GDI+.

.DESCRIPTION
    Draws each frame on a notional 256x256 grid and scales down, then packs the frames as
    PNG data inside an ICO container (Vista and later accept PNG frames directly).

    Requires PowerShell 7 for System.Drawing.Common.

    Careful with variable names in here: PowerShell is case-insensitive, so a local named
    $out would silently collide with the $OutPath parameter and be coerced to a string.

.EXAMPLE
    ./tools/make-icon.ps1 -OutPath ./src/OftScrubber/Assets/app.ico
#>
param([Parameter(Mandatory = $true)][string]$OutPath)

Add-Type -AssemblyName System.Drawing.Common

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Everything is designed on a 256 grid and scaled down.
    $f = $size / 256.0

    $blue  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 15, 108, 189))
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)

    $tile = New-RoundedPath (10 * $f) (10 * $f) (236 * $f) (236 * $f) (54 * $f)
    $g.FillPath($blue, $tile)
    $tile.Dispose()

    # Envelope body.
    $body = New-RoundedPath (48 * $f) (70 * $f) (160 * $f) (116 * $f) (16 * $f)
    $g.FillPath($white, $body)
    $body.Dispose()

    # Envelope flap drawn as a blue chevron.
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 15, 108, 189)), (12 * $f)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (66 * $f), (90 * $f)),
        (New-Object System.Drawing.PointF (128 * $f), (134 * $f)),
        (New-Object System.Drawing.PointF (190 * $f), (90 * $f))))
    $pen.Dispose()

    # Template field placeholder, dropped at small sizes where it would only turn to mud.
    if ($size -ge 32) {
        $field = New-RoundedPath (92 * $f) (152 * $f) (72 * $f) (18 * $f) (6 * $f)
        $g.FillPath($blue, $field)
        $field.Dispose()
    }

    $blue.Dispose()
    $white.Dispose()
    $g.Dispose()

    return $bmp
}

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$frames = @()

foreach ($size in $sizes) {
    $bmp = New-Frame $size
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
    $bmp.Dispose()
}

$icoStream = New-Object System.IO.MemoryStream
$writer = [System.IO.BinaryWriter]::new($icoStream)

$writer.Write([UInt16]0)                 # reserved
$writer.Write([UInt16]1)                 # type: icon
$writer.Write([UInt16]$frames.Count)

$offset = 6 + (16 * $frames.Count)

foreach ($frame in $frames) {
    $dim = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $writer.Write([Byte]$dim)            # width
    $writer.Write([Byte]$dim)            # height
    $writer.Write([Byte]0)               # palette count
    $writer.Write([Byte]0)               # reserved
    $writer.Write([UInt16]1)             # colour planes
    $writer.Write([UInt16]32)            # bits per pixel
    $writer.Write([UInt32]$frame.Bytes.Length)
    $writer.Write([UInt32]$offset)
    $offset += $frame.Bytes.Length
}

foreach ($frame in $frames) {
    $writer.Write($frame.Bytes)
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($OutPath, $icoStream.ToArray())
$writer.Dispose()
$icoStream.Dispose()

Write-Output "Wrote $OutPath ($((Get-Item $OutPath).Length) bytes, $($frames.Count) frames)"
