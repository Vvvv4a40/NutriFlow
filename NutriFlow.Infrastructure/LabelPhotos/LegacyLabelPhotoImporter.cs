using Microsoft.EntityFrameworkCore;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.LabelPhotos;

public static class LegacyLabelPhotoImporter
{
    public static async Task ImportAsync(
        NutriFlowDbContext dbContext,
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        string fullPath = Path.GetFullPath(storagePath);
        Guid ownerId = await dbContext.Users.AsNoTracking()
            .Where(user => user.IsLegacyLocal)
            .Select(user => user.Id)
            .SingleAsync(cancellationToken);
        Directory.CreateDirectory(fullPath);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        int claimed = await dbContext.Users
            .Where(user => user.Id == ownerId && !user.LegacyLabelPhotosImported)
            .ExecuteUpdateAsync(
                update => update.SetProperty(user => user.LegacyLabelPhotosImported, true),
                cancellationToken);

        if (claimed == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        DateTimeOffset registeredAtUtc = DateTimeOffset.UtcNow;

        foreach (string filePath in Directory.EnumerateFiles(fullPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(filePath);

            if (LabelPhotoStore.FindFile(fullPath, fileName) is null)
            {
                continue;
            }

            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "LabelPhotos" ("FileName", "UserId", "RegisteredAtUtc")
                VALUES ({fileName}, {ownerId}, {registeredAtUtc})
                ON CONFLICT ("FileName") DO NOTHING
                """, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
