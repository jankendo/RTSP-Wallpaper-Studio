using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using RTSPWallpaperStudio.Core.Domain;

namespace RTSPWallpaperStudio.Renderer;

internal sealed class NativeRendererWindow : IDisposable
{
    internal const string ClassName = "RTSPWallpaperStudio.RendererHost";
    private static readonly RendererWin32.WndProcDelegate WndProc = WindowProc;
    private static readonly ConcurrentDictionary<nint, NativeRendererWindow> Instances = new();
    private static ushort _classAtom;
    private bool _disposed;
    private SoftwareVideoFrameBuffer? _frameBuffer;
    private TestPatternSurface? _testPattern;
    private long _paintRequestCount;
    private long _paintCount;
    private long _presentedFrameCount;
    private long _invalidationRequestCount;
    private DateTimeOffset? _lastPaintAt;
    private DateTimeOffset? _lastPresentedAt;
    private ulong _lastPresentedChecksum;
    private int _lastPaintResult;
    private TaskCompletionSource<RendererPresentationMetrics> _firstPresentation = CreatePresentationSource();

    public NativeRendererWindow()
    {
        RegisterClass();
        var instance = RendererWin32.GetModuleHandle(null);
        Hwnd = RendererWin32.CreateWindowEx(
            (int)(RendererWin32.WsExToolWindow | RendererWin32.WsExNoActivate),
            ClassName,
            null,
            unchecked((int)(RendererWin32.WsPopup | RendererWin32.WsClipChildren | RendererWin32.WsClipSiblings)),
            0,
            0,
            1,
            1,
            0,
            0,
            instance,
            0);
        if (Hwnd == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Renderer HWNDの作成に失敗しました。");
        }

        Instances[Hwnd] = this;
        RendererWin32.ShowWindow(Hwnd, RendererWin32.SwHide);
    }

    public nint Hwnd { get; }

    public void ResetPresentationObservation()
    {
        _firstPresentation = CreatePresentationSource();
        Interlocked.Exchange(ref _paintRequestCount, 0);
        Interlocked.Exchange(ref _paintCount, 0);
        Interlocked.Exchange(ref _presentedFrameCount, 0);
        Interlocked.Exchange(ref _invalidationRequestCount, 0);
        _lastPaintAt = null;
        _lastPresentedAt = null;
        _lastPresentedChecksum = 0;
        _lastPaintResult = 0;
    }

    public void SetFrameBuffer(SoftwareVideoFrameBuffer? frameBuffer)
    {
        EnsureNotDisposed();
        _testPattern?.Dispose();
        _testPattern = null;
        _frameBuffer = frameBuffer;
        InvalidateVideoFrame();
    }

    public void SetTestPattern()
    {
        EnsureNotDisposed();
        _frameBuffer = null;
        _testPattern?.Dispose();
        _testPattern = new TestPatternSurface(() =>
        {
            if (!_disposed)
            {
                _ = RendererWin32.SendMessage(Hwnd, RendererWin32.WmRenderTick, 1, 0);
            }
        });
        InvalidateVideoFrame();
    }

    public Task<RendererPresentationMetrics> WaitForPresentationAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return _firstPresentation.Task.WaitAsync(timeout, cancellationToken);
    }

    public RendererPresentationMetrics GetPresentationMetrics()
    {
        var frameBuffer = _frameBuffer;
        return new RendererPresentationMetrics(
            frameBuffer?.FrameCount ?? _testPattern?.FrameNumber ?? 0,
            frameBuffer?.FrameCount ?? 0,
            Interlocked.Read(ref _paintRequestCount),
            Interlocked.Read(ref _paintCount),
            Interlocked.Read(ref _presentedFrameCount),
            frameBuffer?.LastFrameChecksum ?? _testPattern?.LastChecksum ?? 0,
            _lastPresentedChecksum,
            frameBuffer?.LastFrameAt,
            _lastPaintAt,
            _lastPresentedAt,
            _lastPaintResult,
            _lastPaintResult > 0 && (frameBuffer?.HasFrame == true || _testPattern?.HasRenderedFrame == true),
            false,
            false,
            _testPattern?.HasRenderedFrame == true,
            Interlocked.Read(ref _invalidationRequestCount),
            _testPattern?.TickCount ?? 0);
    }

    public void InvalidateVideoFrame()
    {
        if (!_disposed && Hwnd != 0)
        {
            _ = RendererWin32.InvalidateRect(Hwnd, 0, false);
        }
    }

    public void ShowAfterValidation()
    {
        EnsureNotDisposed();
        RendererWin32.ShowWindow(Hwnd, RendererWin32.SwShowNoActivate);
        RendererWin32.UpdateWindow(Hwnd);
    }

    public void Hide()
    {
        if (!_disposed && Hwnd != 0)
        {
            RendererWin32.ShowWindow(Hwnd, RendererWin32.SwHide);
        }
    }

    public void PrepareForPlayback(RectD bounds)
    {
        EnsureNotDisposed();
        var width = Math.Max(2, (int)Math.Round(bounds.Width));
        var height = Math.Max(2, (int)Math.Round(bounds.Height));
        RendererWin32.SetWindowPos(Hwnd, 0,
            (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y), width, height,
            RendererWin32.SwpNoActivate | RendererWin32.SwpNoZOrder | RendererWin32.SwpFrameChanged);
    }

    public void CloseFromAnyThread()
    {
        if (!_disposed && Hwnd != 0)
        {
            RendererWin32.PostMessage(Hwnd, RendererWin32.WmClose, 0, 0);
        }
    }

    public void ResetToHiddenTopLevel()
    {
        EnsureNotDisposed();
        Hide();
        RendererWin32.SetParent(Hwnd, 0);
        var style = RendererWin32.GetWindowLongPtr(Hwnd, RendererWin32.GwlStyle).ToInt64();
        style = (style | RendererWin32.WsPopup | RendererWin32.WsClipChildren | RendererWin32.WsClipSiblings) & ~RendererWin32.WsChild;
        RendererWin32.SetWindowLongPtr(Hwnd, RendererWin32.GwlStyle, new nint(style));
        RendererWin32.SetWindowPos(Hwnd, 0, 0, 0, 1, 1,
            RendererWin32.SwpNoActivate | RendererWin32.SwpNoZOrder | RendererWin32.SwpFrameChanged);
    }

    public static void RunMessageLoop()
    {
        while (true)
        {
            var result = RendererWin32.GetMessage(out var message, 0, 0, 0);
            if (result <= 0)
            {
                return;
            }

            RendererWin32.TranslateMessage(ref message);
            RendererWin32.DispatchMessage(ref message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _frameBuffer = null;
        _testPattern?.Dispose();
        _testPattern = null;
        Instances.TryRemove(Hwnd, out _);
        if (Hwnd != 0)
        {
            RendererWin32.DestroyWindow(Hwnd);
        }
    }

    private static void RegisterClass()
    {
        if (_classAtom != 0)
        {
            return;
        }

        var wndClass = new RendererWin32.WndClassEx
        {
            Size = (uint)Marshal.SizeOf<RendererWin32.WndClassEx>(),
            WndProc = WndProc,
            Instance = RendererWin32.GetModuleHandle(null),
            ClassName = ClassName
        };
        _classAtom = RendererWin32.RegisterClassEx(ref wndClass);
        if (_classAtom == 0 && Marshal.GetLastWin32Error() != 1410)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Rendererウィンドウクラスの登録に失敗しました。");
        }
    }

    private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (Instances.TryGetValue(hwnd, out var window))
        {
            if (message == RendererWin32.WmPaint)
            {
                window.Paint();
                return 0;
            }

            if (message == RendererWin32.WmEraseBkgnd)
            {
                return 1;
            }

            if (message == RendererWin32.WmRenderTick)
            {
                window.InvalidateVideoFrameImmediately();
                return 0;
            }
        }

        if (message == RendererWin32.WmDestroy)
        {
            Instances.TryRemove(hwnd, out _);
            RendererWin32.PostQuitMessage(0);
        }

        if (message == RendererWin32.WmNcDestroy)
        {
            return 0;
        }

        return RendererWin32.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void Paint()
    {
        Interlocked.Increment(ref _paintRequestCount);
        var hdc = RendererWin32.BeginPaint(Hwnd, out var paintStruct);
        try
        {
            if (hdc == 0 || !RendererWin32.GetClientRect(Hwnd, out var clientRect))
            {
                return;
            }

            var width = clientRect.Right - clientRect.Left;
            var height = clientRect.Bottom - clientRect.Top;
            var result = _frameBuffer?.Paint(hdc, width, height) ??
                         _testPattern?.Paint(hdc, width, height, Hwnd, Environment.ProcessId) ?? 0;
            _lastPaintResult = result;
            _lastPaintAt = DateTimeOffset.UtcNow;
            if (result > 0)
            {
                Interlocked.Increment(ref _paintCount);
                Interlocked.Increment(ref _presentedFrameCount);
                _lastPresentedChecksum = _frameBuffer?.LastFrameChecksum ?? _testPattern?.LastChecksum ?? 0;
                _lastPresentedAt = DateTimeOffset.UtcNow;
                _firstPresentation.TrySetResult(GetPresentationMetrics());
            }
        }
        finally
        {
            RendererWin32.EndPaint(Hwnd, ref paintStruct);
        }
    }

    private void InvalidateVideoFrameImmediately()
    {
        if (!_disposed && Hwnd != 0)
        {
            Interlocked.Increment(ref _invalidationRequestCount);
            _ = RendererWin32.InvalidateRect(Hwnd, 0, false);
            _ = RendererWin32.UpdateWindow(Hwnd);
        }
    }

    private static TaskCompletionSource<RendererPresentationMetrics> CreatePresentationSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
