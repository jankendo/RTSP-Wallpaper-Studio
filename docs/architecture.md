# アーキテクチャ

```text
App.exe
  ├─ Core        URL、設定モデル、再接続、レイアウト、IPC DTO
  ├─ Infrastructure 設定JSON、DPAPI、Renderer起動、Named Pipe client
  └─ Interop    モニター列挙、WorkerW、Win32 P/Invoke

Renderer.exe
  ├─ Named Pipe server
  ├─ WorkerWへ配置するトップレベルWindow
  └─ LibVLCSharp VideoView / MediaPlayer
```

AppとRendererを分離し、VLCのネイティブ異常が管理GUIへ直接波及しない境界を作っています。Rendererは親PIDを2秒間隔で軽量確認し、親が終了した場合に自分も終了します。

公開版では機能を増やす前に、Rendererの状態イベントをAppへ返すIPC、クラッシュ履歴による再起動ループ抑制、複数モニターのRendererセッション管理を追加します。
