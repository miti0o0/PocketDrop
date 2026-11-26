# Generates the app icon (PNG + multi-size ICO) for the desktop app and the web sender.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icons.ps1
param([string]$Root = (Split-Path -Parent $PSScriptRoot))

Add-Type -AssemblyName System.Drawing

# $FullBleed: square, opaque, no rounded corners — for iOS home screen and maskable PWA icons,
# where the system applies its own mask (transparent corners would turn black on iOS).
function New-IconBitmap([int]$size, [switch]$FullBleed) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 256.0
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($FullBleed) {
        $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
        $path.AddRectangle($rect)
    } else {
        $r = 56 * $s
        $rect = New-Object System.Drawing.RectangleF (8 * $s), (8 * $s), (240 * $s), (240 * $s)
        $d = $r * 2
        $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
        $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
        $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
        $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
        $path.CloseFigure()
    }

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(255, 64, 176, 255)),
        ([System.Drawing.Color]::FromArgb(255, 10, 92, 230)), 60
    $g.FillPath($brush, $path)

    $white = [System.Drawing.Color]::White
    $pen = New-Object System.Drawing.Pen $white, (18 * $s)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'

    # Tray (U shape)
    $tray = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tray.AddLine((64 * $s), (150 * $s), (64 * $s), (186 * $s))
    $tray.AddLine((64 * $s), (186 * $s), (192 * $s), (186 * $s))
    $tray.AddLine((192 * $s), (186 * $s), (192 * $s), (150 * $s))
    $g.DrawPath($pen, $tray)

    # Down arrow
    $g.DrawLine($pen, (128 * $s), (58 * $s), (128 * $s), (146 * $s))
    $g.DrawLine($pen, (92 * $s), (112 * $s), (128 * $s), (148 * $s))
    $g.DrawLine($pen, (164 * $s), (112 * $s), (128 * $s), (148 * $s))

    $g.Dispose()
    return $bmp
}

function Save-Png($bmp, $file) {
    $dir = Split-Path -Parent $file
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
}

function Save-Ico([int[]]$sizes, $file) {
    $images = @()
    foreach ($sz in $sizes) {
        $b = New-IconBitmap $sz
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += , @($sz, $ms.ToArray())
        $b.Dispose()
    }
    $fs = [System.IO.File]::Create($file)
    $w = New-Object System.IO.BinaryWriter $fs
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        $sz = $img[0]; $data = $img[1]
        $dim = if ($sz -ge 256) { 0 } else { $sz }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32)
        $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
        $offset += $data.Length
    }
    foreach ($img in $images) { $w.Write([byte[]]$img[1]) }
    $w.Close()
}

$assets = Join-Path $Root 'desktop\PocketDrop.Desktop\Assets'
$web = Join-Path $Root 'desktop\PocketDrop.Desktop\Web'
New-Item -ItemType Directory -Force $assets, $web | Out-Null

$big = New-IconBitmap 256; Save-Png $big (Join-Path $assets 'app.png'); $big.Dispose()
$webIcon = New-IconBitmap 512; Save-Png $webIcon (Join-Path $web 'icon.png'); $webIcon.Dispose()
# iPhone home screen (180x180 is what current iPhones use) and Android/Chrome install icons.
foreach ($w in @(@('apple-touch-icon.png', 180), @('icon-192.png', 192), @('icon-512.png', 512))) {
    $b = New-IconBitmap $w[1] -FullBleed
    Save-Png $b (Join-Path $web $w[0])
    $b.Dispose()
}
Save-Ico @(16, 20, 24, 32, 40, 48, 64, 128, 256) (Join-Path $assets 'app.ico')
Copy-Item (Join-Path $assets 'app.ico') (Join-Path $Root 'desktop\installer\app.ico') -ErrorAction SilentlyContinue

# Android launcher PNGs for API 24-25 (API 26+ uses the adaptive icon).
$res = Join-Path $Root 'android\app\src\main\res'
foreach ($d in @(@('mdpi', 48), @('hdpi', 72), @('xhdpi', 96), @('xxhdpi', 144), @('xxxhdpi', 192))) {
    $b = New-IconBitmap $d[1]
    Save-Png $b (Join-Path $res "mipmap-$($d[0])\ic_launcher.png")
    $b.Dispose()
}
Write-Host "Icons written to $assets and $web"
