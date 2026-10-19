namespace NutriFlow.Mobile;

internal static class BackupFileAccess
{
    public static Task<bool> SaveAsync(string archivePath, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(['/', '\\', ':']) >= 0 ||
            !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Укажите имя ZIP-файла без пути.", nameof(fileName));
        }

#if ANDROID
        return SaveAndroidAsync(archivePath, fileName);
#else
        return Task.FromException<bool>(new FeatureNotSupportedException(
            "Сохранение резервной копии пока доступно только на Android."));
#endif
    }

#if ANDROID
    private static async Task<bool> SaveAndroidAsync(string archivePath, string fileName)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("Файл резервной копии не найден.", archivePath);
        }

        Android.Net.Uri? destination = await MainThread.InvokeOnMainThreadAsync(() =>
        {
            MainActivity activity = Platform.CurrentActivity as MainActivity
                ?? throw new InvalidOperationException("Откройте приложение и повторите сохранение.");
            return activity.CreateBackupDocumentAsync(fileName);
        });
        if (destination is null)
        {
            return false;
        }

        Android.Content.ContentResolver resolver = Android.App.Application.Context.ContentResolver
            ?? throw new InvalidOperationException("Не удалось открыть системное хранилище.");
        try
        {
            await Task.Run(async () =>
            {
                await using FileStream source = new(archivePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 81920, useAsync: true);
                await using Stream output = resolver.OpenOutputStream(destination, "w")
                    ?? throw new IOException("Не удалось открыть выбранный файл для записи.");
                await source.CopyToAsync(output);
                await output.FlushAsync();
            });
            return true;
        }
        catch
        {
            try
            {
                await Task.Run(() => Android.Provider.DocumentsContract.DeleteDocument(resolver, destination));
            }
            catch (Exception)
            {
            }
            throw;
        }
    }
#endif
}
