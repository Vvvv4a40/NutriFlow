using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NutriFlow.Api.Contracts;
using NutriFlow.Infrastructure.LabelPhotos;

namespace NutriFlow.Api.Tests;

public sealed class MealWorkflowApiTests
{
    private static readonly string[] DemoMessages =
    {
        "Добавил 200 г демо-продукта A.",
        "Потом добавил 100 г демо-продукта B.",
        "Готовое блюдо весит 250 г.",
        "Съел 125 г, потом ещё две порции по 62,5 г."
    };

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Root_ReturnsWebDashboard()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("NutriFlow", html, StringComparison.Ordinal);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            "default-src 'self'",
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthEndpoints_ReportLiveAndMigratedDatabase()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage live = await client.GetAsync("/health/live");
        HttpResponseMessage ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HealthEndpoints_WhenSchemaIsNotMigrated_ReportNotReady()
    {
        await using TestApiFactory factory = new TestApiFactory(
            applyMigrations: false,
            seedDemoData: false);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage live = await client.GetAsync("/health/live");
        HttpResponseMessage ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Swagger_InDevelopment_IsNotBlockedByDashboardPolicy()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public async Task ExternalEndpoint_WhenRequestLimitIsExceeded_ReturnsTooManyRequests()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        List<HttpStatusCode> statuses = new();

        for (int attempt = 0; attempt < 13; attempt++)
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/meal-drafts/parse",
                new ParseMealDraftRequest(DemoMessages));
            statuses.Add(response.StatusCode);
        }

        Assert.All(
            statuses.Take(12),
            status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);

        using HttpResponseMessage rejected = await client.PostAsJsonAsync(
            "/api/meal-drafts/parse",
            new ParseMealDraftRequest(DemoMessages));
        using JsonDocument problem = JsonDocument.Parse(
            await rejected.Content.ReadAsStringAsync());

        Assert.Equal(
            "application/problem+json",
            rejected.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "Too many requests",
            problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task FullWorkflow_CalculatesConfirmsAndUpdatesDailyProgress()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        DateOnly date = new DateOnly(2026, 9, 6);

        HttpResponseMessage goalResponse = await client.PutAsJsonAsync(
            $"/api/daily-goals/{date:yyyy-MM-dd}",
            new SetDailyGoalRequest(2000m, 100m, 70m, 250m));
        MealSessionResponse session = await CreateDemoSessionAsync(
            client,
            date);

        Assert.Equal(HttpStatusCode.OK, goalResponse.StatusCode);
        Assert.Equal("ReadyForConfirmation", session.Status);
        Assert.True(session.CanConfirm);
        Assert.Equal(64, session.PreviewToken.Length);
        Assert.Empty(session.Issues);
        DishPreviewResponse dish = Assert.Single(session.Dishes);
        AssertNutrition(dish.TotalNutrition, 400m, 25m, 20m, 30m);
        AssertNutrition(dish.NutritionPer100Grams, 160m, 10m, 8m, 12m);
        Assert.Collection(
            dish.Portions,
            portion => AssertNutrition(
                portion.Nutrition,
                200m,
                12.5m,
                10m,
                15m),
            portion => AssertNutrition(
                portion.Nutrition,
                100m,
                6.25m,
                5m,
                7.5m),
            portion => AssertNutrition(
                portion.Nutrition,
                100m,
                6.25m,
                5m,
                7.5m));

        HttpResponseMessage confirmationResponse = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{session.Id}/confirm",
            new ConfirmMealSessionRequest(session.PreviewToken));
        ConfirmMealSessionResponse confirmation = await ReadAsync<
            ConfirmMealSessionResponse>(confirmationResponse);

        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);
        Assert.Equal("Confirmed", confirmation.Outcome);
        Assert.Equal("Confirmed", confirmation.Session.Status);
        Assert.False(confirmation.Session.CanConfirm);
        Assert.Equal(3, confirmation.Entries.Count);

        DailyProgressResponse progress = await client.GetFromJsonAsync<
            DailyProgressResponse>(
            $"/api/daily-progress/{date:yyyy-MM-dd}",
            JsonOptions) ?? throw new InvalidDataException();

        AssertNutrition(progress.Consumed, 400m, 25m, 20m, 30m);
        AssertNutrition(progress.Remaining, 1600m, 75m, 50m, 220m);
        AssertNutrition(progress.Exceeded, 0m, 0m, 0m, 0m);
        Assert.Equal(3, progress.Entries.Count);

        HttpResponseMessage repeatedResponse = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{session.Id}/confirm",
            new ConfirmMealSessionRequest(session.PreviewToken));
        ConfirmMealSessionResponse repeated = await ReadAsync<
            ConfirmMealSessionResponse>(repeatedResponse);
        DailyProgressResponse progressAfterRetry = await client.GetFromJsonAsync<
            DailyProgressResponse>(
            $"/api/daily-progress/{date:yyyy-MM-dd}",
            JsonOptions) ?? throw new InvalidDataException();

        Assert.Equal(HttpStatusCode.OK, repeatedResponse.StatusCode);
        Assert.Equal("AlreadyConfirmed", repeated.Outcome);
        Assert.Equal(3, repeated.Entries.Count);
        Assert.Equal(3, progressAfterRetry.Entries.Count);
    }

    [Fact]
    public async Task Confirm_WhenCatalogChanges_RejectsStalePreview()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        MealSessionResponse session = await CreateDemoSessionAsync(
            client,
            new DateOnly(2026, 9, 6));
        CreateManualProductRequest betterProduct = new CreateManualProductRequest(
            "Демо-продукт A",
            150m,
            12m,
            5m,
            8m,
            IsEstimated: false);

        HttpResponseMessage productResponse = await client.PostAsJsonAsync(
            "/api/products/manual",
            betterProduct);
        HttpResponseMessage confirmationResponse = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{session.Id}/confirm",
            new ConfirmMealSessionRequest(session.PreviewToken));
        ConfirmMealSessionResponse confirmation = await ReadAsync<
            ConfirmMealSessionResponse>(confirmationResponse);

        Assert.Equal(HttpStatusCode.Created, productResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, confirmationResponse.StatusCode);
        Assert.Equal("StalePreview", confirmation.Outcome);
        Assert.NotEqual(session.PreviewToken, confirmation.Session.PreviewToken);
        Assert.Equal("ReadyForConfirmation", confirmation.Session.Status);
    }

    [Fact]
    public async Task Confirm_WhenDraftNeedsClarification_ReturnsConflict()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        string[] messages =
        {
            "Добавил 500 г макарон.",
            "Готовое блюдо весит 900 г.",
            "Съел 300 г."
        };
        HttpResponseMessage createResponse = await client.PostAsJsonAsync(
            "/api/meal-sessions",
            new CreateMealSessionRequest(messages, new DateOnly(2026, 9, 6)));
        MealSessionResponse session = await ReadAsync<MealSessionResponse>(
            createResponse);

        HttpResponseMessage confirmationResponse = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{session.Id}/confirm",
            new ConfirmMealSessionRequest(session.PreviewToken));
        ConfirmMealSessionResponse confirmation = await ReadAsync<
            ConfirmMealSessionResponse>(confirmationResponse);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal("NeedsClarification", session.Status);
        Assert.False(session.CanConfirm);
        Assert.Single(session.ClarificationQuestions);
        Assert.Equal(HttpStatusCode.Conflict, confirmationResponse.StatusCode);
        Assert.Equal("NotReady", confirmation.Outcome);
        Assert.Empty(confirmation.Entries);
    }

    [Fact]
    public async Task Confirm_WhenRequestsRace_CreatesOnlyOneSetOfEntries()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        DateOnly date = new DateOnly(2026, 9, 6);
        MealSessionResponse session = await CreateDemoSessionAsync(client, date);

        Task<HttpResponseMessage>[] requests = Enumerable.Range(0, 2)
            .Select(_ => client.PostAsJsonAsync(
                $"/api/meal-sessions/{session.Id}/confirm",
                new ConfirmMealSessionRequest(session.PreviewToken)))
            .ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(requests);
        ConfirmMealSessionResponse[] confirmations = await Task.WhenAll(
            responses.Select(ReadAsync<ConfirmMealSessionResponse>));
        DailyProgressResponse progress = await client.GetFromJsonAsync<
            DailyProgressResponse>(
            $"/api/daily-progress/{date:yyyy-MM-dd}",
            JsonOptions) ?? throw new InvalidDataException();

        Assert.All(responses, response => Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode));
        Assert.Contains(
            confirmations,
            confirmation => confirmation.Outcome == "Confirmed");
        Assert.Contains(
            confirmations,
            confirmation => confirmation.Outcome == "AlreadyConfirmed");
        Assert.Equal(3, progress.Entries.Count);
        AssertNutrition(progress.Consumed, 400m, 25m, 20m, 30m);
    }

    [Fact]
    public async Task ClarificationAndCatalogUpdate_MakeSessionConfirmable()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        DateOnly date = new DateOnly(2026, 9, 6);
        string[] initialMessages =
        {
            "Добавил 500 г макарон.",
            "Готовое блюдо весит 900 г.",
            "Съел 300 г."
        };
        HttpResponseMessage createResponse = await client.PostAsJsonAsync(
            "/api/meal-sessions",
            new CreateMealSessionRequest(initialMessages, date));
        MealSessionResponse initial = await ReadAsync<MealSessionResponse>(
            createResponse);

        HttpResponseMessage clarificationResponse = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{initial.Id}/messages",
            new AddMealSessionMessageRequest("В сухом виде."));
        MealSessionResponse clarified = await ReadAsync<MealSessionResponse>(
            clarificationResponse);

        Assert.Equal(HttpStatusCode.OK, clarificationResponse.StatusCode);
        Assert.Equal("NeedsProducts", clarified.Status);
        Assert.Empty(clarified.ClarificationQuestions);
        Assert.Contains(
            clarified.Issues,
            issue => issue.Code == "product_not_found" &&
                     issue.IngredientName == "Макароны сухие");

        HttpResponseMessage productResponse = await client.PostAsJsonAsync(
            "/api/products/manual",
            new CreateManualProductRequest(
                "Макароны сухие",
                350m,
                12m,
                1.5m,
                70m,
                IsEstimated: false));
        MealSessionResponse refreshed = await client.GetFromJsonAsync<
            MealSessionResponse>(
            $"/api/meal-sessions/{initial.Id}",
            JsonOptions) ?? throw new InvalidDataException();

        Assert.Equal(HttpStatusCode.Created, productResponse.StatusCode);
        Assert.Equal("ReadyForConfirmation", refreshed.Status);
        Assert.True(refreshed.CanConfirm);
        Assert.Empty(refreshed.Issues);
        Assert.NotEqual(clarified.PreviewToken, refreshed.PreviewToken);
        AssertNutrition(
            Assert.Single(refreshed.Dishes).TotalNutrition,
            1750m,
            60m,
            7.5m,
            350m);
    }

    [Fact]
    public async Task CreateProductFromLabel_PreservesPhotoProvenance()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        string photoReference;

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            LabelPhotoStore store =
                scope.ServiceProvider.GetRequiredService<LabelPhotoStore>();
            ValidatedLabelPhoto photo = LabelPhotoValidator.Validate(
                new byte[]
                {
                    0x89, 0x50, 0x4E, 0x47,
                    0x0D, 0x0A, 0x1A, 0x0A
                },
                "image/png");
            photoReference = await store.SaveAsync(photo);
        }

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/products/from-label",
            new CreateLabelProductRequest(
                photoReference,
                "Творог с этикетки",
                121m,
                17m,
                5m,
                3m));
        ProductResponse product = await ReadAsync<ProductResponse>(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("LabelPhoto", product.SourceKind);
        Assert.Equal("Verified", product.DataQuality);
        Assert.Equal(photoReference, product.SourceReference);
    }

    private static async Task<MealSessionResponse> CreateDemoSessionAsync(
        HttpClient client,
        DateOnly date)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/meal-sessions",
            new CreateMealSessionRequest(DemoMessages, date));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<MealSessionResponse>(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();

        return JsonSerializer.Deserialize<T>(json, JsonOptions) ??
               throw new InvalidDataException(
                   $"The response could not be read as {typeof(T).Name}: {json}");
    }

    private static void AssertNutrition(
        NutritionResponse? actual,
        decimal calories,
        decimal protein,
        decimal fat,
        decimal carbohydrates)
    {
        Assert.NotNull(actual);
        Assert.Equal(calories, actual.Calories);
        Assert.Equal(protein, actual.ProteinGrams);
        Assert.Equal(fat, actual.FatGrams);
        Assert.Equal(carbohydrates, actual.CarbohydratesGrams);
    }

    private sealed class TestApiFactory : WebApplicationFactory<Program>
    {
        private readonly bool _applyMigrations;
        private readonly string _directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"nutriflow-api-{Guid.NewGuid():N}");
        private readonly bool _seedDemoData;

        public TestApiFactory(
            bool applyMigrations = true,
            bool seedDemoData = true)
        {
            _applyMigrations = applyMigrations;
            _seedDemoData = seedDemoData;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_directoryPath);
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.UseSetting(
                "Database:Path",
                Path.Combine(_directoryPath, "nutriflow.db"));
            builder.UseSetting(
                "Database:ApplyMigrationsOnStartup",
                _applyMigrations.ToString());
            builder.UseSetting(
                "Storage:LabelPhotosPath",
                Path.Combine(_directoryPath, "label-photos"));
            builder.UseSetting("Ai:Provider", "Fake");
            builder.UseSetting("Demo:SeedData", _seedDemoData.ToString());
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(_directoryPath))
            {
                Directory.Delete(_directoryPath, recursive: true);
            }
        }
    }
}
