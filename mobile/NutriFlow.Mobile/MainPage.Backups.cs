using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private Task ExportBackupAsync(Guid profileId) => RunAsync(async () =>
    {
        await FlushInputDraftAsync();
        string path = Path.Combine(FileSystem.CacheDirectory, $"nutriflow-backup-{Guid.NewGuid():N}.zip");
        try
        {
            try
            {
                await new LocalProfileBackup(_profiles).ExportAsync(profileId, path, ProfileSettings.SessionId(profileId));
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidOperationException("Не удалось создать копию: данные повреждены, не поддерживаются или превышают допустимый размер. Исходные файлы не изменены.", exception);
            }
            if (await BackupFileAccess.SaveAsync(path, $"NutriFlow-{DateTime.Now:yyyyMMdd-HHmmss}.zip"))
            {
                await DisplayAlertAsync("Копия сохранена", "Сохранён выбранный профиль без ключа Groq. Храните архив в надёжном месте.", "Готово");
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    });

    private Task RestoreBackupPageAsync()
    {
        FormPage page = new("Восстановление");
        page.Fields.Children.Add(Ui.Text("Копия будет восстановлена в новый профиль. Существующие данные не изменятся."));
        page.Fields.Children.Add(Ui.Text("Имя нового профиля"));
        Entry name = Ui.Field("Например, Восстановленный");
        name.AutomationId = "backup-profile-name";
        name.MaxLength = 80;
        page.Fields.Children.Add(name);
        page.Fields.Children.Add(Ui.Text("Ключ Groq нужно добавить заново.", true));
        Button restore = Ui.Action("Выбрать ZIP и восстановить", () => page.RunAsync(async () =>
        {
            string profileName = Ui.Name(name, "Имя профиля");
            FileResult? file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Копия NutriFlow",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.Android] = ["application/zip", "application/x-zip-compressed"],
                    [DevicePlatform.iOS] = ["public.zip-archive"],
                    [DevicePlatform.MacCatalyst] = ["public.zip-archive"],
                    [DevicePlatform.WinUI] = [".zip"]
                })
            });
            if (file is null)
            {
                return;
            }

            LocalProfileBackupResult result;
            await FlushInputDraftAsync();
            try
            {
                await using Stream stream = await file.OpenReadAsync();
                result = await new LocalProfileBackup(_profiles).ImportAsync(stream, profileName);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidOperationException("Копия повреждена или не поддерживается. Существующие данные не изменены.", exception);
            }

            if (result.ActiveSessionId is Guid sessionId)
            {
                ProfileSettings.SaveSession(result.Profile.Id, sessionId);
            }

            await _profiles.SelectAsync(result.Profile.Id);
            await Navigation.PopAsync();
            await DisplayAlertAsync("Восстановлено", $"Создан профиль «{result.Profile.Name}». Исходные профили сохранены.", "Готово");
        }));
        restore.AutomationId = "backup-import";
        page.Fields.Children.Add(restore);
        return Navigation.PushAsync(page);
    }
}
