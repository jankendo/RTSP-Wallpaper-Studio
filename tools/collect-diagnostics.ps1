[CmdletBinding()]
param([string]$Output = "")

$ErrorActionPreference = "Stop"
$root = Join-Path $env:LOCALAPPDATA "RTSPWallpaperStudio"
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $env:USERPROFILE "Desktop\RTSPWallpaperStudio-diagnostics.zip" }
$stage = Join-Path $env:TEMP ("RTSPWallpaperStudio-diagnostics-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stage | Out-Null
if (Test-Path (Join-Path $root "Logs")) { Copy-Item (Join-Path $root "Logs") $stage -Recurse -Force }
if (Test-Path (Join-Path $root "settings.json")) { Copy-Item (Join-Path $root "settings.json") (Join-Path $stage "settings.json") }
Get-ComputerInfo -Property WindowsProductName,WindowsVersion,OsBuildNumber | Out-File (Join-Path $stage "system.txt") -Encoding utf8
Get-Process RTSPWallpaperStudio* -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,WorkingSet64,StartTime | Out-File (Join-Path $stage "processes.txt") -Encoding utf8

# Remove settings and any possible secrets from the exported diagnostic package.
$settingsCopy = Join-Path $stage "settings.json"
if (Test-Path $settingsCopy) {
    $json = Get-Content -Raw $settingsCopy | ConvertFrom-Json
    foreach ($profile in $json.profiles) { $profile.protectedPassword = "<redacted>" }
    $json | ConvertTo-Json -Depth 10 | Set-Content $settingsCopy -Encoding utf8
}
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $Output -Force
Remove-Item $stage -Recurse -Force
Write-Host "診断ZIPを作成しました: $Output"
