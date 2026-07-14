[CmdletBinding()]
param(
    [string]$Url = "rtsp://127.0.0.1:8554/switchbot3mp",
    [switch]$KeepExistingPort
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$qaRoot = Join-Path $repoRoot "artifacts\qa"
$toolRoot = Join-Path $qaRoot "rtsp-test-server"
New-Item -ItemType Directory -Force -Path $qaRoot, $toolRoot | Out-Null
$uri = [Uri]$Url

$existingTcp = Test-NetConnection -ComputerName $uri.Host -Port $uri.Port -WarningAction SilentlyContinue
if ($existingTcp.TcpTestSucceeded) {
    Write-Host "既存RTSPエンドポイントを検出しました: $Url"
    exit 0
}

$ffmpeg = $null
$knownFfmpeg = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-8.1-full_build\bin\ffmpeg.exe"
if (Test-Path -LiteralPath $knownFfmpeg) {
    $ffmpeg = $knownFfmpeg
} else {
    $ffmpegCommand = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($ffmpegCommand) {
        $ffmpeg = [string]$ffmpegCommand.Definition
    }
}
if ([string]::IsNullOrWhiteSpace($ffmpeg)) {
    $ffmpegLink = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\ffmpeg.exe"
    if (Test-Path -LiteralPath $ffmpegLink) {
        $ffmpeg = $ffmpegLink
    }
}
if ([string]::IsNullOrWhiteSpace($ffmpeg)) {
    $ffmpeg = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\Gyan.FFmpeg*" -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName "ffmpeg-8.1-full_build\bin\ffmpeg.exe" } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($ffmpeg)) {
    Write-Warning "FFmpegが見つからないため、テスト配信を開始できません。"
    exit 2
}
if (-not (Test-Path -LiteralPath $ffmpeg)) {
    Write-Warning "FFmpeg実行ファイルが見つからないため、テスト配信を開始できません: $ffmpeg"
    exit 2
}
$ffmpeg = (Get-Item -LiteralPath $ffmpeg).FullName

$mediaServerPath = Join-Path -Path $toolRoot -ChildPath "mediamtx.exe"
if (-not (Test-Path -LiteralPath $mediaServerPath)) {
    Write-Host "MediaMTXテストサーバーを取得しています（アプリ本体には同梱しません）。"
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/bluenviron/mediamtx/releases/latest" -Headers @{ "User-Agent" = "RTSP-Wallpaper-Studio-QA" }
    $asset = $release.assets | Where-Object { $_.name -match "windows_amd64\.zip$" } | Select-Object -First 1
    if ($null -eq $asset) { throw "MediaMTX Windows x64 asset was not found." }
    $zip = Join-Path $toolRoot "mediamtx.zip"
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -Headers @{ "User-Agent" = "RTSP-Wallpaper-Studio-QA" }
    $extract = Join-Path $toolRoot "extract"
    if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
    Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force
    $found = Get-ChildItem -LiteralPath $extract -Filter mediamtx.exe -File -Recurse | Select-Object -First 1
    if ($null -eq $found) { throw "MediaMTX executable was not found after extraction." }
    Copy-Item -LiteralPath $found.FullName -Destination $mediaServerPath -Force
}
$mediaServerPath = (Get-Item -LiteralPath $mediaServerPath).FullName
$mediaServerConfig = Join-Path $toolRoot "mediamtx.yml"
@"
rtspAddress: :$($uri.Port)
logLevel: info
paths:
  all:
"@ | Set-Content -LiteralPath $mediaServerConfig -Encoding utf8

$mtxLog = Join-Path $toolRoot "mediamtx.log"
$mtxOut = Join-Path $toolRoot "mediamtx.out.log"
$ffmpegLog = Join-Path $toolRoot "ffmpeg.log"
$ffmpegOut = Join-Path $toolRoot "ffmpeg.out.log"
$mtx = Start-Process -FilePath $mediaServerPath -WorkingDirectory $toolRoot -ArgumentList @() -RedirectStandardOutput $mtxOut -RedirectStandardError $mtxLog -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 2
$publishUrl = "rtsp://127.0.0.1:$($uri.Port)/test"
$ff = Start-Process -FilePath $ffmpeg -WorkingDirectory $toolRoot -ArgumentList @(
    "-hide_banner", "-loglevel", "warning", "-re",
    "-f", "lavfi", "-i", "testsrc=size=640x360:rate=15",
    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
    "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-g", "15", "-keyint_min", "15", "-sc_threshold", "0", "-pix_fmt", "yuv420p",
    "-c:a", "aac", "-f", "rtsp", "-rtsp_transport", "tcp", $publishUrl
) -RedirectStandardOutput $ffmpegOut -RedirectStandardError $ffmpegLog -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 3

$startedTcp = Test-NetConnection -ComputerName "127.0.0.1" -Port $uri.Port -WarningAction SilentlyContinue
if (-not $startedTcp.TcpTestSucceeded) {
    throw "MediaMTX did not start listening on port $($uri.Port). See $mtxLog"
}

Write-Host "テストRTSP配信を開始しました: $publishUrl"
Write-Host "MediaMTX PID: $($mtx.Id); FFmpeg PID: $($ff.Id)"
Write-Host "停止する場合: Stop-Process -Id $($ff.Id),$($mtx.Id)"
