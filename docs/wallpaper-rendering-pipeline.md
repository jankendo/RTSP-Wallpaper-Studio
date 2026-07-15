# 壁紙描画パイプライン

RTSP再生は、ネットワーク、デコード、CPUバックバッファ、GDIペイント、Windows Shell合成の段階に分離しています。

```text
RTSP/go2rtc
  ↓ TCP / LibVLC software decode
decoded frame
  ↓ SoftwareVideoFrameBuffer
RendererHost WM_PAINT / GDI
  ↓ hidden attachment transaction
Progman → WorkerW → RendererHost
  ↓ Shell composition validation
desktop wallpaper with icons/taskbar in front
```

映像出力が最初に見えた時点ではまだ壁紙成功ではありません。Rendererはまず非表示でフレーム進行を待ち、WorkerWへの親子付け、矩形、スタイル、ownerを検証します。表示後は自身のペイント数・チェックサム・フレーム進行を取得し、実デスクトップDCを補助診断としてサンプリングします。

最終成功は、Rendererのフレーム進行と`WallpaperShellCompositionVerified`の両方が成立した後にだけ発行されます。Shell合成が崩れた場合は、映像が描画できていても壁紙を停止します。これにより、アイコン/タスクバーを覆う、Alt+Tabに出る、入力を奪う、フォーカスを取得するタイプの不具合を「再生成功」と誤認しません。

RTSPフリーズ監視はMediaPlayerの時刻だけに依存しません。ライブ配信では時刻が0のままになるため、デコードフレームと単調時計による進行を使い、停止を検出したらRendererをいったん非表示にして再接続します。再接続後も同じShell合成検証をやり直します。
