[CmdletBinding()]
param([string]$Subject = "CN=Jankendo", [string]$Output = "")

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path (Resolve-Path "$PSScriptRoot\..\..") "artifacts\RTSPWallpaperStudio-dev.pfx" }
New-Item -ItemType Directory -Path (Split-Path $Output) -Force | Out-Null
$certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -CertStoreLocation "Cert:\CurrentUser\My"
$password = Read-Host "PFX password" -AsSecureString
Export-PfxCertificate -Cert $certificate -FilePath $Output -Password $password | Out-Null
Write-Host "開発用証明書を作成しました。正式鍵はリポジトリへ保存しないでください: $Output"
