using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NutriFlow.Infrastructure.Backups;

public static class DataBackup
{
    private const string DatabaseName = "nutriflow.db";
    private const string PhotoDirectoryName = "label-photos";
    private const string ManifestName = "manifest.json";
    private const long MaximumManifestLength = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<BackupManifest> CreateAsync(
        string databasePath,
        string labelPhotosPath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        databasePath = BackupFiles.FullPath(databasePath);
        labelPhotosPath = BackupFiles.FullPath(labelPhotosPath);
        destinationPath = BackupFiles.FullPath(destinationPath);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException("The backup destination already exists; it will not be overwritten.");
        }
        if (BackupFiles.IsWithin(destinationPath, labelPhotosPath) ||
            BackupFiles.IsWithin(labelPhotosPath, destinationPath) ||
            BackupFiles.IsWithin(databasePath, labelPhotosPath) ||
            BackupFiles.IsDatabaseDestination(destinationPath, databasePath))
        {
            throw new ArgumentException("Backup and photo paths must not overlap the source data.");
        }

        string[] databaseSuffixes = BackupFiles.ReadDatabaseSuffixes(databasePath);
        string[] photoNames = BackupFiles.ReadPhotoNames(labelPhotosPath);
        string parentPath = Path.GetDirectoryName(destinationPath) ??
                            throw new ArgumentException("The backup destination needs a parent directory.");
        Directory.CreateDirectory(parentPath);
        string stagingPath = Path.Combine(parentPath, $".nutriflow-backup-{Guid.NewGuid():N}.partial");
        Directory.CreateDirectory(stagingPath);
        bool published = false;
        try
        {
            string rawPath = Path.Combine(stagingPath, "source");
            Directory.CreateDirectory(rawPath);
            string rawDatabasePath = Path.Combine(rawPath, DatabaseName);
            List<BackupFile> sourceDatabaseFiles = new();
            foreach (string suffix in databaseSuffixes)
            {
                sourceDatabaseFiles.Add(await BackupFiles.CopyAsync(
                    databasePath + suffix, rawDatabasePath + suffix, suffix, cancellationToken));
            }
            foreach (BackupFile file in sourceDatabaseFiles)
            {
                await EnsureMatchesAsync(rawDatabasePath + file.Path, file, cancellationToken);
            }

            string backupPhotoPath = Path.Combine(stagingPath, PhotoDirectoryName);
            Directory.CreateDirectory(backupPhotoPath);
            List<BackupFile> files = new();
            foreach (string name in photoNames)
            {
                files.Add(await BackupFiles.CopyAsync(Path.Combine(labelPhotosPath, name),
                    Path.Combine(backupPhotoPath, name), $"{PhotoDirectoryName}/{name}", cancellationToken));
            }

            string backupDatabasePath = Path.Combine(stagingPath, DatabaseName);
            await CreateDatabaseSnapshotAsync(rawDatabasePath, backupDatabasePath, backupPhotoPath, cancellationToken);
            await ValidateDatabaseAsync(backupDatabasePath, backupPhotoPath, cancellationToken);
            BackupFiles.DeleteOwnedDirectory(rawPath);
            files.Insert(0, await BackupFiles.DescribeAsync(backupDatabasePath, DatabaseName, cancellationToken));

            if (!databaseSuffixes.SequenceEqual(BackupFiles.ReadDatabaseSuffixes(databasePath)) ||
                !photoNames.SequenceEqual(BackupFiles.ReadPhotoNames(labelPhotosPath)))
            {
                throw new IOException("Source files changed during the backup; stop all writers and retry.");
            }
            foreach (BackupFile file in sourceDatabaseFiles)
            {
                await EnsureMatchesAsync(databasePath + file.Path, file, cancellationToken);
            }
            foreach (BackupFile file in files.Skip(1))
            {
                string name = file.Path[(PhotoDirectoryName.Length + 1)..];
                await EnsureMatchesAsync(Path.Combine(labelPhotosPath, name), file, cancellationToken);
                await EnsureMatchesAsync(Path.Combine(backupPhotoPath, name), file, cancellationToken);
            }

            BackupManifest manifest = new(1, DateTimeOffset.UtcNow, files.AsReadOnly());
            await using (FileStream stream = new(Path.Combine(stagingPath, ManifestName),
                             FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                if (stream.Length > MaximumManifestLength)
                {
                    throw new InvalidDataException("The backup manifest is too large.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            BackupFiles.EnsureNoLinks(destinationPath);
            Directory.Move(stagingPath, destinationPath);
            published = true;
            return manifest;
        }
        finally
        {
            if (!published && Directory.Exists(stagingPath))
            {
                BackupFiles.DeleteOwnedDirectory(stagingPath);
            }
        }
    }

    public static async Task<BackupManifest> VerifyAsync(
        string backupPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        backupPath = BackupFiles.FullPath(backupPath);
        string manifestPath = Path.Combine(backupPath, ManifestName);
        BackupFiles.EnsureNoLinks(manifestPath);
        BackupManifest manifest;
        await using (FileStream stream = BackupFiles.OpenRead(manifestPath))
        {
            if (stream.Length > MaximumManifestLength)
            {
                throw new InvalidDataException("The backup manifest is too large.");
            }
            try
            {
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonOptions, cancellationToken)
                           ?? throw new InvalidDataException("The backup manifest is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The backup manifest is invalid.", exception);
            }
        }
        ValidateManifest(manifest);
        string[] photoNames = BackupFiles.ReadPhotoNames(Path.Combine(backupPath, PhotoDirectoryName));
        string[] expectedNames = manifest.Files.Select(file => file.Path).Order(StringComparer.Ordinal).ToArray();
        string[] actualNames = photoNames.Select(name => $"{PhotoDirectoryName}/{name}")
            .Append(DatabaseName).Order(StringComparer.Ordinal).ToArray();
        string[] rootNames = Directory.GetFileSystemEntries(backupPath).Select(Path.GetFileName)
            .Order(StringComparer.Ordinal).ToArray()!;
        if (!expectedNames.SequenceEqual(actualNames) ||
            !rootNames.SequenceEqual(new[] { PhotoDirectoryName, ManifestName, DatabaseName }.Order(StringComparer.Ordinal)))
        {
            throw new InvalidDataException("The backup contains missing or unexpected files.");
        }
        foreach (BackupFile file in manifest.Files)
        {
            await EnsureMatchesAsync(Path.Combine(backupPath, file.Path.Replace('/', Path.DirectorySeparatorChar)),
                file, cancellationToken);
        }

        string checkPath = Path.Combine(Path.GetTempPath(), $"nutriflow-backup-check-{Guid.NewGuid():N}");
        BackupFiles.EnsureNoLinks(checkPath);
        Directory.CreateDirectory(checkPath);
        try
        {
            string checkDatabase = Path.Combine(checkPath, DatabaseName);
            BackupFile databaseFile = manifest.Files.Single(file => file.Path == DatabaseName);
            BackupFile copied = await BackupFiles.CopyAsync(Path.Combine(backupPath, DatabaseName),
                checkDatabase, DatabaseName, cancellationToken);
            if (!FilesMatch(databaseFile, copied))
            {
                throw new InvalidDataException("The backup database changed during verification.");
            }
            await ValidateDatabaseAsync(checkDatabase, Path.Combine(backupPath, PhotoDirectoryName), cancellationToken);
        }
        finally
        {
            BackupFiles.DeleteOwnedDirectory(checkPath);
        }
        return manifest;
    }

    private static void ValidateManifest(BackupManifest manifest)
    {
        if (manifest.FormatVersion != 1 || manifest.CreatedAtUtc == default ||
            manifest.CreatedAtUtc.Offset != TimeSpan.Zero || manifest.Files is null || manifest.Files.Count == 0)
        {
            throw new InvalidDataException("The backup manifest format is not supported.");
        }
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (BackupFile? file in manifest.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path) || !names.Add(file.Path) ||
                file.Length < 0 || file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("A backup manifest entry is invalid or duplicated.");
            }
            if (file.Path != DatabaseName)
            {
                if (!file.Path.StartsWith(PhotoDirectoryName + "/", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("A backup manifest path is invalid.");
                }
                BackupFiles.ValidateFileName(file.Path[(PhotoDirectoryName.Length + 1)..]);
            }
        }
        if (!manifest.Files.Any(file => file.Path == DatabaseName))
        {
            throw new InvalidDataException("The backup manifest has no database.");
        }
    }

    private static async Task EnsureMatchesAsync(string path, BackupFile expected, CancellationToken cancellationToken)
    {
        BackupFile actual = await BackupFiles.DescribeAsync(path, expected.Path, cancellationToken);
        if (!FilesMatch(expected, actual))
        {
            throw new InvalidDataException("A backup or source file failed the length and SHA-256 check.");
        }
    }

    private static bool FilesMatch(BackupFile expected, BackupFile actual) =>
        expected.Length == actual.Length && expected.Sha256.Equals(actual.Sha256, StringComparison.OrdinalIgnoreCase);

    private static async Task CreateDatabaseSnapshotAsync(
        string sourcePath, string destinationPath, string photoPath, CancellationToken cancellationToken)
    {
        await ValidateDatabaseAsync(sourcePath, photoPath, cancellationToken);
        await using SqliteConnection source = OpenConnection(sourcePath, SqliteOpenMode.ReadWrite);
        await using SqliteConnection destination = OpenConnection(destinationPath, SqliteOpenMode.ReadWriteCreate);
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);
        using SqliteCommand journal = destination.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode=DELETE;";
        if (!string.Equals((string?)await journal.ExecuteScalarAsync(cancellationToken), "delete", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The backup database could not be made self-contained.");
        }
    }

    private static async Task ValidateDatabaseAsync(string path, string photoPath, CancellationToken cancellationToken)
    {
        await using (FileStream file = BackupFiles.OpenRead(path))
        {
            byte[] header = new byte[16];
            if (file.Length < 100)
            {
                throw new InvalidDataException("The file is not a valid SQLite database.");
            }
            await file.ReadExactlyAsync(header, cancellationToken);
            if (!header.SequenceEqual(Encoding.ASCII.GetBytes("SQLite format 3\0")))
            {
                throw new InvalidDataException("The file is not a valid SQLite database.");
            }
        }
        await using SqliteConnection connection = OpenConnection(path, SqliteOpenMode.ReadWrite);
        await connection.OpenAsync(cancellationToken);
        using SqliteCommand integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        await using (SqliteDataReader reader = await integrity.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != "ok" ||
                await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException("The SQLite integrity check failed.");
            }
        }
        using SqliteCommand foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        await using (SqliteDataReader reader = await foreignKeys.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException("The SQLite foreign-key check failed.");
            }
        }
        HashSet<string> requiredPhotos = new(StringComparer.Ordinal);
        if (await HasTableAsync(connection, "LabelPhotos", cancellationToken))
        {
            using SqliteCommand photos = connection.CreateCommand();
            photos.CommandText = "SELECT FileName FROM LabelPhotos;";
            await using SqliteDataReader reader = await photos.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(0))
                {
                    throw new InvalidDataException("A registered photo has no file name.");
                }
                requiredPhotos.Add(reader.GetString(0));
            }
        }
        if (await HasTableAsync(connection, "Products", cancellationToken))
        {
            using SqliteCommand columns = connection.CreateCommand();
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Products') WHERE name = 'SourceReference';";
            if (Convert.ToInt64(await columns.ExecuteScalarAsync(cancellationToken)) != 0)
            {
                using SqliteCommand references = connection.CreateCommand();
                references.CommandText = "SELECT SourceReference FROM Products WHERE SourceReference IS NOT NULL;";
                await using SqliteDataReader reader = await references.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    string reference = reader.GetString(0);
                    if (reference.StartsWith("label-photo:", StringComparison.Ordinal))
                    {
                        requiredPhotos.Add(reference["label-photo:".Length..]);
                    }
                }
            }
        }
        foreach (string name in requiredPhotos)
        {
            BackupFiles.ValidateFileName(name);
            string filePath = Path.Combine(photoPath, name);
            BackupFiles.EnsureNoLinks(filePath);
            if (!File.Exists(filePath))
            {
                throw new InvalidDataException("A photo referenced by the database is missing.");
            }
        }
    }

    private static SqliteConnection OpenConnection(string path, SqliteOpenMode mode) => new(
        new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 5 }.ToString());

    private static async Task<bool> HasTableAsync(SqliteConnection connection, string name, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }
}
