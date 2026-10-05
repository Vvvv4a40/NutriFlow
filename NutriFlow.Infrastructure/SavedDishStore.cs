using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure;

public sealed class SavedDishStore
{
    private readonly NutriFlowDbContext _dbContext;
    private readonly Guid _ownerId;

    public SavedDishStore(NutriFlowDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
        _ownerId = dbContext.Users
            .AsNoTracking()
            .Where(user => user.IsLegacyLocal)
            .Select(user => user.Id)
            .Single();
    }

    public SavedDishStore(NutriFlowDbContext dbContext, Guid ownerId)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An owner ID cannot be empty.", nameof(ownerId));
        }

        _dbContext = dbContext;
        _ownerId = ownerId;
    }

    public async Task<StoredSavedDish?> FindAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        SavedDishRecord? record = await _dbContext.SavedDishes
            .AsNoTracking()
            .SingleOrDefaultAsync(
                dish => dish.Id == id && dish.UserId == _ownerId,
                cancellationToken);

        return record is null ? null : MapDish(record);
    }

    public async Task<StoredSavedDish?> FindBySessionIdAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        SavedDishRecord? record = await _dbContext.SavedDishes
            .AsNoTracking()
            .SingleOrDefaultAsync(
                dish => dish.SourceSessionId == sessionId && dish.UserId == _ownerId,
                cancellationToken);

        return record is null ? null : MapDish(record);
    }

    public async Task<StoredSavedDish?> FindByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        string normalizedName = NormalizeName(name);
        SavedDishRecord? record = await _dbContext.SavedDishes
            .AsNoTracking()
            .SingleOrDefaultAsync(
                dish => dish.UserId == _ownerId && dish.NormalizedName == normalizedName,
                cancellationToken);

        return record is null ? null : MapDish(record);
    }

    public async Task<IReadOnlyList<StoredSavedDish>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        List<SavedDishRecord> records = await _dbContext.SavedDishes
            .AsNoTracking()
            .Where(dish => dish.UserId == _ownerId)
            .OrderBy(dish => dish.NormalizedName)
            .ThenBy(dish => dish.Id)
            .ToListAsync(cancellationToken);

        return records.Select(MapDish).ToArray();
    }

    public async Task<IReadOnlyList<string>> GetNamesAsync(
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.SavedDishes
            .AsNoTracking()
            .Where(dish => dish.UserId == _ownerId)
            .OrderBy(dish => dish.NormalizedName)
            .Select(dish => dish.Name)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<MealSessionConfirmationResult> ConfirmSessionAsync(
        Guid sessionId,
        string expectedPreviewToken,
        string name,
        decimal finalWeightInGrams,
        NutritionValues nutritionPer100Grams,
        DataQuality quality,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPreviewToken);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(finalWeightInGrams);
        string normalizedName = NormalizeName(name);
        Guid dishId = Guid.NewGuid();
        Product product = CreateProduct(dishId, name.Trim().Normalize(), nutritionPer100Grams, quality);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        int claimedCount = await _dbContext.MealSessions
            .Where(session =>
                session.Id == sessionId &&
                session.UserId == _ownerId &&
                session.Purpose == MealSessionPurpose.CreateDish &&
                session.Status == MealSessionStatus.ReadyForConfirmation &&
                session.PreviewToken == expectedPreviewToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(session => session.Status, MealSessionStatus.Confirmed)
                    .SetProperty(session => session.UpdatedAtUtc, now)
                    .SetProperty(session => session.ConfirmedAtUtc, now),
                cancellationToken);

        if (claimedCount == 0)
        {
            MealSessionRecord? current = await _dbContext.MealSessions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    session => session.Id == sessionId && session.UserId == _ownerId,
                    cancellationToken);

            if (current is null)
            {
                throw new KeyNotFoundException($"Meal session '{sessionId}' was not found.");
            }

            if (current.Purpose != MealSessionPurpose.CreateDish)
            {
                return MealSessionConfirmationResult.NotReady;
            }

            if (current.Status == MealSessionStatus.Confirmed)
            {
                return MealSessionConfirmationResult.AlreadyConfirmed;
            }

            return current.Status == MealSessionStatus.ReadyForConfirmation
                ? MealSessionConfirmationResult.StalePreview
                : MealSessionConfirmationResult.NotReady;
        }

        SavedDishRecord record = new SavedDishRecord
        {
            Id = dishId,
            UserId = _ownerId,
            SourceSessionId = sessionId,
            Name = product.Name,
            NormalizedName = normalizedName,
            FinalWeightInGrams = finalWeightInGrams,
            Calories = nutritionPer100Grams.Calories,
            ProteinGrams = nutritionPer100Grams.ProteinGrams,
            FatGrams = nutritionPer100Grams.FatGrams,
            CarbohydratesGrams = nutritionPer100Grams.CarbohydratesGrams,
            Quality = quality,
            CreatedAtUtc = now
        };
        _dbContext.SavedDishes.Add(record);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            _dbContext.Entry(record).State = EntityState.Detached;
            await transaction.RollbackAsync(CancellationToken.None);
            await transaction.DisposeAsync();

            if (await _dbContext.SavedDishes
                .AsNoTracking()
                .AnyAsync(
                    dish => dish.UserId == _ownerId && dish.NormalizedName == normalizedName,
                    CancellationToken.None))
            {
                throw new MealSessionConflictException(
                    "A saved dish with this name already exists. Choose another name.");
            }

            throw;
        }
        catch
        {
            _dbContext.Entry(record).State = EntityState.Detached;
            throw;
        }

        return MealSessionConfirmationResult.Confirmed;
    }

    private static StoredSavedDish MapDish(SavedDishRecord record)
    {
        return new StoredSavedDish(
            record.Id,
            record.SourceSessionId,
            CreateProduct(
                record.Id,
                record.Name,
                new NutritionValues(
                    record.Calories,
                    record.ProteinGrams,
                    record.FatGrams,
                    record.CarbohydratesGrams),
                record.Quality),
            record.FinalWeightInGrams,
            record.CreatedAtUtc);
    }

    private static Product CreateProduct(
        Guid id,
        string name,
        NutritionValues nutrition,
        DataQuality quality)
    {
        return new Product(
            name,
            nutrition,
            new NutritionSource(
                NutritionSourceKind.SavedDish,
                quality,
                "Saved NutriFlow dish",
                $"saved-dish:{id:N}"));
    }

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalizedName = name.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant();

        if (normalizedName.Length > 200)
        {
            throw new ArgumentException(
                "A saved dish name cannot exceed 200 characters.",
                nameof(name));
        }

        return normalizedName;
    }
}
