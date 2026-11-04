using Microsoft.Data.Sqlite;
using NutriFlow.Infrastructure;
using NutriFlow.Infrastructure.Backups;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Mobile.Core;

// databases of profile backups: the profile file next to its photos by default; the browser keeps them elsewhere
public class LocalProfileBackupDatabases : BackupDatabaseFiles
{
    protected const long MaximumDatabaseBytes = 512L * 1024 * 1024;

    public static new LocalProfileBackupDatabases Native { get; } = new();

    // a consistent copy of the profile database while the profile stays in use
    public virtual async Task SnapshotProfileAsync(string profileDatabasePath, Guid profileId, string destinationPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(profileDatabasePath))
        {
            await using NutriFlowDbContext initialized = await LocalProfileDatabase.OpenAsync(profileDatabasePath, profileId, cancellationToken);
        }
        if (new FileInfo(profileDatabasePath).Length > MaximumDatabaseBytes)
        {
            throw new InvalidDataException("База профиля превышает предел резервной копии 512 МБ.");
        }
        await using SqliteConnection source = Open(profileDatabasePath, SqliteOpenMode.ReadOnly);
        await using SqliteConnection snapshot = Open(destinationPath, SqliteOpenMode.ReadWriteCreate);
        await source.OpenAsync(cancellationToken);
        await snapshot.OpenAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(snapshot);
    }

    // takes the checked database of a restored profile before the profile is published; here it moves with the folder
    public virtual Task InstallRestoredAsync(Guid profileId, string preparedDirectory, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    // the restored profile was not published: removes what InstallRestoredAsync took
    public virtual Task DiscardRestoredAsync(Guid profileId, CancellationToken cancellationToken) => Task.CompletedTask;

    private static SqliteConnection Open(string path, SqliteOpenMode mode) => new(
        new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, ForeignKeys = true }.ToString());
}
