# Generates the MSIX tile asset set for the Windows Store package from the app's real icon.
#
# The source of truth is the same transparent glyph the other heads use,
# src/RioEditor.App/Assets/icon.png, so Windows cannot drift from Android, iOS and macOS.
# Change the artwork there and re-run this; every tile size regenerates together.
#
#   powershell.exe -ExecutionPolicy Bypass -File packaging\windows\generate-icons.ps1
#
# The tiles are written with a TRANSPARENT background on purpose. Windows fills a tile with
# the manifest's VisualElements/@BackgroundColor and composites the logo over it, and it draws
# its own "plate" behind the plated targetsize assets. Baking a background into the PNG would
# fight both. BackgroundColor is #FFFFFF to match rio_icon_background in the Android adaptive
# icon and the white ground of the macOS iconset.
#
# The .exe icon is NOT generated here: src/RioEditor.Desktop/RioEditor.ico is checked in and
# referenced by the csproj. This script owns Store tiles only.

[CmdletBinding()]
param(
    [string] $Source,
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$packagingRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot      = (Resolve-Path (Join-Path $packagingRoot '..\..')).Path

if (-not $Source)          { $Source = Join-Path $repoRoot 'src\RioEditor.App\Assets\icon.png' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $packagingRoot 'Images' }

if (-not (Test-Path $Source)) {
    throw "Source icon not found at $Source. Pass -Source to point at the app icon PNG."
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$glyph = [System.Drawing.Image]::FromFile((Resolve-Path $Source).Path)
Write-Host "source: $Source ($($glyph.Width)x$($glyph.Height))"
Write-Host ''

# Fraction of the tile's SHORT edge that the glyph spans. Small assets need to fill more of
# their canvas to stay legible; large tiles need more breathing room, the same way the Android
# adaptive icon insets its foreground by 18%.
function Save-Tile {
    param(
        [string] $Name,
        [int]    $Width,
        [int]    $Height,
        [double] $Fill,
        [switch] $OpaqueWhite
    )

    $bmp = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality

    if ($OpaqueWhite) {
        $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $g.FillRectangle($white, 0, 0, $Width, $Height)
        $white.Dispose()
    } else {
        $g.Clear([System.Drawing.Color]::Transparent)
    }

    # Square, centred, aspect preserved — the source is square, but never assume it.
    $short  = [Math]::Min($Width, $Height)
    $target = [double]$short * $Fill
    $scale  = [Math]::Min($target / $glyph.Width, $target / $glyph.Height)
    $w      = $glyph.Width  * $scale
    $h      = $glyph.Height * $scale
    $rect   = New-Object System.Drawing.RectangleF(
        [float](($Width - $w) / 2.0), [float](($Height - $h) / 2.0), [float]$w, [float]$h)

    $g.DrawImage($glyph, $rect)
    $g.Dispose()

    $bmp.Save((Join-Path $OutputDirectory $Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ("  {0,-52} {1}x{2}" -f $Name, $Width, $Height)
}

# Clear out the previous set so a renamed asset cannot linger and get packed.
Get-ChildItem $OutputDirectory -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $OutputDirectory -Filter *.ico -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host 'Tiles:'
Save-Tile 'Square44x44Logo.png'              44   44   0.72
Save-Tile 'Square44x44Logo.scale-200.png'    88   88   0.72
Save-Tile 'Square71x71Logo.png'              71   71   0.70
Save-Tile 'Square71x71Logo.scale-200.png'    142  142  0.70
Save-Tile 'Square150x150Logo.png'            150  150  0.66
Save-Tile 'Square150x150Logo.scale-200.png'  300  300  0.66
Save-Tile 'Square310x310Logo.png'            310  310  0.62
Save-Tile 'Square310x310Logo.scale-200.png'  620  620  0.62
Save-Tile 'Wide310x150Logo.png'              310  150  0.62
Save-Tile 'Wide310x150Logo.scale-200.png'    620  300  0.62
Save-Tile 'StoreLogo.png'                    50   50   0.76
Save-Tile 'StoreLogo.scale-200.png'          100  100  0.76
Save-Tile 'SplashScreen.png'                 620  300  0.50
Save-Tile 'SplashScreen.scale-200.png'       1240 600  0.50

# Target-size assets: the app list, taskbar, Alt+Tab and search results. Both forms stay
# transparent — the plated one gets the manifest background drawn behind it by Windows, the
# unplated one sits directly on the taskbar.
Write-Host 'Target sizes:'
foreach ($t in 16, 24, 32, 48, 256) {
    $fill = if ($t -le 32) { 0.88 } else { 0.80 }
    Save-Tile "Square44x44Logo.targetsize-$t.png"                  $t $t $fill
    Save-Tile "Square44x44Logo.targetsize-${t}_altform-unplated.png" $t $t $fill
}

# Partner Center listing artwork. Not part of the package, and flattened onto white because a
# listing image is shown as-is rather than composited over a background colour.
Write-Host 'Store listing:'
Save-Tile 'StoreListing-300x300.png' 300 300 0.66 -OpaqueWhite

$glyph.Dispose()
Write-Host ''
Write-Host "Done. $((Get-ChildItem $OutputDirectory -Filter *.png).Count) files in $OutputDirectory"
