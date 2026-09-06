using Microsoft.EntityFrameworkCore;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"nutriflow-workflow-{Guid.NewGuid():N}.db");

    public NutriFlowDbContext CreateContext()
    {
        DbContextOptions<NutriFlowDbContext> options =
            new DbContextOptionsBuilder<NutriFlowDbContext>()
                .UseSqlite($"Data Source={_databasePath};Pooling=False")
                .Options;

        return new NutriFlowDbContext(options);
    }

    public async Task MigrateAsync()
    {
        await using NutriFlowDbContext context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync()
    {
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");

        return ValueTask.CompletedTask;
    }
}
