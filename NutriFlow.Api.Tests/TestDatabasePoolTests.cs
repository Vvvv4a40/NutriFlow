using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace NutriFlow.Api.Tests;

public sealed class TestDatabasePoolTests
{
    [Fact]
    public void Clear_ClosedOwnedConnection_ReleasesPooledHandle()
    {
        using TemporaryDatabases files = new();
        using SqliteConnection connection = new($"Data Source={files.OwnedDatabasePath}");
        connection.Open();
        sqlite3 originalHandle = Assert.IsType<sqlite3>(connection.Handle);
        connection.Close();
        Assert.False(originalHandle.IsClosed);

        TestDatabasePool.Clear(files.OwnedDatabasePath);

        Assert.True(originalHandle.IsClosed);
        connection.Open();
        Assert.NotSame(originalHandle, connection.Handle);
        Assert.False(Assert.IsType<sqlite3>(connection.Handle).IsClosed);
    }

    [Fact]
    public void Clear_DifferentDatabase_PreservesAndReusesUnrelatedPooledHandle()
    {
        using TemporaryDatabases files = new();
        using SqliteConnection ownedConnection = new($"Data Source={files.OwnedDatabasePath}");
        ownedConnection.Open();
        sqlite3 ownedHandle = Assert.IsType<sqlite3>(ownedConnection.Handle);
        ownedConnection.Close();
        using SqliteConnection unrelatedConnection = new($"Data Source={files.UnrelatedDatabasePath}");
        unrelatedConnection.Open();
        sqlite3 unrelatedHandle = Assert.IsType<sqlite3>(unrelatedConnection.Handle);
        unrelatedConnection.Close();
        Assert.False(unrelatedHandle.IsClosed);

        TestDatabasePool.Clear(files.OwnedDatabasePath);

        Assert.True(ownedHandle.IsClosed);
        Assert.False(unrelatedHandle.IsClosed);
        unrelatedConnection.Open();
        Assert.Same(unrelatedHandle, unrelatedConnection.Handle);
    }

    [Fact]
    public void Clear_MissingDatabase_DoesNotCreateDatabaseFile()
    {
        using TemporaryDatabases files = new();
        Assert.False(File.Exists(files.OwnedDatabasePath));

        TestDatabasePool.Clear(files.OwnedDatabasePath);

        Assert.False(File.Exists(files.OwnedDatabasePath));
        Assert.Empty(Directory.GetFiles(files.DirectoryPath));
    }

    [Fact]
    public async Task FactoryDisposal_PreservesAndReusesUnrelatedPooledHandle()
    {
        using TemporaryDatabases files = new();
        using SqliteConnection unrelatedConnection = new($"Data Source={files.UnrelatedDatabasePath}");
        unrelatedConnection.Open();
        sqlite3 unrelatedHandle = Assert.IsType<sqlite3>(unrelatedConnection.Handle);
        unrelatedConnection.Close();

        await using (TestApiFactory factory = new(databasePath: files.OwnedDatabasePath, seedDemoData: false))
        {
            using HttpClient client = factory.CreateClient();
        }

        Assert.False(unrelatedHandle.IsClosed);
        unrelatedConnection.Open();
        Assert.Same(unrelatedHandle, unrelatedConnection.Handle);
    }

    private sealed class TemporaryDatabases : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(
            Path.GetTempPath(), $"nutriflow-test-pool-{Guid.NewGuid():N}");

        public TemporaryDatabases()
        {
            Directory.CreateDirectory(DirectoryPath);
        }

        public string OwnedDatabasePath => Path.Combine(DirectoryPath, "owned.db");
        public string UnrelatedDatabasePath => Path.Combine(DirectoryPath, "unrelated.db");

        public void Dispose()
        {
            TestDatabasePool.Clear(OwnedDatabasePath);
            TestDatabasePool.Clear(UnrelatedDatabasePath);
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
