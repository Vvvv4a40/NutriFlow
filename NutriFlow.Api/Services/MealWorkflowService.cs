using System.Security.Cryptography;
using System.Text.Json;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure;

namespace NutriFlow.Api.Services;

public sealed class MealWorkflowService
{
    private const int MaximumMessageCount = 50;
    private const int MaximumMessageLength = 4000;
    private const int MaximumTotalMessageLength = 20_000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IMealParser _parser;
    private readonly LocalProductCatalog _catalog;
    private readonly MealSessionStore _sessionStore;
    private readonly DailyDiaryStore _diaryStore;

    public MealWorkflowService(
        IMealParser parser,
        LocalProductCatalog catalog,
        MealSessionStore sessionStore,
        DailyDiaryStore diaryStore)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(diaryStore);

        _parser = parser;
        _catalog = catalog;
        _sessionStore = sessionStore;
        _diaryStore = diaryStore;
    }

    public async Task<MealSessionResponse> CreateAsync(
        IReadOnlyList<string?>? messages,
        DateOnly mealDate,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> validatedMessages = ValidateMessages(messages);
        MealDraft draft = await ParseAsync(validatedMessages, cancellationToken);
        MealEvaluation evaluation = await EvaluateAsync(draft, cancellationToken);
        StoredMealSession session = await _sessionStore.CreateAsync(
            validatedMessages,
            draft,
            evaluation.PreviewJson,
            evaluation.PreviewToken,
            evaluation.Status,
            mealDate,
            cancellationToken);

        return MapSession(session, evaluation.Document, evaluation.Status);
    }

    public async Task<MealSessionResponse?> FindAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        StoredMealSession? session = await _sessionStore.FindAsync(
            id,
            cancellationToken);

        if (session is null)
        {
            return null;
        }

        if (session.Status == MealSessionStatus.Confirmed)
        {
            return MapSession(
                session,
                DeserializePreview(session.PreviewJson),
                MealSessionStatus.Confirmed);
        }

        MealEvaluation evaluation = await EvaluateAsync(
            session.Draft,
            cancellationToken);

        if (session.Status != evaluation.Status ||
            !string.Equals(
                session.PreviewToken,
                evaluation.PreviewToken,
                StringComparison.Ordinal))
        {
            await _sessionStore.UpdatePreviewAsync(
                id,
                evaluation.PreviewJson,
                evaluation.PreviewToken,
                evaluation.Status,
                cancellationToken);
            session = session with
            {
                PreviewJson = evaluation.PreviewJson,
                PreviewToken = evaluation.PreviewToken,
                Status = evaluation.Status,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        return MapSession(session, evaluation.Document, evaluation.Status);
    }

    public async Task<MealSessionResponse?> AddMessageAsync(
        Guid id,
        string? message,
        CancellationToken cancellationToken = default)
    {
        StoredMealSession? existing = await _sessionStore.FindAsync(
            id,
            cancellationToken);

        if (existing is null)
        {
            return null;
        }

        if (existing.Status == MealSessionStatus.Confirmed)
        {
            throw new InvalidOperationException(
                "A confirmed meal session cannot receive new messages.");
        }

        List<string?> messages = existing.Messages
            .Cast<string?>()
            .Append(message)
            .ToList();
        IReadOnlyList<string> validatedMessages = ValidateMessages(messages);
        MealDraft draft = await ParseAsync(validatedMessages, cancellationToken);
        MealEvaluation evaluation = await EvaluateAsync(draft, cancellationToken);
        StoredMealSession session = await _sessionStore.ReplaceDraftAsync(
            id,
            validatedMessages,
            draft,
            evaluation.PreviewJson,
            evaluation.PreviewToken,
            evaluation.Status,
            cancellationToken);

        return MapSession(session, evaluation.Document, evaluation.Status);
    }

    public async Task<MealConfirmationOutcome?> ConfirmAsync(
        Guid id,
        string? previewToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previewToken);

        StoredMealSession? session = await _sessionStore.FindAsync(
            id,
            cancellationToken);

        if (session is null)
        {
            return null;
        }

        if (!string.Equals(
                session.PreviewToken,
                previewToken,
                StringComparison.Ordinal))
        {
            return new MealConfirmationOutcome(
                MealConfirmationOutcomeKind.StalePreview,
                MapSession(
                    session,
                    DeserializePreview(session.PreviewJson),
                    session.Status),
                Array.Empty<MealEntry>());
        }

        if (session.Status == MealSessionStatus.Confirmed)
        {
            IReadOnlyList<MealEntry> existingEntries =
                await _diaryStore.GetSessionEntriesAsync(id, cancellationToken);

            return new MealConfirmationOutcome(
                MealConfirmationOutcomeKind.AlreadyConfirmed,
                MapSession(
                    session,
                    DeserializePreview(session.PreviewJson),
                    MealSessionStatus.Confirmed),
                existingEntries);
        }

        MealPreviewDocument storedDocument = DeserializePreview(
            session.PreviewJson);

        MealEvaluation currentEvaluation = await EvaluateAsync(
            session.Draft,
            cancellationToken);

        if (session.Status != currentEvaluation.Status ||
            !string.Equals(
                session.PreviewToken,
                currentEvaluation.PreviewToken,
                StringComparison.Ordinal))
        {
            await _sessionStore.UpdatePreviewAsync(
                id,
                currentEvaluation.PreviewJson,
                currentEvaluation.PreviewToken,
                currentEvaluation.Status,
                cancellationToken);
            StoredMealSession updatedSession = session with
            {
                PreviewJson = currentEvaluation.PreviewJson,
                PreviewToken = currentEvaluation.PreviewToken,
                Status = currentEvaluation.Status,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            return new MealConfirmationOutcome(
                MealConfirmationOutcomeKind.StalePreview,
                MapSession(
                    updatedSession,
                    currentEvaluation.Document,
                    currentEvaluation.Status),
                Array.Empty<MealEntry>());
        }

        if (currentEvaluation.Status != MealSessionStatus.ReadyForConfirmation)
        {
            return new MealConfirmationOutcome(
                MealConfirmationOutcomeKind.NotReady,
                MapSession(session, currentEvaluation.Document, session.Status),
                Array.Empty<MealEntry>());
        }

        MealSessionConfirmationResult storeResult =
            await _diaryStore.ConfirmSessionAsync(
                id,
                previewToken,
                currentEvaluation.Entries,
                cancellationToken);

        if (storeResult == MealSessionConfirmationResult.Confirmed)
        {
            StoredMealSession confirmedSession = session with
            {
                Status = MealSessionStatus.Confirmed,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ConfirmedAtUtc = DateTimeOffset.UtcNow
            };

            return new MealConfirmationOutcome(
                MealConfirmationOutcomeKind.Confirmed,
                MapSession(
                    confirmedSession,
                    currentEvaluation.Document,
                    MealSessionStatus.Confirmed),
                currentEvaluation.Entries);
        }

        StoredMealSession latestSession =
            await _sessionStore.FindAsync(id, cancellationToken) ?? session;
        MealPreviewDocument latestDocument = DeserializePreview(
            latestSession.PreviewJson);
        IReadOnlyList<MealEntry> latestEntries =
            storeResult == MealSessionConfirmationResult.AlreadyConfirmed
                ? await _diaryStore.GetSessionEntriesAsync(id, cancellationToken)
                : Array.Empty<MealEntry>();

        return new MealConfirmationOutcome(
            storeResult switch
            {
                MealSessionConfirmationResult.AlreadyConfirmed =>
                    MealConfirmationOutcomeKind.AlreadyConfirmed,
                MealSessionConfirmationResult.StalePreview =>
                    MealConfirmationOutcomeKind.StalePreview,
                _ => MealConfirmationOutcomeKind.NotReady
            },
            MapSession(latestSession, latestDocument, latestSession.Status),
            latestEntries);
    }

    public async Task SetDailyGoalAsync(
        DateOnly date,
        SetDailyGoalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        DailyGoal goal = new DailyGoal(new NutritionValues(
            request.Calories,
            request.ProteinGrams,
            request.FatGrams,
            request.CarbohydratesGrams));

        await _diaryStore.SetGoalAsync(date, goal, cancellationToken);
    }

    public async Task<DailyProgressResponse> GetDailyProgressAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        DailyGoal? goal = await _diaryStore.FindGoalAsync(
            date,
            cancellationToken);
        IReadOnlyList<MealEntry> entries = await _diaryStore.GetEntriesAsync(
            date,
            cancellationToken);
        NutritionValues consumed = SumNutrition(entries);
        NutritionValues? remaining = null;
        NutritionValues? exceeded = null;

        if (goal is not null)
        {
            DailyProgress progress = new DailyProgress(goal, entries);
            remaining = progress.CalculateRemainingNutrition();
            exceeded = progress.CalculateExceededNutrition();
        }

        return new DailyProgressResponse(
            date,
            goal is null ? null : MapNutrition(goal.TargetNutrition),
            MapNutrition(consumed),
            remaining is null ? null : MapNutrition(remaining),
            exceeded is null ? null : MapNutrition(exceeded),
            entries.Select(MapEntry).ToArray());
    }

    private async Task<MealDraft> ParseAsync(
        IReadOnlyList<string> messages,
        CancellationToken cancellationToken)
    {
        CaptureSession captureSession = new CaptureSession();

        foreach (string message in messages)
        {
            captureSession.AddEvent(new InputEvent(message));
        }

        captureSession.FinishCollecting();

        return await _parser.ParseAsync(captureSession, cancellationToken);
    }

    private async Task<MealEvaluation> EvaluateAsync(
        MealDraft draft,
        CancellationToken cancellationToken)
    {
        List<WorkflowIssueResponse> issues = new List<WorkflowIssueResponse>();
        List<DishPreviewResponse> dishPreviews = new List<DishPreviewResponse>();
        List<MealEntry> entries = new List<MealEntry>();
        bool hasMissingFacts = draft.RequiresClarification;
        bool hasProductIssue = false;

        foreach (DishDraft dish in draft.Dishes)
        {
            List<IngredientPreviewResponse> ingredientPreviews =
                new List<IngredientPreviewResponse>();
            List<DishIngredient> resolvedIngredients =
                new List<DishIngredient>();
            bool dishCanBeCalculated = true;

            foreach (IngredientDraft ingredient in dish.Ingredients)
            {
                ProductSelection selection = await ResolveProductAsync(
                    ingredient.ProductName,
                    cancellationToken);

                if (selection.Product is null)
                {
                    hasProductIssue = true;
                    dishCanBeCalculated = false;
                    issues.Add(new WorkflowIssueResponse(
                        selection.IsAmbiguous
                            ? "product_ambiguous"
                            : "product_not_found",
                        dish.Name,
                        ingredient.ProductName,
                        selection.IsAmbiguous
                            ? "Several equally reliable products have different nutrition values."
                            : "The product is not available in the local catalog."));
                }

                if (ingredient.WeightInGrams is null)
                {
                    hasMissingFacts = true;
                    dishCanBeCalculated = false;
                    issues.Add(new WorkflowIssueResponse(
                        "ingredient_weight_missing",
                        dish.Name,
                        ingredient.ProductName,
                        "The ingredient weight is required for calculation."));
                }

                NutritionValues? ingredientNutrition = null;

                if (selection.Product is not null &&
                    ingredient.WeightInGrams is not null)
                {
                    DishIngredient resolvedIngredient = new DishIngredient(
                        selection.Product,
                        ingredient.WeightInGrams.Value);
                    resolvedIngredients.Add(resolvedIngredient);
                    ingredientNutrition = resolvedIngredient.CalculateNutrition();
                }

                ingredientPreviews.Add(new IngredientPreviewResponse(
                    ingredient.ProductName,
                    ingredient.WeightInGrams,
                    ingredient.WeightQuality.ToString(),
                    selection.Product is null
                        ? null
                        : MapProduct(selection.Product),
                    ingredientNutrition is null
                        ? null
                        : MapNutrition(ingredientNutrition)));
            }

            if (dish.FinalWeightInGrams is null)
            {
                hasMissingFacts = true;
                dishCanBeCalculated = false;
                issues.Add(new WorkflowIssueResponse(
                    "final_weight_missing",
                    dish.Name,
                    null,
                    "The final dish weight is required for portion calculation."));
            }

            if (dish.Portions.Count == 0)
            {
                hasMissingFacts = true;
                dishCanBeCalculated = false;
                issues.Add(new WorkflowIssueResponse(
                    "portion_missing",
                    dish.Name,
                    null,
                    "At least one eaten portion is required before confirmation."));
            }

            NutritionValues? totalNutrition = null;
            NutritionValues? nutritionPer100Grams = null;
            List<PortionPreviewResponse> portionPreviews =
                new List<PortionPreviewResponse>();

            if (dishCanBeCalculated)
            {
                DishBatch batch = new DishBatch(
                    dish.Name,
                    resolvedIngredients,
                    dish.FinalWeightInGrams!.Value);
                totalNutrition = batch.CalculateTotalNutrition();
                nutritionPer100Grams = batch.CalculateNutritionPer100Grams();

                for (int portionIndex = 0;
                     portionIndex < dish.Portions.Count;
                     portionIndex++)
                {
                    PortionDraft portion = dish.Portions[portionIndex];
                    NutritionValues portionNutrition =
                        batch.CalculatePortionNutrition(portion.WeightInGrams);
                    portionPreviews.Add(new PortionPreviewResponse(
                        portion.WeightInGrams,
                        portion.WeightQuality.ToString(),
                        MapNutrition(portionNutrition)));
                    string entryName = dish.Portions.Count == 1
                        ? dish.Name
                        : $"{dish.Name} — portion {portionIndex + 1}";
                    entries.Add(new MealEntry(
                        entryName,
                        portion.WeightInGrams,
                        portionNutrition));
                }
            }
            else
            {
                portionPreviews.AddRange(dish.Portions.Select(portion =>
                    new PortionPreviewResponse(
                        portion.WeightInGrams,
                        portion.WeightQuality.ToString(),
                        null)));
            }

            dishPreviews.Add(new DishPreviewResponse(
                dish.Name,
                dish.FinalWeightInGrams,
                dish.FinalWeightQuality.ToString(),
                totalNutrition is null ? null : MapNutrition(totalNutrition),
                nutritionPer100Grams is null
                    ? null
                    : MapNutrition(nutritionPer100Grams),
                ingredientPreviews,
                portionPreviews));
        }

        MealSessionStatus status = hasMissingFacts
            ? MealSessionStatus.NeedsClarification
            : hasProductIssue
                ? MealSessionStatus.NeedsProducts
                : MealSessionStatus.ReadyForConfirmation;
        MealPreviewDocument document = new MealPreviewDocument(
            draft.ClarificationQuestions.ToArray(),
            issues,
            dishPreviews);
        string previewJson = JsonSerializer.Serialize(
            document,
            SerializerOptions);
        string previewToken = Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                document,
                SerializerOptions)));

        return new MealEvaluation(
            document,
            previewJson,
            previewToken,
            status,
            entries);
    }

    private async Task<ProductSelection> ResolveProductAsync(
        string productName,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Product> matches = await _catalog.FindByNameAsync(
            productName,
            cancellationToken);

        if (matches.Count == 0)
        {
            return new ProductSelection(null, IsAmbiguous: false);
        }

        int bestQuality = matches.Max(product => QualityRank(
            product.Source.Quality));
        Product[] bestMatches = matches
            .Where(product => QualityRank(product.Source.Quality) == bestQuality)
            .ToArray();
        int distinctNutritionCount = bestMatches
            .Select(product => new
            {
                product.NutritionPer100Grams.Calories,
                product.NutritionPer100Grams.ProteinGrams,
                product.NutritionPer100Grams.FatGrams,
                product.NutritionPer100Grams.CarbohydratesGrams
            })
            .Distinct()
            .Count();

        if (distinctNutritionCount > 1)
        {
            return new ProductSelection(null, IsAmbiguous: true);
        }

        Product selected = bestMatches
            .OrderBy(product => SourceRank(product.Source.Kind))
            .ThenBy(product => product.Source.Name, StringComparer.Ordinal)
            .First();

        return new ProductSelection(selected, IsAmbiguous: false);
    }

    private static IReadOnlyList<string> ValidateMessages(
        IReadOnlyList<string?>? messages)
    {
        if (messages is null || messages.Count == 0)
        {
            throw new ArgumentException(
                "At least one captured message is required.",
                nameof(messages));
        }

        if (messages.Count > MaximumMessageCount)
        {
            throw new ArgumentException(
                $"A meal session cannot contain more than {MaximumMessageCount} messages.",
                nameof(messages));
        }

        int totalLength = 0;
        List<string> validated = new List<string>(messages.Count);

        foreach (string? message in messages)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException(
                    "Messages cannot be null, empty, or whitespace-only.",
                    nameof(messages));
            }

            if (message.Length > MaximumMessageLength)
            {
                throw new ArgumentException(
                    $"A single message cannot exceed {MaximumMessageLength} characters.",
                    nameof(messages));
            }

            totalLength += message.Length;

            if (totalLength > MaximumTotalMessageLength)
            {
                throw new ArgumentException(
                    $"Captured messages cannot exceed {MaximumTotalMessageLength} characters in total.",
                    nameof(messages));
            }

            validated.Add(message);
        }

        return validated.AsReadOnly();
    }

    private static MealSessionResponse MapSession(
        StoredMealSession session,
        MealPreviewDocument document,
        MealSessionStatus status)
    {
        return new MealSessionResponse(
            session.Id,
            status.ToString(),
            session.MealDate,
            session.Messages,
            session.PreviewToken,
            status == MealSessionStatus.ReadyForConfirmation,
            document.ClarificationQuestions,
            document.Issues,
            document.Dishes);
    }

    private static MealPreviewDocument DeserializePreview(string json)
    {
        try
        {
            MealPreviewDocument? document =
                JsonSerializer.Deserialize<MealPreviewDocument>(
                    json,
                    SerializerOptions);

            if (document is null ||
                document.ClarificationQuestions is null ||
                document.Issues is null ||
                document.Dishes is null)
            {
                throw new InvalidDataException(
                    "Stored meal preview is incomplete.");
            }

            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Stored meal preview JSON is invalid.",
                exception);
        }
    }

    private static NutritionValues SumNutrition(
        IReadOnlyList<MealEntry> entries)
    {
        NutritionValues total = new NutritionValues(0m, 0m, 0m, 0m);

        foreach (MealEntry entry in entries)
        {
            total = total.Add(entry.Nutrition);
        }

        return total;
    }

    private static ProductResponse MapProduct(Product product)
    {
        return new ProductResponse(
            product.Name,
            product.NutritionPer100Grams.Calories,
            product.NutritionPer100Grams.ProteinGrams,
            product.NutritionPer100Grams.FatGrams,
            product.NutritionPer100Grams.CarbohydratesGrams,
            product.Source.Kind.ToString(),
            product.Source.Quality.ToString(),
            product.Source.Name,
            product.Source.Reference,
            product.Barcode);
    }

    private static NutritionResponse MapNutrition(NutritionValues nutrition)
    {
        return new NutritionResponse(
            nutrition.Calories,
            nutrition.ProteinGrams,
            nutrition.FatGrams,
            nutrition.CarbohydratesGrams);
    }

    private static MealEntryResponse MapEntry(MealEntry entry)
    {
        return new MealEntryResponse(
            entry.Name,
            entry.WeightInGrams,
            MapNutrition(entry.Nutrition));
    }

    private static int QualityRank(DataQuality quality)
    {
        return quality switch
        {
            DataQuality.Exact => 3,
            DataQuality.Verified => 2,
            DataQuality.Estimated => 1,
            _ => 0
        };
    }

    private static int SourceRank(NutritionSourceKind kind)
    {
        return kind switch
        {
            NutritionSourceKind.LabelPhoto => 0,
            NutritionSourceKind.ManualInput => 1,
            NutritionSourceKind.NutriFlowCatalog => 2,
            NutritionSourceKind.WebPage => 3,
            NutritionSourceKind.ExternalService => 4,
            NutritionSourceKind.DishPhoto => 5,
            _ => 6
        };
    }

    private sealed record ProductSelection(Product? Product, bool IsAmbiguous);

    private sealed record MealPreviewDocument(
        IReadOnlyList<string> ClarificationQuestions,
        IReadOnlyList<WorkflowIssueResponse> Issues,
        IReadOnlyList<DishPreviewResponse> Dishes);

    private sealed record MealEvaluation(
        MealPreviewDocument Document,
        string PreviewJson,
        string PreviewToken,
        MealSessionStatus Status,
        IReadOnlyList<MealEntry> Entries);
}

public sealed record MealConfirmationOutcome(
    MealConfirmationOutcomeKind Kind,
    MealSessionResponse Session,
    IReadOnlyList<MealEntry> Entries);

public enum MealConfirmationOutcomeKind
{
    Confirmed = 0,
    AlreadyConfirmed = 1,
    NotReady = 2,
    StalePreview = 3
}
