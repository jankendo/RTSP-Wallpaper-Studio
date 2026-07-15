using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace RTSPWallpaperStudio.Infrastructure.Diagnostics;

/// <summary>
/// Minimal vmem sink used by the connection test. It verifies that decoded
/// frames reach LibVLC's video callbacks without creating a D3D11 window.
/// </summary>
internal sealed class LibVlcFrameProbe : IDisposable
{
    private const int BufferCount = 4;
    private readonly object _gate = new();
    private readonly List<nint> _buffers = [];
    private readonly HashSet<nint> _inUse = [];
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCallback;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCallback;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCallback;
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCallback;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCallback;
    private long _frameCount;
    private string? _lastError;
    private int _frameSize;
    private bool _disposed;

    public LibVlcFrameProbe()
    {
        _lockCallback = LockVideo;
        _unlockCallback = UnlockVideo;
        _displayCallback = DisplayVideo;
        _formatCallback = ConfigureFormat;
        _cleanupCallback = CleanupFormat;
    }

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

    public bool HasFrame => FrameCount > 0;

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

    public void Configure(MediaPlayer player)
    {
        player.SetVideoCallbacks(_lockCallback, _unlockCallback, _displayCallback);
        player.SetVideoFormatCallbacks(_formatCallback, _cleanupCallback);
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

                var buffer = _buffers.FirstOrDefault(x => !_inUse.Contains(x));
                if (buffer == 0)
                {
                    return 0;
                }

                _inUse.Add(buffer);
                Marshal.WriteIntPtr(planes, buffer);
                return buffer;
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
    }

    private void DisplayVideo(nint opaque, nint picture)
    {
        lock (_gate)
        {
            if (_disposed || picture == 0)
            {
                return;
            }

            _inUse.Remove(picture);
            _frameCount++;
        }
    }

    private uint ConfigureFormat(ref nint opaque, nint chroma, ref uint width, ref uint height,
        ref uint pitches, ref uint lines)
    {
        try
        {
            var requestedWidth = checked((int)width);
            var requestedHeight = checked((int)height);
            var pitch = checked(requestedWidth * 4);
            _frameSize = checked(pitch * requestedHeight);
            if (chroma == 0 || requestedWidth <= 0 || requestedHeight <= 0)
            {
                return 0;
            }

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
                _frameCount = 0;
                for (var i = 0; i < BufferCount; i++)
                {
                    _buffers.Add(Marshal.AllocHGlobal(_frameSize));
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
            if (buffer != 0)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        _buffers.Clear();
        _inUse.Clear();
    }

    private void RecordError(Exception exception)
    {
        lock (_gate)
        {
            _lastError = exception.GetType().Name + ": " + exception.Message;
        }
    }
}
