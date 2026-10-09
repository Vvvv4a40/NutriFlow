using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure;

public static class LocalProfileDatabase
{
    public static async Task<NutriFlowDbContext> OpenAsync(
        string databasePath,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!Path.IsPathFullyQualified(databasePath))
        {
            throw new ArgumentException("The profile database path must be absolute.", nameof(databasePath));
        }

        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An owner ID cannot be empty.", nameof(ownerId));
        }

        string fullPath = Path.GetFullPath(databasePath);
        if (string.IsNullOrEmpty(Path.GetFileName(fullPath)) || Directory.Exists(fullPath))
        {
            throw new ArgumentException("The profile database path must identify a file.", nameof(databasePath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Pooling = false
        }.ToString();
        DbContextOptions<NutriFlowDbContext> options =
            new DbContextOptionsBuilder<NutriFlowDbContext>()
                .UseSqlite(connectionString)
                .Options;
        NutriFlowDbContext context = new(options);

        try
        {
            await context.Database.MigrateAsync(cancellationToken);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Users" ("Id", "CreatedAtUtc", "IsLegacyLocal", "LegacyLabelPhotosImported")
                VALUES ({ownerId}, {DateTimeOffset.UtcNow}, {false}, {false})
                ON CONFLICT ("Id") DO NOTHING
                """, cancellationToken);
            return context;
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }
}
