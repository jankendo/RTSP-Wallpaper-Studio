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
}
