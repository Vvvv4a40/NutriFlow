using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Api.Tests;

public sealed class ManualMealApiTests
{
    private static readonly DateOnly MealDate = new(2026, 11, 9);

    [Fact]
    public async Task ManualMeal_UsesSelectedProductNutritionAndEntersDiaryOnlyAfterConfirmation()
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        int productId = await AddProductAsync(factory, client, "Каша", 160.125m);

        MealSessionResponse session = await CreateManualMealAsync(factory, client, productId, 150m);

        Assert.Equal("ReadyForConfirmation", session.Status);
        Assert.Equal("Diary", session.Purpose);
        Assert.True(session.CanConfirm);
        Assert.Empty(session.Issues);
        DishPreviewResponse dish = Assert.Single(session.Dishes);
        AssertNutrition(dish.TotalNutrition!, 240.1875m, 15.375m, 6.1875m, 30.75m);
        AssertNutrition(Assert.Single(dish.Portions).Nutrition!, 240.1875m, 15.375m, 6.1875m, 30.75m);
        Assert.Equal(productId, Assert.Single(dish.Ingredients).ResolvedProduct!.Id);
        Assert.Empty((await GetDayAsync(factory, client)).Entries);

        ConfirmMealSessionResponse confirmed = await ConfirmAsync(factory, client, session);

        Assert.Equal("Confirmed", confirmed.Outcome);
        MealEntryResponse entry = Assert.Single(confirmed.Entries);
        Assert.Equal("Каша", entry.Name);
        Assert.Equal(150m, entry.WeightInGrams);
        AssertNutrition(entry.Nutrition, 240.1875m, 15.375m, 6.1875m, 30.75m);
        DailyProgressResponse day = await GetDayAsync(factory, client);
        Assert.Equal(entry, Assert.Single(day.Entries));
        AssertNutrition(day.Consumed, 240.1875m, 15.375m, 6.1875m, 30.75m);
    }

    [Fact]
    public async Task ManualMeal_SelectsEachNamesakeByIdWithoutAmbiguity()
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        int firstId = await AddProductAsync(factory, client, "Яблоко", 50.125m);
        int secondId = await AddProductAsync(factory, client, "Яблоко", 60.875m);
        Assert.NotEqual(firstId, secondId);

        foreach ((int id, decimal calories) in new[] { (firstId, 50.125m), (secondId, 60.875m) })
        {
            MealSessionResponse session = await CreateManualMealAsync(factory, client, id, 150m);

            Assert.Equal("ReadyForConfirmation", session.Status);
            Assert.True(session.CanConfirm);
            Assert.DoesNotContain(session.Issues, issue => issue.Code == "product_ambiguous");
            DishPreviewResponse dish = Assert.Single(session.Dishes);
            ProductResponse selected = Assert.IsType<ProductResponse>(Assert.Single(dish.Ingredients).ResolvedProduct);
            Assert.Equal(id, selected.Id);
            Assert.Equal(calories, selected.Calories);
            Assert.Equal(calories * 1.5m, dish.TotalNutrition!.Calories);
        }
    }

    [Fact]
    public async Task ManualMeal_IdempotencyReusesSessionAndAliasAndRejectsDifferentWeight()
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        int productId = await AddProductAsync(factory, client, "Молоко");
        Guid key = Guid.NewGuid();
        int aliasesBefore = await AliasCountAsync(factory);

        MealSessionResponse first = await CreateManualMealAsync(factory, client, productId, 150m, key);
        Assert.Equal(aliasesBefore + 1, await AliasCountAsync(factory));
        MealSessionResponse repeated = await CreateManualMealAsync(
            factory, client, productId, 150m, key, HttpStatusCode.OK);

        Assert.Equal(first.Id, repeated.Id);
        Assert.Equal(first.PreviewToken, repeated.PreviewToken);
        Assert.Equal(aliasesBefore + 1, await AliasCountAsync(factory));

        using HttpResponseMessage conflict = await PostManualMealAsync(client, productId, 200m, key);

        await AssertProblemAsync(factory, conflict, HttpStatusCode.Conflict, "idempotency_conflict");
        Assert.Equal(aliasesBefore + 1, await AliasCountAsync(factory));
        MealSessionResponse unchanged = await GetSessionAsync(factory, client, first.Id);
        Assert.Equal(first.Messages, unchanged.Messages);
        Assert.Equal(150m, Assert.Single(unchanged.Dishes).FinalWeightInGrams);
    }

    [Fact]
    public async Task ManualMeal_SameKeyForANamesakeIsAConflictNotTheFirstSession()
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        int first = await AddProductAsync(factory, client, "Яблоко", 50m);
        int namesake = await AddProductAsync(factory, client, "Яблоко", 90m);
        Guid key = Guid.NewGuid();
        MealSessionResponse session = await CreateManualMealAsync(factory, client, first, 100m, key);

        using HttpResponseMessage conflict = await PostManualMealAsync(client, namesake, 100m, key);

        await AssertProblemAsync(factory, conflict, HttpStatusCode.Conflict, "idempotency_conflict");
        Assert.Equal(50m, Assert.Single(session.Dishes).TotalNutrition!.Calories);
    }

    [Fact]
    public async Task ManualMeal_UnknownProductReturnsProductNotFound()
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostManualMealAsync(client, int.MaxValue, 150m);

        await AssertProblemAsync(factory, response, HttpStatusCode.NotFound, "product_not_found");
    }

    [Theory]
    [InlineData("productId")]
    [InlineData("weightInGrams")]
    [InlineData("mealDate")]
    [InlineData("verifiedQuality")]
    [InlineData("zeroWeight")]
    public async Task ManualMeal_InvalidInputReturnsValidationFailed(string invalidField)
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        int productId = await AddProductAsync(factory, client, "Молоко");
        Dictionary<string, object> body = new()
        {
            ["productId"] = productId,
            ["weightInGrams"] = 150m,
            ["mealDate"] = MealDate
        };
        if (invalidField == "verifiedQuality")
        {
            body["weightQuality"] = "Verified";
        }
        else if (invalidField == "zeroWeight")
        {
            body["weightInGrams"] = 0m;
        }
        else
        {
            body.Remove(invalidField);
        }

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/meal-sessions/manual-meal", body);

        await AssertProblemAsync(factory, response, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task ManualDish_ConfirmsCompositionAndCalculatesSavedDishPortion()
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        int firstId = await AddProductAsync(factory, client, "Морковь", 100m);
        int secondId = await AddProductAsync(factory, client, "Картофель", 200m);
        using HttpResponseMessage creation = await client.PostAsJsonAsync("/api/meal-sessions/manual-dish", new
        {
            name = "Рагу",
            ingredients = new[]
            {
                new { productId = firstId, weightInGrams = 300m, weightQuality = "Estimated", removedWeightInGrams = 100m, removedWeightQuality = "Exact" },
                new { productId = secondId, weightInGrams = 100m, weightQuality = "Exact", removedWeightInGrams = 0m, removedWeightQuality = "Exact" }
            },
            finalWeightInGrams = 400m,
            finalWeightQuality = "Exact",
            mealDate = MealDate
        });
        await ApiTestAssertions.AssertStatusAsync(factory, creation, HttpStatusCode.Created);
        MealSessionResponse session = await ReadAsync<MealSessionResponse>(creation);

        Assert.Equal($"/api/meal-sessions/{session.Id}", creation.Headers.Location?.OriginalString);
        Assert.Equal("CreateDish", session.Purpose);
        Assert.Equal("ReadyForConfirmation", session.Status);
        DishPreviewResponse dish = Assert.Single(session.Dishes);
        Assert.Empty(dish.Portions);
        Assert.Equal(new int?[] { firstId, secondId }, dish.Ingredients.Select(item => item.ResolvedProduct!.Id));
        Assert.Equal(300m, dish.Ingredients[0].WeightInGrams);
        Assert.Equal("Estimated", dish.Ingredients[0].WeightQuality);
        Assert.Equal(100m, dish.Ingredients[0].RemovedWeightInGrams);
        Assert.Equal("Exact", dish.Ingredients[0].RemovedWeightQuality);
        Assert.Equal(200m, dish.Ingredients[0].IncludedWeightInGrams);
        Assert.Equal(400m, dish.FinalWeightInGrams);
        Assert.Equal("Exact", dish.FinalWeightQuality);
        AssertNutrition(dish.TotalNutrition!, 400m, 30.75m, 12.375m, 61.5m);

        ConfirmMealSessionResponse confirmed = await ConfirmAsync(factory, client, session);

        Assert.Equal("Confirmed", confirmed.Outcome);
        Assert.Empty(confirmed.Entries);
        SavedDishResponse saved = Assert.IsType<SavedDishResponse>(confirmed.SavedDish);
        Assert.Equal(session.Id, saved.SourceSessionId);
        Assert.Equal("Рагу", saved.Name);
        Assert.Equal("Estimated", saved.NutritionQuality);
        AssertNutrition(saved.NutritionPer100Grams, 100m, 7.6875m, 3.09375m, 15.375m);
        Assert.Empty((await GetDayAsync(factory, client)).Entries);

        MealSessionResponse portion = await CreateSavedDishMealAsync(factory, client, saved.Id, 250m);

        Assert.Equal("ReadyForConfirmation", portion.Status);
        Assert.Equal("Diary", portion.Purpose);
        DishPreviewResponse portionDish = Assert.Single(portion.Dishes);
        ProductResponse resolved = Assert.IsType<ProductResponse>(Assert.Single(portionDish.Ingredients).ResolvedProduct);
        Assert.Equal("SavedDish", resolved.SourceKind);
        Assert.Equal($"saved-dish:{saved.Id:N}", resolved.SourceReference);
        AssertNutrition(Assert.Single(portionDish.Portions).Nutrition!, 250m, 19.21875m, 7.734375m, 38.4375m);
    }

    [Fact]
    public async Task SavedDishMeal_UnknownDishReturnsSavedDishNotFound()
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/meal-sessions/saved-dish-meal",
            new { savedDishId = Guid.NewGuid(), weightInGrams = 250m, mealDate = MealDate });

        await AssertProblemAsync(factory, response, HttpStatusCode.NotFound, "saved_dish_not_found");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task ManualDish_InvalidIngredientCountReturnsValidationFailed(int count)
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        int productId = await AddProductAsync(factory, client, "Морковь");

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/meal-sessions/manual-dish", new
        {
            name = "Рагу",
            ingredients = Enumerable.Range(0, count).Select(_ => new { productId, weightInGrams = 100m }).ToArray(),
            finalWeightInGrams = 5100m,
            mealDate = MealDate
        });

        await AssertProblemAsync(factory, response, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task ManualDish_UnknownIngredientReturnsProductNotFound()
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostManualDishAsync(client, int.MaxValue);

        await AssertProblemAsync(factory, response, HttpStatusCode.NotFound, "product_not_found");
    }

    [Fact]
    public async Task ManualMeal_NewNamesakeRejectsClarificationWithoutChangingMessages()
    {
        StubParser parser = new("Яблоко");
        await using TestApiFactory factory = new(seedDemoData: false, parser: parser);
        using HttpClient client = factory.CreateClient();
        int productId = await AddProductAsync(factory, client, "Яблоко", 50m);
        MealSessionResponse before = await CreateManualMealAsync(factory, client, productId);
        await AddProductAsync(factory, client, "Яблоко", 80m);

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/meal-sessions/{before.Id}/messages",
            new { message = "Съел 150 г." });

        await AssertProblemAsync(factory, response, HttpStatusCode.Conflict, "product_selection_ambiguous");
        MealSessionResponse after = await GetSessionAsync(factory, client, before.Id);
        Assert.Equal(before.Messages, after.Messages);
        Assert.Equal(before.PreviewToken, after.PreviewToken);
        Assert.Equal(50m, Assert.Single(after.Dishes).TotalNutrition!.Calories);
        Assert.Equal(0, parser.CallCount);
    }

    [Fact]
    public async Task ManualMeal_UniqueNameAcceptsClarification()
    {
        StubParser parser = new("Яблоко");
        await using TestApiFactory factory = new(seedDemoData: false, parser: parser);
        using HttpClient client = factory.CreateClient();
        int productId = await AddProductAsync(factory, client, "Яблоко", 50m);
        MealSessionResponse before = await CreateManualMealAsync(factory, client, productId);
        const string message = "Съел 150 г.";

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/meal-sessions/{before.Id}/messages",
            new { message });

        await ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.OK);
        MealSessionResponse after = await ReadAsync<MealSessionResponse>(response);
        Assert.Equal(before.Messages.Append(message), after.Messages);
        Assert.Equal("ReadyForConfirmation", after.Status);
        Assert.Equal(75m, Assert.Single(after.Dishes).TotalNutrition!.Calories);
        Assert.Equal(1, parser.CallCount);
        Assert.Equal(after.Messages, (await GetSessionAsync(factory, client, before.Id)).Messages);
    }

    [Fact]
    public async Task SavedDishMeal_NewCatalogNamesakeRejectsClarificationWithoutChangingMessages()
    {
        StubParser parser = new("Рагу");
        await using TestApiFactory factory = new(seedDemoData: false, parser: parser);
        using HttpClient client = factory.CreateClient();
        int productId = await AddProductAsync(factory, client, "Морковь");
        using HttpResponseMessage creation = await PostManualDishAsync(client, productId);
        await ApiTestAssertions.AssertStatusAsync(factory, creation, HttpStatusCode.Created);
        ConfirmMealSessionResponse confirmed = await ConfirmAsync(factory, client, await ReadAsync<MealSessionResponse>(creation));
        SavedDishResponse saved = Assert.IsType<SavedDishResponse>(confirmed.SavedDish);
        MealSessionResponse before = await CreateSavedDishMealAsync(factory, client, saved.Id, 100m);
        await AddProductAsync(factory, client, "Рагу", 800m);

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/meal-sessions/{before.Id}/messages",
            new { message = "Съел 150 г." });

        await AssertProblemAsync(factory, response, HttpStatusCode.Conflict, "product_selection_ambiguous");
        MealSessionResponse after = await GetSessionAsync(factory, client, before.Id);
        Assert.Equal(before.Messages, after.Messages);
        Assert.Equal(before.PreviewToken, after.PreviewToken);
        Assert.Equal(0, parser.CallCount);
    }

    private static async Task<int> AddProductAsync(TestApiFactory factory, HttpClient client, string name, decimal calories = 100m)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/products/manual",
            new CreateManualProductRequest(name, calories, 10.25m, 4.125m, 20.5m, false));
        await ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.Created);
        return Assert.IsType<int>((await ReadAsync<ProductResponse>(response)).Id);
    }

    private static async Task<MealSessionResponse> CreateManualMealAsync(
        TestApiFactory factory, HttpClient client, int productId, decimal grams = 100m, Guid? key = null,
        HttpStatusCode expectedStatus = HttpStatusCode.Created)
    {
        using HttpResponseMessage response = await PostManualMealAsync(client, productId, grams, key);
        await ApiTestAssertions.AssertStatusAsync(factory, response, expectedStatus);
        MealSessionResponse session = await ReadAsync<MealSessionResponse>(response);
        Assert.Equal($"/api/meal-sessions/{session.Id}", response.Headers.Location?.OriginalString);
        return session;
    }

    private static async Task<HttpResponseMessage> PostManualMealAsync(HttpClient client, int productId, decimal grams, Guid? key = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/meal-sessions/manual-meal")
        {
            Content = JsonContent.Create(new { productId, weightInGrams = grams, mealDate = MealDate })
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key.Value.ToString("D"));
        }

        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostManualDishAsync(HttpClient client, int productId) =>
        client.PostAsJsonAsync("/api/meal-sessions/manual-dish", new
        {
            name = "Рагу",
            ingredients = new[] { new { productId, weightInGrams = 100m } },
            finalWeightInGrams = 200m,
            mealDate = MealDate
        });

    private static async Task<MealSessionResponse> CreateSavedDishMealAsync(
        TestApiFactory factory, HttpClient client, Guid dishId, decimal grams)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/meal-sessions/saved-dish-meal",
            new { savedDishId = dishId, weightInGrams = grams, mealDate = MealDate });
        await ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.Created);
        MealSessionResponse session = await ReadAsync<MealSessionResponse>(response);
        Assert.Equal($"/api/meal-sessions/{session.Id}", response.Headers.Location?.OriginalString);
        return session;
    }

    private static async Task<ConfirmMealSessionResponse> ConfirmAsync(TestApiFactory factory, HttpClient client, MealSessionResponse session)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/meal-sessions/{session.Id}/confirm",
            new ConfirmMealSessionRequest(session.PreviewToken));
        await ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.OK);
        return await ReadAsync<ConfirmMealSessionResponse>(response);
    }

    private static async Task<MealSessionResponse> GetSessionAsync(TestApiFactory factory, HttpClient client, Guid id)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/meal-sessions/{id}");
        await ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.OK);
        return await ReadAsync<MealSessionResponse>(response);
    }

    private static async Task<DailyProgressResponse> GetDayAsync(TestApiFactory factory, HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/daily-progress/{MealDate:yyyy-MM-dd}");
        await ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.OK);
        return await ReadAsync<DailyProgressResponse>(response);
    }

    private static async Task<int> AliasCountAsync(TestApiFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        NutriFlowDbContext database = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
        return await database.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM \"ProductAliases\"").SingleAsync();
    }

    private static async Task AssertProblemAsync(TestApiFactory factory, HttpResponseMessage response, HttpStatusCode status, string code)
    {
        await ApiTestAssertions.AssertStatusAsync(factory, response, status);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)status, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) where T : class =>
        await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidDataException();

    private static void AssertNutrition(NutritionResponse nutrition, decimal calories, decimal protein, decimal fat, decimal carbohydrates)
    {
        Assert.Equal(calories, nutrition.Calories);
        Assert.Equal(protein, nutrition.ProteinGrams);
        Assert.Equal(fat, nutrition.FatGrams);
        Assert.Equal(carbohydrates, nutrition.CarbohydratesGrams);
    }

    private sealed class StubParser(string name) : IMealParser
    {
        public int CallCount { get; private set; }

        public Task<MealDraft> ParseAsync(CaptureSession session, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new MealDraft(
                [new DishDraft(name, [new IngredientDraft(name, 150m)], 150m, DataQuality.Exact, [new PortionDraft(150m)])], []));
        }
    }
}
