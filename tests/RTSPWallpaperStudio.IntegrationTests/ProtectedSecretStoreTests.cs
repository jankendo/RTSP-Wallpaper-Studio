using RTSPWallpaperStudio.Infrastructure.Security;

namespace RTSPWallpaperStudio.IntegrationTests;

public sealed class ProtectedSecretStoreTests
{
    [Fact]
    public void Secret_RoundTripsForCurrentUser()
    {
        var store = new ProtectedSecretStore();
        var protectedText = store.Protect("camera-password");

        Assert.NotEqual("camera-password", protectedText);
        Assert.Equal("camera-password", store.Unprotect(protectedText));
        Assert.Equal(string.Empty, store.Unprotect("not-base64"));
    }
}
