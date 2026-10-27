# Installs ScriptHookDotNet 1.7.1.8 (Complete Edition compatible) and the GPS HUD script into a GTA IV folder.
param(
    [string]$GameDir = 'D:\SteamLibrary\steamapps\common\Grand Theft Auto IV\GTAIV'
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $GameDir 'GTAIV.exe'))) { throw "GTAIV.exe not found in '$GameDir'." }
$shdn = Join-Path $PSScriptRoot 'shdn'
$bin  = Join-Path $PSScriptRoot 'bin'
if (-not (Test-Path "$bin\GPSHud.net.dll")) { throw "Run build.ps1 first." }

foreach ($f in 'ScriptHookDotNet.asi', 'ScriptHook.dll', 'aCompleteEditionHook.asi') {
    if (-not (Test-Path (Join-Path $GameDir $f))) {
        Write-Host "Installing $f"
        Copy-Item (Join-Path $shdn $f) $GameDir
    }
}

$scripts = Join-Path $GameDir 'scripts'
New-Item -ItemType Directory -Force $scripts | Out-Null
Copy-Item "$bin\GPSHud.net.dll" $scripts -Force
# keep an existing, possibly user-edited config
if (-not (Test-Path "$scripts\GPSHud.ini")) { Copy-Item "$bin\GPSHud.ini" $scripts }
Write-Host "Installed to $scripts"
