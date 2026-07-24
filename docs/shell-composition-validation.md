# Windows Shell合成検証

RTSP映像を壁紙として表示する成功条件は、Renderer自身のGDI描画だけではありません。Windows Shellが所有するアイコンとタスクバーがRendererより前面にあり、RendererがAlt+Tab・タスクバー・フォーカス・入力を奪わないことを、同じ適用セッションで検証します。

## 配置経路

通常のWindows 10/11 Shellでは、次の階層を優先します。

```text
Progman
├─ SHELLDLL_DefView
│  └─ SysListView32       (デスクトップアイコン)
└─ WorkerW                (壁紙Rendererの親)
   └─ RTSPWallpaperStudio.RendererHost
```

`DesktopHostDiscovery`は、まずProgman直下のExplorer所有WorkerWを探します。WorkerWにSHELLDLL_DefViewがないこと、仮想デスクトップ矩形を覆うこと、禁止されたタスクバー/アイコンHWNDではないことを確認します。RaisedDesktopは互換候補として残っていますが、検証後にShell合成条件を満たせない場合は停止します。

## Rendererのスタイル契約

Rendererは適用中に次の契約を満たします。

- Legacy WorkerWでは`WS_CHILD`で、`WS_POPUP`ではない
- `WS_EX_TOOLWINDOW`と`WS_EX_NOACTIVATE`を持つ
- `WS_EX_TOPMOST`と`WS_EX_APPWINDOW`を持たない
- タスクバー階層の子孫ではない
- 前景ウィンドウ、アクティブウィンドウ、フォーカスを取得しない

Rendererは検証が終わるまで非表示です。表示には`SW_SHOWNOACTIVATE`を使い、`SetForegroundWindow`、`BringWindowToTop`、クリック、キーボード入力は行いません。

## 成功イベント

Shell検証は次の順でイベントを記録します。

`ShellCompositionValidationStarted` → アイコンホストの特定/可視/Z順 → タスクバーの特定/可視/Z順 → Alt+Tab/タスクバー/フォーカス → `WindowFromPoint`入力確認 → `WallpaperShellCompositionVerified` → `WallpaperEndToEndVerified`

どれか一つでも失敗すると`ShellCompositionValidationFailed`と`FatalError`を発行し、Rendererを停止してトップレベル非表示状態へ戻します。したがって`WallpaperVisible`や「成功」だけを見て壁紙設定完了と判断しません。

## CLI証跡

```powershell
.\artifacts\portable\app\RTSPWallpaperStudio.Diagnostics.exe --shell-probe --shell-probe-output artifacts\qa\shell-hierarchy-before.json
.\artifacts\portable\app\RTSPWallpaperStudio.Diagnostics.exe --render-test-pattern --wallpaper-only --desktop-probe --hold-seconds 30
```

`--shell-probe`はShell構造、HWND、親/owner/root、スタイル、矩形、モニター、前後兄弟、Z順をJSONに保存します。通常のアプリがデスクトップのサンプル点を覆っている場合でも、入力検証はRendererがその点を奪っていないこととタスクバー点が`Shell_TrayWnd`へ到達することを記録します。マウスクリックやショートカット入力は必要ありません。
