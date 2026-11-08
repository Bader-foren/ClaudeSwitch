# Draws the ClaudeSwitch icon (two "switch" arrows on a rounded square) and writes
# assets\icon.ico (all Windows sizes) and assets\icon.png (256 px, for the README).
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-icon.ps1

Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot "..\assets"
New-Item -ItemType Directory -Force $assets | Out-Null

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 64; $x = 8; $y = 8; $w = 240; $h = 240
    $path.AddArc($x, $y, $r, $r, 180, 90)
    $path.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
    $path.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90)
    $path.AddArc($x, $y + $h - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 256, 256),
        [System.Drawing.Color]::FromArgb(255, 91, 108, 240), [System.Drawing.Color]::FromArgb(255, 139, 92, 246))
    $g.FillPath($brush, $path)

    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 22
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLine($pen, 70, 98, 182, 98)
    $g.DrawLines($pen, [System.Drawing.Point[]]@((New-Object System.Drawing.Point 148, 64), (New-Object System.Drawing.Point 182, 98), (New-Object System.Drawing.Point 148, 132)))
    $g.DrawLine($pen, 186, 158, 74, 158)
    $g.DrawLines($pen, [System.Drawing.Point[]]@((New-Object System.Drawing.Point 108, 124), (New-Object System.Drawing.Point 74, 158), (New-Object System.Drawing.Point 108, 192)))

    $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

$images = @()
foreach ($sz in 16, 20, 24, 32, 40, 48, 64, 128, 256) {
    $bmp = New-IconBitmap $sz
    if ($sz -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
        $bmp.Save((Join-Path $assets "icon.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    else {
        # Classic 32-bit DIB entry: header, bottom-up BGRA pixels, empty AND mask.
        $rect = New-Object System.Drawing.Rectangle 0, 0, $sz, $sz
        $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $stride = $data.Stride
        $raw = New-Object byte[] ($stride * $sz)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
        $bmp.UnlockBits($data)
        $maskRow = [int]([math]::Ceiling($sz / 32.0) * 4)
        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([int]40); $bw.Write([int]$sz); $bw.Write([int]($sz * 2))
        $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
        $bw.Write([int]($sz * $sz * 4 + $maskRow * $sz))
        $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        for ($row = $sz - 1; $row -ge 0; $row--) { $bw.Write($raw, [int]($row * $stride), [int]($sz * 4)) }
        $bw.Write((New-Object byte[] ($maskRow * $sz)))
        $bw.Flush()
        $bytes = $ms.ToArray()
    }
    $bmp.Dispose()
    $images += , @($sz, $bytes)
}

$fs = [System.IO.File]::Create((Join-Path $assets "icon.ico"))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img[0] -ge 256) { 0 } else { $img[0] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]$img[1].Length); $bw.Write([int]$offset)
    $offset += $img[1].Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img[1]) }
$bw.Close()
Write-Host "Wrote assets\icon.ico and assets\icon.png"
