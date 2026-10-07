using Microsoft.Data.Sqlite;

namespace NutriFlow.Api.Tests;

internal static class TestDatabasePool
{
    public static void Clear(string databasePath)
    {
        using SqliteConnection connection = new($"Data Source={databasePath}");
        SqliteConnection.ClearPool(connection);
    }
}
