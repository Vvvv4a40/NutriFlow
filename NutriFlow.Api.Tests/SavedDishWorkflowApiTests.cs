using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Api.Tests;

public sealed class SavedDishWorkflowApiTests
{
    private static readonly DateOnly MealDate = new(2026, 10, 5);
    private static readonly string[] DishMessages =
    [
        "Добавил 200 г демо-продукта A.",
        "Потом добавил 100 г демо-продукта B.",
        "Готовое блюдо весит 250 г."
    ];

    [Fact]
    public async Task CreateDish_WithoutEatenPortion_SavesCompositionWithoutChangingDiary()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        MealSessionResponse session = await CreateSessionAsync(client, DishMessages);
        Assert.Equal("CreateDish", session.Purpose);
        Assert.True(session.CanConfirm);
        Assert.Empty(Assert.Single(session.Dishes).Portions);
        Assert.DoesNotContain(session.Issues, issue => issue.Code == "portion_missing");
        Assert.Empty(await ListDishesAsync(client));
        ConfirmMealSessionResponse confirmed = await ConfirmAsync(client, session);

        Assert.Equal("Confirmed", confirmed.Outcome);
        Assert.Equal("Confirmed", confirmed.Session.Status);
        Assert.Empty(confirmed.Entries);
        SavedDishResponse saved = Assert.IsType<SavedDishResponse>(confirmed.SavedDish);
        Assert.Equal(session.Id, saved.SourceSessionId);
        Assert.Equal("Демо-блюдо", saved.Name);
        Assert.Equal(250m, saved.FinalWeightInGrams);
        AssertNutrition(saved.NutritionPer100Grams, 160m, 10m, 8m, 12m);
        Assert.Equal("Unknown", saved.NutritionQuality);
        Assert.Equal(saved, Assert.Single(await ListDishesAsync(client)));
        SavedDishDetailResponse detail = await ReadAsync<SavedDishDetailResponse>(
            await client.GetAsync($"/api/saved-dishes/{saved.Id}"));
        Assert.Equal(saved, detail.SavedDish);
        Assert.Equal(2, detail.Dish.Ingredients.Count);
        Assert.Equal(200m, detail.Dish.Ingredients[0].WeightInGrams);
        Assert.Equal(100m, detail.Dish.Ingredients[1].WeightInGrams);
        Assert.Equal("NutriFlow demo dataset", detail.Dish.Ingredients[0].ResolvedProduct!.SourceName);
        AssertNutrition(detail.Dish.TotalNutrition!, 400m, 25m, 20m, 30m);
        await AssertEmptyDiaryAsync(client);
    }

    [Fact]
    public async Task Diary_NaturallyReferencesSavedDish_UsesItsComputedNutritionAndQuality()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        SavedDishResponse saved = (await ConfirmAsync(
            client, await CreateSessionAsync(client, DishMessages))).SavedDish!;

        MealSessionResponse meal = await CreateSessionAsync(
            client, ["Съел 100 г Демо-блюда."], MealSessionPurpose.Diary);
        IngredientPreviewResponse ingredient = Assert.Single(Assert.Single(meal.Dishes).Ingredients);
        ProductResponse product = Assert.IsType<ProductResponse>(ingredient.ResolvedProduct);
        Assert.Equal("SavedDish", product.SourceKind);
        Assert.Equal($"saved-dish:{saved.Id:N}", product.SourceReference);
        Assert.Equal("Unknown", product.DataQuality);
        Assert.Equal("Diary", meal.Purpose);
        Assert.True(meal.CanConfirm);
        AssertNutrition(ingredient.Nutrition!, 160m, 10m, 8m, 12m);

        ConfirmMealSessionResponse confirmed = await ConfirmAsync(client, meal);

        Assert.Null(confirmed.SavedDish);
        MealEntryResponse entry = Assert.Single(confirmed.Entries);
        Assert.Equal("Демо-блюдо", entry.Name);
        Assert.Equal(100m, entry.WeightInGrams);
        Assert.Equal("Unknown", entry.Quality);
        AssertNutrition(entry.Nutrition, 160m, 10m, 8m, 12m);
        DailyProgressResponse day = await GetDiaryAsync(client);
        Assert.Single(day.Entries);
        AssertNutrition(day.Consumed, 160m, 10m, 8m, 12m);
        Assert.Single(await ListDishesAsync(client));
    }

    [Fact]
    public async Task ConfirmDish_ConcurrentRequestsAndRepeat_CreateExactlyOneSavedDish()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        MealSessionResponse session = await CreateSessionAsync(client, DishMessages);
        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => client.PostAsJsonAsync(
                $"/api/meal-sessions/{session.Id}/confirm",
                new ConfirmMealSessionRequest(session.PreviewToken))))
            .WaitAsync(TimeSpan.FromSeconds(20));

        try
        {
            List<ConfirmMealSessionResponse> results = new();
            foreach (HttpResponseMessage response in responses)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                results.Add(await ReadAsync<ConfirmMealSessionResponse>(response));
            }

            Assert.Single(results, result => result.Outcome == "Confirmed");
            Assert.Equal(3, results.Count(result => result.Outcome == "AlreadyConfirmed"));
            Guid savedId = Assert.Single(results.Select(result => result.SavedDish!.Id).Distinct());
            Assert.All(results, result => Assert.Empty(result.Entries));
            ConfirmMealSessionResponse repeated = await ConfirmAsync(client, session);
            Assert.Equal("AlreadyConfirmed", repeated.Outcome);
            Assert.Equal(savedId, repeated.SavedDish!.Id);
            Assert.Equal(savedId, Assert.Single(await ListDishesAsync(client)).Id);
            await AssertEmptyDiaryAsync(client);
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task ConfirmDish_AfterRestart_ReturnsSameSavedResult()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"nutriflow-saved-{Guid.NewGuid():N}.db");
        MealSessionResponse session;
        SavedDishResponse saved;

        try
        {
            await using (TestApiFactory first = new(databasePath: databasePath))
            {
                using HttpClient client = first.CreateClient();
                session = await CreateSessionAsync(client, DishMessages);
                saved = (await ConfirmAsync(client, session)).SavedDish!;
            }

            await using (TestApiFactory second = new(databasePath: databasePath))
            {
                using HttpClient client = second.CreateClient();
                ConfirmMealSessionResponse repeated = await ConfirmAsync(client, session);
                Assert.Equal("AlreadyConfirmed", repeated.Outcome);
                Assert.Equal(saved, repeated.SavedDish);
                Assert.Equal(saved, Assert.Single(await ListDishesAsync(client)));
                await AssertEmptyDiaryAsync(client);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete($"{databasePath}-wal");
            File.Delete($"{databasePath}-shm");
        }
    }

    [Fact]
    public async Task SavedDish_AfterIngredientCatalogChanges_KeepsReviewedSourcesAndNutrition()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        SavedDishResponse saved = (await ConfirmAsync(
            client, await CreateSessionAsync(client, DishMessages))).SavedDish!;
        string before = await client.GetStringAsync($"/api/saved-dishes/{saved.Id}");

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            NutriFlowDbContext database = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
            int changed = await database.Database.ExecuteSqlRawAsync("""
                UPDATE Products SET Calories = '999', SourceName = 'Changed source'
                WHERE Name = 'Демо-продукт A'
                """);
            Assert.Equal(1, changed);
        }

        Assert.Equal(before, await client.GetStringAsync($"/api/saved-dishes/{saved.Id}"));
        MealSessionResponse meal = await CreateSessionAsync(
            client, ["Съел 100 г Демо-блюда."], MealSessionPurpose.Diary);
        ConfirmMealSessionResponse confirmed = await ConfirmAsync(client, meal);
        AssertNutrition(Assert.Single(confirmed.Entries).Nutrition, 160m, 10m, 8m, 12m);
    }

    [Fact]
    public async Task Diary_WithoutPortion_RemainsBlockedAndDoesNotSaveDish()
    {
        await using TestApiFactory factory = new(parser: new DelegateParser(_ => CreateDraft()));
        using HttpClient client = factory.CreateClient();
        MealSessionResponse session = await CreateSessionAsync(
            client, ["Состав блюда без съеденной порции."], MealSessionPurpose.Diary);

        Assert.False(session.CanConfirm);
        Assert.Equal("NeedsClarification", session.Status);
        Assert.Contains(session.Issues, issue => issue.Code == "portion_missing");
        using HttpResponseMessage response = await PostConfirmationAsync(client, session);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("NotReady", (await ReadAsync<ConfirmMealSessionResponse>(response)).Outcome);
        Assert.Empty(await ListDishesAsync(client));
        await AssertEmptyDiaryAsync(client);
    }

    [Theory]
    [InlineData("ingredient", "ingredient_weight_missing", "NeedsClarification")]
    [InlineData("final", "final_weight_missing", "NeedsClarification")]
    [InlineData("removal", "removed_weight_missing", "NeedsClarification")]
    [InlineData("product", "product_not_found", "NeedsProducts")]
    public async Task CreateDish_WithUnresolvedFactsOrProducts_CannotBeConfirmed(
        string missing,
        string issueCode,
        string status)
    {
        await using TestApiFactory factory = new(parser: new DelegateParser(_ => CreateIncompleteDraft(missing)));
        using HttpClient client = factory.CreateClient();
        MealSessionResponse session = await CreateSessionAsync(client, ["Неполное блюдо."]);

        Assert.False(session.CanConfirm);
        Assert.Equal(status, session.Status);
        Assert.Contains(session.Issues, issue => issue.Code == issueCode);
        Assert.DoesNotContain(session.Issues, issue => issue.Code == "portion_missing");
        using HttpResponseMessage response = await PostConfirmationAsync(client, session);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("NotReady", (await ReadAsync<ConfirmMealSessionResponse>(response)).Outcome);
        Assert.Empty(await ListDishesAsync(client));
        await AssertEmptyDiaryAsync(client);
    }

    [Fact]
    public async Task CreateDish_WithMultipleDishes_RequiresOneComposition()
    {
        await using TestApiFactory factory = new(parser: new DelegateParser(_ =>
            new MealDraft([CreateDraft().Dishes[0], CreateDraft("Второе блюдо").Dishes[0]], [])));
        using HttpClient client = factory.CreateClient();
        MealSessionResponse session = await CreateSessionAsync(client, ["Два разных блюда."]);

        Assert.False(session.CanConfirm);
        Assert.Contains(session.Issues, issue => issue.Code == "single_dish_required");
        using HttpResponseMessage response = await PostConfirmationAsync(client, session);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await ListDishesAsync(client));
        await AssertEmptyDiaryAsync(client);
    }

    [Fact]
    public async Task CreateDish_WithNameExpandingBeyondStoredLimit_IsBlockedBeforeConfirmation()
    {
        string name = new string('\u0344', 200);
        Assert.Equal(400, name.Normalize().Length);
        await using TestApiFactory factory = new(parser: new DelegateParser(_ => CreateDraft(name)));
        using HttpClient client = factory.CreateClient();
        MealSessionResponse session = await CreateSessionAsync(client, ["Блюдо с длинным названием."]);

        Assert.False(session.CanConfirm);
        Assert.Contains(session.Issues, issue => issue.Code == "saved_dish_invalid");
        using HttpResponseMessage response = await PostConfirmationAsync(client, session);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("NotReady", (await ReadAsync<ConfirmMealSessionResponse>(response)).Outcome);
        Assert.Empty(await ListDishesAsync(client));
        await AssertEmptyDiaryAsync(client);
    }

    [Fact]
    public async Task CreateDish_WithImplausibleNutritionPer100Grams_IsBlockedBeforeConfirmation()
    {
        await using TestApiFactory factory = new(parser: new DelegateParser(_ => new MealDraft(
            [new DishDraft("Слишком концентрированное блюдо",
                [new IngredientDraft("Демо-продукт A", 100m)],
                0.01m, DataQuality.Exact, [])], [])));
        using HttpClient client = factory.CreateClient();
        MealSessionResponse session = await CreateSessionAsync(client, ["Итоговый вес слишком мал."]);

        Assert.False(session.CanConfirm);
        Assert.Contains(session.Issues, issue => issue.Code == "saved_dish_invalid");
        using HttpResponseMessage response = await PostConfirmationAsync(client, session);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("NotReady", (await ReadAsync<ConfirmMealSessionResponse>(response)).Outcome);
        Assert.Empty(await ListDishesAsync(client));
        await AssertEmptyDiaryAsync(client);
    }

    [Fact]
    public async Task CreateDish_WithEatingDetails_DoesNotTurnThemIntoDiaryEntries()
    {
        await using TestApiFactory factory = new(parser: new DelegateParser(_ => CreateDraft(withPortion: true)));
        using HttpClient client = factory.CreateClient();
        MealSessionResponse session = await CreateSessionAsync(client, ["Приготовил и попробовал блюдо."]);

        Assert.True(session.CanConfirm);
        Assert.Empty(Assert.Single(session.Dishes).Portions);
        ConfirmMealSessionResponse confirmed = await ConfirmAsync(client, session);
        Assert.Empty(confirmed.Entries);
        Assert.NotNull(confirmed.SavedDish);
        await AssertEmptyDiaryAsync(client);
    }

    [Fact]
    public async Task IdempotencyKey_WithChangedPurpose_ReturnsConflictWithoutReparsing()
    {
        DelegateParser parser = new(_ => CreateDraft(withPortion: true));
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        Guid key = Guid.NewGuid();
        using HttpResponseMessage created = await PostSessionAsync(
            client, ["Одинаковая история."], MealSessionPurpose.Diary, key);
        MealSessionResponse original = await ReadAsync<MealSessionResponse>(created);
        using HttpResponseMessage conflict = await PostSessionAsync(
            client, ["Одинаковая история."], MealSessionPurpose.CreateDish, key);
        using HttpResponseMessage repeated = await PostSessionAsync(
            client, ["Одинаковая история."], MealSessionPurpose.Diary, key);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(original.Id, (await ReadAsync<MealSessionResponse>(repeated)).Id);
        Assert.Equal(1, parser.CallCount);
        Assert.Empty(await ListDishesAsync(client));
        await AssertEmptyDiaryAsync(client);
    }

    [Fact]
    public async Task DiaryIdempotencyHash_PreservesPrePurposeFormat()
    {
        string[] messages = ["Прежний дневниковый запрос."];
        DelegateParser parser = new(_ => CreateDraft(withPortion: true));
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        Guid key = Guid.NewGuid();
        using HttpResponseMessage created = await PostSessionAsync(
            client, messages, MealSessionPurpose.Diary, key);
        MealSessionResponse session = await ReadAsync<MealSessionResponse>(created);
        string originalHash = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new { Messages = messages, MealDate })));
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        NutriFlowDbContext database = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
        await using SqliteConnection connection = new(database.Database.GetConnectionString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT OriginalRequestHash FROM MealSessions WHERE Id = $id";
        command.Parameters.AddWithValue("$id", session.Id.ToString().ToUpperInvariant());

        Assert.Equal(originalHash, await command.ExecuteScalarAsync());
        using HttpResponseMessage repeated = await PostSessionAsync(
            client, messages, MealSessionPurpose.Diary, key);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(session.Id, (await ReadAsync<MealSessionResponse>(repeated)).Id);
        Assert.Equal(1, parser.CallCount);
    }

    [Fact]
    public async Task Parser_ReceivesPurposeAndOnlySavedCanonicalNamesWithoutSyntheticMessages()
    {
        DelegateParser parser = new(session => CreateDraft(
            name: "Мой омлет", withPortion: session.Purpose == MealSessionPurpose.Diary));
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        MealSessionResponse dish = await CreateSessionAsync(client, ["Готовлю свой омлет."]);
        Assert.Equal(MealSessionPurpose.CreateDish, parser.LastSession!.Purpose);
        Assert.Empty(parser.LastSession.SavedDishNames);
        await ConfirmAsync(client, dish);

        await CreateSessionAsync(client, ["Съел порцию своего омлета."], MealSessionPurpose.Diary);

        CaptureSession context = Assert.IsType<CaptureSession>(parser.LastSession);
        Assert.Equal(MealSessionPurpose.Diary, context.Purpose);
        Assert.Equal("Мой омлет", Assert.Single(context.SavedDishNames));
        Assert.Equal("Съел порцию своего омлета.", Assert.Single(context.InputEvents).Text);
    }

    [Fact]
    public async Task DuplicateDishName_ConflictsWithoutClosingSessionAndAllowsRename()
    {
        DelegateParser parser = new(session => CreateDraft(
            session.InputEvents.Count > 1 ? "Новое название" : "Омлет"));
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        await ConfirmAsync(client, await CreateSessionAsync(client, ["Первый омлет."]));
        MealSessionResponse duplicate = await CreateSessionAsync(client, ["Второй омлет."]);
        using HttpResponseMessage conflict = await PostConfirmationAsync(client, duplicate);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType!.MediaType);
        MealSessionResponse unchanged = await ReadAsync<MealSessionResponse>(
            await client.GetAsync($"/api/meal-sessions/{duplicate.Id}"));
        Assert.Equal("ReadyForConfirmation", unchanged.Status);
        Assert.True(unchanged.CanConfirm);
        Assert.Equal(duplicate.PreviewToken, unchanged.PreviewToken);
        Assert.Single(await ListDishesAsync(client));
        using HttpResponseMessage addition = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{duplicate.Id}/messages",
            new AddMealSessionMessageRequest("Назови его Новое название."));
        Assert.Equal(HttpStatusCode.OK, addition.StatusCode);
        MealSessionResponse renamed = await ReadAsync<MealSessionResponse>(addition);
        Assert.Equal("CreateDish", renamed.Purpose);
        ConfirmMealSessionResponse confirmed = await ConfirmAsync(client, renamed);
        Assert.Equal("Новое название", confirmed.SavedDish!.Name);
        Assert.Equal(2, (await ListDishesAsync(client)).Count);
        await AssertEmptyDiaryAsync(client);
    }

    [Fact]
    public async Task SavedName_WithDifferentCatalogNutrition_RequiresDisambiguation()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        await ConfirmAsync(client, await CreateSessionAsync(client, DishMessages));
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            LocalProductCatalog catalog = scope.ServiceProvider.GetRequiredService<LocalProductCatalog>();
            await catalog.AddAsync(new Product(
                "Демо-блюдо", new NutritionValues(999m, 1m, 2m, 3m),
                new NutritionSource(NutritionSourceKind.ManualInput, DataQuality.Exact, "Manual input")));
        }

        MealSessionResponse session = await CreateSessionAsync(
            client, ["Съел 100 г Демо-блюда."], MealSessionPurpose.Diary);

        Assert.False(session.CanConfirm);
        Assert.Equal("NeedsProducts", session.Status);
        Assert.Contains(session.Issues, issue => issue.Code == "product_ambiguous");
        Assert.Null(Assert.Single(Assert.Single(session.Dishes).Ingredients).ResolvedProduct);
        using HttpResponseMessage response = await PostConfirmationAsync(client, session);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertEmptyDiaryAsync(client);
    }

    [Fact]
    public async Task SavedDish_ForAnotherOwner_IsNotListedLoadedOrGivenToParser()
    {
        DelegateParser parser = new(session => CreateDraft(
            session.InputEvents[0].Text == "Чужое блюдо." ? "Чужой омлет" : "Мой омлет"));
        await using TestApiFactory factory = new(parser: parser, seedDemoData: false);
        using HttpClient localClient = factory.CreateClient();
        Guid foreignOwner = Guid.NewGuid();
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            NutriFlowDbContext database = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Users (Id, CreatedAtUtc, IsLegacyLocal, LegacyLabelPhotosImported)
                VALUES ({foreignOwner}, {DateTimeOffset.UtcNow}, 0, 0)
                """);
            await AddTestProductAsync(scope.ServiceProvider.GetRequiredService<LocalProductCatalog>());
            await AddTestProductAsync(new LocalProductCatalog(database, foreignOwner));
        }

        SavedDishResponse local = (await ConfirmAsync(
            localClient, await CreateSessionAsync(localClient, ["Своё блюдо."]))).SavedDish!;
        await using var foreignFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<LocalProductCatalog>();
                services.AddScoped(serviceProvider => new LocalProductCatalog(
                    serviceProvider.GetRequiredService<NutriFlowDbContext>(), foreignOwner));
                services.RemoveAll<MealSessionStore>();
                services.AddScoped(serviceProvider => new MealSessionStore(
                    serviceProvider.GetRequiredService<NutriFlowDbContext>(), foreignOwner));
                services.RemoveAll<DailyDiaryStore>();
                services.AddScoped(serviceProvider => new DailyDiaryStore(
                    serviceProvider.GetRequiredService<NutriFlowDbContext>(), foreignOwner));
                services.RemoveAll<SavedDishStore>();
                services.AddScoped(serviceProvider => new SavedDishStore(
                    serviceProvider.GetRequiredService<NutriFlowDbContext>(), foreignOwner));
            }));
        using HttpClient foreignClient = foreignFactory.CreateClient();
        SavedDishResponse foreign = (await ConfirmAsync(
            foreignClient, await CreateSessionAsync(foreignClient, ["Чужое блюдо."]))).SavedDish!;

        Assert.Equal(local.Id, Assert.Single(await ListDishesAsync(localClient)).Id);
        Assert.Equal(foreign.Id, Assert.Single(await ListDishesAsync(foreignClient)).Id);
        using HttpResponseMessage hiddenForeign = await localClient.GetAsync($"/api/saved-dishes/{foreign.Id}");
        using HttpResponseMessage hiddenLocal = await foreignClient.GetAsync($"/api/saved-dishes/{local.Id}");
        Assert.Equal(HttpStatusCode.NotFound, hiddenForeign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hiddenLocal.StatusCode);
        await CreateSessionAsync(localClient, ["Локальная проверка контекста."]);
        Assert.Equal(new[] { "Мой омлет" }, parser.LastSession!.SavedDishNames);
        await CreateSessionAsync(foreignClient, ["Чужая проверка контекста."]);
        Assert.Equal(new[] { "Чужой омлет" }, parser.LastSession!.SavedDishNames);
    }

    private static MealDraft CreateDraft(string name = "Омлет", bool withPortion = false)
    {
        return new MealDraft(
            [new DishDraft(name, [new IngredientDraft("Демо-продукт A", 100m)],
                100m, DataQuality.Exact, withPortion ? [new PortionDraft(50m)] : [])],
            []);
    }

    private static MealDraft CreateIncompleteDraft(string missing)
    {
        IngredientDraft ingredient = missing switch
        {
            "ingredient" => new IngredientDraft("Демо-продукт A", null, DataQuality.Unknown),
            "removal" => new IngredientDraft("Демо-продукт A", 100m, DataQuality.Exact, null, DataQuality.Unknown),
            "product" => new IngredientDraft("Неизвестный продукт", 100m),
            _ => new IngredientDraft("Демо-продукт A", 100m)
        };

        return new MealDraft(
            [new DishDraft("Омлет", [ingredient], missing == "final" ? null : 100m,
                missing == "final" ? DataQuality.Unknown : DataQuality.Exact, [])],
            []);
    }

    private static async Task<MealSessionResponse> CreateSessionAsync(
        HttpClient client,
        IReadOnlyList<string> messages,
        MealSessionPurpose purpose = MealSessionPurpose.CreateDish)
    {
        using HttpResponseMessage response = await PostSessionAsync(client, messages, purpose);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<MealSessionResponse>(response);
    }

    private static async Task<HttpResponseMessage> PostSessionAsync(
        HttpClient client,
        IReadOnlyList<string> messages,
        MealSessionPurpose purpose,
        Guid? key = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/meal-sessions")
        {
            Content = JsonContent.Create(new CreateMealSessionRequest(messages, MealDate, purpose))
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key.Value.ToString("D"));
        }

        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostConfirmationAsync(
        HttpClient client,
        MealSessionResponse session)
    {
        return client.PostAsJsonAsync($"/api/meal-sessions/{session.Id}/confirm",
            new ConfirmMealSessionRequest(session.PreviewToken));
    }

    private static async Task<ConfirmMealSessionResponse> ConfirmAsync(
        HttpClient client,
        MealSessionResponse session)
    {
        using HttpResponseMessage response = await PostConfirmationAsync(client, session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<ConfirmMealSessionResponse>(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) where T : class
    {
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidDataException();
    }

    private static async Task<IReadOnlyList<SavedDishResponse>> ListDishesAsync(HttpClient client)
    {
        return await client.GetFromJsonAsync<SavedDishResponse[]>("/api/saved-dishes")
            ?? throw new InvalidDataException();
    }

    private static async Task<DailyProgressResponse> GetDiaryAsync(HttpClient client)
    {
        return await client.GetFromJsonAsync<DailyProgressResponse>($"/api/daily-progress/{MealDate:yyyy-MM-dd}")
            ?? throw new InvalidDataException();
    }

    private static async Task AssertEmptyDiaryAsync(HttpClient client)
    {
        DailyProgressResponse day = await GetDiaryAsync(client);
        Assert.Empty(day.Entries);
        AssertNutrition(day.Consumed, 0m, 0m, 0m, 0m);
    }

    private static void AssertNutrition(
        NutritionResponse nutrition,
        decimal calories,
        decimal protein,
        decimal fat,
        decimal carbohydrates)
    {
        Assert.Equal(calories, nutrition.Calories);
        Assert.Equal(protein, nutrition.ProteinGrams);
        Assert.Equal(fat, nutrition.FatGrams);
        Assert.Equal(carbohydrates, nutrition.CarbohydratesGrams);
    }

    private static Task<bool> AddTestProductAsync(LocalProductCatalog catalog)
    {
        return catalog.AddAsync(new Product(
            "Демо-продукт A", new NutritionValues(100m, 10m, 4m, 6m),
            new NutritionSource(NutritionSourceKind.ManualInput, DataQuality.Exact, "Manual input")));
    }

    private sealed class DelegateParser(Func<CaptureSession, MealDraft> parse) : IMealParser
    {
        public CaptureSession? LastSession { get; private set; }
        public int CallCount { get; private set; }

        public Task<MealDraft> ParseAsync(CaptureSession session, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSession = session;
            CallCount++;
            return Task.FromResult(parse(session));
        }
    }
}
