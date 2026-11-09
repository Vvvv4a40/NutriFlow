using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Ai;

namespace NutriFlow.Api.Tests;

public sealed class MealDraftEditApiTests
{
    private const string UnknownFood = "Киноа варёная";
    private static readonly DateOnly MealDate = new(2026, 11, 9);

    [Fact]
    public async Task SetWeight_SingleIngredientUpdatesAllWeightsQualityMessagesAndNutrition()
    {
        StubParser parser = new(new MealDraft(
            [new DishDraft("Каша", [new IngredientDraft("Каша", 100m, DataQuality.Estimated)],
                100m, DataQuality.Estimated, [new PortionDraft(100m, DataQuality.Estimated)])], []));
        await using TestApiFactory factory = new(seedDemoData: false, parser: parser);
        using HttpClient client = factory.CreateClient();
        await AddProductAsync(factory, client, "Каша");
        MealSessionResponse before = await CreateSessionAsync(factory, client);

        using HttpResponseMessage response = await SetWeightAsync(client, before, 0, new { weightInGrams = 150m });

        await ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.OK);
        MealSessionResponse after = await ReadAsync<MealSessionResponse>(response);
        Assert.Equal("ReadyForConfirmation", after.Status);
        Assert.True(after.CanConfirm);
        DishPreviewResponse dish = Assert.Single(after.Dishes);
        IngredientPreviewResponse ingredient = Assert.Single(dish.Ingredients);
        PortionPreviewResponse portion = Assert.Single(dish.Portions);
        Assert.Equal(150m, ingredient.WeightInGrams);
        Assert.Equal(150m, ingredient.IncludedWeightInGrams);
        Assert.Equal(150m, dish.FinalWeightInGrams);
        Assert.Equal(150m, portion.WeightInGrams);
        Assert.Equal("Exact", ingredient.WeightQuality);
        Assert.Equal("Exact", dish.FinalWeightQuality);
        Assert.Equal("Exact", portion.WeightQuality);
        Assert.Equal("Exact", portion.NutritionQuality);
        AssertNutrition(ingredient.Nutrition!, 150m, 15m, 6m, 9m);
        AssertNutrition(dish.TotalNutrition!, 150m, 15m, 6m, 9m);
        AssertNutrition(portion.Nutrition!, 150m, 15m, 6m, 9m);
        Assert.Equal(before.Messages.Append("Исправлено вручную: «Каша» — 150 г."), after.Messages);
        Assert.Equal(1, parser.CallCount);
        MealSessionResponse stored = await GetSessionAsync(factory, client, before.Id);
        Assert.Equal(after.PreviewToken, stored.PreviewToken);
        Assert.Equal(after.Messages, stored.Messages);
        Assert.Equal(150m, Assert.Single(stored.Dishes).FinalWeightInGrams);
    }

    [Fact]
    public async Task SetWeight_MultiIngredientPortionAboveFinalWeightReturnsValidationDetail()
    {
        MealDraft draft = new(
            [new DishDraft("Каша с молоком", [new IngredientDraft("Каша", 100m), new IngredientDraft("Молоко", 100m)],
                200m, DataQuality.Exact, [new PortionDraft(100m)])], []);
        await using TestApiFactory factory = new(seedDemoData: false, parser: new StubParser(draft));
        using HttpClient client = factory.CreateClient();
        await AddProductAsync(factory, client, "Каша");
        await AddProductAsync(factory, client, "Молоко");
        MealSessionResponse before = await CreateSessionAsync(factory, client);
        Assert.Equal("ReadyForConfirmation", before.Status);

        using HttpResponseMessage response = await SetWeightAsync(client, before, 0, new { weightInGrams = 201m });

        JsonElement problem = await AssertProblemAsync(factory, response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.True(problem.TryGetProperty("detail", out JsonElement detail),
            $"Expected a validation detail explaining the portion limit. Response: {problem}");
        Assert.Contains("больше всего блюда", detail.GetString());
        MealSessionResponse after = await GetSessionAsync(factory, client, before.Id);
        Assert.Equal(before.Messages, after.Messages);
        Assert.Equal(before.PreviewToken, after.PreviewToken);
    }

    [Theory]
    [InlineData(-1, "weight")]
    [InlineData(1, "weight")]
    [InlineData(-1, "remove")]
    [InlineData(1, "remove")]
    public async Task Edit_UnknownDishReturnsDishNotFound(int index, string operation)
    {
        await using TestApiFactory factory = new(seedDemoData: false, parser: new StubParser(CreateDraft()));
        using HttpClient client = factory.CreateClient();
        MealSessionResponse session = await CreateSessionAsync(factory, client);

        using HttpResponseMessage response = operation == "weight"
            ? await SetWeightAsync(client, session, index, new { weightInGrams = 150m })
            : await RemoveAsync(client, session, index);

        await AssertProblemAsync(factory, response, HttpStatusCode.NotFound, "dish_not_found");
        Assert.Equal(session.Messages, (await GetSessionAsync(factory, client, session.Id)).Messages);
    }

    [Theory]
    [InlineData("weight")]
    [InlineData("remove")]
    [InlineData("estimate")]
    public async Task Edit_UnknownSessionReturnsMealSessionNotFound(string operation)
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        string path = $"/api/meal-sessions/{Guid.NewGuid()}";

        using HttpResponseMessage response = operation switch
        {
            "weight" => await SendAsync(client, HttpMethod.Put, $"{path}/dishes/0/weight", "\"unknown\"", new { weightInGrams = 150m }),
            "remove" => await SendAsync(client, HttpMethod.Delete, $"{path}/dishes/0", "\"unknown\""),
            _ => await client.PostAsync($"{path}/estimate-missing", null)
        };

        await AssertProblemAsync(factory, response, HttpStatusCode.NotFound, "meal_session_not_found");
    }

    [Fact]
    public async Task SetWeight_MissingWeightReturnsValidationFailed()
    {
        await using TestApiFactory factory = new(seedDemoData: false, parser: new StubParser(CreateDraft()));
        using HttpClient client = factory.CreateClient();
        MealSessionResponse before = await CreateSessionAsync(factory, client);

        using HttpResponseMessage response = await SetWeightAsync(client, before, 0, new { });

        await AssertProblemAsync(factory, response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(before.Messages, (await GetSessionAsync(factory, client, before.Id)).Messages);
    }

    [Fact]
    public async Task RemoveDish_RemovesOneOfTwoAndRejectsRemovingLastDish()
    {
        StubParser parser = new(CreateDraft(2));
        await using TestApiFactory factory = new(seedDemoData: false, parser: parser);
        using HttpClient client = factory.CreateClient();
        await AddProductAsync(factory, client, "Каша");
        await AddProductAsync(factory, client, "Молоко");
        MealSessionResponse before = await CreateSessionAsync(factory, client);

        using HttpResponseMessage removal = await RemoveAsync(client, before, 0);

        await ApiTestAssertions.AssertStatusAsync(factory, removal, HttpStatusCode.OK);
        MealSessionResponse after = await ReadAsync<MealSessionResponse>(removal);
        Assert.Equal("Молоко", Assert.Single(after.Dishes).Name);
        Assert.Equal("ReadyForConfirmation", after.Status);
        Assert.Equal(before.Messages.Count + 1, after.Messages.Count);
        Assert.Contains("не ели, убрать", after.Messages[^1]);
        Assert.Contains("Каша", after.Messages[^1]);
        Assert.Equal(1, parser.CallCount);

        using HttpResponseMessage last = await RemoveAsync(client, after, 0);

        await AssertProblemAsync(factory, last, HttpStatusCode.Conflict, "last_dish");
        MealSessionResponse stored = await GetSessionAsync(factory, client, before.Id);
        Assert.Equal(after.Messages, stored.Messages);
        Assert.Equal(after.PreviewToken, stored.PreviewToken);
        Assert.Equal("Молоко", Assert.Single(stored.Dishes).Name);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ConfirmedSession_RejectsWeightChangeAndRemoval(int dishCount)
    {
        await using TestApiFactory factory = new(seedDemoData: false, parser: new StubParser(CreateDraft(dishCount)));
        using HttpClient client = factory.CreateClient();
        await AddProductAsync(factory, client, "Каша");
        await AddProductAsync(factory, client, "Молоко");
        MealSessionResponse session = await CreateSessionAsync(factory, client);
        using HttpResponseMessage confirmation = await client.PostAsJsonAsync($"/api/meal-sessions/{session.Id}/confirm",
            new ConfirmMealSessionRequest(session.PreviewToken));
        await ApiTestAssertions.AssertStatusAsync(factory, confirmation, HttpStatusCode.OK);
        ConfirmMealSessionResponse confirmed = await ReadAsync<ConfirmMealSessionResponse>(confirmation);
        Assert.Equal("Confirmed", confirmed.Outcome);

        using HttpResponseMessage weight = await SetWeightAsync(client, confirmed.Session, 0, new { weightInGrams = 150m });
        await AssertProblemAsync(factory, weight, HttpStatusCode.Conflict, "meal_session_confirmed");

        using HttpResponseMessage removal = await RemoveAsync(client, confirmed.Session, 0);
        await AssertProblemAsync(factory, removal, HttpStatusCode.Conflict, "meal_session_confirmed");
        MealSessionResponse after = await GetSessionAsync(factory, client, session.Id);
        Assert.Equal("Confirmed", after.Status);
        Assert.Equal(dishCount, after.Dishes.Count);
        Assert.Equal(confirmed.Session.Messages, after.Messages);
    }

    [Fact]
    public async Task EstimateMissing_FakeProviderReturnsAiNotConfigured()
    {
        await using TestApiFactory factory = new(seedDemoData: false, parser: new StubParser(CreateUnknownDraft()));
        using HttpClient client = factory.CreateClient();
        MealSessionResponse session = await CreateSessionAsync(factory, client);
        Assert.Equal("NeedsProducts", session.Status);

        using HttpResponseMessage response = await client.PostAsync($"/api/meal-sessions/{session.Id}/estimate-missing", null);

        await AssertProblemAsync(factory, response, HttpStatusCode.ServiceUnavailable, "ai_not_configured");
    }

    [Fact]
    public async Task EstimateMissing_SavesEstimatedProductAndDoesNotEstimateItAgain()
    {
        StubEstimator estimator = new();
        await using TestApiFactory source = new(seedDemoData: false, parser: new StubParser(CreateUnknownDraft()));
        await using WebApplicationFactory<Program> factory = WithEstimator(source, estimator);
        using HttpClient client = factory.CreateClient();
        MealSessionResponse before = await CreateSessionAsync(factory, client);
        Assert.Equal("NeedsProducts", before.Status);
        Assert.False(before.CanConfirm);

        using HttpResponseMessage response = await client.PostAsync($"/api/meal-sessions/{before.Id}/estimate-missing", null);

        await ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.OK);
        MealSessionResponse after = await ReadAsync<MealSessionResponse>(response);
        Assert.Equal("ReadyForConfirmation", after.Status);
        Assert.True(after.CanConfirm);
        Assert.Empty(after.Issues);
        Assert.Equal(before.Messages, after.Messages);
        Assert.Equal(1, estimator.CallCount);
        Assert.Equal(new[] { UnknownFood }, estimator.RequestedFoods);
        PortionPreviewResponse portion = Assert.Single(Assert.Single(after.Dishes).Portions);
        AssertNutrition(portion.Nutrition!, 180m, 6m, 3m, 30m);
        Assert.Equal("Estimated", portion.NutritionQuality);

        using HttpResponseMessage search = await client.GetAsync($"/api/products?q={Uri.EscapeDataString("Киноа")}");
        await ApiTestAssertions.AssertStatusAsync(factory, search, HttpStatusCode.OK);
        ProductResponse product = Assert.Single(await ReadAsync<ProductResponse[]>(search));
        Assert.Equal(UnknownFood, product.Name);
        Assert.Equal("Оценка AI", product.SourceName);
        Assert.Equal("ai-estimate", product.SourceReference);
        Assert.Equal("ExternalService", product.SourceKind);
        Assert.Equal("Estimated", product.DataQuality);
        Assert.True(Assert.IsType<int>(product.Id) > 0);
        Assert.Equal(product.Id, Assert.Single(Assert.Single(after.Dishes).Ingredients).ResolvedProduct!.Id);
        Assert.Equal(120m, product.Calories);
        Assert.Equal(4m, product.ProteinGrams);
        Assert.Equal(2m, product.FatGrams);
        Assert.Equal(20m, product.CarbohydratesGrams);

        using HttpResponseMessage repeated = await client.PostAsync($"/api/meal-sessions/{before.Id}/estimate-missing", null);

        await ApiTestAssertions.AssertStatusAsync(factory, repeated, HttpStatusCode.OK);
        MealSessionResponse reused = await ReadAsync<MealSessionResponse>(repeated);
        Assert.Equal("ReadyForConfirmation", reused.Status);
        Assert.Equal(after.Id, reused.Id);
        Assert.Equal(after.PreviewToken, reused.PreviewToken);
        Assert.Equal(1, estimator.CallCount);
    }

    [Theory]
    [InlineData("unavailable", HttpStatusCode.BadGateway, "ai_unavailable")]
    [InlineData("rateLimit", HttpStatusCode.ServiceUnavailable, "ai_rate_limited")]
    public async Task EstimateMissing_MapsEstimatorFailureToProblemAndRetryAfter(string failure, HttpStatusCode status, string code)
    {
        Exception exception = failure == "rateLimit"
            ? new GroqRateLimitException(TimeSpan.FromSeconds(9.5), false)
            : new HttpRequestException("Upstream unavailable");
        StubEstimator estimator = new(exception);
        await using TestApiFactory source = new(seedDemoData: false, parser: new StubParser(CreateUnknownDraft()));
        await using WebApplicationFactory<Program> factory = WithEstimator(source, estimator);
        using HttpClient client = factory.CreateClient();
        MealSessionResponse before = await CreateSessionAsync(factory, client);
        Assert.Equal("NeedsProducts", before.Status);

        using HttpResponseMessage response = await client.PostAsync($"/api/meal-sessions/{before.Id}/estimate-missing", null);

        await AssertProblemAsync(factory, response, status, code);
        if (failure == "rateLimit")
        {
            Assert.Equal(TimeSpan.FromSeconds(10), response.Headers.RetryAfter?.Delta);
        }
        else
        {
            Assert.Null(response.Headers.RetryAfter);
        }

        Assert.Equal(1, estimator.CallCount);
        MealSessionResponse after = await GetSessionAsync(factory, client, before.Id);
        Assert.Equal("NeedsProducts", after.Status);
        Assert.Equal(before.Messages, after.Messages);
        Assert.Equal(before.PreviewToken, after.PreviewToken);
        using HttpResponseMessage search = await client.GetAsync($"/api/products?q={Uri.EscapeDataString("Киноа")}");
        await ApiTestAssertions.AssertStatusAsync(factory, search, HttpStatusCode.OK);
        Assert.Empty(await ReadAsync<ProductResponse[]>(search));
    }

    [Fact]
    public async Task Edit_WithAnOldPreviewTokenChangesNothing()
    {
        StubParser parser = new(CreateDraft(2));
        await using TestApiFactory factory = new(seedDemoData: false, parser: parser);
        using HttpClient client = factory.CreateClient();
        await AddProductAsync(factory, client, "Каша");
        await AddProductAsync(factory, client, "Молоко");
        MealSessionResponse before = await CreateSessionAsync(factory, client);
        using HttpResponseMessage first = await RemoveAsync(client, before, 0);
        await ApiTestAssertions.AssertStatusAsync(factory, first, HttpStatusCode.OK);
        MealSessionResponse after = await ReadAsync<MealSessionResponse>(first);

        // the same request again would remove "Молоко", which is now line 0
        using HttpResponseMessage repeated = await RemoveAsync(client, before, 0);
        using HttpResponseMessage weight = await SetWeightAsync(client, before, 0, new { weightInGrams = 50m });

        await AssertProblemAsync(factory, repeated, HttpStatusCode.PreconditionFailed, "meal_session_changed");
        await AssertProblemAsync(factory, weight, HttpStatusCode.PreconditionFailed, "meal_session_changed");
        MealSessionResponse stored = await GetSessionAsync(factory, client, before.Id);
        Assert.Equal(after.PreviewToken, stored.PreviewToken);
        Assert.Equal("Молоко", Assert.Single(stored.Dishes).Name);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.PreconditionRequired, "preview_token_required")]
    [InlineData("unquoted", HttpStatusCode.BadRequest, "validation_failed")]
    [InlineData("\"a\", \"b\"", HttpStatusCode.BadRequest, "validation_failed")]
    public async Task Edit_WithoutOneQuotedPreviewTokenChangesNothing(string? ifMatch, HttpStatusCode status, string code)
    {
        await using TestApiFactory factory = new(seedDemoData: false, parser: new StubParser(CreateDraft()));
        using HttpClient client = factory.CreateClient();
        MealSessionResponse before = await CreateSessionAsync(factory, client);
        string path = $"/api/meal-sessions/{before.Id}/dishes/0";

        using HttpResponseMessage weight = await SendAsync(client, HttpMethod.Put, $"{path}/weight", ifMatch, new { weightInGrams = 150m });
        using HttpResponseMessage removal = await SendAsync(client, HttpMethod.Delete, path, ifMatch);

        await AssertProblemAsync(factory, weight, status, code);
        await AssertProblemAsync(factory, removal, status, code);
        Assert.Equal(before.PreviewToken, (await GetSessionAsync(factory, client, before.Id)).PreviewToken);
    }

    private static Task<HttpResponseMessage> SetWeightAsync(HttpClient client, MealSessionResponse session, int index, object body) =>
        SendAsync(client, HttpMethod.Put, $"/api/meal-sessions/{session.Id}/dishes/{index}/weight", $"\"{session.PreviewToken}\"", body);

    private static Task<HttpResponseMessage> RemoveAsync(HttpClient client, MealSessionResponse session, int index) =>
        SendAsync(client, HttpMethod.Delete, $"/api/meal-sessions/{session.Id}/dishes/{index}", $"\"{session.PreviewToken}\"");

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? ifMatch, object? body = null)
    {
        using HttpRequestMessage request = new(method, path);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static WebApplicationFactory<Program> WithEstimator(TestApiFactory factory, INutritionEstimator estimator) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<INutritionEstimator>();
            services.AddSingleton(estimator);
        }));

    private static MealDraft CreateDraft(int count = 1) => new(
        new[] { "Каша", "Молоко" }.Take(count).Select(name => new DishDraft(name,
            [new IngredientDraft(name, 100m)], 100m, DataQuality.Exact, [new PortionDraft(100m)])).ToArray(), []);

    private static MealDraft CreateUnknownDraft() => new(
        [new DishDraft(UnknownFood, [new IngredientDraft(UnknownFood, 150m)],
            150m, DataQuality.Exact, [new PortionDraft(150m)])], []);

    private static async Task AddProductAsync(WebApplicationFactory<Program> factory, HttpClient client, string name)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/products/manual",
            new CreateManualProductRequest(name, 100m, 10m, 4m, 6m, false));
        await ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.Created);
    }

    private static async Task<MealSessionResponse> CreateSessionAsync(WebApplicationFactory<Program> factory, HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/meal-sessions",
            new CreateMealSessionRequest(["Рассчитать приём пищи."], MealDate));
        await ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.Created);
        return await ReadAsync<MealSessionResponse>(response);
    }

    private static async Task<MealSessionResponse> GetSessionAsync(WebApplicationFactory<Program> factory, HttpClient client, Guid id)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/meal-sessions/{id}");
        await ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.OK);
        return await ReadAsync<MealSessionResponse>(response);
    }

    private static async Task<JsonElement> AssertProblemAsync(
        WebApplicationFactory<Program> factory, HttpResponseMessage response, HttpStatusCode status, string code)
    {
        await ApiTestAssertions.AssertStatusAsync(factory, response, status);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)status, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        return problem.RootElement.Clone();
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

    private sealed class StubParser(MealDraft draft) : IMealParser
    {
        public int CallCount { get; private set; }

        public Task<MealDraft> ParseAsync(CaptureSession session, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(draft);
        }
    }

    private sealed class StubEstimator(Exception? failure = null) : INutritionEstimator
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<string> RequestedFoods { get; private set; } = [];

        public Task<IReadOnlyList<NutritionEstimate>> EstimateAsync(IReadOnlyList<string> foods, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            RequestedFoods = foods.ToArray();
            return failure is null
                ? Task.FromResult<IReadOnlyList<NutritionEstimate>>([new NutritionEstimate(UnknownFood, new NutritionValues(120m, 4m, 2m, 20m))])
                : Task.FromException<IReadOnlyList<NutritionEstimate>>(failure);
        }
    }
}
