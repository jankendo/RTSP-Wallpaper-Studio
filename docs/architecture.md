# アーキテクチャ

```text
App.exe
  ├─ Core        URL、設定モデル、レイアウト、IPC DTO、構造化エラーコード
  ├─ Infrastructure 設定JSON、DPAPI、runtime-state、日次ログ、Job Object、Named Pipe server
  └─ Interop    モニター列挙、Win32 HWND、WorkerW/Raised Desktop戦略、Attach transaction

Renderer.exe
  ├─ 純Win32の非表示トップレベルHWND（WPF VideoViewなし）
  ├─ LibVLCSharp.Shared.MediaPlayer.Hwnd
  ├─ First Frame Gate（Playing && VoutCount > 0を連続検証）
  └─ Appと双方向Named Pipeでイベント・Heartbeatを送受信
```

## Desktop attachの安全境界

`DesktopHostDiscovery`はまず既存構造を読み取り、必要なときだけ明示的な復旧操作でShellの0x052C通知を送ります。候補は`LegacyWorkerWStrategy`と`RaisedDesktopStrategy`に分離し、Progman/SHELLDLL_DefView/WorkerWの階層を検査します。タスクバーやアイコンViewへ誤って親子付けしない禁止リストもあります。

`DesktopAttachmentTransaction`はRendererを非表示のまま、スタイル変更、`SetParent`後の実親、スクリーン座標から親座標への変換、矩形、Z順を検証します。どれか一つでも失敗するとスナップショットへロールバックし、ロールバック自体に失敗した場合はRenderer HWNDを破棄します。`WallpaperVisible`はこの検証完了後だけ発行されます。

## IPCとプロセス境界

AppがNamed Pipe serverを先に作成し、Rendererを起動してから同一ストリームでCommand/Eventを交換します。Rendererは親PIDの消失を監視し、AppはJob Objectの`KILL_ON_JOB_CLOSE`でRendererの子プロセスを回収します。IPCには最大長、スキーマバージョン、メッセージ種別の検証があります。

`runtime-state.json`には不完全終了、壁紙適用中、最後の成功/失敗コードを保存します。次回起動時に不完全状態なら自動復元を行わず、GUIにセーフモードを表示します。Ctrl + Alt + Shift + F12はGUIの表示状態に依存せず停止要求を送ります。
