using System.Net;
using System.Text;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Capture;
using NutriFlow.Ui.Catalog;
using NutriFlow.Ui.Pages;

namespace NutriFlow.Ui.Tests;

// products and dishes on the real client and a temporary profile folder: the calculations without Groq start in the
// food input and reach the diary only after the confirmation
public sealed class CatalogTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nutriflow-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly FakeAppHost _host = new();
    private readonly FakeProfileSettings _settings = new();
    private readonly OpenFoodFactsStub _openFoodFacts = new();
    private readonly LocalProfileStore _profiles;
    private readonly LocalInputDraftStore _drafts;
    private readonly LocalMealClient _meals;

    public CatalogTests()
    {
        _profiles = new LocalProfileStore(_root);
        _drafts = new LocalInputDraftStore(_profiles);
        _meals = new LocalMealClient(_profiles, () => new HttpClient(_openFoodFacts, disposeHandler: false));
    }

    [Fact]
    public async Task ManualProductIsFoundAndItsPortionReachesTheDiaryAfterConfirmation()
    {
        Guid profileId = await CreateProfileAsync();
        Assert.True(await _meals.AddProductAsync(profileId, Product("Творог 5%", 121m, 17.2m, 5m, 1.8m)));
        using ProductSearch search = new(_meals, profileId, TimeSpan.Zero);
        search.SetQuery("творог");
        await search.SearchNowAsync();
        ProductResponse found = Assert.Single(search.Results!);
        CaptureState state = await LoadAsync(profileId);

        bool started = await state.StartAsync(id => _meals.CreateManualMealAsync(id, found, 200m, Today.AddDays(-1)), Refuse);

        Assert.True(started);
        MealSessionResponse session = Assert.IsType<MealSessionResponse>(state.Session);
        Assert.True(session.CanConfirm);
        Assert.Equal(session.Id, _settings.Sessions[profileId]);
        Assert.Equal(Today.AddDays(-1), state.Date);
        Assert.Empty((await _meals.GetDailyProgressAsync(profileId, Today.AddDays(-1))).Entries);

        await state.ConfirmAsync();

        DailyProgressResponse day = await _meals.GetDailyProgressAsync(profileId, Today.AddDays(-1));
        MealEntryResponse entry = Assert.Single(day.Entries);
        Assert.Equal(200m, entry.WeightInGrams);
        Assert.Equal(242m, entry.Nutrition.Calories);
    }

    [Fact]
    public async Task TypedTextIsReplacedOnlyWhenTheUserAgrees()
    {
        Guid profileId = await CreateProfileAsync();
        ProductResponse product = Product("Гречка", 110m, 3.6m, 1.3m, 21.3m);
        await _meals.AddProductAsync(profileId, product);
        CaptureState state = await LoadAsync(profileId);
        state.SetText("гречка 150 г");
        int asked = 0;

        Assert.False(await state.StartAsync(id => _meals.CreateManualMealAsync(id, product, 150m, Today), () =>
        {
            asked++;
            return Task.FromResult(false);
        }));
        Assert.Equal(1, asked);
        Assert.Null(state.Session);
        Assert.Equal("гречка 150 г", (await _drafts.ReadAsync(profileId))?.Text);

        Assert.True(await state.StartAsync(id => _meals.CreateManualMealAsync(id, product, 150m, Today), () => Task.FromResult(true)));
        Assert.NotNull(state.Session);
        Assert.Equal("", state.Text);
        Assert.Null(await _drafts.ReadAsync(profileId));
    }

    [Fact]
    public async Task EmptyInputStartsWithoutAQuestionAndAWaitingTextBlocksIt()
    {
        Guid profileId = await CreateProfileAsync();
        ProductResponse product = Product("Гречка", 110m, 3.6m, 1.3m, 21.3m);
        await _meals.AddProductAsync(profileId, product);
        await _drafts.SaveAsync(profileId, new LocalInputDraft("старый текст", Today, MealSessionPurpose.Diary, Guid.NewGuid()));
        CaptureState state = await LoadAsync(profileId);
        Assert.True(state.Blocked);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            state.StartAsync(id => _meals.CreateManualMealAsync(id, product, 150m, Today), Refuse));

        Assert.Equal(CaptureText.Blocked, error.Message);
        await state.DiscardDraftAsync();
        Assert.True(await state.StartAsync(id => _meals.CreateManualMealAsync(id, product, 150m, Today), Refuse));
    }

    [Fact]
    public async Task DishFromTwoProductsIsSavedAndItsPortionCountsFromTheFinalWeight()
    {
        Guid profileId = await CreateProfileAsync();
        ProductResponse rice = Product("Рис", 330m, 7m, 1m, 74m);
        ProductResponse chicken = Product("Куриное бедро", 185m, 18m, 12m, 0m);
        await _meals.AddProductAsync(profileId, rice);
        await _meals.AddProductAsync(profileId, chicken);
        CaptureState state = await LoadAsync(profileId);

        // 200 g of rice and 500 g of chicken with 100 g of bones: 660 + 740 kcal in 1000 g of the cooked dish
        await state.StartAsync(id => _meals.CreateManualDishAsync(id, "Плов",
            [new ManualIngredient(rice, 200m), new ManualIngredient(chicken, 500m, RemovedWeightInGrams: 100m)], 1000m, Today), Refuse);
        Assert.Equal(MealSessionPurpose.CreateDish, state.Purpose);
        Assert.True(state.Session!.CanConfirm);
        ConfirmMealSessionResponse saved = await state.ConfirmAsync();

        SavedDishResponse dish = Assert.IsType<SavedDishResponse>(saved.SavedDish);
        Assert.Equal(Assert.Single(await _meals.GetSavedDishesAsync(profileId)).Id, dish.Id);
        Assert.Equal(140m, dish.NutritionPer100Grams.Calories);
        Assert.Empty((await _meals.GetDailyProgressAsync(profileId, Today)).Entries);
        SavedDishDetailResponse detail = Assert.IsType<SavedDishDetailResponse>(await _meals.FindSavedDishAsync(profileId, dish.Id));
        Assert.Equal(2, detail.Dish.Ingredients.Count);

        await state.StartAsync(id => _meals.CreateSavedDishMealAsync(id, dish.Id, 250m, Today), Refuse);
        Assert.Equal(MealSessionPurpose.Diary, state.Purpose);
        await state.ConfirmAsync();

        MealEntryResponse entry = Assert.Single((await _meals.GetDailyProgressAsync(profileId, Today)).Entries);
        Assert.Equal(350m, entry.Nutrition.Calories);
    }

    [Fact]
    public async Task OnlyTheAnswerToTheLastQueryIsShown()
    {
        Guid profileId = await CreateProfileAsync();
        await _meals.AddProductAsync(profileId, Product("Гречка", 110m, 3.6m, 1.3m, 21.3m));
        await _meals.AddProductAsync(profileId, Product("Творог", 121m, 17.2m, 5m, 1.8m));
        using ProductSearch search = new(_meals, profileId, TimeSpan.FromMilliseconds(30));
        int changes = 0;
        search.Changed += () => changes++;

        search.SetQuery("гре");
        search.SetQuery("тво");
        for (int attempt = 0; attempt < 100 && search.Results is null; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal("Творог", Assert.Single(search.Results!).Name);
        Assert.Equal("тво", search.ResultsQuery);
        Assert.Equal(1, changes);
        await search.SearchNowAsync();
        search.SetQuery("");
        await search.SearchNowAsync();
        Assert.Equal(2, search.Results!.Count);
    }

    [Fact]
    public async Task BarcodeBringsTheProductFromOpenFoodFactsIntoTheCatalog()
    {
        Guid profileId = await CreateProfileAsync();
        _openFoodFacts.Body = """
            {
              "code": "4600000000017",
              "product": {
                "product_name_ru": "Кефир 2,5%",
                "nutriments": { "energy-kcal_100g": 53, "proteins_100g": 2.9, "fat_100g": 2.5, "carbohydrates_100g": 4 }
              }
            }
            """;

        ProductResponse product = Assert.IsType<ProductResponse>(await _meals.LookupBarcodeAsync(profileId, "4600000000017"));

        Assert.Equal("Кефир 2,5%", product.Name);
        Assert.Equal("ExternalService", product.SourceKind);
        Assert.Contains("api/v3/product/4600000000017", _openFoodFacts.LastRequest);
        Assert.Equal("Кефир 2,5%", Assert.Single(await _meals.SearchProductsAsync(profileId, "кефир")).Name);
        // a product from the open database is eaten like any other
        CaptureState state = await LoadAsync(profileId);
        await state.StartAsync(id => _meals.CreateManualMealAsync(id, product, 250m, Today), Refuse);
        Assert.True(state.Session!.CanConfirm);
    }

    [Fact]
    public async Task MissingOrIncompleteBarcodeIsOneCaseAndNoConnectionAnother()
    {
        Guid profileId = await CreateProfileAsync();
        _openFoodFacts.Status = HttpStatusCode.NotFound;
        Assert.Null(await _meals.LookupBarcodeAsync(profileId, "4600000000024"));

        _openFoodFacts.Status = HttpStatusCode.OK;
        _openFoodFacts.Body = """{ "code": "4600000000031", "product": { "product_name": "Без КБЖУ" } }""";
        Exception incomplete = await Assert.ThrowsAnyAsync<Exception>(() => _meals.LookupBarcodeAsync(profileId, "4600000000031"));
        Assert.True(CatalogText.IsBarcodeMissing(incomplete));

        _openFoodFacts.Failure = new HttpRequestException("Failed to fetch");
        Exception offline = await Assert.ThrowsAnyAsync<Exception>(() => _meals.LookupBarcodeAsync(profileId, "4600000000048"));
        Assert.False(CatalogText.IsBarcodeMissing(offline));
        Assert.Equal(CatalogText.OpenFoodFactsUnreachable, CatalogText.BarcodeError(offline));
        Assert.Equal(CatalogText.OpenFoodFactsUnreachable, CatalogText.BarcodeError(new TaskCanceledException()));
    }

    [Theory]
    [InlineData("", "Введите цифры под штрихкодом")]
    [InlineData("4600 0000", "Введите только цифры")]
    [InlineData("12345", "В штрихкоде 8, 12, 13 или 14 цифр, а введено 5.")]
    public void BarcodeFieldSaysWhatToType(string text, string start)
    {
        Assert.StartsWith(start, CatalogText.BarcodeProblem(text));
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData(" 4600000000017 ")]
    [InlineData("12345678901234")]
    public void BarcodeOfASupportedLengthIsAccepted(string text)
    {
        Assert.Null(CatalogText.BarcodeProblem(text));
    }

    [Fact]
    public void ListsCountAndMarkEstimates()
    {
        Assert.Equal("1 продукт", CatalogText.Products(1));
        Assert.Equal("3 продукта", CatalogText.Products(3));
        Assert.Equal("12 блюд", CatalogText.Dishes(12));
        Assert.Equal("22 блюда", CatalogText.Dishes(22));
        Assert.Equal("≈ 1 234", CatalogText.Per100Calories(1234m, "Estimated"));
        Assert.Equal("110", CatalogText.Per100Calories(110m, "Exact"));
        Assert.Equal("200 г, не вошло 50 г", CatalogText.IngredientWeight(200m, 50m));
        Assert.Equal("Творог. На 100 граммов: 121 килокалорий, белки 17, жиры 5, углеводы 2 граммов. точные данные",
            CatalogText.ProductDescription(Product("Творог", 121m, 17.2m, 5m, 1.8m)));
    }

    [Fact]
    public void IngredientSourceDoesNotRepeatItsKind()
    {
        Assert.Equal("Ручной ввод пользователя · точные данные", CatalogText.IngredientSource(Product("Творог", 121m, 17.2m, 5m, 1.8m)));
        Assert.Equal("внешний каталог · качество не установлено", CatalogText.IngredientSource(
            new ProductResponse("Кефир", 53m, 2.9m, 2.5m, 4m, "ExternalService", "Unknown", "", null, "4600000000017")));
    }

    private static Task<bool> Refuse() => throw new InvalidOperationException("Nothing to replace, no question expected.");

    private async Task<Guid> CreateProfileAsync() => (await _profiles.CreateAsync("Тест")).Id;

    private async Task<CaptureState> LoadAsync(Guid profileId)
    {
        CaptureState state = new(_meals, _drafts, _settings, _host, TimeSpan.FromMilliseconds(1));
        await state.LoadAsync(profileId);
        return state;
    }

    private static ProductResponse Product(string name, decimal calories, decimal protein, decimal fat, decimal carbs) =>
        new(name, calories, protein, fat, carbs, "ManualInput", "Exact", "Ручной ввод пользователя", null, null);

    private sealed class OpenFoodFactsStub : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string Body { get; set; } = "{}";

        public Exception? Failure { get; set; }

        public string? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request.RequestUri?.ToString();
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json")
            });
        }
    }

    public void Dispose()
    {
        _openFoodFacts.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
