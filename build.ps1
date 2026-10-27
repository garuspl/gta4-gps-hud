# Builds GPSHud.net.dll with the C# compiler that ships with .NET Framework 4.x (no SDK needed).
param(
    [string]$ShdnDir = (Join-Path $PSScriptRoot 'shdn')
)
$ErrorActionPreference = 'Stop'

$asi = Join-Path $ShdnDir 'ScriptHookDotNet.asi'
if (-not (Test-Path $asi)) { throw "ScriptHookDotNet.asi not found in '$ShdnDir'. Extract ScriptHookDotNet 1.7.1.8 there." }

$fw  = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$out = Join-Path $PSScriptRoot 'bin'
$obj = Join-Path $PSScriptRoot 'obj'
New-Item -ItemType Directory -Force $out, $obj | Out-Null

# the .asi is a .NET assembly; give csc a .dll-named copy to reference
Copy-Item $asi (Join-Path $obj 'ScriptHookDotNet.dll') -Force

& $csc /nologo /target:library /platform:x86 /optimize+ /warn:4 `
    "/out:$out\GPSHud.net.dll" `
    "/reference:$obj\ScriptHookDotNet.dll" `
    "/reference:$fw\System.Drawing.dll" `
    "/reference:$fw\System.Windows.Forms.dll" `
    (Join-Path $PSScriptRoot 'src\*.cs')
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

Copy-Item (Join-Path $PSScriptRoot 'GPSHud.ini') $out -Force
Write-Host "Built $out\GPSHud.net.dll"
