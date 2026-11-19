# Draws moddb\logo.png, the 480x320 card picture of the mod page, and moddb\cover-large.png (the same at 1440x960):
# a screenshot of the game cropped to the boat with the sailor locust at its helm, with the mod's name over it.
# The crop leaves out the HUD, the chat and the player's name. Keep this file ASCII.
param(
    [string]$Source = "$env:USERPROFILE\OneDrive\Imagens\Vintagestory\2026-11-17_19-24-10.png",
    # the part of the screenshot kept (3:2)
    [int]$X = 1110, [int]$Y = 300, [int]$W = 810, [int]$H = 540
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$out = Join-Path $root 'moddb'
New-Item -ItemType Directory -Force $out | Out-Null
Add-Type -AssemblyName System.Drawing
function Rgb($r, $gg, $b, $a = 255) { [System.Drawing.Color]::FromArgb($a, $r, $gg, $b) }

$shot = [System.Drawing.Image]::FromFile($Source)
function Draw([int]$width, [int]$height, [string]$file) {
    $s = $width / 480.0
    $bmp = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    # the game's blocks stay crisp when made bigger, smooth when made smaller
    $g.InterpolationMode = if ($width -gt $W) { 'NearestNeighbor' } else { 'HighQualityBicubic' }
    $g.DrawImage($shot, (New-Object System.Drawing.Rectangle 0, 0, $width, $height), (New-Object System.Drawing.Rectangle $X, $Y, $W, $H), [System.Drawing.GraphicsUnit]::Pixel)

    # a dark band at the foot for the words, fading upwards
    $bandTop = [int]($height * 0.56)
    $rect = New-Object System.Drawing.Rectangle 0, $bandTop, $width, ($height - $bandTop)
    $fade = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (Rgb 8 20 24 0), (Rgb 8 20 24 225), 90
    $g.FillRectangle($fade, $rect)

    $title = New-Object System.Drawing.Font 'Georgia', ([single](38 * $s)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $sub = New-Object System.Drawing.Font 'Georgia', ([single](15.5 * $s)), ([System.Drawing.FontStyle]::Italic), ([System.Drawing.GraphicsUnit]::Pixel)
    $shadow = New-Object System.Drawing.SolidBrush (Rgb 0 0 0 170)
    $cream = New-Object System.Drawing.SolidBrush (Rgb 244 236 214)
    $pale = New-Object System.Drawing.SolidBrush (Rgb 214 226 222)
    $tx = [single](20 * $s); $ty = [single]($height - 92 * $s)
    $g.DrawString('Sailor Locust', $title, $shadow, ($tx + 2 * $s), ($ty + 2 * $s))
    $g.DrawString('Sailor Locust', $title, $cream, $tx, $ty)
    $g.DrawString('Boats that sail themselves, dock to dock', $sub, $shadow, ($tx + 2 * $s + 1), ($ty + 49 * $s + 1))
    $g.DrawString('Boats that sail themselves, dock to dock', $sub, $pale, ($tx + 2 * $s), ($ty + 49 * $s))

    # a thin frame, as on a chart
    $edge = New-Object System.Drawing.Pen (Rgb 244 236 214 150), ([single](1.5 * $s))
    $g.DrawRectangle($edge, [single](5 * $s), [single](5 * $s), [single]($width - 10 * $s), [single]($height - 10 * $s))
    $g.Dispose()
    $bmp.Save((Join-Path $out $file), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
Draw 480 320 'logo.png'
Draw 1440 960 'cover-large.png'
$shot.Dispose()
Get-ChildItem $out -Filter *.png | ForEach-Object { "$($_.Name) $($_.Length) bytes" }
