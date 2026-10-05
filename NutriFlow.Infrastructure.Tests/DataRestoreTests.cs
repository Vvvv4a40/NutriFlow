using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NutriFlow.Infrastructure.Backups;

namespace NutriFlow.Infrastructure.Tests;

public sealed class DataRestoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoreAsync_CopiesVerifiedCurrentAndLegacyDataByteForByteWithoutChangingSource(bool hasPhotoTable)
    {
        using RestoreFixture fixture = new();
        BackupManifest expected = await fixture.CreateBackupAsync(hasPhotoTable);
        Dictionary<string, string> sourceSnapshot = SnapshotDirectory(fixture.SourcePath);
        Dictionary<string, string> backupSnapshot = SnapshotDirectory(fixture.BackupPath);

        BackupManifest restored = await fixture.RestoreAsync();

        Assert.Equal(expected.FormatVersion, restored.FormatVersion);
        Assert.Equal(expected.CreatedAtUtc, restored.CreatedAtUtc);
        Assert.Equal(expected.Files, restored.Files);
        AssertRestoredFilesMatch(fixture, restored);
        Assert.Equal(sourceSnapshot, SnapshotDirectory(fixture.SourcePath));
        Assert.Equal(backupSnapshot, SnapshotDirectory(fixture.BackupPath));
        AssertNoStaging(fixture.RootPath);
    }

    [Theory]
    [InlineData("empty-directory")]
    [InlineData("nonempty-directory")]
    [InlineData("file")]
    public async Task RestoreAsync_DoesNotOverwriteAnyExistingDestination(string destinationKind)
    {
        using RestoreFixture fixture = new();
        await fixture.CreateBackupAsync();
        if (destinationKind == "file")
        {
            await File.WriteAllTextAsync(fixture.DestinationPath, "existing file");
        }
        else
        {
            Directory.CreateDirectory(fixture.DestinationPath);
            if (destinationKind == "nonempty-directory")
            {
                await File.WriteAllTextAsync(Path.Combine(fixture.DestinationPath, "keep.txt"), "existing data");
            }
        }
        Dictionary<string, string> snapshot = SnapshotDirectory(fixture.RootPath);

        Assert.NotNull(await Record.ExceptionAsync(() => fixture.RestoreAsync()));

        Assert.Equal(snapshot, SnapshotDirectory(fixture.RootPath));
        Assert.Equal(destinationKind != "file", Directory.Exists(fixture.DestinationPath));
        Assert.Equal(destinationKind == "file", File.Exists(fixture.DestinationPath));
        AssertNoStaging(fixture.RootPath);
    }

    [Fact]
    public async Task RestoreAsync_RejectsRepeatedRestoreWithoutChangingPreviouslyRestoredData()
    {
        using RestoreFixture fixture = new();
        await fixture.CreateBackupAsync();
        await fixture.RestoreAsync();
        Dictionary<string, string> snapshot = SnapshotDirectory(fixture.RootPath);

        Assert.NotNull(await Record.ExceptionAsync(() => fixture.RestoreAsync()));

        Assert.Equal(snapshot, SnapshotDirectory(fixture.RootPath));
        AssertNoStaging(fixture.RootPath);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("empty")]
    [InlineData("format")]
    [InlineData("length")]
    [InlineData("hash")]
    [InlineData("duplicate")]
    [InlineData("traversal-forward")]
    [InlineData("traversal-backward")]
    [InlineData("absolute")]
    [InlineData("missing-database")]
    public async Task RestoreAsync_RejectsInvalidManifestWithoutPublishingOrChangingArchive(string mutation)
    {
        using RestoreFixture fixture = new();
        await fixture.CreateBackupAsync();
        if (mutation is "invalid-json" or "empty")
        {
            await File.WriteAllTextAsync(fixture.ManifestPath, mutation == "empty" ? "null" : "{");
        }
        else
        {
            JsonObject manifest = fixture.ReadManifest();
            JsonArray files = manifest["files"]!.AsArray();
            JsonObject database = files.Single(file => file!["path"]!.GetValue<string>() == "nutriflow.db")!.AsObject();
            switch (mutation)
            {
                case "format":
                    manifest["formatVersion"] = 999;
                    break;
                case "length":
                    database["length"] = database["length"]!.GetValue<long>() + 1;
                    break;
                case "hash":
                    database["sha256"] = new string('0', 64);
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
        }

        await AssertRestoreRejectedWithoutChangesAsync(fixture);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("database")]
    [InlineData("photo")]
    [InlineData("photos-directory")]
    [InlineData("extra-root-file")]
    [InlineData("extra-photo-file")]
    [InlineData("nested-directory")]
    [InlineData("photo-content")]
    [InlineData("database-content")]
    public async Task RestoreAsync_RejectsMissingUnexpectedOrTamperedFilesWithoutPublishing(string mutation)
    {
        using RestoreFixture fixture = new();
        await fixture.CreateBackupAsync();
        string backupPhotoPath = Path.Combine(fixture.BackupPath, "label-photos");
        switch (mutation)
        {
            case "manifest":
                File.Delete(fixture.ManifestPath);
                break;
            case "database":
                File.Delete(Path.Combine(fixture.BackupPath, "nutriflow.db"));
                break;
            case "photo":
                File.Delete(Path.Combine(backupPhotoPath, "label.jpg"));
                break;
            case "photos-directory":
                Directory.Delete(backupPhotoPath, recursive: true);
                break;
            case "extra-root-file":
                await File.WriteAllTextAsync(Path.Combine(fixture.BackupPath, "unexpected.txt"), "extra data");
                break;
            case "extra-photo-file":
                await File.WriteAllTextAsync(Path.Combine(backupPhotoPath, "unexpected.jpg"), "extra photo");
                break;
            case "nested-directory":
                Directory.CreateDirectory(Path.Combine(backupPhotoPath, "nested"));
                break;
            case "photo-content":
                await File.WriteAllBytesAsync(Path.Combine(backupPhotoPath, "label.jpg"), [9, 2, 3, 4]);
                break;
            case "database-content":
                await File.WriteAllTextAsync(Path.Combine(fixture.BackupPath, "nutriflow.db"), "not a database");
                break;
        }

        await AssertRestoreRejectedWithoutChangesAsync(fixture);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoreAsync_ChecksDatabaseIntegrityEvenWhenManifestHashMatches(bool corruptDatabase)
    {
        using RestoreFixture fixture = new();
        await fixture.CreateBackupAsync();
        string backupDatabasePath = Path.Combine(fixture.BackupPath, "nutriflow.db");
        if (corruptDatabase)
        {
            await File.WriteAllTextAsync(backupDatabasePath, "not a database");
        }
        else
        {
            await using SqliteConnection connection = await RestoreFixture.OpenAsync(backupDatabasePath);
            await RestoreFixture.ExecuteAsync(connection,
                "PRAGMA foreign_keys=OFF; " +
                "CREATE TABLE Children (Id INTEGER PRIMARY KEY, ProductId INTEGER REFERENCES Products(Id)); " +
                "INSERT INTO Children (ProductId) VALUES (999);");
        }
        await fixture.RefreshDatabaseManifestAsync();

        await AssertRestoreRejectedWithoutChangesAsync(fixture);
    }

    [Fact]
    public async Task RestoreAsync_RejectsMissingBackupWithoutCreatingItOrDestination()
    {
        using RestoreFixture fixture = new();
        Directory.CreateDirectory(fixture.RootPath);

        Assert.NotNull(await Record.ExceptionAsync(() => fixture.RestoreAsync()));

        Assert.Empty(Directory.GetFileSystemEntries(fixture.RootPath));
    }

    [Theory]
    [InlineData("destination-inside-backup")]
    [InlineData("backup-inside-destination")]
    [InlineData("same-path")]
    public async Task RestoreAsync_RejectsOverlappingPathsWithoutChangingArchive(string overlap)
    {
        using RestoreFixture fixture = new();
        await fixture.CreateBackupAsync();
        Dictionary<string, string> snapshot = SnapshotDirectory(fixture.RootPath);
        string destination = overlap switch
        {
            "destination-inside-backup" => Path.Combine(fixture.BackupPath, "restored"),
            "backup-inside-destination" => fixture.RootPath,
            _ => fixture.BackupPath
        };

        Assert.NotNull(await Record.ExceptionAsync(() => DataBackup.RestoreAsync(fixture.BackupPath, destination)));

        Assert.Equal(snapshot, SnapshotDirectory(fixture.RootPath));
        Assert.False(Directory.Exists(Path.Combine(fixture.BackupPath, "restored")));
        AssertNoStaging(fixture.RootPath);
    }

    [Fact]
    public async Task RestoreAsync_CancellationDoesNotPublishOrChangeAnyFiles()
    {
        using RestoreFixture fixture = new();
        await fixture.CreateBackupAsync();
        Dictionary<string, string> snapshot = SnapshotDirectory(fixture.RootPath);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DataBackup.RestoreAsync(fixture.BackupPath, fixture.DestinationPath, cancellation.Token));

        Assert.False(Directory.Exists(fixture.DestinationPath));
        Assert.Equal(snapshot, SnapshotDirectory(fixture.RootPath));
        AssertNoStaging(fixture.RootPath);
    }

    [Fact]
    public async Task RestoreAsync_ConcurrentSameDestinationPublishesExactlyOneVerifiedCopyWithoutLeavingStaging()
    {
        using RestoreFixture fixture = new();
        BackupManifest manifest = await fixture.CreateBackupAsync();
        Dictionary<string, string> sourceSnapshot = SnapshotDirectory(fixture.SourcePath);
        Dictionary<string, string> backupSnapshot = SnapshotDirectory(fixture.BackupPath);
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>[] attempts = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                await fixture.RestoreAsync();
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        })).ToArray();

        start.SetResult();
        bool[] results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(result => result));
        AssertRestoredFilesMatch(fixture, manifest);
        Assert.Equal(sourceSnapshot, SnapshotDirectory(fixture.SourcePath));
        Assert.Equal(backupSnapshot, SnapshotDirectory(fixture.BackupPath));
        AssertNoStaging(fixture.RootPath);
    }

    [Theory]
    [InlineData("backup-link")]
    [InlineData("destination-parent-link")]
    [InlineData("photo-directory-link")]
    public async Task RestoreAsync_RejectsDirectoryLinksWithoutChangingLinkTargets(string linkKind)
    {
        using RestoreFixture fixture = new();
        await fixture.CreateBackupAsync();
        string backupPath = fixture.BackupPath;
        string destinationPath = fixture.DestinationPath;
        if (linkKind == "photo-directory-link")
        {
            string backupPhotoPath = Path.Combine(fixture.BackupPath, "label-photos");
            Directory.Delete(backupPhotoPath, recursive: true);
            fixture.CreateDirectoryLink(backupPhotoPath, fixture.PhotosPath);
        }
        else
        {
            string linkPath = Path.Combine(fixture.RootPath, "directory-link");
            fixture.CreateDirectoryLink(linkPath, fixture.BackupPath);
            if (linkKind == "backup-link")
            {
                backupPath = linkPath;
            }
            else
            {
                destinationPath = Path.Combine(linkPath, "restored");
            }
        }
        Dictionary<string, string> sourceSnapshot = SnapshotDirectory(fixture.SourcePath);
        Dictionary<string, string> backupSnapshot = SnapshotDirectory(fixture.BackupPath);

        Assert.NotNull(await Record.ExceptionAsync(() => DataBackup.RestoreAsync(backupPath, destinationPath)));

        Assert.False(Directory.Exists(destinationPath));
        Assert.Equal(sourceSnapshot, SnapshotDirectory(fixture.SourcePath));
        Assert.Equal(backupSnapshot, SnapshotDirectory(fixture.BackupPath));
        AssertNoStaging(fixture.RootPath);
    }

    private static async Task AssertRestoreRejectedWithoutChangesAsync(RestoreFixture fixture)
    {
        Dictionary<string, string> snapshot = SnapshotDirectory(fixture.RootPath);

        Assert.NotNull(await Record.ExceptionAsync(() => fixture.RestoreAsync()));

        Assert.False(File.Exists(fixture.DestinationPath));
        Assert.False(Directory.Exists(fixture.DestinationPath));
        Assert.Equal(snapshot, SnapshotDirectory(fixture.RootPath));
        AssertNoStaging(fixture.RootPath);
    }

    private static void AssertRestoredFilesMatch(RestoreFixture fixture, BackupManifest manifest)
    {
        Assert.Equal(new[] { "label-photos", "nutriflow.db" },
            Directory.GetFileSystemEntries(fixture.DestinationPath).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(manifest.Files.Select(file => file.Path).Order(StringComparer.Ordinal),
            Directory.GetFiles(fixture.DestinationPath, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(fixture.DestinationPath, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal));
        foreach (BackupFile file in manifest.Files)
        {
            string restoredPath = Path.Combine(fixture.DestinationPath, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(file.Length, new FileInfo(restoredPath).Length);
            Assert.Equal(file.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(restoredPath))), ignoreCase: true);
            Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.BackupPath, file.Path)), File.ReadAllBytes(restoredPath));
        }
    }

    private static void AssertNoStaging(string rootPath) =>
        Assert.Empty(Directory.GetDirectories(rootPath, "*.partial", SearchOption.AllDirectories));

    private static Dictionary<string, string> SnapshotDirectory(string directoryPath) =>
        Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToDictionary(path => Path.GetRelativePath(directoryPath, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    private sealed class RestoreFixture : IDisposable
    {
        private readonly List<string> directoryLinks = [];

        public string RootPath { get; } = Path.Combine(Path.GetTempPath(), $"nutriflow-restore-tests-{Guid.NewGuid():N}");
        public string SourcePath => Path.Combine(RootPath, "source");
        public string DatabasePath => Path.Combine(SourcePath, "nutriflow.db");
        public string PhotosPath => Path.Combine(SourcePath, "label-photos");
        public string BackupPath => Path.Combine(RootPath, "backup");
        public string DestinationPath => Path.Combine(RootPath, "restored");
        public string ManifestPath => Path.Combine(BackupPath, "manifest.json");

        public async Task<BackupManifest> CreateBackupAsync(bool hasPhotoTable = true)
        {
            Directory.CreateDirectory(PhotosPath);
            await File.WriteAllBytesAsync(Path.Combine(PhotosPath, "label.jpg"), [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(Path.Combine(PhotosPath, "legacy.png"), [5, 6, 7]);
            await using (SqliteConnection connection = await OpenAsync(DatabasePath))
            {
                await ExecuteAsync(connection,
                    "CREATE TABLE Products (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, SourceReference TEXT); " +
                    "INSERT INTO Products (Name, SourceReference) VALUES ('Рагу', 'label-photo:legacy.png');");
                if (hasPhotoTable)
                {
                    await ExecuteAsync(connection,
                        "CREATE TABLE LabelPhotos (Id INTEGER PRIMARY KEY, FileName TEXT NOT NULL); " +
                        "INSERT INTO LabelPhotos (FileName) VALUES ('label.jpg');");
                }
            }
            return await DataBackup.CreateAsync(DatabasePath, PhotosPath, BackupPath);
        }

        public Task<BackupManifest> RestoreAsync() => DataBackup.RestoreAsync(BackupPath, DestinationPath);

        public JsonObject ReadManifest() => JsonNode.Parse(File.ReadAllText(ManifestPath))!.AsObject();

        public Task WriteManifestAsync(JsonObject manifest) =>
            File.WriteAllTextAsync(ManifestPath, manifest.ToJsonString(JsonOptions));

        public async Task RefreshDatabaseManifestAsync()
        {
            JsonObject manifest = ReadManifest();
            JsonObject database = manifest["files"]!.AsArray()
                .Single(file => file!["path"]!.GetValue<string>() == "nutriflow.db")!.AsObject();
            string path = Path.Combine(BackupPath, "nutriflow.db");
            database["length"] = new FileInfo(path).Length;
            database["sha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
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
