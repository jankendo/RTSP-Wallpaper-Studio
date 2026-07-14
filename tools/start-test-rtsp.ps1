[CmdletBinding()]
param([string]$Url = "rtsp://127.0.0.1:8554/switchbot3mp")

$ErrorActionPreference = "Stop"
$uri = [Uri]$Url
$tcp = Test-NetConnection -ComputerName $uri.Host -Port $uri.Port -WarningAction SilentlyContinue
if ($tcp.TcpTestSucceeded) {
    Write-Host "既存RTSPエンドポイントを検出しました: $Url"
    exit 0
}

if (Get-Command docker -ErrorAction SilentlyContinue) {
    Write-Host "Dockerは見つかりました。MediaMTX等のテスト配信を必要に応じて起動してください。"
} else {
    Write-Host "Dockerは未インストールです。"
}

if (Get-Command ffmpeg -ErrorAction SilentlyContinue) {
    Write-Host "FFmpegは見つかりました。テスト配信の構築は環境依存のため自動起動しません。"
} else {
    Write-Host "FFmpegは未インストールです。"
}

Write-Warning "ローカルRTSP配信は準備されていません。アプリの接続テストは明示的に失敗する可能性があります。"
exit 2
