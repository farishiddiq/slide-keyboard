# Builds artifacts/SlideKeyboard-<version>-win-x64.msi
#   powershell -ExecutionPolicy Bypass -File installer/build.ps1
# Needs the .NET 8 SDK and WiX 5 (dotnet tool install --global wix --version 5.0.2;
# wix extension add -g WixToolset.UI.wixext/5.0.2).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$installer = Join-Path $root 'installer'
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'

[xml]$proj = Get-Content (Join-Path $root 'src\SlideKeyboard.App\SlideKeyboard.App.csproj')
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

Write-Host "Publishing Slide Keyboard $version (self-contained, win-x64)..."
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish (Join-Path $root 'src\SlideKeyboard.App\SlideKeyboard.App.csproj') -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Installer artwork and license page (generated so they always match the app icon and LICENSE).
Add-Type -AssemblyName System.Drawing
# app.ico stores PNG frames (largest last); read the largest one directly.
$icoBytes = [IO.File]::ReadAllBytes((Join-Path $root 'src\SlideKeyboard.App\Assets\app.ico'))
$count = [BitConverter]::ToUInt16($icoBytes, 4)
$entry = 6 + 16 * ($count - 1)
$pngSize = [BitConverter]::ToInt32($icoBytes, $entry + 8)
$pngOffset = [BitConverter]::ToInt32($icoBytes, $entry + 12)
$iconImage = [System.Drawing.Image]::FromStream((New-Object IO.MemoryStream (, $icoBytes[$pngOffset..($pngOffset + $pngSize - 1)])))

function New-Art([string]$path, [int]$w, [int]$h, [bool]$banner) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::White)
    if ($banner) {
        $g.DrawImage($iconImage, $w - 54, 7, 44, 44)
    } else {
        $panel = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new(164, $h)), ([System.Drawing.Color]::FromArgb(255, 47, 107, 235)), ([System.Drawing.Color]::FromArgb(255, 108, 84, 240))
        $g.FillRectangle($panel, 0, 0, 164, $h)
        $g.DrawImage($iconImage, 34, 40, 96, 96)
        $font = New-Object System.Drawing.Font 'Segoe UI Semibold', 13
        $g.DrawString("Slide`nKeyboard", $font, [System.Drawing.Brushes]::White, 30, 150)
    }
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bmp.Dispose()
}
New-Art (Join-Path $installer 'banner.bmp') 493 58 $true
New-Art (Join-Path $installer 'dialog.bmp') 493 312 $false

$license = (Get-Content (Join-Path $root 'LICENSE') -Raw) -replace '\\', '\\' -replace '\{', '\{' -replace '\}', '\}' -replace "`r?`n", "\par`r`n"
Set-Content -Path (Join-Path $installer 'License.rtf') -Encoding Ascii -Value "{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\fs18 $license}"

$msi = Join-Path $artifacts "SlideKeyboard-$version-win-x64.msi"
Write-Host "Building $msi..."
Push-Location $installer
try {
    wix build Package.wxs -arch x64 -ext WixToolset.UI.wixext -d "Version=$version" -d "PublishDir=$publish" -o $msi
    if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }
} finally { Pop-Location }

$size = (Get-Item $msi).Length / 1MB
Write-Host ("Done: {0} ({1:N1} MB)" -f $msi, $size)
