using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure;

public sealed class DailyDiaryStore
{
    private readonly NutriFlowDbContext _dbContext;

    public DailyDiaryStore(NutriFlowDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task SetGoalAsync(
        DateOnly date,
        DailyGoal goal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goal);

        DailyGoalRecord? record = await _dbContext.DailyGoals
            .SingleOrDefaultAsync(item => item.Date == date, cancellationToken);

        if (record is null)
        {
            record = new DailyGoalRecord { Date = date };
            _dbContext.DailyGoals.Add(record);
        }

        record.Calories = goal.TargetNutrition.Calories;
        record.ProteinGrams = goal.TargetNutrition.ProteinGrams;
        record.FatGrams = goal.TargetNutrition.FatGrams;
        record.CarbohydratesGrams =
            goal.TargetNutrition.CarbohydratesGrams;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<DailyGoal?> FindGoalAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        DailyGoalRecord? record = await _dbContext.DailyGoals
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Date == date, cancellationToken);

        return record is null
            ? null
            : new DailyGoal(new NutritionValues(
                record.Calories,
                record.ProteinGrams,
                record.FatGrams,
                record.CarbohydratesGrams));
    }

    public async Task<IReadOnlyList<MealEntry>> GetEntriesAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        List<MealEntryRecord> records = await _dbContext.MealEntries
            .AsNoTracking()
            .Where(entry => entry.MealDate == date)
            .ToListAsync(cancellationToken);

        return records
            .OrderBy(entry => entry.CreatedAtUtc)
            .ThenBy(entry => entry.MealSessionId)
            .ThenBy(entry => entry.Sequence)
            .Select(MapEntry)
            .ToArray();
    }

    public async Task<IReadOnlyList<MealEntry>> GetSessionEntriesAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        List<MealEntryRecord> records = await _dbContext.MealEntries
            .AsNoTracking()
            .Where(entry => entry.MealSessionId == sessionId)
            .OrderBy(entry => entry.Sequence)
            .ToListAsync(cancellationToken);

        return records.Select(MapEntry).ToArray();
    }

    public async Task<MealSessionConfirmationResult> ConfirmSessionAsync(
        Guid sessionId,
        string expectedPreviewToken,
        IReadOnlyList<MealEntry> mealEntries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPreviewToken);
        ArgumentNullException.ThrowIfNull(mealEntries);

        if (mealEntries.Count == 0 || mealEntries.Any(entry => entry is null))
        {
            throw new ArgumentException(
                "Confirmation requires at least one valid meal entry.",
                nameof(mealEntries));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        int claimedSessionCount = await _dbContext.MealSessions
            .Where(session =>
                session.Id == sessionId &&
                session.Status == MealSessionStatus.ReadyForConfirmation &&
                session.PreviewToken == expectedPreviewToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        session => session.Status,
                        MealSessionStatus.Confirmed)
                    .SetProperty(session => session.UpdatedAtUtc, now)
                    .SetProperty(session => session.ConfirmedAtUtc, now),
                cancellationToken);

        if (claimedSessionCount == 0)
        {
            MealSessionRecord? current = await _dbContext.MealSessions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    session => session.Id == sessionId,
                    cancellationToken);

            if (current is null)
            {
                throw new KeyNotFoundException(
                    $"Meal session '{sessionId}' was not found.");
            }

            if (current.Status == MealSessionStatus.Confirmed)
            {
                return MealSessionConfirmationResult.AlreadyConfirmed;
            }

            return current.Status != MealSessionStatus.ReadyForConfirmation
                ? MealSessionConfirmationResult.NotReady
                : MealSessionConfirmationResult.StalePreview;
        }

        DateOnly mealDate = await _dbContext.MealSessions
            .AsNoTracking()
            .Where(session => session.Id == sessionId)
            .Select(session => session.MealDate)
            .SingleAsync(cancellationToken);

        for (int index = 0; index < mealEntries.Count; index++)
        {
            MealEntry entry = mealEntries[index];
            _dbContext.MealEntries.Add(new MealEntryRecord
            {
                MealSessionId = sessionId,
                Sequence = index,
                MealDate = mealDate,
                Name = entry.Name,
                WeightInGrams = entry.WeightInGrams,
                Calories = entry.Nutrition.Calories,
                ProteinGrams = entry.Nutrition.ProteinGrams,
                FatGrams = entry.Nutrition.FatGrams,
                CarbohydratesGrams = entry.Nutrition.CarbohydratesGrams,
                CreatedAtUtc = now
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return MealSessionConfirmationResult.Confirmed;
    }

    private static MealEntry MapEntry(MealEntryRecord record)
    {
        return new MealEntry(
            record.Name,
            record.WeightInGrams,
            new NutritionValues(
                record.Calories,
                record.ProteinGrams,
                record.FatGrams,
                record.CarbohydratesGrams));
    }
}
