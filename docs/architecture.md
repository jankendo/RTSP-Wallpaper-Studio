# アーキテクチャ

```text
App.exe
  ├─ Core        URL、設定モデル、レイアウト、IPC DTO、構造化エラーコード
  ├─ Infrastructure 設定JSON、DPAPI、runtime-state、日次ログ、Job Object、Named Pipe server
  └─ Interop    モニター列挙、Win32 HWND、WorkerW/Raised Desktop戦略、Attach transaction

Renderer.exe
  ├─ 純Win32の非表示トップレベルHWND（WPF VideoViewなし）
  ├─ LibVLC vmem + CPUフレームコールバック（D3D11のネイティブvoutを経由しない）
  ├─ GDIのStretchDIBitsで最新フレームをHWNDへ描画
  ├─ First Frame Gate（Playing、デコード済みフレーム、単調時計による安定進行を検証）
  ├─ Playback Stall Gate（デコード済みフレームの進行を監視し、8秒停止で自動再接続）
  └─ Appと双方向Named Pipeでイベント・Heartbeatを送受信
```

H.265/HEVCの一部のRTSP配信では、LibVLC 3系のD3D11 surface queueが
WorkerWへ配置するHWNDとの組み合わせでbuffer deadlockになることがあります。
RendererはCPU読取可能なRV32フレームを受け取り、デコード済みフレームの到着を
表示・停止監視の基準にすることで、この経路を回避します。

## Desktop attachの安全境界

`DesktopHostDiscovery`はまず既存構造を読み取り、必要なときだけ明示的な復旧操作でShellの0x052C通知を送ります。候補は`LegacyWorkerWStrategy`と`RaisedDesktopStrategy`に分離し、Progman/SHELLDLL_DefView/WorkerWの階層を検査します。タスクバーやアイコンViewへ誤って親子付けしない禁止リストもあります。

`DesktopAttachmentTransaction`はRendererを非表示のまま、スタイル変更、`SetParent`後の実親、スクリーン座標から親座標への変換、矩形を検証します。続く`DesktopShellCompositionProbe`がSHELLDLL_DefView/SysListView32、WorkerW、全対象モニターのタスクバーの可視性とZ順、Alt+Tab/タスクバー除外、フォーカス、`WindowFromPoint`を確認します。どれか一つでも失敗するとスナップショットへロールバックし、ロールバック自体に失敗した場合はRenderer HWNDを破棄します。`WallpaperVisible`と`WallpaperEndToEndVerified`は`WallpaperShellCompositionVerified`の後だけ発行されます。

## IPCとプロセス境界

AppがNamed Pipe serverを先に作成し、Rendererを起動してから同一ストリームでCommand/Eventを交換します。Rendererは親PIDの消失を監視し、AppはJob Objectの`KILL_ON_JOB_CLOSE`でRendererの子プロセスを回収します。IPCには最大長、スキーマバージョン、メッセージ種別の検証があります。

`runtime-state.json`には不完全終了、壁紙適用中、最後の成功/失敗コードを保存します。次回起動時に不完全状態なら自動復元を行わず、GUIにセーフモードを表示します。Ctrl + Alt + Shift + F12はGUIの表示状態に依存せず停止要求を送ります。
