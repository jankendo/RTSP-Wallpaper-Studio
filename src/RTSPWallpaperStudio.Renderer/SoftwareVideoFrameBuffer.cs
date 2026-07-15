using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace RTSPWallpaperStudio.Renderer;

/// <summary>
/// Receives LibVLC decoded frames in CPU memory and supplies the latest frame
/// to the renderer window. This deliberately avoids the Windows D3D11 vout;
/// that vout can deadlock with HEVC streams from go2rtc on some adapters.
/// </summary>
internal sealed class SoftwareVideoFrameBuffer : IDisposable
{
    private const int BufferCount = 6;
    private readonly object _gate = new();
    private readonly Action _invalidateWindow;
    private readonly Action _frameDisplayed;
    private readonly List<FrameBuffer> _buffers = [];
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCallback;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCallback;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCallback;
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCallback;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCallback;
    private byte[] _latestFrame = [];
    private int _width;
    private int _height;
    private long _frameCount;
    private DateTimeOffset? _lastFrameAt;
    private string? _lastError;
    private bool _disposed;

    public SoftwareVideoFrameBuffer(Action invalidateWindow, Action frameDisplayed)
    {
        _invalidateWindow = invalidateWindow;
        _frameDisplayed = frameDisplayed;
        _lockCallback = LockVideo;
        _unlockCallback = UnlockVideo;
        _displayCallback = DisplayVideo;
        _formatCallback = ConfigureFormat;
        _cleanupCallback = CleanupFormat;
    }

    public MediaPlayer.LibVLCVideoLockCb LockCallback => _lockCallback;
    public MediaPlayer.LibVLCVideoUnlockCb UnlockCallback => _unlockCallback;
    public MediaPlayer.LibVLCVideoDisplayCb DisplayCallback => _displayCallback;
    public MediaPlayer.LibVLCVideoFormatCb FormatCallback => _formatCallback;
    public MediaPlayer.LibVLCVideoCleanupCb CleanupCallback => _cleanupCallback;

    public long FrameCount
    {
        get
        {
            lock (_gate)
            {
                return _frameCount;
            }
        }
    }

    public DateTimeOffset? LastFrameAt
    {
        get
        {
            lock (_gate)
            {
                return _lastFrameAt;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    public bool HasFrame => FrameCount > 0;

    public void Configure(MediaPlayer player)
    {
        player.SetVideoCallbacks(_lockCallback, _unlockCallback, _displayCallback);
        player.SetVideoFormatCallbacks(_formatCallback, _cleanupCallback);
    }

    public void Paint(nint hdc, int destinationWidth, int destinationHeight)
    {
        lock (_gate)
        {
            if (_latestFrame.Length == 0 || _width <= 0 || _height <= 0 || destinationWidth <= 0 || destinationHeight <= 0)
            {
                return;
            }

            var bitmapInfo = new RendererWin32.BitmapInfo
            {
                Header = new RendererWin32.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<RendererWin32.BitmapInfoHeader>(),
                    Width = _width,
                    Height = -_height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = RendererWin32.BiRgb,
                    SizeImage = (uint)_latestFrame.Length
                }
            };

            var handle = GCHandle.Alloc(_latestFrame, GCHandleType.Pinned);
            try
            {
                _ = RendererWin32.StretchDIBits(hdc, 0, 0, destinationWidth, destinationHeight,
                    0, 0, _width, _height, handle.AddrOfPinnedObject(), ref bitmapInfo,
                    RendererWin32.DibRgbColors, RendererWin32.SrcCopy);
            }
            finally
            {
                handle.Free();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseBuffersLocked();
            _latestFrame = [];
        }
    }

    private nint LockVideo(nint opaque, nint planes)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || planes == 0)
                {
                    return 0;
                }

                var buffer = _buffers.FirstOrDefault(x => !x.InUse);
                if (buffer is null)
                {
                    return 0;
                }

                buffer.InUse = true;
                Marshal.WriteIntPtr(planes, buffer.Pointer);
                return buffer.Pointer;
            }
        }
        catch (Exception ex)
        {
            RecordError(ex);
            return 0;
        }
    }

    private void UnlockVideo(nint opaque, nint picture, nint planes)
    {
        // The display callback copies the frame and releases the buffer. This
        // callback is intentionally empty because LibVLC may call unlock before
        // it calls display.
    }

    private void DisplayVideo(nint opaque, nint picture)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || picture == 0 || _latestFrame.Length == 0)
                {
                    return;
                }

                Marshal.Copy(picture, _latestFrame, 0, _latestFrame.Length);
                var buffer = _buffers.FirstOrDefault(x => x.Pointer == picture);
                if (buffer is not null)
                {
                    buffer.InUse = false;
                }

                _frameCount++;
                _lastFrameAt = DateTimeOffset.UtcNow;
            }

            _frameDisplayed();
            _invalidateWindow();
        }
        catch (Exception ex)
        {
            RecordError(ex);
        }
    }

    private uint ConfigureFormat(ref nint opaque, nint chroma, ref uint width, ref uint height,
        ref uint pitches, ref uint lines)
    {
        try
        {
            var requestedWidth = checked((int)width);
            var requestedHeight = checked((int)height);
            if (chroma == 0 || requestedWidth <= 0 || requestedHeight <= 0)
            {
                return 0;
            }

            var pitch = checked(requestedWidth * 4);
            var frameSize = checked(pitch * requestedHeight);
            Marshal.WriteByte(chroma, 0, (byte)'R');
            Marshal.WriteByte(chroma, 1, (byte)'V');
            Marshal.WriteByte(chroma, 2, (byte)'3');
            Marshal.WriteByte(chroma, 3, (byte)'2');
            pitches = (uint)pitch;
            lines = (uint)requestedHeight;

            lock (_gate)
            {
                if (_disposed)
                {
                    return 0;
                }

                ReleaseBuffersLocked();
                _width = requestedWidth;
                _height = requestedHeight;
                _latestFrame = new byte[frameSize];
                _frameCount = 0;
                _lastFrameAt = null;
                for (var i = 0; i < BufferCount; i++)
                {
                    _buffers.Add(new FrameBuffer(Marshal.AllocHGlobal(frameSize)));
                }
            }

            return BufferCount;
        }
        catch (Exception ex)
        {
            RecordError(ex);
            return 0;
        }
    }

    private void CleanupFormat(ref nint opaque)
    {
        lock (_gate)
        {
            ReleaseBuffersLocked();
        }
    }

    private void ReleaseBuffersLocked()
    {
        foreach (var buffer in _buffers)
        {
            if (buffer.Pointer != 0)
            {
                Marshal.FreeHGlobal(buffer.Pointer);
            }
        }

        _buffers.Clear();
    }

    private void RecordError(Exception exception)
    {
        lock (_gate)
        {
            _lastError = exception.GetType().Name + ": " + exception.Message;
        }
    }

    private sealed class FrameBuffer(nint pointer)
    {
        public nint Pointer { get; } = pointer;
        public bool InUse { get; set; }
    }
}
