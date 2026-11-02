using NutriFlow.Ui.Hosting;

namespace NutriFlow.Web.Services;

public sealed class WebAppHost(AppInfo info, BrowserPlatform platform, PersistentDirectory files) : IAppHost
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

    public Task<bool> InstallAsync() => platform.InstallAsync();

    public Task<bool> IsStoragePersistentAsync() => platform.IsPersistedAsync();

    public Task RequestPersistentStorageAsync() => platform.RequestPersistenceAsync();

    // profiles.json, drafts and label photos live in memory; nothing else copies them to opfs
    public Task SaveProfileFilesAsync() => files.FlushAsync();
}
