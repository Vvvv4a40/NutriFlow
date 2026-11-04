using NutriFlow.Web.Services;

namespace NutriFlow.Web.Tests;

public sealed class WebAppHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nutriflow-web-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeJsRuntime _js = new();

    [Fact]
    public async Task InstallButtonFollowsTheBrowserPrompt()
    {
        _js.Platform.InstallAvailable = true;
        BrowserPlatform platform = new(_js);
        WebAppHost host = Create(platform);
        int changes = 0;
        host.Changed += () => changes++;

        await platform.StartAsync();
        Assert.True(host.CanInstall);
        Assert.Same(platform, Assert.IsAssignableFrom<Microsoft.JSInterop.DotNetObjectReference<BrowserPlatform>>(_js.Platform.Watcher).Value);

        // the prompt was used, then chrome reports an installed app; a repeated report changes nothing
        platform.OnInstallAvailabilityChanged(false);
        platform.OnInstallAvailabilityChanged(false);

        Assert.False(host.CanInstall);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task HiddenPageIsReportedToTheScreens()
    {
        BrowserPlatform platform = new(_js);
        WebAppHost host = Create(platform);
        int hidden = 0;
        host.Hidden += () => hidden++;

        await platform.StartAsync();
        platform.OnHidden();

        Assert.Same(platform, Assert.IsAssignableFrom<Microsoft.JSInterop.DotNetObjectReference<BrowserPlatform>>(_js.Platform.VisibilityWatcher).Value);
        Assert.Equal(1, hidden);
    }

    [Theory]
    [InlineData("accepted", true)]
    [InlineData("dismissed", false)]
    public async Task InstallReportsWhetherTheUserAccepted(string outcome, bool accepted)
    {
        _js.Platform.InstallAvailable = true;
        _js.Platform.InstallOutcome = outcome;

        Assert.Equal(accepted, await Create(new BrowserPlatform(_js)).InstallAsync());
    }

    [Fact]
    public async Task InstallWithoutAPromptIsNotAccepted()
    {
        Assert.False(await Create(new BrowserPlatform(_js)).InstallAsync());
    }

    [Fact]
    public async Task StorageAndVersionComeFromTheBrowserAndTheChannel()
    {
        _js.Platform.Persisted = true;
        WebAppHost host = Create(new BrowserPlatform(_js), "production");

        await host.RequestPersistentStorageAsync();

        Assert.True(await host.IsStoragePersistentAsync());
        Assert.Equal(1, _js.Platform.PersistenceRequests);
        Assert.False(host.IsTestChannel);
        Assert.Equal("Версия 0.2.0 · веб", host.VersionText);
        Assert.StartsWith("Дневник хранится только в этом браузере на этом устройстве.", host.StorageNote);
    }

    [Fact]
    public async Task SavingProfileFilesCopiesThemToTheBrowserStorage()
    {
        PersistentDirectory files = new(new OpfsFiles(_js), _root);
        await files.RestoreAsync();
        await File.WriteAllTextAsync(Path.Combine(_root, "profiles.json"), "{}");
        WebAppHost host = new(new AppInfo(new Version(0, 2, 0), "test"), new BrowserPlatform(_js), files);

        await host.SaveProfileFilesAsync();

        Assert.Equal(["nutriflow/files/profiles.json"], _js.Storage.Writes);
    }

    [Theory]
    [InlineData("saved", true)]
    [InlineData("cancelled", false)]
    public async Task SavingAFileHandsItToTheBrowser(string outcome, bool saved)
    {
        _js.Platform.SaveOutcome = outcome;

        Assert.Equal(saved, await Create(new BrowserPlatform(_js)).SaveFileAsync("NutriFlow.zip", "application/zip", [1, 2]));

        (string name, string contentType, byte[] content) = Assert.Single(_js.Platform.SavedFiles);
        Assert.Equal(("NutriFlow.zip", "application/zip"), (name, contentType));
        Assert.Equal([1, 2], content);
    }

    private WebAppHost Create(BrowserPlatform platform, string channel = "test") =>
        new(new AppInfo(new Version(0, 2, 0), channel), platform, new PersistentDirectory(new OpfsFiles(_js), _root));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
