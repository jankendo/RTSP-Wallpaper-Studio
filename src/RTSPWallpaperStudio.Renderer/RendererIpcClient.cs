using System.IO.Pipes;
using System.Text;
using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Core.Services;

namespace RTSPWallpaperStudio.Renderer;

internal sealed class RendererIpcClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public RendererIpcClient(string pipeName) => _pipeName = pipeName;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _pipe.ConnectAsync(timeout.Token);
        _reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(_pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    }

    public async Task RunCommandLoopAsync(Func<IpcMessage, Task> handler, CancellationToken cancellationToken)
    {
        if (_reader is null)
        {
            throw new InvalidOperationException("IPC接続が開始されていません。");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await _reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            if (IpcProtocol.TryDeserializeMessage(line, out var message) && message is not null && message.Kind == IpcMessageKind.Command)
            {
                await handler(message);
            }
        }
    }

    public async Task SendEventAsync(RendererEvent rendererEvent, CancellationToken cancellationToken = default)
    {
        var message = new IpcMessage(IpcMessageKind.Event, rendererEvent.Type.ToString(),
            IpcProtocol.SerializePayload(rendererEvent), RendererId: rendererEvent.RendererId);
        await SendAsync(message, cancellationToken);
    }

    public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        if (_writer is null)
        {
            throw new InvalidOperationException("IPC接続が開始されていません。");
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _writer.WriteLineAsync(IpcProtocol.Serialize(message)).WaitAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync();
        }

        _writeLock.Dispose();
    }
}
