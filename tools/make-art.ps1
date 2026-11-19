# Writes the mod's own art: the block shapes of the dock and the buoy (plain boxes, textures of the game by reference)
# and the texture of the sailor hat. Run once; the results are kept in assets. Keep this file ASCII.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$shapes = Join-Path $root 'assets\sailorlocust\shapes\block'
$tex = Join-Path $root 'assets\sailorlocust\textures\entity'
New-Item -ItemType Directory -Force $shapes, $tex | Out-Null
$inv = [Globalization.CultureInfo]::InvariantCulture

function N([double]$v) { $v.ToString('0.###', $inv) }

function Box([string]$name, [double[]]$from, [double[]]$to, [string]$texture) {
    $w = [Math]::Min(16, $to[0] - $from[0]); $h = [Math]::Min(16, $to[1] - $from[1]); $d = [Math]::Min(16, $to[2] - $from[2])
    $side = { param($a, $b) "{ `"texture`": `"#$texture`", `"uv`": [0, 0, $(N $a), $(N $b)] }" }
    @"
		{
			"name": "$name",
			"from": [$(N $from[0]), $(N $from[1]), $(N $from[2])],
			"to": [$(N $to[0]), $(N $to[1]), $(N $to[2])],
			"faces": {
				"north": $(& $side $w $h),
				"east": $(& $side $d $h),
				"south": $(& $side $w $h),
				"west": $(& $side $d $h),
				"up": $(& $side $w $d),
				"down": $(& $side $w $d)
			}
		}
"@
}

function Shape([string]$file, [string[]]$textures, [string[]]$boxes) {
    $t = ($textures | ForEach-Object { "`"$_`": `"$_`"" }) -join ', '
    $json = "{`n`t`"editor`": { `"allAngles`": true },`n`t`"textureWidth`": 16,`n`t`"textureHeight`": 16,`n`t`"textures`": { $t },`n`t`"elements`": [`n" + ($boxes -join ",`n") + "`n`t]`n}`n"
    [IO.File]::WriteAllText((Join-Path $shapes $file), $json, (New-Object Text.UTF8Encoding $false))
}

Shape 'sailordock.json' @('wood', 'plank', 'metal', 'cloth') @(
    (Box 'Post' @(6.5, 0, 6.5) @(9.5, 13, 9.5) 'wood'),
    (Box 'Foot' @(5.5, 0, 5.5) @(10.5, 1.5, 10.5) 'plank'),
    (Box 'Board' @(2, 7, 7.5) @(14, 11, 8.5) 'plank'),
    (Box 'LampSeat' @(6, 13, 6) @(10, 14, 10) 'metal'),
    (Box 'Lamp' @(6.75, 14, 6.75) @(9.25, 16, 9.25) 'metal'),
    (Box 'Pennant' @(9.5, 11.5, 7.8) @(14, 14.5, 8.2) 'cloth')
)
Shape 'sailorbuoy.json' @('wood', 'red', 'white', 'metal') @(
    (Box 'Float' @(5, -3.5, 5) @(11, 2.5, 11) 'red'),
    (Box 'Ring' @(4.5, -0.5, 4.5) @(11.5, 1, 11.5) 'white'),
    (Box 'Mast' @(7.5, 2.5, 7.5) @(8.5, 11, 8.5) 'wood'),
    (Box 'Top' @(6.75, 11, 6.75) @(9.25, 13.5, 9.25) 'metal')
)

# the hat: a flat sailor cap, white with a navy band, a red pompom and a navy ribbon
Add-Type -AssemblyName System.Drawing
$bmp = New-Object Drawing.Bitmap 32, 32
$rnd = New-Object Random 7
function Fill([int]$x0, [int]$y0, [int]$x1, [int]$y1, [int[]]$rgb, [int]$noise) {
    for ($y = $y0; $y -lt $y1; $y++) {
        for ($x = $x0; $x -lt $x1; $x++) {
            $n = $rnd.Next(-$noise, $noise + 1)
            $c = [Drawing.Color]::FromArgb(255, [Math]::Max(0, [Math]::Min(255, $rgb[0] + $n)), [Math]::Max(0, [Math]::Min(255, $rgb[1] + $n)), [Math]::Max(0, [Math]::Min(255, $rgb[2] + $n)))
            $bmp.SetPixel($x, $y, $c)
        }
    }
}
Fill 0 0 32 32 @(236, 232, 220) 5          # linen white everywhere
Fill 0 16 14 19 @(34, 48, 92) 6            # the band
Fill 0 17 14 18 @(226, 222, 210) 4         # its white stripe
Fill 16 16 20 20 @(178, 44, 40) 10         # the pompom
Fill 20 16 26 22 @(34, 48, 92) 6           # the ribbon
# a seam ring on the crown's top
for ($i = 17; $i -lt 31; $i++) { foreach ($p in @(@($i, 1), @($i, 14), @(17, ($i - 16)), @(30, ($i - 16)))) { if ($p[1] -ge 1 -and $p[1] -le 14) { $bmp.SetPixel($p[0], $p[1], [Drawing.Color]::FromArgb(255, 205, 200, 186)) } } }
$bmp.Save((Join-Path $tex 'sailorhat.png'), [Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
'art written'
