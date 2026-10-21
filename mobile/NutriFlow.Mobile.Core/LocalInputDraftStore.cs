using System.Text.Json;

namespace NutriFlow.Mobile.Core;

public sealed class LocalInputDraftStore
{
    private const int MaximumFileBytes = 128 * 1024;
    private readonly LocalProfileStore _profiles;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalInputDraftStore(LocalProfileStore profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = profiles;
    }

    public Task<LocalInputDraft?> ReadAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        WithProfileAsync(profileId, ReadFileAsync, cancellationToken);

    public Task SaveAsync(Guid profileId, LocalInputDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Validate(draft);
        return WithProfileAsync(profileId, async (path, token) =>
        {
            await ReadFileAsync(path, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            string directory = Path.GetDirectoryName(path)!;
            DeleteInterruptedWrites(directory);
            if (string.IsNullOrWhiteSpace(draft.Text))
            {
                File.Delete(path);
                return true;
            }

            Directory.CreateDirectory(directory);
            string temporaryPath = Path.Combine(directory, $"input-draft.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, new DraftFile(1, draft), cancellationToken: token)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return true;
        }, cancellationToken);
    }

    public Task ClearAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        WithProfileAsync(profileId, async (path, token) =>
        {
            await ReadFileAsync(path, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Delete(path);
            return true;
        }, cancellationToken);

    public Task DiscardAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        WithProfileAsync(profileId, (path, token) =>
        {
            token.ThrowIfCancellationRequested();
            File.Delete(path);
            return Task.FromResult(true);
        }, cancellationToken);

    private async Task<T> WithProfileAsync<T>(Guid profileId,
        Func<string, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                await _profiles.RequireAsync(profileId, cancellationToken).ConfigureAwait(false);
                string path = Path.Combine(Path.GetDirectoryName(_profiles.GetDatabasePath(profileId))!, "input-draft.json");
                return await operation(path, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static async Task<LocalInputDraft?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (stream.Length > MaximumFileBytes)
            {
                throw new InvalidDataException("Файл ввода превышает допустимый размер. Исходный файл не изменён.");
            }

            DraftFile file = await JsonSerializer.DeserializeAsync<DraftFile>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("Файл ввода пуст. Исходный файл не изменён.");
            if (file.Version != 1 || file.Draft is null)
            {
                throw new InvalidDataException("Версия или содержимое файла ввода не поддерживается. Исходный файл не изменён.");
            }

            Validate(file.Draft);
            return file.Draft;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException("Не удалось прочитать сохранённый ввод. Исходный файл не изменён.", exception);
        }
    }

    // a process killed between writing and renaming leaves its temporary file behind
    private static void DeleteInterruptedWrites(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string temporaryPath in Directory.EnumerateFiles(directory, "input-draft.*.tmp"))
        {
            File.Delete(temporaryPath);
        }
    }

    private static void Validate(LocalInputDraft draft)
    {
        if (draft.Text is null || draft.Text.Length > MealInputText.MaximumLength ||
            draft.MealDate == default || !Enum.IsDefined(draft.Purpose) || draft.SessionId == Guid.Empty)
        {
            throw new ArgumentException("Сохранённый ввод содержит недопустимые значения.", nameof(draft));
        }

        for (int index = 0; index < draft.Text.Length; index++)
        {
            char character = draft.Text[index];
            if (char.IsHighSurrogate(character))
            {
                if (++index >= draft.Text.Length || !char.IsLowSurrogate(draft.Text[index]))
                {
                    throw new ArgumentException("Текст содержит неполный символ Unicode.", nameof(draft));
                }
            }
            else if (char.IsLowSurrogate(character))
            {
                throw new ArgumentException("Текст содержит неполный символ Unicode.", nameof(draft));
            }
        }
    }

    private sealed record DraftFile(int Version, LocalInputDraft Draft);
}
