using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Backups;

// sqlite work of backups on database files: the native engine by default; a host without it (the browser) brings its own
public class BackupDatabaseFiles
{
    public static BackupDatabaseFiles Native { get; } = new();

    // a consistent copy of a database file
    public virtual async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using SqliteConnection source = Open(sourcePath, SqliteOpenMode.ReadWrite);
        await using SqliteConnection destination = Open(destinationPath, SqliteOpenMode.ReadWriteCreate);
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);
    }

    // runs work on an open connection to the file; changes of writable work stay in the file once it completes
    public virtual async Task<T> UseAsync<T>(string path, bool writable,
        Func<DbConnection, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using SqliteConnection connection = Open(path, writable ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(cancellationToken);
        return await work(connection, cancellationToken);
    }

    // an ef context on an open connection of UseAsync, for the model and the migration list; the connection stays open
    public virtual NutriFlowDbContext CreateContext(DbConnection connection) =>
        new(new DbContextOptionsBuilder<NutriFlowDbContext>().UseSqlite(connection).Options);

    public static DbCommand Command(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);
        DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        Pooling = false,
        ForeignKeys = true,
        DefaultTimeout = 5
    }.ToString());
}
