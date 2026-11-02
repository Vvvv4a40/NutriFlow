using Microsoft.JSInterop;
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

    public Task SaveProfileFilesAsync()
    {
        SavedFiles++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeProfileSettings : IProfileSettings
{
    public Dictionary<Guid, string> Themes { get; } = [];

    public Task<string> GetThemeAsync(Guid profileId) => Task.FromResult(Themes.GetValueOrDefault(profileId, "terracotta"));

    public Task SaveThemeAsync(Guid profileId, string theme)
    {
        Themes[profileId] = theme;
        return Task.CompletedTask;
    }

    public Task<bool> HasGroqAsync(Guid profileId) => Task.FromResult(false);

    public Task<string> GetTextModelAsync(Guid profileId) => Task.FromResult("text");

    public Task<string> GetVisionModelAsync(Guid profileId) => Task.FromResult("vision");

    public Task<string> GetSpeechModelAsync(Guid profileId) => Task.FromResult("speech");

    public Task SaveGroqAsync(Guid profileId, string? apiKey, string textModel, string visionModel, string speechModel) =>
        Task.CompletedTask;

    public Task RemoveGroqAsync(Guid profileId) => Task.CompletedTask;
}
