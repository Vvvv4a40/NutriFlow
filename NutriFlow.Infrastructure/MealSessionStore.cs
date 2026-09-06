using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure;

public sealed class MealSessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly NutriFlowDbContext _dbContext;

    public MealSessionStore(NutriFlowDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<StoredMealSession> CreateAsync(
        IReadOnlyList<string> messages,
        MealDraft draft,
        string previewJson,
        string previewToken,
        MealSessionStatus status,
        DateOnly mealDate,
        CancellationToken cancellationToken = default)
    {
        ValidateMessages(messages);
        ArgumentNullException.ThrowIfNull(draft);
        ValidatePreview(previewJson, previewToken);
        ValidateEditableStatus(status);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        MealSessionRecord record = new MealSessionRecord
        {
            Id = Guid.NewGuid(),
            MessagesJson = JsonSerializer.Serialize(messages, SerializerOptions),
            DraftJson = SerializeDraft(draft),
            PreviewJson = previewJson,
            PreviewToken = previewToken,
            Status = status,
            MealDate = mealDate,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _dbContext.MealSessions.Add(record);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return MapSession(record);
    }

    public async Task<StoredMealSession?> FindAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        MealSessionRecord? record = await _dbContext.MealSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(session => session.Id == id, cancellationToken);

        return record is null ? null : MapSession(record);
    }

    public async Task<StoredMealSession> ReplaceDraftAsync(
        Guid id,
        IReadOnlyList<string> messages,
        MealDraft draft,
        string previewJson,
        string previewToken,
        MealSessionStatus status,
        CancellationToken cancellationToken = default)
    {
        ValidateMessages(messages);
        ArgumentNullException.ThrowIfNull(draft);
        ValidatePreview(previewJson, previewToken);
        ValidateEditableStatus(status);

        MealSessionRecord record = await _dbContext.MealSessions
            .SingleOrDefaultAsync(session => session.Id == id, cancellationToken) ??
            throw new KeyNotFoundException($"Meal session '{id}' was not found.");

        if (record.Status == MealSessionStatus.Confirmed)
        {
            throw new InvalidOperationException(
                "A confirmed meal session cannot be changed.");
        }

        record.MessagesJson = JsonSerializer.Serialize(messages, SerializerOptions);
        record.DraftJson = SerializeDraft(draft);
        record.PreviewJson = previewJson;
        record.PreviewToken = previewToken;
        record.Status = status;
        record.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return MapSession(record);
    }

    public async Task UpdatePreviewAsync(
        Guid id,
        string previewJson,
        string previewToken,
        MealSessionStatus status,
        CancellationToken cancellationToken = default)
    {
        ValidatePreview(previewJson, previewToken);
        ValidateEditableStatus(status);

        MealSessionRecord record = await _dbContext.MealSessions
            .SingleOrDefaultAsync(session => session.Id == id, cancellationToken) ??
            throw new KeyNotFoundException($"Meal session '{id}' was not found.");

        if (record.Status == MealSessionStatus.Confirmed)
        {
            throw new InvalidOperationException(
                "A confirmed meal session cannot be changed.");
        }

        record.PreviewJson = previewJson;
        record.PreviewToken = previewToken;
        record.Status = status;
        record.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
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
            record.ConfirmedAtUtc);
    }

    private static string SerializeDraft(MealDraft draft)
    {
        StoredDraft document = new StoredDraft(
            draft.Dishes.Select(dish => new StoredDish(
                dish.Name,
                dish.Ingredients.Select(ingredient => new StoredIngredient(
                    ingredient.ProductName,
                    ingredient.WeightInGrams,
                    ingredient.WeightQuality)).ToArray(),
                dish.FinalWeightInGrams,
                dish.FinalWeightQuality,
                dish.Portions.Select(portion => new StoredPortion(
                    portion.WeightInGrams,
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
                            validIngredient.WeightQuality);
                    }).ToArray(),
                    validDish.FinalWeightInGrams,
                    validDish.FinalWeightQuality,
                    validDish.Portions.Select(portion =>
                    {
                        StoredPortion validPortion = portion!;

                        return new PortionDraft(
                            validPortion.WeightInGrams,
                            validPortion.WeightQuality);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(previewToken);

        if (previewToken.Length != 64 ||
            previewToken.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "A preview token must be a 64-character hexadecimal SHA-256 value.",
                nameof(previewToken));
        }
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
        DataQuality WeightQuality);

    private sealed record StoredPortion(
        decimal WeightInGrams,
        DataQuality WeightQuality);
}
