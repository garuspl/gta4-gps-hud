# Merges user changes into a new GPSHud.ini template.
# A value counts as a user change if the installed file differs from the previously shipped file.
param(
    [Parameter(Mandatory)] [string]$Installed,
    [Parameter(Mandatory)] [string]$PreviousShipped,
    [Parameter(Mandatory)] [string]$NewTemplate,
    [Parameter(Mandatory)] [string]$Out
)
$ErrorActionPreference = 'Stop'

function Read-Ini([string]$path) {
    $map = @{}
    $section = ''
    foreach ($line in Get-Content $path) {
        $t = $line.Trim()
        if ($t -match '^\[(.+)\]$') { $section = $Matches[1].ToLowerInvariant(); continue }
        if ($t -match '^([^;=]+?)\s*=\s*(.*)$') { $map["$section|$($Matches[1].ToLowerInvariant())"] = $Matches[2] }
    }
    return $map
}

$inst = Read-Ini $Installed
$prev = Read-Ini $PreviousShipped
$changes = @{}
foreach ($k in $inst.Keys) {
    if (-not $prev.ContainsKey($k) -or $prev[$k] -ne $inst[$k]) { $changes[$k] = $inst[$k] }
}

$section = ''
$result = foreach ($line in Get-Content $NewTemplate) {
    $t = $line.Trim()
    if ($t -match '^\[(.+)\]$') { $section = $Matches[1].ToLowerInvariant(); $line; continue }
    if ($t -match '^([^;=]+?)\s*=') {
        $key = "$section|$($Matches[1].ToLowerInvariant())"
        if ($changes.ContainsKey($key)) {
            Write-Host "keeping user value: $key = $($changes[$key])"
            "$($Matches[1].Trim())=$($changes[$key])"
            continue
        }
    }
    $line
}
Set-Content -Path $Out -Value $result -Encoding ASCII
