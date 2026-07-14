using RTSPWallpaperStudio.Infrastructure.Startup;

namespace RTSPWallpaperStudio.IntegrationTests;

public sealed class StartupRegistrationServiceTests
{
    [Fact]
    public void BuildLaunchCommand_QuotesPortablePathAndAddsStartupArgument()
    {
        var path = Path.Combine("C:\\Program Files", "RTSP Wallpaper Studio", "RTSPWallpaperStudio.App.exe");

        var command = StartupRegistrationService.BuildLaunchCommand(path);

        Assert.Equal($"\"{Path.GetFullPath(path)}\" --startup", command);
    }

    [Fact]
    public void BuildLaunchCommand_DoesNotAddTrailingSpaceWithoutArguments()
    {
        var path = Path.Combine("C:\\RTSP", "RTSPWallpaperStudio.App.exe");

        var command = StartupRegistrationService.BuildLaunchCommand(path, string.Empty);

        Assert.Equal($"\"{Path.GetFullPath(path)}\"", command);
    }
}
