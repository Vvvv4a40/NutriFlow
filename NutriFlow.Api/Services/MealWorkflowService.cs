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
    private readonly SavedDishStore _savedDishStore;

    public MealWorkflowService(
        IMealParser parser,
        LocalProductCatalog catalog,
        MealSessionStore sessionStore,
        DailyDiaryStore diaryStore,
        SavedDishStore savedDishStore)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(sessionStore);
        ArgumentNullException.ThrowIfNull(diaryStore);
        ArgumentNullException.ThrowIfNull(savedDishStore);

        _parser = parser;
        _catalog = catalog;
        _sessionStore = sessionStore;
        _diaryStore = diaryStore;
        _savedDishStore = savedDishStore;
    }

    public async Task<MealSessionCreationResult> CreateAsync(
        IReadOnlyList<string?>? messages,
        DateOnly mealDate,
        CancellationToken cancellationToken = default,
        Guid? idempotencyKey = null,
        MealSessionPurpose purpose = MealSessionPurpose.Diary)
    {
        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose));
        }

        IReadOnlyList<string> validatedMessages = ValidateMessages(messages);
        string? originalRequestHash = null;

        if (idempotencyKey is not null)
        {
            if (idempotencyKey == Guid.Empty)
            {
                throw new ArgumentException(
                    "The idempotency key cannot be empty.",
                    nameof(idempotencyKey));
            }

            originalRequestHash = ComputeOriginalRequestHash(
                validatedMessages,
                mealDate,
                purpose);
            StoredMealSession? existing =
                await _sessionStore.FindByIdempotencyKeyAsync(
                    idempotencyKey.Value,
                    cancellationToken);

            if (existing is not null)
            {
                return await ReuseSessionAsync(
                    existing,
                    originalRequestHash,
                    cancellationToken);
            }
        }

        MealDraft draft = await ParseAsync(validatedMessages, cancellationToken, purpose);
        MealEvaluation evaluation = await EvaluateAsync(draft, validatedMessages, cancellationToken, purpose);
        if (idempotencyKey is not null)
        {
            (StoredMealSession session, bool created) =
                await _sessionStore.CreateWithIdempotencyKeyAsync(
                    validatedMessages,
                    draft,
                    evaluation.PreviewJson,
                    evaluation.PreviewToken,
                    evaluation.Status,
                    mealDate,
                    idempotencyKey.Value,
                    originalRequestHash!,
                    cancellationToken,
                    purpose);

            return created
                ? new MealSessionCreationResult(
                    MapSession(session, evaluation.Document, evaluation.Status),
                    Created: true)
                : await ReuseSessionAsync(
                    session,
                    originalRequestHash!,
                    cancellationToken);
        }

        StoredMealSession newSession = await _sessionStore.CreateAsync(
            validatedMessages,
            draft,
            evaluation.PreviewJson,
            evaluation.PreviewToken,
            evaluation.Status,
            mealDate,
            cancellationToken,
            purpose);

        return new MealSessionCreationResult(
            MapSession(newSession, evaluation.Document, evaluation.Status),
            Created: true);
    }

    private async Task<MealSessionCreationResult> ReuseSessionAsync(
        StoredMealSession existing,
        string originalRequestHash,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                existing.OriginalRequestHash,
                originalRequestHash,
                StringComparison.Ordinal))
        {
            throw new MealSessionConflictException(
                "The idempotency key was already used for different messages, meal date, or purpose.");
        }

        MealSessionResponse response = await FindAsync(
            existing.Id,
            cancellationToken) ?? throw new InvalidDataException(
                "The existing meal session could not be loaded.");

        return new MealSessionCreationResult(response, Created: false);
    }

    private static string ComputeOriginalRequestHash(
        IReadOnlyList<string> messages,
        DateOnly mealDate,
        MealSessionPurpose purpose)
    {
        object request = purpose == MealSessionPurpose.Diary
            ? new { Messages = messages, MealDate = mealDate }
            : (object)new { Messages = messages, MealDate = mealDate, Purpose = purpose };
        byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(
            request,
            SerializerOptions);

        return Convert.ToHexString(SHA256.HashData(requestBytes));
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
            session.Messages,
            cancellationToken,
            session.Purpose);

        if (session.Status != evaluation.Status ||
            !string.Equals(
                session.PreviewToken,
                evaluation.PreviewToken,
                StringComparison.Ordinal))
        {
            try
            {
                session = await _sessionStore.UpdatePreviewAsync(
                    id,
                    session.PreviewToken,
                    evaluation.PreviewJson,
                    evaluation.PreviewToken,
                    evaluation.Status,
                    cancellationToken);
            }
            catch (MealSessionConflictException)
            {
                StoredMealSession? latest = await _sessionStore.FindAsync(
                    id,
                    cancellationToken);

                return latest is null
                    ? null
                    : MapSession(
                        latest,
                        DeserializePreview(latest.PreviewJson),
                        latest.Status);
            }
        }

        return MapSession(session, DeserializePreview(session.PreviewJson), session.Status);
    }

    public async Task<MealSessionResponse?> AddMessageAsync(
        Guid id,
        string? message,
        CancellationToken cancellationToken = default,
        Guid? idempotencyKey = null)
    {
        StoredMealSession? existing = await _sessionStore.FindAsync(
            id,
            cancellationToken);

        if (existing is null)
        {
            return null;
        }

        string? messageRequestHash = null;

        if (idempotencyKey is not null)
        {
            if (idempotencyKey == Guid.Empty)
            {
                throw new ArgumentException(
                    "The idempotency key cannot be empty.",
                    nameof(idempotencyKey));
            }

            messageRequestHash = Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions)));

            if (existing.MessageRequestHashes.TryGetValue(
                    idempotencyKey.Value,
                    out string? originalHash))
            {
                return await ReuseMessageAsync(
                    existing,
                    originalHash,
                    messageRequestHash,
                    cancellationToken);
            }
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
        MealDraft draft = await ParseAsync(validatedMessages, cancellationToken, existing.Purpose);
        MealEvaluation evaluation = await EvaluateAsync(draft, validatedMessages, cancellationToken, existing.Purpose);
        IReadOnlyDictionary<Guid, string>? updatedHashes = null;

        if (idempotencyKey is not null)
        {
            Dictionary<Guid, string> hashes = new(existing.MessageRequestHashes)
            {
                [idempotencyKey.Value] = messageRequestHash!
            };
            updatedHashes = hashes;
        }

        StoredMealSession session;

        try
        {
            session = await _sessionStore.ReplaceDraftAsync(
                id,
                existing.PreviewToken,
                validatedMessages,
                draft,
                evaluation.PreviewJson,
                evaluation.PreviewToken,
                evaluation.Status,
                cancellationToken,
                updatedHashes);
        }
        catch (MealSessionConflictException) when (idempotencyKey is not null)
        {
            StoredMealSession? latest = await _sessionStore.FindAsync(
                id,
                cancellationToken);

            if (latest is not null && latest.MessageRequestHashes.TryGetValue(
                    idempotencyKey.Value,
                    out string? originalHash))
            {
                return await ReuseMessageAsync(
                    latest,
                    originalHash,
                    messageRequestHash!,
                    cancellationToken);
            }

            throw;
        }

        return MapSession(session, DeserializePreview(session.PreviewJson), session.Status);
    }

    private async Task<MealSessionResponse> ReuseMessageAsync(
        StoredMealSession session,
        string originalHash,
        string messageRequestHash,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(originalHash, messageRequestHash, StringComparison.Ordinal))
        {
            throw new MealSessionConflictException(
                "The idempotency key was already used for a different message.");
        }

        return await FindAsync(session.Id, cancellationToken) ??
               throw new InvalidDataException(
                   "The existing meal session could not be loaded.");
    }

    public async Task<MealConfirmationOutcome?> ConfirmAsync(
        Guid id,
        string? previewToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previewToken);
        StoredMealSession? session = await _sessionStore.FindAsync(id, cancellationToken);

        if (session is null)
        {
            return null;
        }

        if (!string.Equals(session.PreviewToken, previewToken, StringComparison.Ordinal))
        {
            return new MealConfirmationOutcome(
                MealConfirmationOutcomeKind.StalePreview,
                MapSession(session, DeserializePreview(session.PreviewJson), session.Status),
                Array.Empty<StoredMealEntry>());
        }

        if (session.Status == MealSessionStatus.Confirmed)
        {
            return await ReadConfirmationAsync(
                MealConfirmationOutcomeKind.AlreadyConfirmed, session, cancellationToken);
        }

        MealEvaluation evaluation = await EvaluateAsync(
            session.Draft, session.Messages, cancellationToken, session.Purpose);

        if (session.Status != evaluation.Status ||
            !string.Equals(session.PreviewToken, evaluation.PreviewToken, StringComparison.Ordinal))
        {
            try
            {
                session = await _sessionStore.UpdatePreviewAsync(
                    id, session.PreviewToken, evaluation.PreviewJson, evaluation.PreviewToken,
                    evaluation.Status, cancellationToken);
            }
            catch (MealSessionConflictException)
            {
                StoredMealSession latest = await _sessionStore.FindAsync(id, cancellationToken) ?? session;
                return await ReadConfirmationAsync(
                    latest.Status == MealSessionStatus.Confirmed
                        ? MealConfirmationOutcomeKind.AlreadyConfirmed
                        : MealConfirmationOutcomeKind.StalePreview,
                    latest, cancellationToken);
            }

            return await ReadConfirmationAsync(
                MealConfirmationOutcomeKind.StalePreview, session, cancellationToken);
        }

        if (evaluation.Status != MealSessionStatus.ReadyForConfirmation)
        {
            return new MealConfirmationOutcome(
                MealConfirmationOutcomeKind.NotReady,
                MapSession(session, evaluation.Document, session.Status),
                Array.Empty<StoredMealEntry>());
        }

        MealSessionConfirmationResult result;

        if (session.Purpose == MealSessionPurpose.CreateDish)
        {
            DishPreviewResponse dish = evaluation.Document.Dishes.Single();
            NutritionResponse nutrition = dish.NutritionPer100Grams!;
            result = await _savedDishStore.ConfirmSessionAsync(
                id, previewToken, dish.Name, dish.FinalWeightInGrams!.Value,
                new NutritionValues(nutrition.Calories, nutrition.ProteinGrams,
                    nutrition.FatGrams, nutrition.CarbohydratesGrams),
                Enum.Parse<DataQuality>(dish.NutritionPer100GramsQuality!), cancellationToken);
        }
        else
        {
            result = await _diaryStore.ConfirmSessionAsync(
                id, previewToken, evaluation.Entries, cancellationToken);
        }

        StoredMealSession latestSession = await _sessionStore.FindAsync(id, cancellationToken)
            ?? throw new InvalidDataException("The confirmed meal session could not be loaded.");
        MealConfirmationOutcomeKind kind = result switch
        {
            MealSessionConfirmationResult.Confirmed => MealConfirmationOutcomeKind.Confirmed,
            MealSessionConfirmationResult.AlreadyConfirmed => MealConfirmationOutcomeKind.AlreadyConfirmed,
            MealSessionConfirmationResult.StalePreview => MealConfirmationOutcomeKind.StalePreview,
            _ => MealConfirmationOutcomeKind.NotReady
        };

        return await ReadConfirmationAsync(kind, latestSession, cancellationToken);
    }

    private async Task<MealConfirmationOutcome> ReadConfirmationAsync(
        MealConfirmationOutcomeKind kind,
        StoredMealSession session,
        CancellationToken cancellationToken)
    {
        bool hasResult = kind is MealConfirmationOutcomeKind.Confirmed or MealConfirmationOutcomeKind.AlreadyConfirmed;
        IReadOnlyList<StoredMealEntry> entries = hasResult && session.Purpose == MealSessionPurpose.Diary
            ? await _diaryStore.GetStoredSessionEntriesAsync(session.Id, cancellationToken)
            : Array.Empty<StoredMealEntry>();
        StoredSavedDish? savedDish = hasResult && session.Purpose == MealSessionPurpose.CreateDish
            ? await _savedDishStore.FindBySessionIdAsync(session.Id, cancellationToken)
                ?? throw new InvalidDataException("The saved dish for this session is missing.")
            : null;

        return new MealConfirmationOutcome(
            kind, MapSession(session, DeserializePreview(session.PreviewJson), session.Status),
            entries, savedDish);
    }

    public async Task<IReadOnlyList<SavedDishResponse>> GetSavedDishesAsync(
        CancellationToken cancellationToken = default)
    {
        return (await _savedDishStore.ListAsync(cancellationToken))
            .Select(ResponseMapper.ToSavedDishResponse).ToArray();
    }

    public async Task<SavedDishDetailResponse?> FindSavedDishAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        StoredSavedDish? dish = await _savedDishStore.FindAsync(id, cancellationToken);

        if (dish is null)
        {
            return null;
        }

        StoredMealSession session = await _sessionStore.FindAsync(dish.SourceSessionId, cancellationToken)
            ?? throw new InvalidDataException("The saved dish's source session is missing.");

        if (session.Purpose != MealSessionPurpose.CreateDish || session.Status != MealSessionStatus.Confirmed)
        {
            throw new InvalidDataException("The saved dish's source session is invalid.");
        }

        return new SavedDishDetailResponse(
            ResponseMapper.ToSavedDishResponse(dish), DeserializePreview(session.PreviewJson).Dishes.Single());
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
        IReadOnlyList<StoredMealEntry> storedEntries = await _diaryStore.GetStoredEntriesAsync(
            date,
            cancellationToken);
        MealEntry[] entries = storedEntries.Select(entry => entry.Entry).ToArray();
        DailyProgress? progress = goal is null ? null : new DailyProgress(goal, entries);
        NutritionValues consumed = progress?.CalculateConsumedNutrition() ?? SumNutrition(entries);
        NutritionValues? remaining = null;
        NutritionValues? exceeded = null;

        if (progress is not null)
        {
            remaining = progress.CalculateRemainingNutrition();
            exceeded = progress.CalculateExceededNutrition();
        }

        return new DailyProgressResponse(
            date,
            goal is null ? null : ResponseMapper.ToNutritionResponse(goal.TargetNutrition),
            ResponseMapper.ToNutritionResponse(consumed),
            remaining is null ? null : ResponseMapper.ToNutritionResponse(remaining),
            exceeded is null ? null : ResponseMapper.ToNutritionResponse(exceeded),
            storedEntries.Select(ResponseMapper.ToMealEntryResponse).ToArray());
    }

    private async Task<MealDraft> ParseAsync(
        IReadOnlyList<string> messages,
        CancellationToken cancellationToken,
        MealSessionPurpose purpose)
    {
        IReadOnlyList<string> savedNames = await _savedDishStore.GetNamesAsync(cancellationToken);
        CaptureSession captureSession = new(purpose, savedNames);

        foreach (string message in messages)
        {
            captureSession.AddEvent(new InputEvent(message));
        }

        captureSession.FinishCollecting();

        try
        {
            return await _parser.ParseAsync(captureSession, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            throw new MealParserResponseException(exception);
        }
    }

    private async Task<MealEvaluation> EvaluateAsync(
        MealDraft draft,
        IReadOnlyList<string> messages,
        CancellationToken cancellationToken,
        MealSessionPurpose purpose)
    {
        List<WorkflowIssueResponse> issues = new List<WorkflowIssueResponse>();
        List<DishPreviewResponse> dishPreviews = new List<DishPreviewResponse>();
        List<MealEntry> entries = new List<MealEntry>();
        Dictionary<string, ProductSelection> productSelections = new(StringComparer.Ordinal);
        bool hasMissingFacts = draft.RequiresClarification;
        bool hasProductIssue = false;

        if (purpose == MealSessionPurpose.CreateDish && draft.Dishes.Count != 1)
        {
            hasMissingFacts = true;
            issues.Add(new WorkflowIssueResponse(
                "single_dish_required",
                draft.Dishes[0].Name,
                null,
                "Describe one named dish to save its composition."));
        }

        foreach (DishDraft dish in draft.Dishes)
        {
            List<IngredientPreviewResponse> ingredientPreviews =
                new List<IngredientPreviewResponse>();
            List<DishIngredient> resolvedIngredients =
                new List<DishIngredient>();
            List<DataQuality> ingredientNutritionQualities =
                new List<DataQuality>();
            bool ingredientsCanBeCalculated = true;

            foreach (IngredientDraft ingredient in dish.Ingredients)
            {
                string productKey = ingredient.ProductName.Trim().Normalize().ToUpperInvariant();
                if (!productSelections.TryGetValue(productKey, out ProductSelection? selection))
                {
                    selection = await ResolveProductAsync(ingredient.ProductName, cancellationToken);
                    productSelections.Add(productKey, selection);
                }

                if (selection.Product is null)
                {
                    hasProductIssue = true;
                    ingredientsCanBeCalculated = false;
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
                    ingredientsCanBeCalculated = false;
                    issues.Add(new WorkflowIssueResponse(
                        "ingredient_weight_missing",
                        dish.Name,
                        ingredient.ProductName,
                        "The ingredient weight is required for calculation."));
                }

                if (ingredient.RemovedWeightInGrams is null)
                {
                    hasMissingFacts = true;
                    ingredientsCanBeCalculated = false;
                    issues.Add(new WorkflowIssueResponse(
                        "removed_weight_missing",
                        dish.Name,
                        ingredient.ProductName,
                        "The removed ingredient weight is required for calculation."));
                }

                NutritionValues? ingredientNutrition = null;

                if (selection.Product is not null &&
                    ingredient.WeightInGrams is not null &&
                    ingredient.RemovedWeightInGrams is not null)
                {
                    DishIngredient resolvedIngredient = new DishIngredient(
                        selection.Product,
                        ingredient.WeightInGrams.Value,
                        ingredient.RemovedWeightInGrams.Value);
                    resolvedIngredients.Add(resolvedIngredient);
                    ingredientNutritionQualities.Add(WorstQuality(
                        [
                            selection.Product.Source.Quality,
                            ingredient.WeightQuality,
                            ingredient.RemovedWeightQuality
                        ]));
                    ingredientNutrition = resolvedIngredient.CalculateNutrition();
                }

                ingredientPreviews.Add(new IngredientPreviewResponse(
                    ingredient.ProductName,
                    ingredient.WeightInGrams,
                    ingredient.WeightQuality.ToString(),
                    ingredient.RemovedWeightInGrams,
                    ingredient.RemovedWeightQuality.ToString(),
                    ingredient.IncludedWeightInGrams,
                    selection.Product is null
                        ? null
                        : ResponseMapper.ToProductResponse(selection.Product),
                    ingredientNutrition is null
                        ? null
                        : ResponseMapper.ToNutritionResponse(ingredientNutrition)));
            }

            if (dish.FinalWeightInGrams is null)
            {
                hasMissingFacts = true;
                issues.Add(new WorkflowIssueResponse(
                    "final_weight_missing",
                    dish.Name,
                    null,
                    "The final dish weight is required for portion calculation."));
            }

            if (purpose == MealSessionPurpose.Diary && dish.Portions.Count == 0)
            {
                hasMissingFacts = true;
                issues.Add(new WorkflowIssueResponse(
                    "portion_missing",
                    dish.Name,
                    null,
                    "At least one eaten portion is required before confirmation."));
            }

            NutritionValues? totalNutrition = null;
            DataQuality? totalNutritionQuality = null;
            NutritionValues? nutritionPer100Grams = null;
            DataQuality? nutritionPer100GramsQuality = null;
            List<PortionPreviewResponse> portionPreviews =
                new List<PortionPreviewResponse>();

            DishBatch? batch = null;

            if (ingredientsCanBeCalculated)
            {
                totalNutrition = SumNutrition(resolvedIngredients);
                totalNutritionQuality = WorstQuality(
                    ingredientNutritionQualities);

                if (dish.FinalWeightInGrams is not null)
                {
                    batch = new DishBatch(
                        dish.Name,
                        resolvedIngredients,
                        dish.FinalWeightInGrams.Value);
                    nutritionPer100Grams = batch.CalculateNutritionPer100Grams();
                    nutritionPer100GramsQuality = WorstQuality(
                        totalNutritionQuality.Value,
                        dish.FinalWeightQuality);

                    if (purpose == MealSessionPurpose.CreateDish)
                    {
                        try
                        {
                            string normalizedDishName = dish.Name.Trim().Normalize();

                            if (normalizedDishName.ToUpperInvariant().Length > 200)
                            {
                                throw new ArgumentException("The normalized dish name is too long.");
                            }

                            _ = new Product(normalizedDishName, nutritionPer100Grams,
                                new NutritionSource(NutritionSourceKind.SavedDish,
                                    nutritionPer100GramsQuality.Value, "Saved NutriFlow dish", "saved-dish:preview"));
                        }
                        catch (ArgumentException)
                        {
                            hasMissingFacts = true;
                            issues.Add(new WorkflowIssueResponse("saved_dish_invalid", dish.Name, null,
                                "Check the dish name and final weight: the calculated nutrition exceeds product limits."));
                        }
                    }
                }
            }

            for (int portionIndex = 0;
                 purpose == MealSessionPurpose.Diary && portionIndex < dish.Portions.Count;
                 portionIndex++)
            {
                PortionDraft portion = dish.Portions[portionIndex];
                decimal? portionWeight = dish.FinalWeightInGrams is null
                    ? portion.WeightInGrams
                    : portion.ResolveWeightInGrams(
                        dish.FinalWeightInGrams.Value);
                NutritionValues? portionNutrition = batch is null ||
                                                    portionWeight is null
                    ? null
                    : batch.CalculatePortionNutrition(portionWeight.Value);
                DataQuality? portionNutritionQuality = portionNutrition is null
                    ? null
                    : portion.FractionOfDish is not null
                        ? WorstQuality(
                            totalNutritionQuality!.Value,
                            portion.WeightQuality)
                        : WorstQuality(
                            nutritionPer100GramsQuality!.Value,
                            portion.WeightQuality);
                portionPreviews.Add(new PortionPreviewResponse(
                    portionWeight,
                    portion.FractionOfDish,
                    portion.WeightQuality.ToString(),
                    portionNutrition is null
                        ? null
                        : ResponseMapper.ToNutritionResponse(portionNutrition),
                    portionNutritionQuality?.ToString()));

                if (portionNutrition is not null)
                {
                    string entryName = dish.Portions.Count == 1
                        ? dish.Name
                        : $"{dish.Name} — порция {portionIndex + 1}";
                    entries.Add(new MealEntry(
                        entryName,
                        portionWeight!.Value,
                        portionNutrition,
                        WorstQuality(
                            ingredientNutritionQualities
                                .Append(dish.FinalWeightQuality)
                                .Append(portion.WeightQuality))));
                }
            }

            dishPreviews.Add(new DishPreviewResponse(
                dish.Name,
                dish.FinalWeightInGrams,
                dish.FinalWeightQuality.ToString(),
                totalNutrition is null ? null : ResponseMapper.ToNutritionResponse(totalNutrition),
                totalNutritionQuality?.ToString(),
                nutritionPer100Grams is null
                    ? null
                    : ResponseMapper.ToNutritionResponse(nutritionPer100Grams),
                nutritionPer100GramsQuality?.ToString(),
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
        // новые сообщения меняют версию, даже если расчёт остался прежним
        object preview = purpose == MealSessionPurpose.Diary
            ? new { Messages = messages, Preview = document }
            : (object)new { Messages = messages, Preview = document, Purpose = purpose };
        string previewToken = Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
                preview,
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
        StoredSavedDish? savedDish = await _savedDishStore.FindByNameAsync(productName, cancellationToken);

        if (savedDish is not null)
        {
            NutritionValues savedNutrition = savedDish.Product.NutritionPer100Grams;
            bool conflictingProduct = matches.Any(product =>
                product.NutritionPer100Grams.Calories != savedNutrition.Calories ||
                product.NutritionPer100Grams.ProteinGrams != savedNutrition.ProteinGrams ||
                product.NutritionPer100Grams.FatGrams != savedNutrition.FatGrams ||
                product.NutritionPer100Grams.CarbohydratesGrams != savedNutrition.CarbohydratesGrams);

            return conflictingProduct
                ? new ProductSelection(null, IsAmbiguous: true)
                : new ProductSelection(savedDish.Product, IsAmbiguous: false);
        }

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
            document.Dishes,
            session.Purpose.ToString());
    }

    private static MealPreviewDocument DeserializePreview(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            MealPreviewValidator.Validate(document.RootElement);

            return document.RootElement.Deserialize<MealPreviewDocument>(SerializerOptions)
                ?? throw new InvalidDataException("Stored meal preview is incomplete.");
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

    private static NutritionValues SumNutrition(
        IReadOnlyList<DishIngredient> ingredients)
    {
        NutritionValues total = new NutritionValues(0m, 0m, 0m, 0m);

        foreach (DishIngredient ingredient in ingredients)
        {
            total = total.Add(ingredient.CalculateNutrition());
        }

        return total;
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

    private static DataQuality WorstQuality(
        DataQuality first,
        DataQuality second)
    {
        return QualityRank(first) <= QualityRank(second) ? first : second;
    }

    private static DataQuality WorstQuality(IEnumerable<DataQuality> qualities)
    {
        return qualities.Aggregate(WorstQuality);
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

public sealed record MealSessionCreationResult(
    MealSessionResponse Session,
    bool Created);

public sealed record MealConfirmationOutcome(
    MealConfirmationOutcomeKind Kind,
    MealSessionResponse Session,
    IReadOnlyList<StoredMealEntry> Entries,
    StoredSavedDish? SavedDish = null);

public enum MealConfirmationOutcomeKind
{
    Confirmed = 0,
    AlreadyConfirmed = 1,
    NotReady = 2,
    StalePreview = 3
}
