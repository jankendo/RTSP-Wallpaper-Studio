# 配布

Portable版は自己完結型win-x64 publishをAppとRendererへ行い、同じ出力フォルダーへ結合してZIP化します。LibVLCのネイティブファイルを削除しないでください。

MSIXは`installer/msix/AppxManifest.xml`をテンプレートとして`makeappx.exe`で生成します。PublisherとVersionはビルド時に差し替え、秘密鍵や正式証明書はリポジトリへ保存しません。

未署名成果物は検証用です。正式配布では証明書、署名検証、SmartScreen、アップデート手順を別途確認してください。
