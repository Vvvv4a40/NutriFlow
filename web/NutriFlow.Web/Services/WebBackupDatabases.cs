using System.Data.Common;
using System.Text;
using Bit.Bsqlite;
using Bit.Bsqlite.Data;
using Bit.Bsqlite.Interop;
using Microsoft.EntityFrameworkCore;
using NutriFlow.Infrastructure.Persistence;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Web.Services;

// sqlite of profile backups in the browser: there is no native engine, and profile databases live in opfs, not in the
// profile folder. A database file of a backup (in memory) is checked by bsqlite on a temporary opfs copy, and
// writable work is copied back from a snapshot of that copy.
public sealed class WebBackupDatabases(
    IBsqliteSessionFactory sessions,
    IBsqliteStorageBindingStore bindings,
    WebProfileDatabase profiles,
    OpfsFiles files) : LocalProfileBackupDatabases
{
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    private const string DatabaseDirectory = "nutriflow";
    private const string TemporaryPrefix = "backup-";

    internal static string TemporaryName() => $"{DatabaseDirectory}/{TemporaryPrefix}{Guid.NewGuid():N}.db";

    // a sqlite file image of an open database, consistent as of the last committed write
    internal static async Task<byte[]> ExportAsync(BsqliteConnection connection, CancellationToken cancellationToken)
    {
        // the opfs file system of wa-sqlite answers the first lock with «busy» and takes the file in the background;
        // queries retry, sqlite3_serialize does not. A read first takes the file, which then stays with this connection
        await using DbCommand read = connection.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
        await read.ExecuteScalarAsync(cancellationToken);
        return await connection.ExportAsync(cancellationToken);
    }

    // a page closed in the middle of a backup leaves its copies of the profile behind; only one page runs at a time
    public async Task RemoveLeftoversAsync()
    {
        IReadOnlyList<string> paths;
        try
        {
            paths = await files.ListAsync(DatabaseDirectory);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"NutriFlow: backup leftovers were not checked. {exception}");
            return;
        }

        foreach (string path in paths)
        {
            if (path.StartsWith(TemporaryPrefix, StringComparison.Ordinal) && path.EndsWith(".db", StringComparison.Ordinal))
            {
                await RemoveAsync($"{DatabaseDirectory}/{path}");
            }
        }
    }

    // the plain sqlite provider would need the native engine that only bsqlite sets up in the browser
    public override NutriFlowDbContext CreateContext(DbConnection connection) =>
        new(new DbContextOptionsBuilder<NutriFlowDbContext>().UseBitBsqlite(connection, contextOwnsConnection: false).Options);

    public override async Task SnapshotProfileAsync(string profileDatabasePath, Guid profileId, string destinationPath,
        CancellationToken cancellationToken)
    {
        byte[] image = await profiles.ExportAsync(profileId, cancellationToken);
        if (image.LongLength > MaximumDatabaseBytes)
        {
            throw new InvalidDataException("База профиля превышает предел резервной копии 512 МБ.");
        }

        await WriteNewAsync(destinationPath, image, cancellationToken);
    }

    public override async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        byte[] image = await UseAsync(sourcePath, writable: false,
            (connection, token) => ExportAsync((BsqliteConnection)connection, token), cancellationToken);
        await WriteNewAsync(destinationPath, image, cancellationToken);
    }

    public override async Task<T> UseAsync<T>(string path, bool writable,
        Func<DbConnection, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        byte[] image = await File.ReadAllBytesAsync(path, cancellationToken);
        // bsqlite reports a file that is not sqlite at all through script errors rather than a database error
        if (image.Length < 100 || !image.AsSpan(0, SqliteHeader.Length).SequenceEqual(SqliteHeader))
        {
            throw new InvalidDataException("The file is not a valid SQLite database.");
        }

        string name = TemporaryName();
        try
        {
            await files.WriteDatabaseAsync(name, image, cancellationToken);
            await using BsqliteSession session = await sessions.OpenAsync(name, cancellationToken);
            T result = await work(session.Connection, cancellationToken);
            if (writable)
            {
                await File.WriteAllBytesAsync(path, await ExportAsync((BsqliteConnection)session.Connection, cancellationToken),
                    cancellationToken);
            }

            return result;
        }
        catch (BsqliteQuotaExceededException exception)
        {
            // not a damaged backup: the backup code turns database errors into «повреждена»
            throw new IOException("QuotaExceededError: the browser storage is full.", exception);
        }
        finally
        {
            await RemoveAsync(name);
        }
    }

    public override async Task InstallRestoredAsync(Guid profileId, string preparedDirectory, CancellationToken cancellationToken)
    {
        string path = Path.Combine(preparedDirectory, "nutriflow.db");
        await files.WriteDatabaseAsync(WebProfileDatabase.DatabaseName(profileId), await File.ReadAllBytesAsync(path, cancellationToken),
            cancellationToken);
        // the folder goes to the mirrored profile files, the database is already where WebProfileDatabase opens it
        File.Delete(path);
    }

    public override Task DiscardRestoredAsync(Guid profileId, CancellationToken cancellationToken) =>
        RemoveAsync(WebProfileDatabase.DatabaseName(profileId));

    // a database nobody uses: the result of the backup does not depend on it, so a file that stays is only reported
    private async Task RemoveAsync(string name)
    {
        try
        {
            // the worker of a closed session lets the file go a moment later
            await WebProfileDatabase.RetryWhileFileLockedAsync(async token =>
            {
                await files.RemoveDatabaseAsync(name, token);
                return true;
            }, Task.Delay, CancellationToken.None);
            await bindings.RemoveAsync(name, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"NutriFlow: a backup database was not removed: {name}. {exception}");
        }
    }

    private static async Task WriteNewAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        await using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(content, cancellationToken);
    }
}
