# Tries Sailor Locust in the game itself: builds the mod and runs a dedicated server with nobody on it, on a new world
# in a folder of its own, with the mod and (unless -NoShipwright) a copy of Shipwright. The self test inside the mod
# (src\SelfTest.cs) logs "[sailorlocust-selftest]" lines and stops the server.
#   powershell -File tests\ingame\run.ps1 [-Timeout 1500] [-Seed 102] [-Keep]
# Keep this file ASCII.
param(
    [string]$GameDir = "$env:APPDATA\Vintagestory",
    [string]$RuntimeDir = "C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.0",
    [string]$ConfigTemplate = "$env:APPDATA\VintagestoryData-seed\serverconfig.json",
    [string]$Shipwright = "$env:APPDATA\VintagestoryData\Mods-teste-s4\shipwright_1.4.1.zip",
    [string]$Seed = '102',
    [string]$Landcover = '0.4',
    [string]$OceanScale = '2',
    [int]$Timeout = 1500,
    [int]$SailSeconds = 1100,
    [int]$Port = 42477,
    [switch]$NoShipwright,
    # keeps the world of the last run (to try a second start on the same save)
    [switch]$Keep,
    [string]$Extra = ''
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path (Split-Path $here)
& (Join-Path $root 'build.ps1') -GameDir $GameDir -RuntimeDir $RuntimeDir

$work = Join-Path $env:TEMP 'sailorlocust-selftest'
$mods = Join-Path $work 'mods'
$data = Join-Path $work 'data'
if (-not $Keep -and (Test-Path $work)) { Remove-Item -Recurse -Force $work }
if (Test-Path $mods) { Remove-Item -Recurse -Force $mods }
New-Item -ItemType Directory -Force $mods, $data | Out-Null
Copy-Item (Get-ChildItem (Join-Path $root 'dist') -Filter 'sailorlocust_*.zip' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName $mods
if (-not $NoShipwright) {
    if (Test-Path $Shipwright) { Copy-Item $Shipwright $mods } else { "Shipwright not found at ${Shipwright}; its boat is skipped" }
}
"mods: " + ((Get-ChildItem $mods | ForEach-Object Name) -join ', ')

# the server's settings: a known file as the mould, with this run's world, port and folders
$cfg = Get-Content -Raw $ConfigTemplate | ConvertFrom-Json
$cfg.Port = $Port
$cfg.AdvertiseServer = $false
$cfg.Upnp = $false
$cfg.PassTimeWhenEmpty = $true
$cfg.MapSizeX = 20480
$cfg.MapSizeZ = 20480
$cfg.ModPaths = @('Mods', ($mods -replace '\\', '/'))
$cfg.WorldConfig.Seed = $Seed
$cfg.WorldConfig.WorldName = 'sailorlocust selftest'
$cfg.WorldConfig.SaveFileLocation = ((Join-Path $data 'Saves\selftest.vcdbs') -replace '\\', '/')
$wc = New-Object PSObject
$wc | Add-Member NoteProperty polarEquatorDistance '10000'
$wc | Add-Member NoteProperty worldWidth '20480'
$wc | Add-Member NoteProperty worldLength '20480'
$wc | Add-Member NoteProperty landcover $Landcover
$wc | Add-Member NoteProperty oceanscale $OceanScale
$cfg.WorldConfig.WorldConfiguration = $wc
[IO.File]::WriteAllText((Join-Path $data 'serverconfig.json'), ($cfg | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding $false))

$env:SAILORLOCUST_SELFTEST = '1'
$env:SAILORLOCUST_SELFTEST_SECONDS = "$SailSeconds"
if ($Extra) { $env:SAILORLOCUST_SELFTEST_MODE = $Extra }
$server = Start-Process -FilePath (Join-Path $GameDir 'VintagestoryServer.exe') -PassThru -NoNewWindow `
    -ArgumentList "--dataPath `"$data`"" `
    -RedirectStandardOutput (Join-Path $work 'stdout.txt') -RedirectStandardError (Join-Path $work 'stderr.txt')
if (-not $server.WaitForExit($Timeout * 1000)) {
    $server.Kill()
    "TIMEOUT after $Timeout s; the server was stopped"
}
Remove-Item Env:\SAILORLOCUST_SELFTEST, Env:\SAILORLOCUST_SELFTEST_SECONDS
if ($Extra) { Remove-Item Env:\SAILORLOCUST_SELFTEST_MODE }

$log = Join-Path $data 'Logs\server-main.log'
if (-not (Test-Path $log)) { throw "no server log at $log" }
$all = Get-Content $log
$all | Where-Object { $_ -match '\[sailorlocust' } | ForEach-Object { $_ -replace '^.*?\[(Notification|Warning|Error)\] ', '' }
$errors = $all | Where-Object { $_ -match '\[(Error|Fatal)\]' } | Select-Object -First 12
if ($errors) { "== errors in the server log"; $errors }
$warn = $all | Where-Object { $_ -match '\[Warning\]' -and $_ -match 'sailorlocust|locust|sailor' -and $_ -notmatch '\[sailorlocust' } | Select-Object -First 12
if ($warn) { "== warnings that name the mod"; $warn }
$done = $all | Where-Object { $_ -match 'DONE: (\d+) passed, (\d+) failed' } | Select-Object -Last 1
if (-not $done) { "NO RESULT: the self test did not finish (log: $log)"; exit 2 }
if ($done -match ', 0 failed') { "SELF TEST OK"; exit 0 }
"SELF TEST FAILED"; exit 1
