# Regenerates src/SlideKeyboard.App/Assets/app.ico (multi-size, PNG-compressed frames).
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\SlideKeyboard.App\Assets\app.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$sizes = 16, 24, 32, 48, 64, 128, 256

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square with a blue gradient
    $r = [single]($s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = [single]($s - 1)
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($s, $s)), ([System.Drawing.Color]::FromArgb(255, 61, 125, 255)), ([System.Drawing.Color]::FromArgb(255, 108, 84, 240))
    $g.FillPath($brush, $path)

    # Three rows of key dots
    $dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 255, 255, 255))
    $d = $s * 0.085
    foreach ($row in 0..2) {
        $count = 5 - ($row -eq 2)
        $y = $s * (0.30 + $row * 0.17)
        $x0 = $s * (0.20 + ($row * 0.04))
        for ($i = 0; $i -lt $count; $i++) {
            $g.FillEllipse($dot, [single]($x0 + $i * $s * 0.135), [single]$y, [single]$d, [single]$d)
        }
    }

    # Swipe stroke
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single][Math]::Max(1.5, $s * 0.075))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $pts = @(
        [System.Drawing.PointF]::new($s * 0.22, $s * 0.66),
        [System.Drawing.PointF]::new($s * 0.40, $s * 0.36),
        [System.Drawing.PointF]::new($s * 0.58, $s * 0.62),
        [System.Drawing.PointF]::new($s * 0.78, $s * 0.32)
    )
    $g.DrawCurve($pen, $pts, 0.5)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$frames = $sizes | ForEach-Object { , (New-Frame $_) }
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$frames[$i].Length); $bw.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Close()
Write-Host "Wrote $out"
