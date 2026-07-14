using RTSPWallpaperStudio.Core.Services;

namespace RTSPWallpaperStudio.UnitTests;

public sealed class RtspUrlServiceTests
{
    [Fact]
    public void Normalize_RemovesCredentialsFromStoredUrl()
    {
        var valid = RtspUrlService.TryNormalize("rtsp://camera:secret@example.test:8554/live?token=abc", out var parts, out _);

        Assert.True(valid);
        Assert.Equal("camera", parts.UserName);
        Assert.Equal("secret", parts.Password);
        Assert.DoesNotContain("secret", parts.Url);
        Assert.DoesNotContain("camera@", parts.Url);
    }

    [Theory]
    [InlineData("http://example.test/live")]
    [InlineData("file:///video.mp4")]
    [InlineData("not-a-url")]
    public void Normalize_RejectsUnsupportedInput(string value)
    {
        Assert.False(RtspUrlService.TryNormalize(value, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void SanitizeForLog_RedactsCredentialsAndQuery()
    {
        var sanitized = RtspUrlService.SanitizeForLog("rtsp://camera:secret@example.test:8554/live?token=abc");

        Assert.Contains("camera:***@", sanitized);
        Assert.DoesNotContain("secret", sanitized);
        Assert.DoesNotContain("token=abc", sanitized);
    }
}
