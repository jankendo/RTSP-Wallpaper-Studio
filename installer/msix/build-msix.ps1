[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "0.1.0.0",
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$makeAppx = (Get-Command makeappx.exe -ErrorAction SilentlyContinue)
if ($null -eq $makeAppx) {
    throw "makeappx.exeが見つかりません。Windows SDKのMSIX Packaging Toolをインストールしてください。"
}

$manifest = Join-Path $PSScriptRoot "AppxManifest.xml"
$assets = Join-Path $PSScriptRoot "Assets"
if (-not (Test-Path -LiteralPath $assets)) {
    throw "MSIXのロゴAssetsがありません。配布用PNGをAssetsへ配置してから実行してください。"
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $root "artifacts\msix" }
if (Test-Path -LiteralPath $OutputRoot) { Remove-Item -LiteralPath $OutputRoot -Recurse -Force }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null

& (Join-Path $root "installer\scripts\build-portable.ps1") -Configuration $Configuration -OutputRoot (Join-Path $OutputRoot "portable")
$packageRoot = Join-Path $OutputRoot "package"
New-Item -ItemType Directory -Path $packageRoot | Out-Null
Copy-Item -Path (Join-Path $root "artifacts\portable\app\*") -Destination $packageRoot -Recurse -Force
Copy-Item -LiteralPath $manifest -Destination $packageRoot -Force
Copy-Item -LiteralPath $assets -Destination $packageRoot -Recurse -Force

$msix = Join-Path $OutputRoot "RTSPWallpaperStudio-$Version-x64.msix"
& $makeAppx pack /d $packageRoot /p $msix /o
Write-Host "MSIX package: $msix"
