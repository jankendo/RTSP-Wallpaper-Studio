# RTSP Wallpaper Studio

RTSP映像を、Windowsデスクトップアイコンの背面にあるWorkerWへネイティブ動画ウィンドウとして配置する、Windows 10/11 x64向けのライブ壁紙アプリです。

> 現在は公開開発版です。純Win32 Renderer、First Frame Gate、WorkerW/Raised Desktopの安全なAttach transaction、双方向IPC、安全停止と診断GUIを実装しています。実機カメラでの長時間再生、Explorer再起動、マルチモニター抜き差し、MSIX署名は未検証です。

## 主な機能

- WPF製の日本語GUI
- `rtsp://` / `rtsps://` URLの検証と資格情報分離
- LibVLCSharpによるRenderer別プロセス再生
- WorkerW / Raised Desktopへの壁紙ウィンドウ配置（実親・スタイル・矩形・Z順の検証付き）
- H.265/HEVC向けLibVLC vmem + CPUフレームコールバック描画（D3D11 voutのデッドロック回避）
- Appが先に作る現在ユーザー限定の双方向Named Pipe IPC
- First Frame Gate：`Playing`、デコード済みフレーム、単調時計による安定進行を確認するまでRendererは表示しない
- Job Object、親PID監視、runtime-state.json、Ctrl + Alt + Shift + F12緊急停止
- モニター列挙、永続ID生成、Fill / Fit / Stretch / Center / 1:1のレイアウト計算
- LibVLCによるRTSP接続テスト（Playing + 映像出力の確認）
- 再生後の映像進行監視（8秒停止で `RTSP_PLAYBACK_STALLED` を記録し、MediaPlayerを安全に再生成）
- 初回接続と自動復旧のバックオフ再試行（起動直後のカメラ・go2rtc準備遅延で壁紙を終了させない）
- TCP / UDP / 自動方式、ネットワークキャッシュ、ハードウェアデコード設定の共通化
- 起動時の `C:\go2rtc\go2rtc.exe` 自動起動、8554待受確認、アプリ所有プロセスの安全な終了
- 接続テストの段階表示、キャンセル、具体的なエラーコード、認証情報の優先順位表示
- 日次ファイルログ、診断ページ、ライト/ダークテーマ
- DPAPI CurrentUserによるパスワード暗号化
- アトミックなJSON設定保存と1世代バックアップ
- 通知領域への常駐、ウィンドウを閉じても終了しない安全なトレイ運用
- 設定画面からのWindows起動時自動起動（現在ユーザーHKCU、管理者権限不要）
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
4. 「接続テスト」を実行します。成功条件はTCPポートの開通だけではなく、LibVLCの再生状態と映像出力です。
5. 成功後に「テスト成功後に壁紙へ設定」を押します。失敗時は壁紙を表示しません。

このSwitchBot 3MP形式のURLは、ローカルRTSP中継サービスが起動している場合の入力例です。アプリは起動時に `C:\go2rtc\go2rtc.exe` を探し、8554が未使用なら非表示で起動して待受を確認します。既に別プロセスが8554を使用している場合は、そのプロセスを終了させません。SwitchBot公式機能そのものを保証するURLではありません。

接続テストは、入力欄のユーザー名・パスワード、URL内の認証情報、保存済みDPAPIパスワードの順に採用します。保存・ログ・診断情報にはパスワード、トークン、完全な認証付きURLを残しません。`Transport=Automatic` はTCP再生を試してからLibVLC自動方式を再試行します。UDP指定と自動方式に `:rtsp-tcp` は付加しません。

実接続診断は次の補助スクリプトで行えます。

```powershell
.\tools\start-test-rtsp.ps1
```

ユーザーURLのポートが未使用の場合、スクリプトはMediaMTXとFFmpegの合成H.264配信を入力URLのポートに準備します。確認結果は `artifacts\qa\` に保存します。再現性確認用の診断CLIは次で実行できます。

```powershell
dotnet run --project .\src\RTSPWallpaperStudio.Diagnostics\RTSPWallpaperStudio.Diagnostics.csproj -c Release -p:Platform=x64 -- --url rtsp://127.0.0.1:8554/switchbot3mp --transport tcp --timeout 10
dotnet run --project .\src\RTSPWallpaperStudio.Diagnostics\RTSPWallpaperStudio.Diagnostics.csproj -c Release -p:Platform=x64 -- --url rtsp://127.0.0.1:8554/test --transport tcp --timeout 10 --wallpaper
dotnet run --project .\src\RTSPWallpaperStudio.Diagnostics\RTSPWallpaperStudio.Diagnostics.csproj -c Release -p:Platform=x64 -- --start-go2rtc --url rtsp://127.0.0.1:8554/switchbot3mp --transport tcp --timeout 15 --wallpaper
dotnet run --project .\src\RTSPWallpaperStudio.Diagnostics\RTSPWallpaperStudio.Diagnostics.csproj -c Release -p:Platform=x64 -- --ipc-smoke
dotnet run --project .\src\RTSPWallpaperStudio.Diagnostics\RTSPWallpaperStudio.Diagnostics.csproj -c Release -p:Platform=x64 -- --url rtsp://127.0.0.1:8555/test --transport tcp --wallpaper --hold-seconds 30
dotnet run --project .\src\RTSPWallpaperStudio.Diagnostics\RTSPWallpaperStudio.Diagnostics.csproj -c Release -p:Platform=x64 -- --url rtsp://127.0.0.1:8555/test --transport tcp --wallpaper-only --hold-seconds 30
dotnet run --project .\src\RTSPWallpaperStudio.Diagnostics\RTSPWallpaperStudio.Diagnostics.csproj -c Release -p:Platform=x64 -- --url rtsp://127.0.0.1:8555/test --transport tcp --wallpaper-only --desktop-probe --hold-seconds 30
```

`--wallpaper` を付けると、接続テスト成功後にRendererを起動し、`WallpaperVisible`イベントを受信してから停止します。`--hold-seconds` を併用すると指定秒数だけ表示を維持し、長時間再生・フリーズ監視を検証できます。`--start-go2rtc` は `C:\go2rtc\go2rtc.exe` を診断プロセスの所有下で起動し、検証終了時にそれだけを停止します。`--ipc-smoke` はRTSP接続を省略してRendererのNamed Pipe接続、Ready/Heartbeat/停止イベントだけを検証します。GUIを使わないため、CI・障害再現・ログ採取に利用できます。
`--wallpaper-only` は接続テストを省略し、アプリの「壁紙に設定」操作と同じRenderer直接起動を検証します。`--desktop-probe` は表示HWNDの親・owner・class・style・矩形・可視状態を確認し、実デスクトップDCの非黒サンプルと1秒差分から動画の動きを確認します。既にGUIから表示中のHWNDを検証する場合は、ログ／診断画面のHWNDを使って次を実行できます。

```powershell
dotnet run --project .\src\RTSPWallpaperStudio.Diagnostics\RTSPWallpaperStudio.Diagnostics.csproj -c Release -p:Platform=x64 -- --probe-hwnd 0x123456 --probe-seconds 2
```

アプリは閉じるボタンまたは最小化で終了せず、通知領域へ格納されます。トレイの「表示」で画面を戻し、「緊急停止」でRendererを停止し、「終了」で完全終了します。設定画面の「Windows起動時に起動」を有効にして保存すると、現在ユーザーのHKCU Runへアプリ本体を登録します。「起動時は画面を表示しない」を有効にすると、ログオン時はトレイだけで起動します。

自動起動設定はGUIを使わず次の診断コマンドでも確認できます。

```powershell
dotnet run --project .\src\RTSPWallpaperStudio.Diagnostics\RTSPWallpaperStudio.Diagnostics.csproj -c Release -- --startup-status
dotnet run --project .\src\RTSPWallpaperStudio.Diagnostics\RTSPWallpaperStudio.Diagnostics.csproj -c Release -- --startup-enable --startup-exe "C:\Path\To\RTSPWallpaperStudio.App.exe"
dotnet run --project .\src\RTSPWallpaperStudio.Diagnostics\RTSPWallpaperStudio.Diagnostics.csproj -c Release -- --startup-disable
```

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

出力先は `artifacts\portable\` です。App直下に同梱Rendererを配置するほか、`renderer` siblingフォルダーも解決できるため、配布レイアウトを変更しても実行ファイル・作業ディレクトリ・LibVLC native/pluginsの取り違えを防止します。起動時には解決した絶対パス、配置種別、作業ディレクトリ、ファイルバージョン、更新時刻、SHA256をログへ記録します。

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
- GUIの現在版は1プロファイル・1 Rendererを中心とした基本フローです。複数Rendererによる複製・スパンの実行制御、MSIX自動更新、診断ZIPは今後の拡張対象です。
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
