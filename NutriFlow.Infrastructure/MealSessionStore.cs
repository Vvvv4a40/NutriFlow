using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure;

public sealed class MealSessionStore
{
    private const int MaxMessageRequestHashes = 50;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly NutriFlowDbContext _dbContext;
    private readonly Guid _ownerId;

    public MealSessionStore(NutriFlowDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
        _ownerId = dbContext.Users
            .AsNoTracking()
            .Where(user => user.IsLegacyLocal)
            .Select(user => user.Id)
            .Single();
    }

    public MealSessionStore(NutriFlowDbContext dbContext, Guid ownerId)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An owner ID cannot be empty.", nameof(ownerId));
        }

        _dbContext = dbContext;
        _ownerId = ownerId;
    }

    public Task<StoredMealSession> CreateAsync(
        IReadOnlyList<string> messages,
        MealDraft draft,
        string previewJson,
        string previewToken,
        MealSessionStatus status,
        DateOnly mealDate,
        CancellationToken cancellationToken = default)
    {
        return CreateCoreAsync(
            messages,
            draft,
            previewJson,
            previewToken,
            status,
            mealDate,
            null,
            null,
            cancellationToken);
    }

    public async Task<(StoredMealSession Session, bool Created)> CreateWithIdempotencyKeyAsync(
        IReadOnlyList<string> messages,
        MealDraft draft,
        string previewJson,
        string previewToken,
        MealSessionStatus status,
        DateOnly mealDate,
        Guid idempotencyKey,
        string originalRequestHash,
        CancellationToken cancellationToken = default)
    {
        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("An idempotency key cannot be empty.", nameof(idempotencyKey));
        }

        ValidateHash(originalRequestHash, nameof(originalRequestHash), "An original request hash");

        try
        {
            StoredMealSession created = await CreateCoreAsync(
                messages,
                draft,
                previewJson,
                previewToken,
                status,
                mealDate,
                idempotencyKey,
                originalRequestHash,
                cancellationToken);

            return (created, true);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            StoredMealSession? existing = await FindByIdempotencyKeyAsync(
                idempotencyKey,
                cancellationToken);

            if (existing is null)
            {
                throw;
            }

            return (existing, false);
        }
    }

    public async Task<StoredMealSession?> FindByIdempotencyKeyAsync(
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        MealSessionRecord? record = await _dbContext.MealSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                session => session.UserId == _ownerId &&
                    session.IdempotencyKey == idempotencyKey,
                cancellationToken);

        return record is null ? null : MapSession(record);
    }

    private async Task<StoredMealSession> CreateCoreAsync(
        IReadOnlyList<string> messages,
        MealDraft draft,
        string previewJson,
        string previewToken,
        MealSessionStatus status,
        DateOnly mealDate,
        Guid? idempotencyKey,
        string? originalRequestHash,
        CancellationToken cancellationToken)
    {
        ValidateMessages(messages);
        ArgumentNullException.ThrowIfNull(draft);
        ValidatePreview(previewJson, previewToken);
        ValidateEditableStatus(status);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        MealSessionRecord record = new MealSessionRecord
        {
            Id = Guid.NewGuid(),
            UserId = _ownerId,
            MessagesJson = JsonSerializer.Serialize(messages, SerializerOptions),
            DraftJson = SerializeDraft(draft),
            PreviewJson = previewJson,
            PreviewToken = previewToken,
            Status = status,
            MealDate = mealDate,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            IdempotencyKey = idempotencyKey,
            OriginalRequestHash = originalRequestHash
        };

        _dbContext.MealSessions.Add(record);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            _dbContext.Entry(record).State = EntityState.Detached;
            throw;
        }

        return MapSession(record);
    }

    public async Task<StoredMealSession?> FindAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        MealSessionRecord? record = await _dbContext.MealSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                session => session.Id == id && session.UserId == _ownerId,
                cancellationToken);

        return record is null ? null : MapSession(record);
    }

    public async Task<StoredMealSession> ReplaceDraftAsync(
        Guid id,
        string expectedPreviewToken,
        IReadOnlyList<string> messages,
        MealDraft draft,
        string previewJson,
        string previewToken,
        MealSessionStatus status,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<Guid, string>? messageRequestHashes = null)
    {
        ValidateMessages(messages);
        ArgumentNullException.ThrowIfNull(draft);
        ValidatePreviewToken(expectedPreviewToken, nameof(expectedPreviewToken));
        ValidatePreview(previewJson, previewToken);
        ValidateEditableStatus(status);

        string messagesJson = JsonSerializer.Serialize(messages, SerializerOptions);
        string draftJson = SerializeDraft(draft);
        string? messageRequestHashesJson = messageRequestHashes is null
            ? null
            : SerializeMessageRequestHashes(messageRequestHashes);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int updatedCount = await _dbContext.MealSessions
            .Where(session =>
                session.Id == id &&
                session.UserId == _ownerId &&
                session.Status != MealSessionStatus.Confirmed &&
                session.PreviewToken == expectedPreviewToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(session => session.MessagesJson, messagesJson)
                    .SetProperty(session => session.DraftJson, draftJson)
                    .SetProperty(session => session.PreviewJson, previewJson)
                    .SetProperty(session => session.PreviewToken, previewToken)
                    .SetProperty(session => session.Status, status)
                    .SetProperty(
                        session => session.MessageRequestHashesJson,
                        session => messageRequestHashesJson ?? session.MessageRequestHashesJson)
                    .SetProperty(session => session.UpdatedAtUtc, now),
                cancellationToken);

        if (updatedCount == 0)
        {
            await ThrowWriteFailureAsync(id, cancellationToken);
        }

        MealSessionRecord record = await _dbContext.MealSessions
            .AsNoTracking()
            .SingleAsync(
                session => session.Id == id && session.UserId == _ownerId,
                cancellationToken);

        return MapSession(record);
    }

    public async Task<StoredMealSession> UpdatePreviewAsync(
        Guid id,
        string expectedPreviewToken,
        string previewJson,
        string previewToken,
        MealSessionStatus status,
        CancellationToken cancellationToken = default)
    {
        ValidatePreviewToken(expectedPreviewToken, nameof(expectedPreviewToken));
        ValidatePreview(previewJson, previewToken);
        ValidateEditableStatus(status);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        int updatedCount = await _dbContext.MealSessions
            .Where(session =>
                session.Id == id &&
                session.UserId == _ownerId &&
                session.Status != MealSessionStatus.Confirmed &&
                session.PreviewToken == expectedPreviewToken)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(session => session.PreviewJson, previewJson)
                    .SetProperty(session => session.PreviewToken, previewToken)
                    .SetProperty(session => session.Status, status)
                    .SetProperty(session => session.UpdatedAtUtc, now),
                cancellationToken);

        if (updatedCount == 0)
        {
            await ThrowWriteFailureAsync(id, cancellationToken);
        }

        MealSessionRecord record = await _dbContext.MealSessions
            .AsNoTracking()
            .SingleAsync(
                session => session.Id == id && session.UserId == _ownerId,
                cancellationToken);

        return MapSession(record);
    }

    private static StoredMealSession MapSession(MealSessionRecord record)
    {
        List<string>? messages = JsonSerializer.Deserialize<List<string>>(
            record.MessagesJson,
            SerializerOptions);

        if (messages is null || messages.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException(
                $"Stored messages for meal session '{record.Id}' are invalid.");
        }

        return new StoredMealSession(
            record.Id,
            messages.AsReadOnly(),
            DeserializeDraft(record.DraftJson),
            record.PreviewJson,
            record.PreviewToken,
            record.Status,
            record.MealDate,
            record.CreatedAtUtc,
            record.UpdatedAtUtc,
            record.ConfirmedAtUtc,
            record.IdempotencyKey,
            record.OriginalRequestHash,
            DeserializeMessageRequestHashes(record.MessageRequestHashesJson));
    }

    private static string SerializeMessageRequestHashes(
        IReadOnlyDictionary<Guid, string> messageRequestHashes)
    {
        ValidateMessageRequestHashes(messageRequestHashes);

        return JsonSerializer.Serialize(messageRequestHashes, SerializerOptions);
    }

    private static IReadOnlyDictionary<Guid, string> DeserializeMessageRequestHashes(string json)
    {
        try
        {
            Dictionary<Guid, string>? messageRequestHashes =
                JsonSerializer.Deserialize<Dictionary<Guid, string>>(json, SerializerOptions);

            if (messageRequestHashes is null)
            {
                throw new InvalidDataException("Stored message request hashes are invalid.");
            }

            ValidateMessageRequestHashes(messageRequestHashes);

            return new ReadOnlyDictionary<Guid, string>(messageRequestHashes);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Stored message request hashes JSON is invalid.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Stored message request hashes are invalid.", exception);
        }
    }

    private static void ValidateMessageRequestHashes(
        IReadOnlyDictionary<Guid, string> messageRequestHashes)
    {
        if (messageRequestHashes.Count > MaxMessageRequestHashes)
        {
            throw new ArgumentException(
                "A meal session cannot contain more than 50 message request hashes.",
                nameof(messageRequestHashes));
        }

        foreach ((Guid key, string hash) in messageRequestHashes)
        {
            if (key == Guid.Empty ||
                hash is null ||
                hash.Length != 64 ||
                hash.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            {
                throw new ArgumentException(
                    "Message request hashes require nonempty keys and uppercase SHA-256 values.",
                    nameof(messageRequestHashes));
            }
        }
    }

    private static string SerializeDraft(MealDraft draft)
    {
        StoredDraft document = new StoredDraft(
            draft.Dishes.Select(dish => new StoredDish(
                dish.Name,
                dish.Ingredients.Select(ingredient => new StoredIngredient(
                    ingredient.ProductName,
                    ingredient.WeightInGrams,
                    ingredient.WeightQuality,
                    ingredient.RemovedWeightInGrams,
                    ingredient.RemovedWeightQuality,
                    RemovalSpecified: true)).ToArray(),
                dish.FinalWeightInGrams,
                dish.FinalWeightQuality,
                dish.Portions.Select(portion => new StoredPortion(
                    portion.WeightInGrams,
                    portion.FractionOfDish,
                    portion.WeightQuality)).ToArray())).ToArray(),
            draft.ClarificationQuestions.ToArray());

        return JsonSerializer.Serialize(document, SerializerOptions);
    }

    private static MealDraft DeserializeDraft(string json)
    {
        try
        {
            StoredDraft? document = JsonSerializer.Deserialize<StoredDraft>(
                json,
                SerializerOptions);

            if (document?.Dishes is null ||
                document.ClarificationQuestions is null)
            {
                throw new InvalidDataException("Stored meal draft is incomplete.");
            }

            if (document.Dishes.Any(dish =>
                    dish is null ||
                    dish.Ingredients is null ||
                    dish.Portions is null ||
                    dish.Ingredients.Any(ingredient => ingredient is null) ||
                    dish.Portions.Any(portion => portion is null)))
            {
                throw new InvalidDataException(
                    "Stored meal draft contains null nested values.");
            }

            List<DishDraft> dishes = document.Dishes.Select(dish =>
            {
                StoredDish validDish = dish!;

                return new DishDraft(
                    validDish.Name,
                    validDish.Ingredients.Select(ingredient =>
                    {
                        StoredIngredient validIngredient = ingredient!;

                        return new IngredientDraft(
                            validIngredient.ProductName,
                            validIngredient.WeightInGrams,
                            validIngredient.WeightQuality,
                            validIngredient.RemovalSpecified
                                ? validIngredient.RemovedWeightInGrams
                                : 0m,
                            validIngredient.RemovalSpecified
                                ? validIngredient.RemovedWeightQuality
                                : DataQuality.Exact);
                    }).ToArray(),
                    validDish.FinalWeightInGrams,
                    validDish.FinalWeightQuality,
                    validDish.Portions.Select(portion =>
                    {
                        StoredPortion validPortion = portion!;

                        if (validPortion.WeightInGrams is not null)
                        {
                            return new PortionDraft(
                                validPortion.WeightInGrams.Value,
                                validPortion.WeightQuality);
                        }

                        if (validPortion.FractionOfDish is not null)
                        {
                            return PortionDraft.FromFraction(
                                validPortion.FractionOfDish.Value,
                                validPortion.WeightQuality);
                        }

                        throw new InvalidDataException(
                            "Stored portion has neither a weight nor a fraction.");
                    }).ToArray());
            }).ToList();

            return new MealDraft(dishes, document.ClarificationQuestions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Stored meal draft JSON is invalid.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Stored meal draft violates domain rules.",
                exception);
        }
    }

    private static void ValidateMessages(IReadOnlyList<string> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0 || messages.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "A meal session must contain non-blank messages.",
                nameof(messages));
        }
    }

    private static void ValidatePreview(string previewJson, string previewToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previewJson);
        ValidatePreviewToken(previewToken, nameof(previewToken));
    }

    private static void ValidatePreviewToken(string previewToken, string parameterName)
    {
        ValidateHash(previewToken, parameterName, "A preview token");
    }

    private static void ValidateHash(
        string value,
        string parameterName,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (value.Length != 64 ||
            value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                $"{description} must be a 64-character hexadecimal SHA-256 value.",
                parameterName);
        }
    }

    private async Task ThrowWriteFailureAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        MealSessionStatus? currentStatus = await _dbContext.MealSessions
            .AsNoTracking()
            .Where(session => session.Id == id && session.UserId == _ownerId)
            .Select(session => (MealSessionStatus?)session.Status)
            .SingleOrDefaultAsync(cancellationToken);

        if (currentStatus is null)
        {
            throw new KeyNotFoundException($"Meal session '{id}' was not found.");
        }

        string message = currentStatus == MealSessionStatus.Confirmed
            ? "A confirmed meal session cannot be changed."
            : "The meal session changed in another request. Reload it before editing again.";

        throw new MealSessionConflictException(message);
    }

    private static void ValidateEditableStatus(MealSessionStatus status)
    {
        if (!Enum.IsDefined(status) || status == MealSessionStatus.Confirmed)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
    }

    private sealed record StoredDraft(
        IReadOnlyList<StoredDish> Dishes,
        IReadOnlyList<string> ClarificationQuestions);

    private sealed record StoredDish(
        string Name,
        IReadOnlyList<StoredIngredient> Ingredients,
        decimal? FinalWeightInGrams,
        DataQuality FinalWeightQuality,
        IReadOnlyList<StoredPortion> Portions);

    private sealed record StoredIngredient(
        string ProductName,
        decimal? WeightInGrams,
        DataQuality WeightQuality,
        decimal? RemovedWeightInGrams,
        DataQuality RemovedWeightQuality,
        bool RemovalSpecified);

    private sealed record StoredPortion(
        decimal? WeightInGrams,
        decimal? FractionOfDish,
        DataQuality WeightQuality);
}
