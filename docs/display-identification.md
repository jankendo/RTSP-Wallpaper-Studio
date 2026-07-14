# ディスプレイ識別

モニター列挙には`EnumDisplayMonitors`と`GetMonitorInfo`を使用します。表示番号は保存せず、デバイス名・解像度からSHA-256ベースの永続IDを生成します。

この初期実装は、Windows Display Configuration APIからEDID、Adapter LUID、Target IDを完全取得する前段階です。そのため、同じデバイス名・解像度が変わる環境ではIDが変わる可能性があります。将来は`QueryDisplayConfig`、`DisplayConfigGetDeviceInfo`、EDIDを組み合わせます。
