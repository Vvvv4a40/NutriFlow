using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure;

public sealed class DailyDiaryStore
{
    private readonly NutriFlowDbContext _dbContext;
    private readonly Guid _ownerId;

    public DailyDiaryStore(NutriFlowDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
        _ownerId = dbContext.Users
            .AsNoTracking()
            .Where(user => user.IsLegacyLocal)
            .Select(user => user.Id)
            .Single();
    }

    public DailyDiaryStore(NutriFlowDbContext dbContext, Guid ownerId)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("Owner ID cannot be empty.", nameof(ownerId));
        }

        _dbContext = dbContext;
        _ownerId = ownerId;
    }

    public async Task SetGoalAsync(
        DateOnly date,
        DailyGoal goal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goal);

        NutritionValues nutrition = goal.TargetNutrition;
        int updatedCount = await UpdateGoalAsync(date, nutrition, cancellationToken);

        if (updatedCount > 0)
        {
            return;
        }

        DailyGoalRecord record = new DailyGoalRecord
        {
            UserId = _ownerId,
            Date = date,
            Calories = nutrition.Calories,
            ProteinGrams = nutrition.ProteinGrams,
            FatGrams = nutrition.FatGrams,
            CarbohydratesGrams = nutrition.CarbohydratesGrams
        };
        _dbContext.DailyGoals.Add(record);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _dbContext.Entry(record).State = EntityState.Detached;
            updatedCount = await UpdateGoalAsync(date, nutrition, cancellationToken);

            if (updatedCount == 0)
            {
                throw;
            }
        }
        catch
        {
            _dbContext.Entry(record).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<DailyGoal?> FindGoalAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        DailyGoalRecord? record = await _dbContext.DailyGoals
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.UserId == _ownerId && item.Date == date,
                cancellationToken);

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
        return (await GetStoredEntriesAsync(date, cancellationToken))
            .Select(entry => entry.Entry).ToArray();
    }

    public async Task<IReadOnlyList<MealEntry>> GetSessionEntriesAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        return (await GetStoredSessionEntriesAsync(sessionId, cancellationToken))
            .Select(entry => entry.Entry).ToArray();
    }

    public async Task<IReadOnlyList<StoredMealEntry>> GetStoredEntriesAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        List<MealEntryRecord> records = await GetEligibleEntries()
            .Where(entry => entry.MealDate == date &&
                (entry.Adjustment == null || !entry.Adjustment.IsDeleted))
            .ToListAsync(cancellationToken);

        return records
            .OrderBy(entry => entry.CreatedAtUtc)
            .ThenBy(entry => entry.MealSessionId)
            .ThenBy(entry => entry.Sequence)
            .Select(MapStoredEntry)
            .ToArray();
    }

    public async Task<IReadOnlyList<StoredMealEntry>> GetStoredSessionEntriesAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        List<MealEntryRecord> records = await GetEligibleEntries()
            .Where(entry => entry.MealSessionId == sessionId &&
                (entry.Adjustment == null || !entry.Adjustment.IsDeleted))
            .OrderBy(entry => entry.Sequence)
            .ToListAsync(cancellationToken);

        return records.Select(MapStoredEntry).ToArray();
    }

    public async Task<StoredMealEntry?> FindEntryAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        MealEntryRecord? record = await GetEligibleEntries()
            .SingleOrDefaultAsync(entry => entry.Id == id &&
                (entry.Adjustment == null || !entry.Adjustment.IsDeleted), cancellationToken);

        return record is null ? null : MapStoredEntry(record);
    }

    public Task<MealEntryChangeResult> UpdateEntryWeightAsync(
        int id,
        int expectedRevision,
        decimal weightInGrams,
        DataQuality weightQuality = DataQuality.Exact,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weightInGrams);

        if (weightQuality is not (DataQuality.Exact or DataQuality.Estimated))
        {
            throw new ArgumentOutOfRangeException(nameof(weightQuality));
        }

        return ChangeEntryAsync(id, expectedRevision, weightInGrams, weightQuality, cancellationToken);
    }

    public Task<MealEntryChangeResult> DeleteEntryAsync(
        int id,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);

        return ChangeEntryAsync(id, expectedRevision, null, DataQuality.Exact, cancellationToken);
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
                session.UserId == _ownerId &&
                session.Purpose == MealSessionPurpose.Diary &&
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
                    session =>
                        session.Id == sessionId &&
                        session.UserId == _ownerId,
                    cancellationToken);

            if (current is null)
            {
                throw new KeyNotFoundException(
                    $"Meal session '{sessionId}' was not found.");
            }

            if (current.Purpose != MealSessionPurpose.Diary)
            {
                return MealSessionConfirmationResult.NotReady;
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
            .Where(session =>
                session.Id == sessionId &&
                session.UserId == _ownerId)
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
                Quality = entry.Quality,
                CreatedAtUtc = now
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return MealSessionConfirmationResult.Confirmed;
    }

    private Task<int> UpdateGoalAsync(
        DateOnly date,
        NutritionValues nutrition,
        CancellationToken cancellationToken)
    {
        return _dbContext.DailyGoals
            .Where(item => item.UserId == _ownerId && item.Date == date)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Calories, nutrition.Calories)
                    .SetProperty(item => item.ProteinGrams, nutrition.ProteinGrams)
                    .SetProperty(item => item.FatGrams, nutrition.FatGrams)
                    .SetProperty(item => item.CarbohydratesGrams, nutrition.CarbohydratesGrams),
                cancellationToken);
    }

    private IQueryable<MealEntryRecord> GetEligibleEntries()
    {
        return _dbContext.MealEntries
            .AsNoTracking()
            .Include(entry => entry.Adjustment)
            .Where(entry => entry.MealSession.UserId == _ownerId &&
                entry.MealSession.Status == MealSessionStatus.Confirmed &&
                entry.MealSession.Purpose == MealSessionPurpose.Diary);
    }

    private async Task<MealEntryChangeResult> ChangeEntryAsync(
        int id,
        int expectedRevision,
        decimal? weightInGrams,
        DataQuality weightQuality,
        CancellationToken cancellationToken)
    {
        bool isDeletion = weightInGrams is null;
        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        MealEntryRecord? record = await GetEligibleEntries()
            .SingleOrDefaultAsync(entry => entry.Id == id, cancellationToken);

        if (record is null)
        {
            return new MealEntryChangeResult(MealEntryChangeKind.NotFound);
        }

        if (record.Adjustment?.IsDeleted == true)
        {
            return new MealEntryChangeResult(isDeletion
                ? MealEntryChangeKind.Deleted
                : MealEntryChangeKind.NotFound);
        }

        StoredMealEntry current = MapStoredEntry(record);

        if (current.Revision != expectedRevision)
        {
            return new MealEntryChangeResult(MealEntryChangeKind.RevisionConflict, current);
        }

        MealEntry? changedEntry = null;
        if (!isDeletion)
        {
            try
            {
                changedEntry = MapOriginalEntry(record).WithWeight(weightInGrams!.Value, weightQuality);
            }
            catch (OverflowException)
            {
                throw new ArgumentOutOfRangeException(nameof(weightInGrams), weightInGrams,
                    "The proposed portion weight produces nutrition values outside the supported range.");
            }
        }

        if (current.Revision == int.MaxValue)
        {
            throw new InvalidDataException("The stored meal entry revision cannot be advanced.");
        }

        int nextRevision = current.Revision + 1;
        decimal adjustedWeight = weightInGrams ?? current.Entry.WeightInGrams;
        DataQuality adjustedWeightQuality = isDeletion
            ? record.Adjustment?.WeightQuality ?? DataQuality.Exact
            : weightQuality;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MealEntryAdjustmentRecord? addedAdjustment = null;

        try
        {
            if (record.Adjustment is null)
            {
                addedAdjustment = new MealEntryAdjustmentRecord
                {
                    MealEntryId = id,
                    WeightInGrams = adjustedWeight,
                    WeightQuality = adjustedWeightQuality,
                    Revision = nextRevision,
                    IsDeleted = isDeletion,
                    UpdatedAtUtc = now
                };
                _dbContext.MealEntryAdjustments.Add(addedAdjustment);

                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            else
            {
                int updatedCount = await _dbContext.MealEntryAdjustments
                    .Where(adjustment => adjustment.MealEntryId == id &&
                        adjustment.Revision == expectedRevision && !adjustment.IsDeleted &&
                        adjustment.MealEntry.MealSession.UserId == _ownerId &&
                        adjustment.MealEntry.MealSession.Status == MealSessionStatus.Confirmed &&
                        adjustment.MealEntry.MealSession.Purpose == MealSessionPurpose.Diary)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(adjustment => adjustment.WeightInGrams, adjustedWeight)
                        .SetProperty(adjustment => adjustment.WeightQuality, adjustedWeightQuality)
                        .SetProperty(adjustment => adjustment.Revision, nextRevision)
                        .SetProperty(adjustment => adjustment.IsDeleted, isDeletion)
                        .SetProperty(adjustment => adjustment.UpdatedAtUtc, now), cancellationToken);

                if (updatedCount == 0)
                {
                    MealEntryRecord? latest = await GetEligibleEntries()
                        .SingleOrDefaultAsync(entry => entry.Id == id, cancellationToken);

                    if (latest is null || latest.Adjustment?.IsDeleted == true)
                    {
                        return new MealEntryChangeResult(isDeletion && latest is not null
                            ? MealEntryChangeKind.Deleted
                            : MealEntryChangeKind.NotFound);
                    }

                    return new MealEntryChangeResult(MealEntryChangeKind.RevisionConflict, MapStoredEntry(latest));
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (addedAdjustment is not null)
            {
                _dbContext.Entry(addedAdjustment).State = EntityState.Detached;
            }

            throw;
        }

        return isDeletion
            ? new MealEntryChangeResult(MealEntryChangeKind.Deleted)
            : new MealEntryChangeResult(MealEntryChangeKind.Updated,
                new StoredMealEntry(record.Id, nextRevision, record.MealDate, record.MealSessionId, changedEntry!));
    }

    private static StoredMealEntry MapStoredEntry(MealEntryRecord record)
    {
        try
        {
            MealEntry entry = MapOriginalEntry(record);
            int revision = 0;

            if (record.Adjustment is not null)
            {
                if (record.Adjustment.Revision <= 0)
                {
                    throw new InvalidDataException("The stored meal entry revision is invalid.");
                }

                revision = record.Adjustment.Revision;
                entry = entry.WithWeight(record.Adjustment.WeightInGrams, record.Adjustment.WeightQuality);
            }

            return new StoredMealEntry(record.Id, revision, record.MealDate, record.MealSessionId, entry);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new InvalidDataException("The stored meal entry is invalid.", exception);
        }
    }

    private static MealEntry MapOriginalEntry(MealEntryRecord record)
    {
        return new MealEntry(
            record.Name,
            record.WeightInGrams,
            new NutritionValues(
                record.Calories,
                record.ProteinGrams,
                record.FatGrams,
                record.CarbohydratesGrams),
            record.Quality);
    }
}
