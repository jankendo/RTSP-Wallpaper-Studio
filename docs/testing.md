# テストと手動QA

自動テストはURL検証、資格情報分離、ログ用サニタイズ、再接続遅延、映像レイアウト、負座標のスパン、IPCメッセージ、DPAPIを対象にします。

手動QAでは次を確認してください。

- アイコンが映像より前面にあり、デスクトップ右クリックとタスクバーが操作できる
- RendererがAlt+Tabに表示されない
- RTSP停止後の再接続、URL変更、認証失敗
- PlayingかつVoutCount>0のまま映像が停止した場合の `RTSP_PLAYBACK_STALLED` 検出と自動再接続
- 配信元を停止・復帰させたときの `WallpaperVisible` 再到達、FatalErrorなし
- Explorer再起動、Win+D、ロック/解除、スリープ復帰
- 左側モニターの負座標、縦置き、異なるDPI、切断・再接続
- 2時間以上のメモリ、ハンドル、スレッド数

GUIなしの長時間再生プローブは次で実行できます。

```powershell
dotnet run --project .\src\RTSPWallpaperStudio.Diagnostics\RTSPWallpaperStudio.Diagnostics.csproj -c Release -p:Platform=x64 -- --url rtsp://127.0.0.1:8555/test --transport tcp --wallpaper --hold-seconds 30
```

実機ストリームがない場合は、`tools/start-test-rtsp.ps1`が既存RTSP、Docker MediaMTX、FFmpegを順番に確認し、準備できない場合は明示的にスキップします。
