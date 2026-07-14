# RTSP Wallpaper Studio

RTSP映像を、Windowsデスクトップアイコンの背面にあるWorkerWへネイティブ動画ウィンドウとして配置する、Windows 10/11 x64向けのライブ壁紙アプリです。

> 現在は公開開発版です。純Win32 Renderer、First Frame Gate、WorkerW/Raised Desktopの安全なAttach transaction、双方向IPC、安全停止と診断GUIを実装しています。実機カメラでの長時間再生、Explorer再起動、マルチモニター抜き差し、MSIX署名は未検証です。

## 主な機能

- WPF製の日本語GUI
- `rtsp://` / `rtsps://` URLの検証と資格情報分離
- LibVLCSharpによるRenderer別プロセス再生
- WorkerW / Raised Desktopへの壁紙ウィンドウ配置（実親・スタイル・矩形・Z順の検証付き）
- Appが先に作る現在ユーザー限定の双方向Named Pipe IPC
- First Frame Gate：映像出力が確認されるまでRendererは表示しない
- Job Object、親PID監視、runtime-state.json、Ctrl + Alt + Shift + F12緊急停止
- モニター列挙、永続ID生成、Fill / Fit / Stretch / Center / 1:1のレイアウト計算
- LibVLCによるRTSPメディア解析と映像トラック検証
- 日次ファイルログ、診断ページ、ライト/ダークテーマ
- DPAPI CurrentUserによるパスワード暗号化
- アトミックなJSON設定保存と1世代バックアップ
- xUnit単体テスト、Windows統合テスト、GitHub Actions
- Portable ZIPとMSIX生成のためのスクリプト

## 対応環境

- Windows 11 x64
- Windows 10 22H2 x64（Per-Monitor DPI V2対応環境）
- .NET 10 SDK（開発・ビルド時）

管理者権限、Windowsサービス、外部RTSPサーバーへの映像送信は必要ありません。FFmpegはアプリ本体に同梱しません。

## 初回起動

1. `RTSPWallpaperStudio.App.exe`を起動します。
2. URL欄にRTSP URLを入力します。初期例は `rtsp://127.0.0.1:8554/switchbot3mp` です。
3. 対象ディスプレイを選択します。
4. 必要に応じて「接続テスト」を実行します。
5. 「壁紙に設定」を押します。

このSwitchBot 3MP形式のURLは、ローカルRTSP中継サービスが起動している場合の入力例です。SwitchBot公式機能そのものを保証するURLではありません。

## ビルド

```powershell
dotnet restore RTSPWallpaperStudio.sln --configfile NuGet.Config
dotnet build RTSPWallpaperStudio.sln -c Release -p:Platform=x64
dotnet test RTSPWallpaperStudio.sln -c Release -p:Platform=x64 --no-build
```

Portable ZIPを作成するには、PowerShellで次を実行します。

```powershell
.\installer\scripts\build-portable.ps1
```

出力先は `artifacts\portable\` です。AppとRendererを同じフォルダーへ配置し、LibVLC関連ファイルも同梱します。

MSIXは `installer\msix\build-msix.ps1` を使います。開発用自己署名証明書は端末ごとに信頼が必要で、正式配布には正式なコード署名証明書を使用してください。

## 設定・ログ

```text
%LOCALAPPDATA%\RTSPWallpaperStudio\settings.json
%LOCALAPPDATA%\RTSPWallpaperStudio\settings.json.bak
%LOCALAPPDATA%\RTSPWallpaperStudio\Logs\
%LOCALAPPDATA%\RTSPWallpaperStudio\CrashReports\
%LOCALAPPDATA%\RTSPWallpaperStudio\Screenshots\
%LOCALAPPDATA%\RTSPWallpaperStudio\runtime-state.json
```

パスワードはDPAPIのCurrentUserスコープで暗号化します。認証付きURLは保存前にユーザー名・パスワードを分離し、ログへ出す場合はパスワードとクエリを伏せます。

## 既知の制約

- Windowsには動画壁紙用の安定した公開APIがなく、WorkerWは非公開Shell挙動に依存します。Windows大型更新で修正が必要になる可能性があります。
- 実機RTSP映像、Explorer再起動後の10秒以内復旧、画面ロック/スリープ、モニター抜き差しの実機QAは未実施です。
- GUIの現在版は1プロファイル・1 Rendererを中心とした基本フローです。複数Rendererによる複製・スパンの実行制御、タスクトレイ、MSIX自動更新、診断ZIPは今後の拡張対象です。
- DRM保護映像、RTSPサーバーの接続数制限、GPUドライバー依存のハードウェアデコードは対象環境の制約を受けます。
- 本プロジェクトは商用配布前のライセンス確認を代替しません。LibVLC/LibVLCSharpの配布条件を確認してください。

## リポジトリ構成

```text
src/RTSPWallpaperStudio.App            WPF GUI、MVVM、設定操作
src/RTSPWallpaperStudio.Renderer       LibVLC再生、WorkerW配置、IPC
src/RTSPWallpaperStudio.Core            ドメインモデル、検証、レイアウト
src/RTSPWallpaperStudio.Infrastructure  DPAPI、設定、IPCクライアント
src/RTSPWallpaperStudio.Interop         Win32 P/Invoke、モニター、WorkerW
tests/                                  単体テスト・統合テスト
installer/                             Portable / MSIXスクリプト
tools/                                  テストRTSP・診断補助
docs/                                   設計、セキュリティ、QA文書
```

## ライセンス

本体はMIT Licenseです。依存するLibVLC/LibVLCSharpなどは各ライセンスに従います。詳細は `THIRD-PARTY-NOTICES.txt` と `docs/third-party-licenses.md` を参照してください。
