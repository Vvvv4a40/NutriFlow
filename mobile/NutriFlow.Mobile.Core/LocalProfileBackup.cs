using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NutriFlow.Domain;
using NutriFlow.Infrastructure;
using NutriFlow.Infrastructure.Backups;
using NutriFlow.Infrastructure.LabelPhotos;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Mobile.Core;

public sealed class LocalProfileBackup
{
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const int MaximumEntries = 4096;
    private const string ProfileManifestName = "profile.json";
    private static readonly string[] OwnerTables =
        ["Products", "ProductAliases", "MealSessions", "DailyGoals", "SavedDishes", "LabelPhotos"];
    private readonly LocalProfileStore _profiles;

    public LocalProfileBackup(LocalProfileStore profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = profiles;
    }

    public Task ExportAsync(Guid profileId, string destinationPath, Guid? activeSessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        return Task.Run(() => ExportCoreAsync(profileId, Path.GetFullPath(destinationPath), activeSessionId,
            cancellationToken), cancellationToken);
    }

    public Task<LocalProfileBackupResult> ImportAsync(Stream source, string restoredName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(restoredName);
        return Task.Run(() => ImportCoreAsync(source, restoredName, cancellationToken), cancellationToken);
    }

    private async Task ExportCoreAsync(Guid profileId, string destinationPath, Guid? activeSessionId,
        CancellationToken cancellationToken)
    {
        LocalProfile profile = await _profiles.RequireAsync(profileId, cancellationToken);
        string profileDirectory = Path.GetDirectoryName(_profiles.GetDatabasePath(profileId))!;
        if (destinationPath.StartsWith(profileDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            destinationPath.Equals(profileDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Копию нельзя сохранять внутри исходного профиля.", nameof(destinationPath));
        }
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException("Файл резервной копии уже существует и не будет перезаписан.");
        }

        string working = _profiles.CreateBackupWorkingDirectory();
        string archivePath = Path.Combine(working, "backup.zip");
        try
        {
            string snapshotDirectory = Path.Combine(working, "snapshot");
            Directory.CreateDirectory(snapshotDirectory);
            string snapshotPath = Path.Combine(snapshotDirectory, "nutriflow.db");
            string sourcePath = _profiles.GetDatabasePath(profileId);
            if (!File.Exists(sourcePath))
            {
                await using NutriFlowDbContext initialized = await LocalProfileDatabase.OpenAsync(sourcePath, profileId, cancellationToken);
            }
            if (new FileInfo(sourcePath).Length > MaximumExpandedBytes)
            {
                throw new InvalidDataException("База профиля превышает предел резервной копии 512 МБ.");
            }
            await using (SqliteConnection source = OpenDatabase(sourcePath, SqliteOpenMode.ReadOnly))
            await using (SqliteConnection snapshot = OpenDatabase(snapshotPath, SqliteOpenMode.ReadWriteCreate))
            {
                await source.OpenAsync(cancellationToken);
                await snapshot.OpenAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                source.BackupDatabase(snapshot);
            }
            long snapshotLength = new FileInfo(snapshotPath).Length;
            if (snapshotLength > MaximumExpandedBytes)
            {
                throw new InvalidDataException("База профиля превышает предел резервной копии 512 МБ.");
            }

            string snapshotPhotos = Path.Combine(snapshotDirectory, "label-photos");
            Directory.CreateDirectory(snapshotPhotos);
            string photosDirectory = _profiles.GetPhotoDirectory(profileId);
            if (Directory.Exists(photosDirectory))
            {
                int photoCount = 0;
                foreach (string photo in Directory.EnumerateFileSystemEntries(photosDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++photoCount > MaximumEntries - 3)
                    {
                        throw new InvalidDataException("Слишком много фотографий для одной резервной копии.");
                    }
                    string name = Path.GetFileName(photo);
                    ValidatePhotoName(name);
                    if ((File.GetAttributes(photo) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    {
                        throw new InvalidDataException("Каталог фотографий содержит неподдерживаемый файл.");
                    }
                    snapshotLength = checked(snapshotLength + new FileInfo(photo).Length);
                    if (snapshotLength > MaximumExpandedBytes)
                    {
                        throw new InvalidDataException("Данные профиля превышают предел резервной копии 512 МБ.");
                    }
                    await CopyBoundedAsync(File.OpenRead(photo), Path.Combine(snapshotPhotos, name),
                        LabelPhotoValidator.MaximumFileSizeInBytes, cancellationToken, disposeSource: true);
                }
            }

            await ValidateDatabaseAsync(snapshotPath, profileId, activeSessionId, cancellationToken);
            string dataDirectory = Path.Combine(working, "data");
            await DataBackup.CreateAsync(snapshotPath, snapshotPhotos, dataDirectory, cancellationToken);
            ProfileManifest manifest = new(1, profile.Id, profile.Name, DateTimeOffset.UtcNow, activeSessionId);
            await WriteJsonAsync(Path.Combine(working, ProfileManifestName), manifest, cancellationToken);
            using (FileStream output = new(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (ZipArchive archive = new(output, ZipArchiveMode.Create))
            {
                string[] paths = Directory.GetFiles(dataDirectory, "*", SearchOption.AllDirectories)
                    .Prepend(Path.Combine(working, ProfileManifestName)).ToArray();
                if (paths.Length > MaximumEntries)
                {
                    throw new InvalidDataException("Слишком много файлов для одной резервной копии.");
                }
                long totalLength = 0;
                foreach (string path in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    totalLength = checked(totalLength + new FileInfo(path).Length);
                    if (totalLength > MaximumExpandedBytes)
                    {
                        throw new InvalidDataException("Данные профиля превышают предел резервной копии 512 МБ.");
                    }
                    string relative = Path.GetRelativePath(working, path).Replace(Path.DirectorySeparatorChar, '/');
                    ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                    await using Stream target = entry.Open();
                    await using FileStream input = File.OpenRead(path);
                    await input.CopyToAsync(target, cancellationToken);
                }
            }
            if (new FileInfo(archivePath).Length > MaximumArchiveBytes)
            {
                throw new InvalidDataException("Архив превышает предел 256 МБ.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Move(archivePath, destinationPath);
        }
        finally
        {
            TryDeleteWorkingDirectory(working);
        }
    }

    private async Task<LocalProfileBackupResult> ImportCoreAsync(Stream source, string restoredName,
        CancellationToken cancellationToken)
    {
        string working = _profiles.CreateBackupWorkingDirectory();
        try
        {
            string archivePath = Path.Combine(working, "backup.zip");
            await CopyBoundedAsync(source, archivePath, MaximumArchiveBytes, cancellationToken);
            string extracted = Path.Combine(working, "extracted");
            Directory.CreateDirectory(extracted);
            using (FileStream input = File.OpenRead(archivePath))
            using (ZipArchive archive = new(input, ZipArchiveMode.Read))
            {
                if (archive.Entries.Count is < 3 or > MaximumEntries)
                {
                    throw new InvalidDataException("Неверное число файлов в резервной копии.");
                }
                HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
                long totalLength = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = entry.FullName;
                    ValidateArchiveName(name);
                    if (!names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                        (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException("Архив содержит повторяющиеся файлы или ссылки.");
                    }
                    long entryLimit = name switch
                    {
                        ProfileManifestName => 16 * 1024,
                        "data/manifest.json" => 2 * 1024 * 1024,
                        "data/nutriflow.db" => MaximumExpandedBytes,
                        _ => LabelPhotoValidator.MaximumFileSizeInBytes
                    };
                    totalLength = checked(totalLength + entry.Length);
                    if (entry.Length < 0 || entry.Length > entryLimit || totalLength > MaximumExpandedBytes)
                    {
                        throw new InvalidDataException("Резервная копия превышает допустимый размер.");
                    }
                    string destination = Path.Combine(extracted, name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await using Stream entryStream = entry.Open();
                    long copied = await CopyBoundedAsync(entryStream, destination, Math.Min(entry.Length, entryLimit), cancellationToken);
                    if (copied != entry.Length)
                    {
                        throw new InvalidDataException("Размер файла в архиве не совпадает с объявленным.");
                    }
                }
                if (!names.Contains(ProfileManifestName) || !names.Contains("data/manifest.json") || !names.Contains("data/nutriflow.db"))
                {
                    throw new InvalidDataException("В резервной копии отсутствуют обязательные файлы.");
                }
            }

            ProfileManifest manifest = await ReadManifestAsync(Path.Combine(extracted, ProfileManifestName), cancellationToken);
            string dataDirectory = Path.Combine(extracted, "data");
            Directory.CreateDirectory(Path.Combine(dataDirectory, "label-photos"));
            string databasePath = Path.Combine(dataDirectory, "nutriflow.db");
            await ValidateDatabaseAsync(databasePath, manifest.ProfileId, manifest.ActiveSessionId, cancellationToken);
            await DataBackup.VerifyAsync(dataDirectory, cancellationToken);
            Guid restoredId = Guid.NewGuid();
            await RemapOwnerAsync(databasePath, manifest.ProfileId, restoredId, cancellationToken);
            await ValidateDatabaseAsync(databasePath, restoredId, manifest.ActiveSessionId, cancellationToken);
            File.Delete(Path.Combine(dataDirectory, "manifest.json"));
            LocalProfile profile = await _profiles.PublishRestoredAsync(restoredId, restoredName, dataDirectory, cancellationToken);
            return new LocalProfileBackupResult(profile, manifest.ActiveSessionId);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Описание резервной копии повреждено.", exception);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException("База данных резервной копии повреждена или несовместима.", exception);
        }
        finally
        {
            TryDeleteWorkingDirectory(working);
        }
    }

    private static async Task ValidateDatabaseAsync(string path, Guid ownerId, Guid? activeSessionId,
        CancellationToken cancellationToken)
    {
        DbContextOptions<NutriFlowDbContext> options = new DbContextOptionsBuilder<NutriFlowDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()).Options;
        await using NutriFlowDbContext model = new(options);
        await using SqliteConnection database = OpenDatabase(path, SqliteOpenMode.ReadOnly);
        await database.OpenAsync(cancellationToken);
        string[] expectedMigrations = model.Database.GetMigrations().ToArray();
        List<string> migrations = await ReadStringsAsync(database,
            "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;", cancellationToken);
        if (!migrations.SequenceEqual(expectedMigrations))
        {
            throw new InvalidDataException("Версия базы не поддерживается. Используйте такую же версию NutriFlow, как при создании копии.");
        }

        Dictionary<string, Dictionary<string, string>> tables = model.Model.GetEntityTypes().ToDictionary(
            type => type.GetTableName()!, type => type.GetProperties().ToDictionary(
                property => property.GetColumnName(StoreObjectIdentifier.Table(type.GetTableName()!, null))!,
                property => property.GetColumnType() ?? property.GetRelationalTypeMapping().StoreType));
        tables["__EFMigrationsHistory"] = new() { ["MigrationId"] = "TEXT", ["ProductVersion"] = "TEXT" };
        tables["__EFMigrationsLock"] = new() { ["Id"] = "INTEGER", ["Timestamp"] = "TEXT" };
        List<string> objects = await ReadStringsAsync(database,
            "SELECT type || ':' || name FROM sqlite_master WHERE name NOT GLOB 'sqlite_*' AND type IN ('table','view','trigger') ORDER BY name;",
            cancellationToken);
        foreach (string item in objects)
        {
            if (!item.StartsWith("table:", StringComparison.Ordinal) || !tables.TryGetValue(item[6..], out Dictionary<string, string>? columns))
            {
                throw new InvalidDataException("База содержит неподдерживаемые объекты.");
            }
            using SqliteCommand command = database.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{item[6..]}\");";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            Dictionary<string, string> actual = new();
            while (await reader.ReadAsync(cancellationToken))
            {
                actual.Add(reader.GetString(1), reader.GetString(2));
            }
            if (actual.Count != columns.Count || columns.Any(column => !actual.TryGetValue(column.Key, out string? type) || type != column.Value))
            {
                throw new InvalidDataException("Структура базы не соответствует версии NutriFlow.");
            }
        }
        if (tables.Keys.Where(name => name != "__EFMigrationsLock").Any(name => !objects.Contains("table:" + name)))
        {
            throw new InvalidDataException("В базе отсутствуют обязательные таблицы.");
        }
        using (SqliteCommand command = database.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM Users WHERE Id = $owner AND IsLegacyLocal = 0;";
            command.Parameters.AddWithValue("$owner", ownerId);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 1)
            {
                throw new InvalidDataException("Владелец резервной копии не найден.");
            }
            command.CommandText = "SELECT COUNT(*) FROM Users WHERE Id != $owner AND IsLegacyLocal = 0;";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0)
            {
                throw new InvalidDataException("Копия содержит данные другого профиля.");
            }
            foreach (string table in OwnerTables)
            {
                string invalidOwner = table == "Products"
                    ? "UserId IS NOT NULL AND UserId != $owner"
                    : "UserId IS NULL OR UserId != $owner";
                command.CommandText = $"SELECT COUNT(*) FROM \"{table}\" WHERE {invalidOwner};";
                if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0)
                {
                    throw new InvalidDataException("Копия содержит записи другого владельца.");
                }
            }
            if (activeSessionId.HasValue)
            {
                command.CommandText = "SELECT COUNT(*) FROM MealSessions WHERE Id = $session AND UserId = $owner AND Status != $confirmed;";
                command.Parameters.AddWithValue("$session", activeSessionId.Value);
                command.Parameters.AddWithValue("$confirmed", (int)MealSessionStatus.Confirmed);
                if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 1)
                {
                    throw new InvalidDataException("Активный черновик не принадлежит профилю или уже подтверждён.");
                }
            }
        }
        List<string> photoNames = await ReadStringsAsync(database, "SELECT FileName FROM LabelPhotos;", cancellationToken);
        foreach (string name in photoNames)
        {
            ValidatePhotoName(name);
        }
        List<string> references = await ReadStringsAsync(database,
            $"SELECT SourceReference FROM Products WHERE SourceKind = {(int)NutritionSourceKind.LabelPhoto};", cancellationToken);
        if (references.Any(reference => !reference.StartsWith("label-photo:", StringComparison.Ordinal) ||
                !photoNames.Contains(reference["label-photo:".Length..], StringComparer.Ordinal)))
        {
            throw new InvalidDataException("Источник продукта ссылается на отсутствующую фотографию.");
        }
        List<string> sharedReferences = await ReadStringsAsync(database,
            $"SELECT CASE WHEN SourceKind = {(int)NutritionSourceKind.ExternalService} THEN SourceReference ELSE NULL END FROM Products WHERE UserId IS NULL;",
            cancellationToken);
        if (sharedReferences.Any(reference => !Uri.TryCreate(reference, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidDataException("Общий продукт не содержит проверяемый внешний источник.");
        }
    }

    private static async Task RemapOwnerAsync(string path, Guid sourceId, Guid destinationId, CancellationToken cancellationToken)
    {
        await using SqliteConnection database = OpenDatabase(path, SqliteOpenMode.ReadWrite);
        await database.OpenAsync(cancellationToken);
        using SqliteTransaction transaction = database.BeginTransaction();
        using SqliteCommand command = database.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$destination", destinationId);
        command.CommandText = "INSERT INTO Users (Id, CreatedAtUtc, IsLegacyLocal, LegacyLabelPhotosImported) SELECT $destination, CreatedAtUtc, 0, LegacyLabelPhotosImported FROM Users WHERE Id = $source;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        foreach (string table in OwnerTables)
        {
            command.CommandText = $"UPDATE \"{table}\" SET UserId = $destination WHERE UserId = $source;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        command.CommandText = "DELETE FROM Users WHERE Id = $source;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private static void ValidateArchiveName(string name)
    {
        if (name is ProfileManifestName or "data/manifest.json" or "data/nutriflow.db")
        {
            return;
        }
        const string prefix = "data/label-photos/";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Архив содержит неподдерживаемый файл.");
        }
        ValidatePhotoName(name[prefix.Length..]);
    }

    private static void ValidatePhotoName(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        if (!Guid.TryParseExact(stem, "N", out Guid id) || stem != id.ToString("N") ||
            Path.GetExtension(name) is not (".jpg" or ".png" or ".webp") ||
            name != stem + Path.GetExtension(name))
        {
            throw new InvalidDataException("Неверное имя фотографии в резервной копии.");
        }
    }

    private static async Task<ProfileManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream input = File.OpenRead(path);
        ProfileManifest manifest = await JsonSerializer.DeserializeAsync<ProfileManifest>(input, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Описание резервной копии отсутствует.");
        if (manifest.FormatVersion != 1 || manifest.ProfileId == Guid.Empty ||
            string.IsNullOrWhiteSpace(manifest.ProfileName) || manifest.ProfileName.Length > 80 ||
            manifest.ProfileName.Any(char.IsControl) || manifest.CreatedAtUtc == default ||
            manifest.CreatedAtUtc.Offset != TimeSpan.Zero || manifest.ActiveSessionId == Guid.Empty)
        {
            throw new InvalidDataException("Версия или описание резервной копии не поддерживаются.");
        }
        return manifest;
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, value, cancellationToken: cancellationToken);
    }

    private static async Task<long> CopyBoundedAsync(Stream source, string destination, long maximumBytes,
        CancellationToken cancellationToken, bool disposeSource = false)
    {
        try
        {
            await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);
            byte[] buffer = new byte[81920];
            long total = 0;
            int length;
            while ((length = await source.ReadAsync(buffer, cancellationToken)) != 0)
            {
                total = checked(total + length);
                if (total > maximumBytes)
                {
                    throw new InvalidDataException("Файл резервной копии превышает допустимый размер.");
                }
                await output.WriteAsync(buffer.AsMemory(0, length), cancellationToken);
            }
            return total;
        }
        finally
        {
            if (disposeSource)
            {
                await source.DisposeAsync();
            }
        }
    }

    private static async Task<List<string>> ReadStringsAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        List<string> values = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0))
            {
                throw new InvalidDataException("Обязательное значение базы отсутствует.");
            }
            values.Add(reader.GetString(0));
        }
        return values;
    }

    private static SqliteConnection OpenDatabase(string path, SqliteOpenMode mode) => new(
        new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, ForeignKeys = true }.ToString());

    private static void TryDeleteWorkingDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        const string prefix = ".profile-backup-";
        string name = Path.GetFileName(fullPath);
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _))
        {
            throw new InvalidOperationException("Удалять можно только временный каталог текущей резервной копии.");
        }
        try
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }
            Directory.Delete(fullPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ProfileManifest(int FormatVersion, Guid ProfileId, string ProfileName,
        DateTimeOffset CreatedAtUtc, Guid? ActiveSessionId);
}

public sealed record LocalProfileBackupResult(LocalProfile Profile, Guid? ActiveSessionId);
