namespace NutriFlow.Ui.Pages;

// texts of the backup (MainPage.Backups.cs and the data block of MainPage.Settings.cs on android)
public static class BackupText
{
    public const string Note =
        "Копия — ZIP с дневником, продуктами, блюдами и фото. С ней дневник переносят на другое устройство. Она не зашифрована, ключа Groq в ней нет.";
    public const string ExportFailed =
        "Не удалось создать копию: данные профиля не прошли проверку или занимают больше 512 МБ. Дневник при этом не изменился.";
    public const string RestoreFailed = "Это не копия NutriFlow или файл повреждён. Выберите другой файл — существующие профили не изменились.";
    public const string ReadyTitle = "Копия готова";
    public const string Ready = "Сохраните файл вне этого браузера — в «Файлы», облачный диск или на другое устройство.";
    public const string SavedTitle = "Копия сохранена";
    public const string Saved = "Храните файл в надёжном месте: без него дневник не перенести.";
    public const string ChooseFile = "Выберите файл копии — ZIP, сохранённый в NutriFlow.";
    public const string FileTooLarge = "Файл больше 256 МБ — это не копия NutriFlow. Выберите другой файл.";
    public const string EnterName = "Введите имя нового профиля.";
    public const string DuplicateName = "Профиль с таким именем уже есть. Введите другое имя.";

    // a restore from the welcome screen has no other profiles to reassure about
    public static string Restored(string profileName, bool hasOtherProfiles) =>
        $"Создан профиль «{profileName}».{(hasOtherProfiles ? " Остальные профили не изменились." : null)} " +
        "Ключ Groq в копию не входит — добавьте его в настройках, чтобы разбирать описания.";
}
