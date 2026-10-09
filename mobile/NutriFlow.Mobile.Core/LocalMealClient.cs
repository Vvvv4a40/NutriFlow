using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using NutriFlow.Api.Contracts;
using NutriFlow.Api.Services;
using NutriFlow.Domain;
using NutriFlow.Infrastructure;
using NutriFlow.Infrastructure.Ai;
using NutriFlow.Infrastructure.Audio;
using NutriFlow.Infrastructure.ExternalProducts;
using NutriFlow.Infrastructure.LabelPhotos;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Mobile.Core;

public sealed class LocalMealClient
{
    private readonly LocalProfileStore _profiles;
    private readonly Func<HttpClient> _httpClientFactory;

    public LocalMealClient(LocalProfileStore profiles, Func<HttpClient>? httpClientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = profiles;
        _httpClientFactory = httpClientFactory ?? (() => new HttpClient());
    }

    public Task<DailyProgressResponse> GetDailyProgressAsync(
        Guid profileId, DateOnly date, CancellationToken cancellationToken = default)
    {
        return WithWorkflowAsync(profileId, null,
            workflow => workflow.GetDailyProgressAsync(date, cancellationToken), cancellationToken);
    }

    public Task SetDailyGoalAsync(
        Guid profileId, DateOnly date, NutritionResponse goal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goal);
        return WithContextAsync(profileId, async context =>
        {
            await new DailyDiaryStore(context, profileId).SetGoalAsync(
                date, new DailyGoal(ToNutrition(goal)), cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ProductResponse>> SearchProductsAsync(
        Guid profileId, string? query = null, CancellationToken cancellationToken = default)
    {
        return WithContextAsync<IReadOnlyList<ProductResponse>>(profileId, async context =>
        {
            IReadOnlyList<Product> products = await new LocalProductCatalog(context, profileId)
                .SearchAsync(query, 200, cancellationToken);
            return products.Select(ResponseMapper.ToProductResponse).ToArray();
        }, cancellationToken);
    }

    public Task<bool> AddProductAsync(
        Guid profileId, ProductResponse product, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (!Enum.TryParse(product.SourceKind, out NutritionSourceKind kind) ||
            !Enum.TryParse(product.DataQuality, out DataQuality quality) ||
            kind is not (NutritionSourceKind.ManualInput or NutritionSourceKind.LabelPhoto) ||
            quality is not (DataQuality.Exact or DataQuality.Estimated))
        {
            throw new ArgumentException("Ручной продукт должен содержать ручной источник или свою этикетку и точное либо приблизительное качество.", nameof(product));
        }

        Product validated = new(product.Name,
            new NutritionValues(product.Calories, product.ProteinGrams, product.FatGrams, product.CarbohydratesGrams),
            new NutritionSource(kind, quality, product.SourceName, product.SourceReference), product.Barcode);
        return WithContextAsync(profileId, async context =>
        {
            if (kind == NutritionSourceKind.LabelPhoto &&
                !await new LabelPhotoStore(context, _profiles.GetPhotoDirectory(profileId), profileId)
                    .ContainsAsync(product.SourceReference!, cancellationToken))
            {
                throw new ArgumentException("Фотография этикетки не принадлежит выбранному профилю.", nameof(product));
            }

            return await new LocalProductCatalog(context, profileId).AddAsync(validated, cancellationToken);
        }, cancellationToken);
    }

    public Task<ProductResponse?> LookupBarcodeAsync(
        Guid profileId, string barcode, CancellationToken cancellationToken = default)
    {
        return WithContextAsync(profileId, async context =>
        {
            using HttpClient client = _httpClientFactory();
            client.BaseAddress = new Uri("https://world.openfoodfacts.org/");
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NutriFlow/1.0 (https://github.com/Vvvv4a40/NutriFlow)");
            Product? product = await new ProductLookupService(
                new LocalProductCatalog(context, profileId), new OpenFoodFactsClient(client))
                .FindByBarcodeAsync(barcode, cancellationToken);
            return product is null ? null : ResponseMapper.ToProductResponse(product);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<SavedDishResponse>> GetSavedDishesAsync(
        Guid profileId, CancellationToken cancellationToken = default)
    {
        return WithWorkflowAsync(profileId, null,
            workflow => workflow.GetSavedDishesAsync(cancellationToken), cancellationToken);
    }

    public Task<SavedDishDetailResponse?> FindSavedDishAsync(
        Guid profileId, Guid dishId, CancellationToken cancellationToken = default)
    {
        return WithWorkflowAsync(profileId, null,
            workflow => workflow.FindSavedDishAsync(dishId, cancellationToken), cancellationToken);
    }

    public Task<MealSessionResponse> CreateManualMealAsync(
        Guid profileId, string productName, decimal grams, DateOnly date,
        DataQuality quality = DataQuality.Exact, CancellationToken cancellationToken = default)
    {
        MealDraft draft = new([new DishDraft(productName,
            [new IngredientDraft(productName, grams, quality)], grams, quality,
            [new PortionDraft(grams, quality)])], []);
        return WithContextAsync(profileId, async context =>
        {
            await RequireUnambiguousNameAsync(context, profileId, productName, cancellationToken);
            return await CreateDraftAsync(context, profileId, draft, date, MealSessionPurpose.Diary, cancellationToken);
        }, cancellationToken);
    }

    public Task<MealSessionResponse> CreateManualMealAsync(
        Guid profileId, ProductResponse selected, decimal grams, DateOnly date,
        DataQuality quality = DataQuality.Exact, CancellationToken cancellationToken = default)
    {
        Product product = ToProduct(selected);
        _ = new IngredientDraft(product.Name, grams, quality);
        return WithContextAsync(profileId, async context =>
        {
            string alias = await new LocalProductCatalog(context, profileId)
                .CreateSelectionAliasAsync(product, cancellationToken);
            MealDraft draft = new([new DishDraft(product.Name,
                [new IngredientDraft(alias, grams, quality)], grams, quality,
                [new PortionDraft(grams, quality)])], []);
            MealDraft readableDraft = new([new DishDraft(product.Name,
                [new IngredientDraft(product.Name, grams, quality)], grams, quality,
                [new PortionDraft(grams, quality)])], []);
            return await CreateDraftAsync(context, profileId, draft, date, MealSessionPurpose.Diary,
                cancellationToken, readableDraft);
        }, cancellationToken);
    }

    public Task<MealSessionResponse> CreateManualDishAsync(
        Guid profileId, string name, IReadOnlyList<IngredientDraft> ingredients, decimal finalGrams,
        DateOnly date, DataQuality quality = DataQuality.Exact, CancellationToken cancellationToken = default)
    {
        MealDraft draft = new([new DishDraft(name, ingredients, finalGrams, quality, [])], []);
        return WithContextAsync(profileId, async context =>
        {
            foreach (IngredientDraft ingredient in ingredients)
            {
                await RequireUnambiguousNameAsync(context, profileId, ingredient.ProductName, cancellationToken);
            }

            return await CreateDraftAsync(context, profileId, draft, date, MealSessionPurpose.CreateDish, cancellationToken);
        }, cancellationToken);
    }

    public Task<MealSessionResponse> CreateManualDishAsync(
        Guid profileId, string name, IReadOnlyList<ManualIngredient> ingredients, decimal finalGrams,
        DateOnly date, DataQuality quality = DataQuality.Exact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ingredients);
        Product[] products = ingredients.Select(ingredient => ToProduct(ingredient.Product)).ToArray();
        IngredientDraft[] validated = ingredients.Select((ingredient, index) => new IngredientDraft(
            products[index].Name, ingredient.WeightInGrams, ingredient.WeightQuality,
            ingredient.RemovedWeightInGrams, ingredient.RemovedWeightQuality)).ToArray();
        _ = new DishDraft(name, validated, finalGrams, quality, []);
        return WithContextAsync(profileId, async context =>
        {
            LocalProductCatalog catalog = new(context, profileId);
            List<IngredientDraft> selectedIngredients = [];
            for (int index = 0; index < ingredients.Count; index++)
            {
                string alias = await catalog.CreateSelectionAliasAsync(products[index], cancellationToken);
                IngredientDraft ingredient = validated[index];
                selectedIngredients.Add(new IngredientDraft(alias, ingredient.WeightInGrams, ingredient.WeightQuality,
                    ingredient.RemovedWeightInGrams, ingredient.RemovedWeightQuality));
            }

            MealDraft draft = new([new DishDraft(name, selectedIngredients, finalGrams, quality, [])], []);
            MealDraft readableDraft = new([new DishDraft(name, validated, finalGrams, quality, [])], []);
            return await CreateDraftAsync(context, profileId, draft, date, MealSessionPurpose.CreateDish,
                cancellationToken, readableDraft);
        }, cancellationToken);
    }

    public Task<MealSessionResponse> CreateSavedDishMealAsync(
        Guid profileId, Guid dishId, decimal grams, DateOnly date,
        DataQuality quality = DataQuality.Exact, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(grams);
        return WithContextAsync(profileId, async context =>
        {
            StoredSavedDish dish = await new SavedDishStore(context, profileId).FindAsync(dishId, cancellationToken)
                ?? throw new KeyNotFoundException("Блюдо не найдено в этом профиле.");
            MealDraft draft = new([new DishDraft(dish.Product.Name,
                [new IngredientDraft(dish.Product.Source.Reference!, grams, quality)], grams, quality,
                [new PortionDraft(grams, quality)])], []);
            MealDraft readableDraft = new([new DishDraft(dish.Product.Name,
                [new IngredientDraft(dish.Product.Name, grams, quality)], grams, quality,
                [new PortionDraft(grams, quality)])], []);
            return await CreateDraftAsync(context, profileId, draft, date, MealSessionPurpose.Diary,
                cancellationToken, readableDraft);
        }, cancellationToken);
    }

    public async Task<MealSessionResponse> CreateSessionAsync(
        Guid profileId, IReadOnlyList<string> messages, DateOnly date, MealSessionPurpose purpose,
        GroqSettings settings, CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateGroqClient(settings);
        return await WithWorkflowAsync(profileId, new GroqMealParser(client, settings.TextModel), async workflow =>
        {
            MealSessionCreationResult result = await workflow.CreateAsync(
                messages, date, cancellationToken, Guid.NewGuid(), purpose);
            return result.Session;
        }, cancellationToken);
    }

    public async Task<MealSessionResponse?> AddMessageAsync(
        Guid profileId, Guid sessionId, string message, GroqSettings settings,
        CancellationToken cancellationToken = default)
    {
        return await WithContextAsync(profileId, async context =>
        {
            StoredMealSession? session = await new MealSessionStore(context, profileId).FindAsync(sessionId, cancellationToken);
            if (session is null)
            {
                return null;
            }

            await RequireUnambiguousManualSelectionAsync(context, profileId, session, cancellationToken);
            using HttpClient client = CreateGroqClient(settings);
            return await CreateWorkflow(context, profileId, new GroqMealParser(client, settings.TextModel))
                .AddMessageAsync(sessionId, message, cancellationToken);
        }, cancellationToken);
    }

    public Task<MealSessionResponse?> FindSessionAsync(
        Guid profileId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        return WithWorkflowAsync(profileId, null,
            workflow => workflow.FindAsync(sessionId, cancellationToken), cancellationToken);
    }

    public Task<ConfirmMealSessionResponse?> ConfirmAsync(
        Guid profileId, Guid sessionId, string previewToken, CancellationToken cancellationToken = default)
    {
        return WithWorkflowAsync<ConfirmMealSessionResponse?>(profileId, null, async workflow =>
        {
            MealConfirmationOutcome? outcome = await workflow.ConfirmAsync(sessionId, previewToken, cancellationToken);
            return outcome is null ? null : new ConfirmMealSessionResponse(
                outcome.Kind.ToString(), null, outcome.Session,
                outcome.Entries.Select(ResponseMapper.ToMealEntryResponse).ToArray(),
                outcome.SavedDish is null ? null : ResponseMapper.ToSavedDishResponse(outcome.SavedDish));
        }, cancellationToken);
    }

    public Task<MealEntryResponse> UpdateEntryAsync(
        Guid profileId, int id, int revision, decimal grams, DataQuality quality = DataQuality.Exact,
        CancellationToken cancellationToken = default)
    {
        return WithContextAsync(profileId, async context =>
        {
            MealEntryChangeResult result = await new DailyDiaryStore(context, profileId)
                .UpdateEntryWeightAsync(id, revision, grams, quality, cancellationToken);
            RequireChange(result);
            return ResponseMapper.ToMealEntryResponse(result.Entry!);
        }, cancellationToken);
    }

    public Task DeleteEntryAsync(Guid profileId, int id, int revision, CancellationToken cancellationToken = default)
    {
        return WithContextAsync(profileId, async context =>
        {
            MealEntryChangeResult result = await new DailyDiaryStore(context, profileId)
                .DeleteEntryAsync(id, revision, cancellationToken);
            RequireChange(result);
            return true;
        }, cancellationToken);
    }

    public async Task<LabelCapture> ReadLabelAsync(
        Guid profileId, ReadOnlyMemory<byte> image, string mediaType, GroqSettings settings,
        CancellationToken cancellationToken = default)
    {
        await _profiles.RequireAsync(profileId, cancellationToken);
        ValidatedLabelPhoto photo = LabelPhotoValidator.Validate(image, mediaType);
        using HttpClient client = CreateGroqClient(settings);
        NutritionLabelDraft draft = await new GroqNutritionLabelReader(client, settings.VisionModel)
            .ReadAsync(photo.Content, photo.MediaType, cancellationToken);
        string reference = await WithContextAsync(profileId,
            context => new LabelPhotoStore(context, _profiles.GetPhotoDirectory(profileId), profileId)
                .SaveAsync(photo, cancellationToken), cancellationToken);
        return new LabelCapture(draft, reference);
    }

    public async Task<string> TranscribeAsync(
        Guid profileId, ReadOnlyMemory<byte> audio, string? mediaType, GroqSettings settings,
        CancellationToken cancellationToken = default)
    {
        await _profiles.RequireAsync(profileId, cancellationToken);
        ValidatedAudio validated = AudioUploadValidator.Validate(audio, mediaType);
        using HttpClient client = CreateGroqClient(settings);
        return await new GroqSpeechTranscriber(client, settings.SpeechModel)
            .TranscribeAsync(validated, cancellationToken);
    }

    private static async Task<MealSessionResponse> CreateDraftAsync(
        NutriFlowDbContext context, Guid profileId, MealDraft draft, DateOnly date,
        MealSessionPurpose purpose, CancellationToken cancellationToken, MealDraft? readableDraft = null)
    {
        MealSessionCreationResult result = await CreateWorkflow(context, profileId, new FixedMealParser(draft)).CreateAsync(
            DescribeManualDraft(readableDraft ?? draft, purpose), date, cancellationToken, Guid.NewGuid(), purpose);
        return result.Session;
    }

    private static IReadOnlyList<string> DescribeManualDraft(MealDraft draft, MealSessionPurpose purpose)
    {
        List<string> lines = [purpose == MealSessionPurpose.CreateDish
            ? "Ручной ввод: создать сохранённое блюдо (CreateDish)."
            : "Ручной ввод: записать съеденную порцию в дневник (Diary)."];
        foreach (DishDraft dish in draft.Dishes)
        {
            lines.Add($"Блюдо: «{dish.Name}».");
            foreach (IngredientDraft ingredient in dish.Ingredients)
            {
                lines.Add($"Ингредиент «{ingredient.ProductName}»: исходный вес {DescribeWeight(ingredient.WeightInGrams, ingredient.WeightQuality)}; " +
                    $"убрано {DescribeWeight(ingredient.RemovedWeightInGrams, ingredient.RemovedWeightQuality)}.");
            }

            lines.Add($"Готовый вес блюда: {DescribeWeight(dish.FinalWeightInGrams, dish.FinalWeightQuality)}.");
            foreach (PortionDraft portion in dish.Portions)
            {
                lines.Add(portion.WeightInGrams is not null
                    ? $"Съеденная порция: {DescribeWeight(portion.WeightInGrams, portion.WeightQuality)}."
                    : $"Съеденная доля блюда: {portion.FractionOfDish!.Value.ToString("G29", CultureInfo.InvariantCulture)} ({DescribeQuality(portion.WeightQuality)}).");
            }
        }

        List<string> messages = [];
        StringBuilder message = new();
        foreach (string line in lines)
        {
            if (message.Length + line.Length + Environment.NewLine.Length > 4000)
            {
                messages.Add(message.ToString().TrimEnd());
                message.Clear();
            }

            message.AppendLine(line);
        }

        messages.Add(message.ToString().TrimEnd());
        return messages;
    }

    private static string DescribeWeight(decimal? grams, DataQuality quality) => grams is null
        ? "не указан"
        : $"{grams.Value.ToString("G29", CultureInfo.InvariantCulture)} г ({DescribeQuality(quality)})";

    private static string DescribeQuality(DataQuality quality) => quality == DataQuality.Estimated ? "примерно" : "точно";

    private static async Task RequireUnambiguousNameAsync(
        NutriFlowDbContext context, Guid profileId, string name, CancellationToken cancellationToken)
    {
        if ((await new LocalProductCatalog(context, profileId).FindByNameAsync(name, cancellationToken)).Count > 1)
        {
            throw new ArgumentException("Найдено несколько продуктов с таким названием. Выберите конкретный продукт из каталога.", nameof(name));
        }
    }

    private static async Task RequireUnambiguousManualSelectionAsync(
        NutriFlowDbContext context, Guid profileId, StoredMealSession session, CancellationToken cancellationToken)
    {
        LocalProductCatalog catalog = new(context, profileId);
        SavedDishStore dishes = new(context, profileId);
        foreach (IngredientDraft ingredient in session.Draft.Dishes.SelectMany(dish => dish.Ingredients))
        {
            string handle = ingredient.ProductName;
            bool productSelection = handle.StartsWith("selected-", StringComparison.Ordinal) &&
                                    Guid.TryParseExact(handle[9..], "N", out _);
            bool dishSelection = handle.StartsWith("saved-dish:", StringComparison.Ordinal) &&
                                 Guid.TryParseExact(handle[11..], "N", out _);
            if (!productSelection && !dishSelection)
            {
                continue;
            }

            StoredSavedDish? selectedDish = dishSelection ? await dishes.FindByNameAsync(handle, cancellationToken) : null;
            Product? selectedProduct = productSelection
                ? (await catalog.FindByNameAsync(handle, cancellationToken)).SingleOrDefault()
                : selectedDish?.Product;
            if (selectedProduct is null)
            {
                throw AmbiguousManualSelection();
            }

            IReadOnlyList<Product> products = await catalog.FindByNameAsync(selectedProduct.Name, cancellationToken);
            StoredSavedDish? namesakeDish = await dishes.FindByNameAsync(selectedProduct.Name, cancellationToken);
            if (products.Count > 1 || (products.Count != 0 && namesakeDish is not null) ||
                (dishSelection && namesakeDish?.Id != selectedDish?.Id))
            {
                throw AmbiguousManualSelection();
            }
        }
    }

    private static InvalidOperationException AmbiguousManualSelection() =>
        new("У этого названия несколько вариантов. Создайте новый ручной черновик и выберите нужный продукт.");

    private Task<T> WithWorkflowAsync<T>(Guid profileId, IMealParser? parser,
        Func<MealWorkflowService, Task<T>> operation, CancellationToken cancellationToken)
    {
        return WithContextAsync(profileId,
            context => operation(CreateWorkflow(context, profileId, parser)), cancellationToken);
    }

    private async Task<T> WithContextAsync<T>(Guid profileId,
        Func<NutriFlowDbContext, Task<T>> operation, CancellationToken cancellationToken)
    {
        await _profiles.RequireAsync(profileId, cancellationToken);
        await using NutriFlowDbContext context = await LocalProfileDatabase.OpenAsync(
            _profiles.GetDatabasePath(profileId), profileId, cancellationToken);
        return await operation(context);
    }

    private static MealWorkflowService CreateWorkflow(NutriFlowDbContext context, Guid profileId, IMealParser? parser)
    {
        return new MealWorkflowService(parser ?? new FixedMealParser(null),
            new LocalProductCatalog(context, profileId), new MealSessionStore(context, profileId),
            new DailyDiaryStore(context, profileId), new SavedDishStore(context, profileId));
    }

    private HttpClient CreateGroqClient(GroqSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || settings.ApiKey.Length > 2048 ||
            settings.ApiKey.Any(char.IsWhiteSpace) || settings.ApiKey.Any(char.IsControl))
        {
            throw new ArgumentException("Укажи свой Groq-ключ в настройках выбранного профиля.", nameof(settings));
        }

        HttpClient client = _httpClientFactory();
        client.BaseAddress = new Uri("https://api.groq.com/openai/v1/");
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        return client;
    }

    private static NutritionValues ToNutrition(NutritionResponse value) =>
        new(value.Calories, value.ProteinGrams, value.FatGrams, value.CarbohydratesGrams);

    private static Product ToProduct(ProductResponse selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return new Product(selected.Name,
            new NutritionValues(selected.Calories, selected.ProteinGrams, selected.FatGrams, selected.CarbohydratesGrams),
            new NutritionSource(Enum.Parse<NutritionSourceKind>(selected.SourceKind),
                Enum.Parse<DataQuality>(selected.DataQuality), selected.SourceName, selected.SourceReference), selected.Barcode);
    }

    private static void RequireChange(MealEntryChangeResult result)
    {
        if (result.Kind == MealEntryChangeKind.NotFound)
        {
            throw new KeyNotFoundException("Запись не найдена в этом профиле.");
        }

        if (result.Kind == MealEntryChangeKind.RevisionConflict)
        {
            throw new InvalidOperationException("Запись уже изменилась. Обнови дневник и повтори действие.");
        }
    }

    private sealed class FixedMealParser(MealDraft? draft) : IMealParser
    {
        public Task<MealDraft> ParseAsync(CaptureSession session, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(draft ?? throw new InvalidOperationException("Для разбора текста нужен Groq-ключ."));
        }
    }
}
