using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NutriFlow.Infrastructure.Backups;

namespace NutriFlow.Infrastructure.Tests;

public sealed class DataBackupTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateAsync_CopiesCurrentAndLegacyDataIntoVerifiedStandaloneBackup(bool hasPhotoTable)
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync(hasPhotoTable);
        Dictionary<string, string> sourceSnapshot = fixture.SourceSnapshot();

        BackupManifest manifest = await fixture.CreateBackupAsync();
        BackupManifest verified = await DataBackup.VerifyAsync(fixture.BackupPath);

        Assert.Equal(1, manifest.FormatVersion);
        Assert.InRange(manifest.CreatedAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
        Assert.Equal(manifest.CreatedAtUtc, verified.CreatedAtUtc);
        Assert.Equal(manifest.Files, verified.Files);
        Assert.Equal(3, manifest.Files.Count);
        Assert.Contains(manifest.Files, file => file.Path == "nutriflow.db");
        Assert.Contains(manifest.Files, file => file.Path == "label-photos/label.jpg");
        Assert.Contains(manifest.Files, file => file.Path == "label-photos/legacy.png");
        Assert.Equal(sourceSnapshot, fixture.SourceSnapshot());
        Assert.Equal("Рагу", await fixture.ReadBackupValueAsync("SELECT Name FROM Products"));
        Assert.Equal("delete", await fixture.ReadBackupValueAsync("PRAGMA journal_mode"));
        Assert.Equal("ok", await fixture.ReadBackupValueAsync("PRAGMA integrity_check"));
        Assert.False(File.Exists(Path.Combine(fixture.BackupPath, "nutriflow.db-wal")));
        Assert.False(File.Exists(Path.Combine(fixture.BackupPath, "nutriflow.db-shm")));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(fixture.BackupPath, "label-photos", "label.jpg")));
        Assert.Equal([5, 6, 7], File.ReadAllBytes(Path.Combine(fixture.BackupPath, "label-photos", "legacy.png")));
        JsonObject storedManifest = fixture.ReadManifest();
        Assert.Equal(1, storedManifest["formatVersion"]!.GetValue<int>());
        Assert.NotNull(storedManifest["createdAtUtc"]);
        Assert.NotNull(storedManifest["files"]);
    }

    [Fact]
    public async Task CreateAsync_IncludesCommittedWalDataWithoutChangingDatabaseOrSidecars()
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        string writerPath = Path.Combine(fixture.RootPath, "writer.db");
        await using (SqliteConnection writer = await BackupFixture.OpenAsync(writerPath))
        {
            await BackupFixture.ExecuteAsync(writer,
                "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; " +
                "CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL); " +
                "INSERT INTO Products (Name) VALUES ('Запись из WAL');");
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                File.Copy(writerPath + suffix, fixture.DatabasePath + suffix, overwrite: true);
            }
        }
        Assert.True(new FileInfo(fixture.DatabasePath + "-wal").Length > 0);
        Dictionary<string, string> sourceSnapshot = fixture.SourceSnapshot();

        await fixture.CreateBackupAsync();

        Assert.Equal(sourceSnapshot, fixture.SourceSnapshot());
        Assert.Equal("Запись из WAL", await fixture.ReadBackupValueAsync("SELECT Name FROM Products"));
        Assert.Equal("delete", await fixture.ReadBackupValueAsync("PRAGMA journal_mode"));
        await DataBackup.VerifyAsync(fixture.BackupPath);
        Assert.Equal(sourceSnapshot, fixture.SourceSnapshot());
    }

    [Fact]
    public async Task CreateAsync_LeavesCleanWalHeaderWithoutSidecarsUnchanged()
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        await fixture.ExecuteSourceAsync("PRAGMA journal_mode=WAL;");
        Assert.Equal(2, File.ReadAllBytes(fixture.DatabasePath)[18]);
        Assert.False(File.Exists(fixture.DatabasePath + "-wal"));
        Assert.False(File.Exists(fixture.DatabasePath + "-shm"));
        Dictionary<string, string> sourceSnapshot = fixture.SourceSnapshot();

        await fixture.CreateBackupAsync();
        Dictionary<string, string> backupSnapshot = SnapshotDirectory(fixture.BackupPath);
        await DataBackup.VerifyAsync(fixture.BackupPath);

        Assert.Equal(sourceSnapshot, fixture.SourceSnapshot());
        Assert.Equal(backupSnapshot, SnapshotDirectory(fixture.BackupPath));
        Assert.False(File.Exists(fixture.DatabasePath + "-wal"));
        Assert.False(File.Exists(fixture.DatabasePath + "-shm"));
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingPhotoReferencedByDatabase()
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        await fixture.ExecuteSourceAsync("INSERT INTO LabelPhotos (FileName) VALUES ('missing.jpg');");
        Dictionary<string, string> sourceSnapshot = fixture.SourceSnapshot();

        await AssertCreateRejectedAsync(fixture);

        Assert.Equal(sourceSnapshot, fixture.SourceSnapshot());
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingLegacyProductPhotoWithoutModernPhotoTable()
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync(hasPhotoTable: false);
        await fixture.ExecuteSourceAsync(
            "ALTER TABLE Products ADD COLUMN SourceReference TEXT; " +
            "UPDATE Products SET SourceReference='label-photo:missing.jpg';");
        Dictionary<string, string> sourceSnapshot = fixture.SourceSnapshot();

        await AssertCreateRejectedAsync(fixture);

        Assert.Equal(sourceSnapshot, fixture.SourceSnapshot());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateAsync_RejectsMissingSourceWithoutPublishingBackup(bool missingDatabase)
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        if (missingDatabase)
        {
            File.Delete(fixture.DatabasePath);
        }
        else
        {
            Directory.Delete(fixture.PhotosPath, recursive: true);
        }

        await AssertCreateRejectedAsync(fixture);
    }

    [Fact]
    public async Task CreateAsync_DoesNotOverwriteExistingDestination()
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        Directory.CreateDirectory(fixture.BackupPath);
        string existingPath = Path.Combine(fixture.BackupPath, "keep.txt");
        await File.WriteAllTextAsync(existingPath, "existing backup");
        Dictionary<string, string> snapshot = SnapshotDirectory(fixture.BackupPath);

        Assert.NotNull(await Record.ExceptionAsync(() => fixture.CreateBackupAsync()));

        Assert.Equal(snapshot, SnapshotDirectory(fixture.BackupPath));
    }

    [Fact]
    public async Task CreateAsync_RejectsDestinationInsidePhotoSource()
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        string destination = Path.Combine(fixture.PhotosPath, "backup");
        Dictionary<string, string> sourceSnapshot = fixture.SourceSnapshot();

        Assert.NotNull(await Record.ExceptionAsync(() =>
            DataBackup.CreateAsync(fixture.DatabasePath, fixture.PhotosPath, destination)));

        Assert.False(Directory.Exists(destination));
        Assert.Equal(sourceSnapshot, fixture.SourceSnapshot());
    }

    [Theory]
    [InlineData("-wal", false)]
    [InlineData("-wal", true)]
    [InlineData("-shm", false)]
    [InlineData("-shm", true)]
    [InlineData("-journal", false)]
    [InlineData("-journal", true)]
    public async Task CreateAsync_RejectsAbsentSourceSidecarAsDestinationOrParent(string suffix, bool nested)
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        string sidecarPath = fixture.DatabasePath + suffix;
        string destination = nested ? Path.Combine(sidecarPath, "backup") : sidecarPath;
        Assert.False(File.Exists(sidecarPath));
        Assert.False(Directory.Exists(sidecarPath));
        Dictionary<string, string> sourceSnapshot = fixture.SourceSnapshot();

        Assert.NotNull(await Record.ExceptionAsync(() =>
            DataBackup.CreateAsync(fixture.DatabasePath, fixture.PhotosPath, destination)));

        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(sidecarPath));
        Assert.False(Directory.Exists(sidecarPath));
        Assert.Equal(sourceSnapshot, fixture.SourceSnapshot());
    }

    [Fact]
    public async Task CreateAsync_RejectsNestedPhotoDirectories()
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        Directory.CreateDirectory(Path.Combine(fixture.PhotosPath, "nested"));

        await AssertCreateRejectedAsync(fixture);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateAsync_RejectsDirectoryLinksInSourceAndDestination(bool linkInSource)
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        string linkPath = Path.Combine(fixture.RootPath, "photo-link");
        fixture.CreateDirectoryLink(linkPath, fixture.PhotosPath);
        string source = linkInSource ? linkPath : fixture.PhotosPath;
        string destination = linkInSource ? fixture.BackupPath : Path.Combine(linkPath, "backup");
        Dictionary<string, string> sourceSnapshot = fixture.SourceSnapshot();

        Assert.NotNull(await Record.ExceptionAsync(() =>
            DataBackup.CreateAsync(fixture.DatabasePath, source, destination)));

        Assert.False(Directory.Exists(destination));
        Assert.Equal(sourceSnapshot, fixture.SourceSnapshot());
    }

    [Fact]
    public async Task CreateAsync_RejectsCorruptDatabaseWithoutChangingSource()
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        await File.WriteAllTextAsync(fixture.DatabasePath, "not a SQLite database");
        Dictionary<string, string> sourceSnapshot = fixture.SourceSnapshot();

        await AssertCreateRejectedAsync(fixture);

        Assert.Equal(sourceSnapshot, fixture.SourceSnapshot());
    }

    [Fact]
    public async Task CreateAsync_RejectsBrokenForeignKeys()
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        await fixture.ExecuteSourceAsync(
            "PRAGMA foreign_keys=OFF; " +
            "CREATE TABLE Children (Id INTEGER PRIMARY KEY, ProductId INTEGER REFERENCES Products(Id)); " +
            "INSERT INTO Children (ProductId) VALUES (999);");

        await AssertCreateRejectedAsync(fixture);
    }

    [Fact]
    public async Task CreateAsync_CancellationDoesNotPublishBackupOrChangeSource()
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        Dictionary<string, string> sourceSnapshot = fixture.SourceSnapshot();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DataBackup.CreateAsync(fixture.DatabasePath, fixture.PhotosPath, fixture.BackupPath, cancellation.Token));

        Assert.False(Directory.Exists(fixture.BackupPath));
        Assert.Equal(sourceSnapshot, fixture.SourceSnapshot());
    }

    [Fact]
    public async Task CreateAsync_ConcurrentSameDestinationPublishesOnlyOneValidBackup()
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        Dictionary<string, string> sourceSnapshot = fixture.SourceSnapshot();

        Task<bool> first = Task.Run(() => TryCreateAsync(fixture));
        Task<bool> second = Task.Run(() => TryCreateAsync(fixture));
        bool[] results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(result => result));
        await DataBackup.VerifyAsync(fixture.BackupPath);
        Assert.Equal(sourceSnapshot, fixture.SourceSnapshot());
    }

    [Theory]
    [InlineData("length")]
    [InlineData("hash")]
    [InlineData("format")]
    [InlineData("duplicate")]
    [InlineData("traversal-forward")]
    [InlineData("traversal-backward")]
    [InlineData("absolute")]
    [InlineData("missing-database")]
    public async Task VerifyAsync_RejectsMalformedOrTamperedManifestWithoutWritingBackup(string mutation)
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        await fixture.CreateBackupAsync();
        JsonObject manifest = fixture.ReadManifest();
        JsonArray files = manifest["files"]!.AsArray();
        JsonObject database = files.Single(file => file!["path"]!.GetValue<string>() == "nutriflow.db")!.AsObject();
        switch (mutation)
        {
            case "length":
                database["length"] = database["length"]!.GetValue<long>() + 1;
                break;
            case "hash":
                database["sha256"] = new string('0', 64);
                break;
            case "format":
                manifest["formatVersion"] = 999;
                break;
            case "duplicate":
                files.Add(database.DeepClone());
                break;
            case "traversal-forward":
                database["path"] = "../nutriflow.db";
                break;
            case "traversal-backward":
                database["path"] = "..\\nutriflow.db";
                break;
            case "absolute":
                database["path"] = fixture.DatabasePath;
                break;
            case "missing-database":
                files.Remove(database);
                break;
        }
        await fixture.WriteManifestAsync(manifest);
        Dictionary<string, string> snapshot = SnapshotDirectory(fixture.BackupPath);

        Assert.NotNull(await Record.ExceptionAsync(() => DataBackup.VerifyAsync(fixture.BackupPath)));

        Assert.Equal(snapshot, SnapshotDirectory(fixture.BackupPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyAsync_RejectsMissingAndExtraFiles(bool missingFile)
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        await fixture.CreateBackupAsync();
        if (missingFile)
        {
            File.Delete(Path.Combine(fixture.BackupPath, "label-photos", "label.jpg"));
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.BackupPath, "unexpected.txt"), "extra data");
        }

        Assert.NotNull(await Record.ExceptionAsync(() => DataBackup.VerifyAsync(fixture.BackupPath)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyAsync_ChecksDatabaseIntegrityEvenWhenManifestHashMatches(bool corruptDatabase)
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        await fixture.CreateBackupAsync();
        string backupDatabasePath = Path.Combine(fixture.BackupPath, "nutriflow.db");
        if (corruptDatabase)
        {
            await File.WriteAllTextAsync(backupDatabasePath, "not a database");
        }
        else
        {
            await using SqliteConnection connection = await BackupFixture.OpenAsync(backupDatabasePath);
            await BackupFixture.ExecuteAsync(connection,
                "PRAGMA foreign_keys=OFF; " +
                "CREATE TABLE Children (Id INTEGER PRIMARY KEY, ProductId INTEGER REFERENCES Products(Id)); " +
                "INSERT INTO Children (ProductId) VALUES (999);");
        }
        await fixture.RefreshDatabaseManifestAsync();
        Dictionary<string, string> snapshot = SnapshotDirectory(fixture.BackupPath);

        Assert.NotNull(await Record.ExceptionAsync(() => DataBackup.VerifyAsync(fixture.BackupPath)));

        Assert.Equal(snapshot, SnapshotDirectory(fixture.BackupPath));
    }

    [Fact]
    public async Task VerifyAsync_RejectsMissingDatabaseReferencedPhotoEvenAfterManifestRemoval()
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        await fixture.CreateBackupAsync();
        File.Delete(Path.Combine(fixture.BackupPath, "label-photos", "label.jpg"));
        JsonObject manifest = fixture.ReadManifest();
        JsonArray files = manifest["files"]!.AsArray();
        files.Remove(files.Single(file => file!["path"]!.GetValue<string>() == "label-photos/label.jpg"));
        await fixture.WriteManifestAsync(manifest);

        Assert.NotNull(await Record.ExceptionAsync(() => DataBackup.VerifyAsync(fixture.BackupPath)));
    }

    [Fact]
    public async Task VerifyAsync_RejectsMissingLegacyProductPhotoEvenAfterManifestRemoval()
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync(hasPhotoTable: false);
        await fixture.ExecuteSourceAsync(
            "ALTER TABLE Products ADD COLUMN SourceReference TEXT; " +
            "UPDATE Products SET SourceReference='label-photo:legacy.png';");
        await fixture.CreateBackupAsync();
        File.Delete(Path.Combine(fixture.BackupPath, "label-photos", "legacy.png"));
        JsonObject manifest = fixture.ReadManifest();
        JsonArray files = manifest["files"]!.AsArray();
        files.Remove(files.Single(file => file!["path"]!.GetValue<string>() == "label-photos/legacy.png"));
        await fixture.WriteManifestAsync(manifest);

        Assert.NotNull(await Record.ExceptionAsync(() => DataBackup.VerifyAsync(fixture.BackupPath)));
    }

    [Fact]
    public async Task VerifyAsync_RejectsPhotoContentTamperingWithoutChangingBackup()
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        await fixture.CreateBackupAsync();
        await File.WriteAllBytesAsync(Path.Combine(fixture.BackupPath, "label-photos", "label.jpg"), [9, 2, 3, 4]);
        Dictionary<string, string> snapshot = SnapshotDirectory(fixture.BackupPath);

        Assert.NotNull(await Record.ExceptionAsync(() => DataBackup.VerifyAsync(fixture.BackupPath)));

        Assert.Equal(snapshot, SnapshotDirectory(fixture.BackupPath));
    }

    [Fact]
    public async Task VerifyAsync_CancellationDoesNotChangeBackup()
    {
        using BackupFixture fixture = new();
        await fixture.CreateDatabaseAsync();
        await fixture.CreateBackupAsync();
        Dictionary<string, string> snapshot = SnapshotDirectory(fixture.BackupPath);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DataBackup.VerifyAsync(fixture.BackupPath, cancellation.Token));

        Assert.Equal(snapshot, SnapshotDirectory(fixture.BackupPath));
    }

    private static async Task AssertCreateRejectedAsync(BackupFixture fixture)
    {
        Assert.NotNull(await Record.ExceptionAsync(() => fixture.CreateBackupAsync()));
        Assert.False(Directory.Exists(fixture.BackupPath));
    }

    private static async Task<bool> TryCreateAsync(BackupFixture fixture)
    {
        try
        {
            await fixture.CreateBackupAsync();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> SnapshotDirectory(string directoryPath) =>
        Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToDictionary(path => Path.GetRelativePath(directoryPath, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    private sealed class BackupFixture : IDisposable
    {
        private readonly List<string> directoryLinks = [];

        public string RootPath { get; } = Path.Combine(Path.GetTempPath(), $"nutriflow-backup-{Guid.NewGuid():N}");
        public string DatabasePath => Path.Combine(RootPath, "source", "nutriflow.db");
        public string PhotosPath => Path.Combine(RootPath, "source", "label-photos");
        public string BackupPath => Path.Combine(RootPath, "backup");

        public async Task CreateDatabaseAsync(bool hasPhotoTable = true)
        {
            Directory.CreateDirectory(PhotosPath);
            await File.WriteAllBytesAsync(Path.Combine(PhotosPath, "label.jpg"), [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(Path.Combine(PhotosPath, "legacy.png"), [5, 6, 7]);
            await using SqliteConnection connection = await OpenAsync(DatabasePath);
            await ExecuteAsync(connection,
                "CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL); " +
                "INSERT INTO Products (Name) VALUES ('Рагу');");
            if (hasPhotoTable)
            {
                await ExecuteAsync(connection,
                    "CREATE TABLE LabelPhotos (Id INTEGER PRIMARY KEY, FileName TEXT NOT NULL); " +
                    "INSERT INTO LabelPhotos (FileName) VALUES ('label.jpg');");
            }
        }

        public Task<BackupManifest> CreateBackupAsync() => DataBackup.CreateAsync(DatabasePath, PhotosPath, BackupPath);

        public Dictionary<string, string> SourceSnapshot() => SnapshotDirectory(Path.Combine(RootPath, "source"));

        public async Task ExecuteSourceAsync(string commandText)
        {
            await using SqliteConnection connection = await OpenAsync(DatabasePath);
            await ExecuteAsync(connection, commandText);
        }

        public async Task<string> ReadBackupValueAsync(string commandText)
        {
            await using SqliteConnection connection = await OpenAsync(Path.Combine(BackupPath, "nutriflow.db"));
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
        }

        public JsonObject ReadManifest() => JsonNode.Parse(File.ReadAllText(Path.Combine(BackupPath, "manifest.json")))!.AsObject();

        public Task WriteManifestAsync(JsonObject manifest) => File.WriteAllTextAsync(
            Path.Combine(BackupPath, "manifest.json"), manifest.ToJsonString(JsonOptions));

        public async Task RefreshDatabaseManifestAsync()
        {
            JsonObject manifest = ReadManifest();
            JsonObject entry = manifest["files"]!.AsArray()
                .Single(file => file!["path"]!.GetValue<string>() == "nutriflow.db")!.AsObject();
            string databasePath = Path.Combine(BackupPath, "nutriflow.db");
            entry["length"] = new FileInfo(databasePath).Length;
            entry["sha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(databasePath)));
            await WriteManifestAsync(manifest);
        }

        public void CreateDirectoryLink(string linkPath, string targetPath)
        {
            if (OperatingSystem.IsWindows())
            {
                ProcessStartInfo startInfo = new("cmd.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (string argument in new[] { "/c", "mklink", "/J", linkPath, targetPath })
                {
                    startInfo.ArgumentList.Add(argument);
                }
                using Process process = Process.Start(startInfo)!;
                process.WaitForExit();
                Assert.Equal(0, process.ExitCode);
            }
            else
            {
                Directory.CreateSymbolicLink(linkPath, targetPath);
            }
            directoryLinks.Add(linkPath);
        }

        public static async Task<SqliteConnection> OpenAsync(string databasePath)
        {
            SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
                ForeignKeys = false
            }.ToString());
            await connection.OpenAsync();
            return connection;
        }

        public static async Task ExecuteAsync(SqliteConnection connection, string commandText)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync();
        }

        public void Dispose()
        {
            foreach (string linkPath in directoryLinks)
            {
                Directory.Delete(linkPath);
            }
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
