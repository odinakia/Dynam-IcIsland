Add-Type -AssemblyName System.Drawing

$dir = Split-Path -Parent $PSCommandPath
$assets = Join-Path (Split-Path -Parent $dir) 'Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$out = Join-Path $assets 'app.ico'

$size = 256
$bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

# soft glow
$glow = New-Object System.Drawing.Drawing2D.GraphicsPath
$glow.AddEllipse(24, 24, 208, 208)
$brush = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
$brush.CenterColor = [System.Drawing.Color]::FromArgb(70, 127, 224, 255)
$brush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 127, 224, 255))
$g.FillPath($brush, $glow)

# island pill
$pill = New-Object System.Drawing.Drawing2D.GraphicsPath
$r = 66
$x = 20; $y = 88; $w = 216; $h = 80
$pill.AddArc($x, $y, $r, $r, 180, 90)
$pill.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
$pill.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90)
$pill.AddArc($x, $y + $h - $r, $r, $r, 90, 90)
$pill.CloseFigure()
$g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 8, 9, 12))), $pill)

# lens
$lensBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 127, 224, 255))
$g.FillEllipse($lensBrush, 44, 116, 24, 24)
$g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))), 52, 124, 8, 8)

# waveform
$waveBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(235, 255, 255, 255))
$bars = @(
    @(96, 120, 8, 16),
    @(112, 112, 8, 32),
    @(128, 106, 8, 44),
    @(144, 114, 8, 28),
    @(160, 122, 8, 12)
)
foreach ($b in $bars) { $g.FillRectangle($waveBrush, $b[0], $b[1], $b[2], $b[3]) }

$g.Dispose()

$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()
$bmp.Dispose()

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0)      # reserved
$bw.Write([UInt16]1)      # icon
$bw.Write([UInt16]1)      # count
$bw.Write([Byte]0)        # width 256
$bw.Write([Byte]0)        # height 256
$bw.Write([Byte]0)        # palette
$bw.Write([Byte]0)        # reserved
$bw.Write([UInt16]1)      # planes
$bw.Write([UInt16]32)     # bpp
$bw.Write([UInt32]$png.Length)
$bw.Write([UInt32]22)     # offset
$bw.Write($png)
$bw.Dispose()
Write-Host "icon -> $out ($($png.Length) bytes)"
