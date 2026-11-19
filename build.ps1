# Builds Sailor Locust against the game's .NET 10 runtime with the installed SDK's compiler (SDK 7 cannot target
# net10.0 through a csproj, but csc accepts the references directly), then packs the zip. Keep this file ASCII.
param(
    [string]$GameDir = "$env:APPDATA\Vintagestory",
    [string]$RuntimeDir = "C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.0"
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$obj = Join-Path $root 'obj'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $obj, $dist | Out-Null

$csc = Get-ChildItem "C:\Program Files\dotnet\sdk" -Directory | Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'Roslyn\bincore\csc.dll' } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'csc.dll not found in any .NET SDK.' }

$refs = @()
foreach ($dll in Get-ChildItem $RuntimeDir -Filter *.dll) {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($dll.FullName); $refs += $dll.FullName } catch { }
}
foreach ($rel in 'VintagestoryAPI.dll', 'Mods\VSEssentials.dll', 'Mods\VSSurvivalMod.dll', 'Lib\cairo-sharp.dll', 'Lib\Newtonsoft.Json.dll', 'Lib\protobuf-net.dll') { $refs += Join-Path $GameDir $rel }

$version = (Get-Content -Raw (Join-Path $root 'modinfo.json') | ConvertFrom-Json).version
$outDll = Join-Path $obj 'SailorLocust.dll'
$rsp = Join-Path $obj 'build.rsp'
$lines = @('-nologo', '-nostdlib+', '-target:library', '-optimize+', '-deterministic+', '-langversion:11',
    '-codepage:65001', '-warnaserror-', '-nowarn:CS0649,CS1701,CS1702', "-out:`"$outDll`"")
$lines += $refs | ForEach-Object { "-r:`"$_`"" }
$lines += Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs | ForEach-Object { "`"$($_.FullName)`"" }
[IO.File]::WriteAllLines($rsp, $lines)

& dotnet $csc -noconfig "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }

$zip = Join-Path $dist "sailorlocust_$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $outDll, 'SailorLocust.dll')
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $root 'modinfo.json'), 'modinfo.json')
    $assets = Join-Path $root 'assets'
    foreach ($file in Get-ChildItem $assets -Recurse -File) {
        $relative = $file.FullName.Substring($assets.TrimEnd([char]92).Length + 1)
        $entry = 'assets/' + $relative.Replace([string][char]92, '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry)
    }
    # "Portugues" (pt-pt) gets the Brazilian texts rather than English
    $ptbr = Join-Path $assets 'sailorlocust\lang\pt-br.json'
    if (Test-Path $ptbr) { [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $ptbr, 'assets/sailorlocust/lang/pt-pt.json') }
}
finally { $archive.Dispose() }
"OK: $zip"
