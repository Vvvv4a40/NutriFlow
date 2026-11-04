using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Hosting;

namespace NutriFlow.Ui.Tests;

// theme.mjs of the library: remembers which themes were applied to the page
internal sealed class FakeJsRuntime : IJSRuntime, IJSObjectReference
{
    public List<string> AppliedThemes { get; } = [];

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        object? result = identifier switch
        {
            "import" => this,
            "applyTheme" => Record((string)args![0]!),
            _ => throw new InvalidOperationException($"Unexpected call: {identifier}")
        };
        return ValueTask.FromResult((TValue)result!);
    }

    private object? Record(string theme)
    {
        AppliedThemes.Add(theme);
        return null;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeAppHost : IAppHost
{
    public int SavedFiles { get; private set; }

    public int PersistenceRequests { get; private set; }

    public string VersionText => "Версия 0.2.0 · веб · тестовая";

    public bool IsTestChannel => true;

    public string StorageNote => "Дневник хранится только здесь.";

    public bool CanInstall => false;

    public event Action? Changed;

    public event Action? Hidden;

    public void Hide() => Hidden?.Invoke();

    public bool UpdateAvailable => false;

    public int AppliedUpdates { get; private set; }

    // files saved when the update started, to see that the input was written first
    public int SavedFilesBeforeUpdate { get; private set; }

    public Task ApplyUpdateAsync()
    {
        AppliedUpdates++;
        SavedFilesBeforeUpdate = SavedFiles;
        return Task.CompletedTask;
    }

    public Func<Task>? SaveBeforeLeaving { get; set; }

    public Task<bool> InstallAsync()
    {
        Changed?.Invoke();
        return Task.FromResult(false);
    }

    public Task<bool> IsStoragePersistentAsync() => Task.FromResult(false);

    public Task RequestPersistentStorageAsync()
    {
        PersistenceRequests++;
        return Task.CompletedTask;
    }

    public int PersistenceRenewals { get; private set; }

    public bool RenewalFails { get; set; }

    public Task RenewPersistentStorageAsync()
    {
        PersistenceRenewals++;
        return RenewalFails ? Task.FromException(new InvalidOperationException("blocked")) : Task.CompletedTask;
    }

    public bool SaveFails { get; set; }

    public Task SaveProfileFilesAsync()
    {
        SavedFiles++;
        return SaveFails ? Task.FromException(new IOException("quota")) : Task.CompletedTask;
    }

    public List<(string Name, string ContentType, byte[] Content)> HandedFiles { get; } = [];

    public Task<bool> SaveFileAsync(string fileName, string contentType, byte[] content)
    {
        HandedFiles.Add((fileName, contentType, content));
        return Task.FromResult(true);
    }

    public FakeVoiceRecorder Recorder { get; } = new();

    public IVoiceRecorder CreateVoiceRecorder() => Recorder;

    public PickedPhoto? Photo { get; set; }

    public Task<PickedPhoto?> ReadPhotoAsync(ElementReference input) => Task.FromResult(Photo);
}

// speech-capture.mjs without a browser: the test plays the events of a recording
internal sealed class FakeVoiceRecorder : IVoiceRecorder
{
    private Func<RecordedAudio, CancellationToken, Task<string>>? _transcribe;

    public int Starts { get; private set; }

    public int Stops { get; private set; }

    public bool Disposed { get; private set; }

    public event Action<VoicePhase>? PhaseChanged;

    public event Action<string>? Transcribed;

    public event Action<string>? Failed;

    public Task StartAsync(Func<RecordedAudio, CancellationToken, Task<string>> transcribe)
    {
        _transcribe = transcribe;
        Starts++;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Stops++;
        return Task.CompletedTask;
    }

    public void Fail(string message)
    {
        PhaseChanged?.Invoke(VoicePhase.Idle);
        Failed?.Invoke(message);
    }

    public void Begin() => PhaseChanged?.Invoke(VoicePhase.Recording);

    // what the script does once the recording ended: the delegate, then the text or the failure
    public async Task FinishAsync(RecordedAudio audio)
    {
        PhaseChanged?.Invoke(VoicePhase.Transcribing);
        try
        {
            string text = await _transcribe!(audio, CancellationToken.None);
            PhaseChanged?.Invoke(VoicePhase.Idle);
            Transcribed?.Invoke(text);
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeProfileSettings : IProfileSettings
{
    public Dictionary<Guid, string> Themes { get; } = [];

    public Dictionary<Guid, Guid> Sessions { get; } = [];

    public string? GroqKey { get; set; }

    public Task<string> GetThemeAsync(Guid profileId) => Task.FromResult(Themes.GetValueOrDefault(profileId, "terracotta"));

    public Task SaveThemeAsync(Guid profileId, string theme)
    {
        Themes[profileId] = theme;
        return Task.CompletedTask;
    }

    public Task<bool> HasGroqAsync(Guid profileId) => Task.FromResult(GroqKey is not null);

    public Task<string> GetTextModelAsync(Guid profileId) => Task.FromResult("text");

    public Task<string> GetVisionModelAsync(Guid profileId) => Task.FromResult("vision");

    public Task<string> GetSpeechModelAsync(Guid profileId) => Task.FromResult("speech");

    public Task SaveGroqAsync(Guid profileId, string? apiKey, string textModel, string visionModel, string speechModel) =>
        Task.CompletedTask;

    public Task RemoveGroqAsync(Guid profileId) => Task.CompletedTask;

    public Task<GroqSettings> GetGroqAsync(Guid profileId) => GroqKey is null
        ? throw new InvalidOperationException("Добавьте личный ключ Groq в разделе «Настройки».")
        : Task.FromResult(new GroqSettings(GroqKey));

    public Task<Guid?> GetSessionIdAsync(Guid profileId) =>
        Task.FromResult(Sessions.TryGetValue(profileId, out Guid id) ? id : (Guid?)null);

    public Task SaveSessionIdAsync(Guid profileId, Guid sessionId)
    {
        Sessions[profileId] = sessionId;
        return Task.CompletedTask;
    }

    public Task ClearSessionIdAsync(Guid profileId)
    {
        Sessions.Remove(profileId);
        return Task.CompletedTask;
    }
}
