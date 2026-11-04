using Microsoft.AspNetCore.Components;

namespace NutriFlow.Ui.Hosting;

// what the screens need from the app that hosts the library: the browser today, a maui BlazorWebView after the port
public interface IAppHost
{
    string VersionText { get; }

    bool IsTestChannel { get; }

    // where the diary lives on this platform and what erases it
    string StorageNote { get; }

    bool CanInstall { get; }

    // raised when installation or a new version becomes available or unavailable
    event Action? Changed;

    // true when the user accepted the installation
    Task<bool> InstallAsync();

    // a new version is ready and waits for the user
    bool UpdateAvailable { get; }

    // restarts the app in the new version; unsaved input has to be written before this call
    Task ApplyUpdateAsync();

    // what has to be written when the platform closes the app on its own, for example in favour of the installed app
    Func<Task>? SaveBeforeLeaving { set; }

    Task<bool> IsStoragePersistentAsync();

    Task RequestPersistentStorageAsync();

    // asks again at the start of the app, where the platform can do it without bothering the user
    Task RenewPersistentStorageAsync();

    // the app went to the background or is closing: unsaved input has to be written now
    event Action? Hidden;

    // profile files changed in memory reach durable storage only here; a host that writes files directly does nothing
    Task SaveProfileFilesAsync();

    // hands a file to the user (download, share sheet, system save dialog); false when they cancelled.
    // the browser needs the tap that asked for it, so nothing slow may run between the tap and this call
    Task<bool> SaveFileAsync(string fileName, string contentType, byte[] content);

    // a recorder for one voice form; the form disposes it, which also releases the microphone
    IVoiceRecorder CreateVoiceRecorder();

    // the photo chosen in this file input, in a format the label reader accepts; null when nothing is chosen,
    // InvalidDataException when the platform cannot read it
    Task<PickedPhoto?> ReadPhotoAsync(ElementReference input);
}
