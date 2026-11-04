using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Capture;
using NutriFlow.Ui.Hosting;
using NutriFlow.Ui.Pages;

namespace NutriFlow.Ui.Profiles;

// the zip backup of a profile (MainPage.Backups.cs): LocalProfileBackup keeps the format and the checks of the phone,
// the host decides where the databases live
public sealed class ProfileBackups(LocalProfileBackup backup, ProfileState profiles, IProfileSettings settings, CaptureState capture)
{
    public const string ContentType = "application/zip";
    // the archive limit of LocalProfileBackup
    public const long MaximumFileBytes = 256L * 1024 * 1024;

    public static string FileName(DateTime now) => $"NutriFlow-{now:yyyyMMdd-HHmmss}.zip";

    // the typed text is written first, so the copy has it
    public async Task<byte[]> CreateAsync(Guid profileId)
    {
        await capture.FlushAsync();
        string path = Path.Combine(Path.GetTempPath(), $"nutriflow-backup-{Guid.NewGuid():N}.zip");
        try
        {
            try
            {
                await backup.ExportAsync(profileId, path, await settings.GetSessionIdAsync(profileId));
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidOperationException(BackupText.ExportFailed, exception);
            }

            return await File.ReadAllBytesAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // always a new profile, which becomes the current one with its unconfirmed calculation; the others stay as they were.
    // a damaged or foreign file is InvalidDataException with the text for the file field
    public async Task<LocalProfile> RestoreAsync(Stream source, string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        await capture.FlushAsync();
        return await profiles.AddAsync(async () =>
        {
            LocalProfileBackupResult result;
            try
            {
                result = await backup.ImportAsync(source, name);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(BackupText.RestoreFailed, exception);
            }

            if (result.ActiveSessionId is Guid sessionId)
            {
                await settings.SaveSessionIdAsync(result.Profile.Id, sessionId);
            }

            return result.Profile;
        });
    }
}
