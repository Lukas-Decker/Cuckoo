<#
.SYNOPSIS
    Publishes Cuckoo (formerly Twitch Drops Miner) and packages it as a self-extracting archive.

.DESCRIPTION
    Produces two artifacts in dist/:
      - The plain published app files (for a fresh copy / manual use).
      - Cuckoo-Setup.exe: a 7-Zip self-extracting archive. Running it shows a
        folder picker; extract into any instance folder to update it in place. User data
        (settings.json, auth.json, cache/, log.txt) is never in the archive, so an update
        overwrites only the app binaries and leaves each instance's config untouched.

.PARAMETER Configuration
    Build configuration. Defaults to Release.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\Cuckoo"
$staging = Join-Path $root "build\publish"
$dist = Join-Path $root "dist"
$archive = Join-Path $root "build\Cuckoo.7z"
$setupExe = Join-Path $dist "Cuckoo-Setup.exe"

# locate 7-Zip
$sevenZip = @(
    (Get-Command 7z -ErrorAction SilentlyContinue).Source,
    "C:\Program Files\7-Zip\7z.exe",
    "C:\Program Files (x86)\7-Zip\7z.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $sevenZip) {
    throw "7-Zip (7z.exe) was not found. Install it from https://www.7-zip.org/."
}
# The GUI SFX module (7z.sfx) ships with the full install but not with the
# scoop/winget shims, so search known locations rather than assume it sits
# next to whichever 7z.exe is first on PATH.
$sfxModule = @(
    (Join-Path (Split-Path $sevenZip -Parent) "7z.sfx"),
    "C:\Program Files\7-Zip\7z.sfx",
    "C:\Program Files (x86)\7-Zip\7z.sfx",
    "$env:USERPROFILE\scoop\apps\7zip\current\7z.sfx"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $sfxModule) {
    throw "The 7-Zip SFX module (7z.sfx) was not found. Install the full 7-Zip from https://www.7-zip.org/."
}

Write-Host "==> Cleaning staging folder" -ForegroundColor Cyan
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null

Write-Host "==> Publishing ($Configuration, $Runtime, self-contained single-file)" -ForegroundColor Cyan
dotnet publish $project `
    -c $Configuration -r $Runtime --self-contained `
    -p:PublishSingleFile=true `
    -o $staging
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# never ship user/runtime data, in case a previous run left some in the staging folder
Get-ChildItem $staging -Include settings.json, auth.json, log.txt -Recurse -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
$cacheDir = Join-Path $staging "cache"
if (Test-Path $cacheDir) { Remove-Item $cacheDir -Recurse -Force }

Write-Host "==> Refreshing dist/" -ForegroundColor Cyan
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Force $dist | Out-Null }
Copy-Item (Join-Path $staging "*") $dist -Recurse -Force
# clean pre-rename leftovers out of dist
Remove-Item (Join-Path $dist "TwitchDropsMiner.exe"), (Join-Path $dist "TwitchDropsMiner.pdb"), (Join-Path $dist "TwitchDropsMiner-Setup.exe") -Force -ErrorAction SilentlyContinue

Write-Host "==> Building self-extracting archive" -ForegroundColor Cyan
if (Test-Path $archive) { Remove-Item $archive -Force }
# compress the published files (contents of the staging folder, not the folder itself)
& $sevenZip a -t7z -mx=9 $archive (Join-Path $staging "*") | Out-Null
if ($LASTEXITCODE -ne 0) { throw "7-Zip archive creation failed with exit code $LASTEXITCODE" }

# prepend the GUI SFX module: running the result shows an "Extract to:" folder picker
if (Test-Path $setupExe) { Remove-Item $setupExe -Force }
$out = [System.IO.File]::Create($setupExe)
try {
    foreach ($part in @($sfxModule, $archive)) {
        $bytes = [System.IO.File]::ReadAllBytes($part)
        $out.Write($bytes, 0, $bytes.Length)
    }
}
finally {
    $out.Dispose()
}
Remove-Item $archive -Force

$sizeMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  App files:  $dist"
Write-Host "  Updater:    $setupExe ($sizeMb MB)"
Write-Host ""
Write-Host "To update an instance: run Cuckoo-Setup.exe, pick the instance folder,"
Write-Host "and extract. Its settings.json / auth.json / cache are left untouched."
Write-Host "NOTE: instances from before the rename still contain TwitchDropsMiner.exe;"
Write-Host "delete that old exe after extracting (the app is now Cuckoo.exe)."
