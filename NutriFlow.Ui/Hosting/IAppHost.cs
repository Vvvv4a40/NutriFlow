namespace NutriFlow.Ui.Hosting;

// what the screens need from the app that hosts the library: the browser today, a maui BlazorWebView after the port
public interface IAppHost
{
    string VersionText { get; }

    bool IsTestChannel { get; }

    // where the diary lives on this platform and what erases it
    string StorageNote { get; }

    bool CanInstall { get; }

    // raised when installation becomes available or unavailable
    event Action? Changed;

    // true when the user accepted the installation
    Task<bool> InstallAsync();

    Task<bool> IsStoragePersistentAsync();

    Task RequestPersistentStorageAsync();

    // profile files changed in memory reach durable storage only here; a host that writes files directly does nothing
    Task SaveProfileFilesAsync();
}
