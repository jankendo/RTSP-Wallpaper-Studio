using RTSPWallpaperStudio.Core.Domain;
using RTSPWallpaperStudio.Core.Services;

namespace RTSPWallpaperStudio.UnitTests;

public sealed class IpcProtocolTests
{
    [Fact]
    public void Protocol_RoundTripsEnvelope()
    {
        var text = IpcProtocol.Serialize(new IpcEnvelope("start", "{}", "request-1"));

        Assert.True(IpcProtocol.TryDeserialize(text, out var envelope));
        Assert.Equal("start", envelope!.Command);
        Assert.Equal("request-1", envelope.RequestId);
    }

    [Fact]
    public void Protocol_RejectsOversizedMessage()
    {
        var text = new string('x', IpcProtocol.MaxMessageLength + 1);

        Assert.False(IpcProtocol.TryDeserialize(text, out _));
    }

    [Fact]
    public void Protocol_RoundTripsDuplexRendererEvent()
    {
        var rendererEvent = new RendererEvent(
            "renderer-1",
            RendererEventType.WallpaperVisible,
            DateTimeOffset.UnixEpoch,
            Metrics: new RendererMetrics(42, (nint)0x100, (nint)0x200, (nint)0x200, 1, "Playing", null, 1920, 1080, 0,
                DesktopLayoutStrategy.LegacyWorkerW, new RectD(-1920, 0, 1920, 1080), new RectD(-1920, 0, 1920, 1080)));
        var message = new IpcMessage(IpcMessageKind.Event, nameof(RendererEventType.WallpaperVisible),
            IpcProtocol.SerializePayload(rendererEvent), RendererId: rendererEvent.RendererId);

        var text = IpcProtocol.Serialize(message);

        Assert.True(IpcProtocol.TryDeserializeMessage(text, out var parsed));
        Assert.Equal(IpcMessageKind.Event, parsed!.Kind);
        Assert.Equal("renderer-1", parsed.RendererId);
        Assert.True(IpcProtocol.DeserializePayload<RendererEvent>(parsed.Payload!)!.Metrics!.RendererRect.X < 0);
    }
}
