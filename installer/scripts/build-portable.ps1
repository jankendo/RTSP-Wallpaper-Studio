[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $root "artifacts\portable"
}

$appProject = Join-Path $root "src\RTSPWallpaperStudio.App\RTSPWallpaperStudio.App.csproj"
$rendererProject = Join-Path $root "src\RTSPWallpaperStudio.Renderer\RTSPWallpaperStudio.Renderer.csproj"
$diagnosticsProject = Join-Path $root "src\RTSPWallpaperStudio.Diagnostics\RTSPWallpaperStudio.Diagnostics.csproj"
$appPublish = Join-Path $OutputRoot "app"
$rendererPublish = Join-Path $OutputRoot "renderer"
$diagnosticsPublish = Join-Path $OutputRoot "diagnostics"
$zipPath = Join-Path $OutputRoot "RTSPWallpaperStudio-win-x64.zip"

if (Test-Path -LiteralPath $OutputRoot) { Remove-Item -LiteralPath $OutputRoot -Recurse -Force }
New-Item -ItemType Directory -Path $appPublish, $rendererPublish, $diagnosticsPublish | Out-Null

dotnet publish $appProject -c $Configuration -r win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=false -o $appPublish
dotnet publish $rendererProject -c $Configuration -r win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=false -o $rendererPublish
dotnet publish $diagnosticsProject -c $Configuration -r win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=false -o $diagnosticsPublish

# Merge Renderer dependencies without overwriting the App's WPF framework assemblies.
# Both self-contained publishes can contain identically named framework files, but the
# App's implementation must win over Renderer reference/resource assemblies.
Get-ChildItem -LiteralPath $rendererPublish -File | ForEach-Object {
    $destination = Join-Path $appPublish $_.Name
    if (-not (Test-Path -LiteralPath $destination)) {
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }
}
Get-ChildItem -LiteralPath $rendererPublish -Directory | ForEach-Object {
    $sourceRoot = $_.FullName
    $rootDirectoryName = $_.Name
    Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($sourceRoot.Length).TrimStart('\')
        $destination = Join-Path (Join-Path $appPublish $rootDirectoryName) $relative
        $destinationDirectory = Split-Path -Parent $destination
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        if (-not (Test-Path -LiteralPath $destination)) {
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }
    }
}
# Include the command-line diagnostics executable in the same Portable root so
# the exact shipped Renderer/App/Diagnostics identity can be verified without
# falling back to a development bin folder.
Get-ChildItem -LiteralPath $diagnosticsPublish -File | ForEach-Object {
    $destination = Join-Path $appPublish $_.Name
    if (-not (Test-Path -LiteralPath $destination)) {
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }
}
Get-ChildItem -LiteralPath $diagnosticsPublish -Directory | ForEach-Object {
    $sourceRoot = $_.FullName
    $rootDirectoryName = $_.Name
    Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($sourceRoot.Length).TrimStart('\')
        $destination = Join-Path (Join-Path $appPublish $rootDirectoryName) $relative
        $destinationDirectory = Split-Path -Parent $destination
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        if (-not (Test-Path -LiteralPath $destination)) {
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }
    }
}
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination $appPublish -Force
Copy-Item -LiteralPath (Join-Path $root "THIRD-PARTY-NOTICES.txt") -Destination $appPublish -Force

if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $appPublish "*") -DestinationPath $zipPath -CompressionLevel Optimal
Get-FileHash -Algorithm SHA256 $zipPath | ForEach-Object { "$($_.Hash)  $($_.Path | Split-Path -Leaf)" } | Set-Content -Path (Join-Path $OutputRoot "SHA256SUMS.txt") -Encoding utf8
Write-Host "Portable package: $zipPath"
