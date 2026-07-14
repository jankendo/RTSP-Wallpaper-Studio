using System.Windows;
using LibVLCSharp.Shared;
using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Renderer;

internal sealed class RendererPlayback : IDisposable
{
    private readonly LibVLCSharp.WPF.VideoView _videoView;
    private readonly Action<string> _onError;
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _media;
    private RendererStartOptions? _lastOptions;

    public RendererPlayback(LibVLCSharp.WPF.VideoView videoView, Action<string> onError)
    {
        _videoView = videoView;
        _onError = onError;
    }

    public async Task StartAsync(RendererStartOptions options)
    {
        _lastOptions = options;
        await StopAsync();
        try
        {
            _libVlc = new LibVLC("--no-video-title-show", "--quiet");
            _mediaPlayer = new MediaPlayer(_libVlc);
            _mediaPlayer.EncounteredError += OnEncounteredError;
            _mediaPlayer.Playing += OnPlaying;
            _videoView.MediaPlayer = _mediaPlayer;
            var location = BuildLocation(options);
            _media = new Media(_libVlc, location, FromType.FromLocation);
            _media.AddOption($":network-caching={Math.Clamp(options.NetworkCachingMs, 50, 5000)}");
            if (options.Transport == TransportMode.Tcp)
            {
                _media.AddOption(":rtsp-tcp");
            }

            await Task.Run(() => _mediaPlayer.Play(_media));
        }
        catch (Exception ex)
        {
            _onError($"RTSP再生を開始できませんでした。{ex.Message}");
        }
    }

    public async Task ReconnectAsync()
    {
        if (_lastOptions is not null)
        {
            await StartAsync(_lastOptions);
        }
    }

    public async Task StopAsync()
    {
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_mediaPlayer is not null)
            {
                _mediaPlayer.Stop();
                _mediaPlayer.EncounteredError -= OnEncounteredError;
                _mediaPlayer.Playing -= OnPlaying;
                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }

            _media?.Dispose();
            _media = null;
            _libVlc?.Dispose();
            _libVlc = null;
            _videoView.MediaPlayer = null;
        });
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch (Exception) { }
    }

    private void OnEncounteredError(object? sender, EventArgs e) => _onError("RTSPストリームでエラーが発生しました。再接続を試してください。");
    private static void OnPlaying(object? sender, EventArgs e) { }

    private static string BuildLocation(RendererStartOptions options)
    {
        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("RTSP URLが正しくありません。");
        }

        if (string.IsNullOrEmpty(options.UserName))
        {
            return uri.ToString();
        }

        var builder = new UriBuilder(uri)
        {
            UserName = options.UserName,
            Password = options.Password ?? string.Empty
        };
        return builder.Uri.ToString();
    }
}
