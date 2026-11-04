using Microsoft.AspNetCore.Components;
using NutriFlow.Ui.Hosting;

namespace NutriFlow.Web.Services;

public sealed class WebAppHost(AppInfo info, BrowserPlatform platform, BrowserMedia media, PersistentDirectory files) : IAppHost
{
    public string VersionText => info.VersionText;

    public bool IsTestChannel => info.IsTest;

    public string StorageNote =>
        "Дневник хранится только в этом браузере на этом устройстве. Если очистить данные сайта или удалить приложение, записи пропадут.";

    public bool CanInstall => platform.CanInstall;

    public event Action? Changed
    {
        add => platform.InstallChanged += value;
        remove => platform.InstallChanged -= value;
    }

    public event Action? Hidden
    {
        add => platform.Hidden += value;
        remove => platform.Hidden -= value;
    }

    public Task<bool> InstallAsync() => platform.InstallAsync();

    public Task<bool> IsStoragePersistentAsync() => platform.IsPersistedAsync();

    public Task RequestPersistentStorageAsync() => platform.RequestPersistenceAsync();

    public Task RenewPersistentStorageAsync() => platform.RenewPersistenceAsync();

    // profiles.json, drafts and label photos live in memory; nothing else copies them to opfs
    public Task SaveProfileFilesAsync() => files.FlushAsync();

    public Task<bool> SaveFileAsync(string fileName, string contentType, byte[] content) =>
        platform.SaveFileAsync(fileName, contentType, content);

    public IVoiceRecorder CreateVoiceRecorder() => media.CreateVoiceRecorder();

    public Task<PickedPhoto?> ReadPhotoAsync(ElementReference input) => media.ReadPhotoAsync(input);
}
