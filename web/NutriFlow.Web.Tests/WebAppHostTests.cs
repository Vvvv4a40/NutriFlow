using NutriFlow.Ui.Hosting;
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
    public async Task UpdateBannerFollowsTheWaitingWorker()
    {
        BrowserPlatform platform = new(_js);
        WebAppHost host = Create(platform);
        int changes = 0;
        host.Changed += () => changes++;

        await platform.StartAsync();
        Assert.False(host.UpdateAvailable);

        platform.OnUpdateAvailabilityChanged(true);
        platform.OnUpdateAvailabilityChanged(true);
        Assert.True(host.UpdateAvailable);
        Assert.Equal(1, changes);

        await host.ApplyUpdateAsync();
        Assert.Equal(1, _js.Platform.AppliedUpdates);
    }

    [Fact]
    public async Task AWorkerThatWaitedSinceThePreviousStartShowsTheBannerAtOnce()
    {
        _js.Platform.UpdateWaiting = true;
        BrowserPlatform platform = new(_js);

        await platform.StartAsync();

        Assert.True(Create(platform).UpdateAvailable);
    }

    [Fact]
    public async Task TabGivesWayToTheInstalledAppOnlyAfterTheInputIsSaved()
    {
        BrowserPlatform platform = new(_js);
        WebAppHost host = Create(platform);
        await platform.StartAsync();
        Assert.NotNull(_js.Platform.InstalledAppWatcher);
        Assert.True(await platform.OnLeaveRequested());

        bool saved = false;
        host.SaveBeforeLeaving = () =>
        {
            saved = true;
            return Task.CompletedTask;
        };
        Assert.True(await platform.OnLeaveRequested());
        Assert.True(saved);

        host.SaveBeforeLeaving = () => Task.FromException(new InvalidOperationException("quota"));
        Assert.False(await platform.OnLeaveRequested());
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
        WebAppHost host = new(new AppInfo(new Version(0, 2, 0), "test"), new BrowserPlatform(_js), new BrowserMedia(_js), files);

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

    [Fact]
    public async Task StartRenewsTheRequestThroughTheBrowserRule()
    {
        await Create(new BrowserPlatform(_js)).RenewPersistentStorageAsync();

        Assert.Equal(1, _js.Platform.PersistenceRenewals);
        Assert.Equal(0, _js.Platform.PersistenceRequests);
    }

    [Fact]
    public async Task PhotoComesFromTheModuleAndAnUnreadableOneIsReportedAsData()
    {
        WebAppHost host = Create(new BrowserPlatform(_js));
        _js.Media.Photo = new PickedPhoto([1, 2, 3], "image/jpeg");

        PickedPhoto? photo = await host.ReadPhotoAsync(default);
        Assert.Equal("image/jpeg", photo!.MediaType);

        _js.Media.Photo = null;
        Assert.Null(await host.ReadPhotoAsync(default));

        _js.Media.PhotoFails = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => host.ReadPhotoAsync(default));
    }

    [Fact]
    public async Task BarcodeComesFromTheModuleAndAnUnreadablePhotoIsReportedAsData()
    {
        WebAppHost host = Create(new BrowserPlatform(_js));
        _js.Media.Barcode = new PickedBarcode("3017620422003");

        Assert.Equal("3017620422003", (await host.ReadBarcodeAsync(default))!.Digits);

        _js.Media.Barcode = null;
        Assert.Null(await host.ReadBarcodeAsync(default));

        _js.Media.PhotoFails = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => host.ReadBarcodeAsync(default));
    }

    [Fact]
    public async Task VoiceRecorderWorksThroughTheCaptureOfTheModule()
    {
        await using IVoiceRecorder recorder = Create(new BrowserPlatform(_js)).CreateVoiceRecorder();
        List<VoicePhase> phases = [];
        List<string> texts = [];
        recorder.PhaseChanged += phases.Add;
        recorder.Transcribed += texts.Add;

        await recorder.StartAsync((audio, _) => Task.FromResult($"{audio.MediaType}:{audio.Content.Length}"));
        await recorder.StopAsync();

        Assert.Equal(["start", "stop"], _js.Media.Capture.Calls);
        Assert.Equal(WebVoiceRecorder.MaximumMilliseconds, _js.Media.MaximumMilliseconds);

        WebVoiceRecorder web = (WebVoiceRecorder)recorder;
        web.OnPhaseChanged("recording");
        web.OnPhaseChanged("transcribing");
        Assert.Equal("audio/webm:2", await web.OnRecorded([1, 2], "audio/webm"));
        web.OnTranscribed("гречка");

        Assert.Equal([VoicePhase.Recording, VoicePhase.Transcribing], phases);
        Assert.Equal(["гречка"], texts);
    }

    [Fact]
    public async Task FailedTranscriptionReachesTheScreenWithItsOwnMessage()
    {
        await using IVoiceRecorder recorder = Create(new BrowserPlatform(_js)).CreateVoiceRecorder();
        List<string> failures = [];
        recorder.Failed += failures.Add;
        await recorder.StartAsync((_, _) => throw new InvalidOperationException("Не удалось связаться с Groq."));
        WebVoiceRecorder web = (WebVoiceRecorder)recorder;

        // the script gets an empty text and reports its own message about silence
        Assert.Equal("", await web.OnRecorded([1], "audio/webm"));
        web.OnFailed("Речь не распознана.");
        // a later failure of the microphone has nothing to do with that request
        web.OnFailed("Нет доступа к микрофону.");

        Assert.Equal(["Не удалось связаться с Groq.", "Нет доступа к микрофону."], failures);
    }

    [Fact]
    public async Task ClosingTheFormReleasesTheMicrophone()
    {
        IVoiceRecorder recorder = Create(new BrowserPlatform(_js)).CreateVoiceRecorder();
        await recorder.StartAsync((_, _) => Task.FromResult("текст"));

        await recorder.DisposeAsync();

        Assert.Equal(["start", "dispose"], _js.Media.Capture.Calls);
    }

    private WebAppHost Create(BrowserPlatform platform, string channel = "test") =>
        new(new AppInfo(new Version(0, 2, 0), channel), platform, new BrowserMedia(_js), new PersistentDirectory(new OpfsFiles(_js), _root));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
