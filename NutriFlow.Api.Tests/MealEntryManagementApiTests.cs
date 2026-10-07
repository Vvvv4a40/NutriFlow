using System.Net;
using System.Net.Http.Json;
using System.Text;
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

public sealed class MealEntryManagementApiTests
{
    private static readonly DateOnly MealDate = new(2026, 10, 5);
    private static readonly string[] DemoMessages =
    [
        "Добавил 200 г демо-продукта A.",
        "Потом добавил 100 г демо-продукта B.",
        "Готовое блюдо весит 250 г.",
        "Съел 125 г, потом ещё две порции по 62,5 г."
    ];

    [Fact]
    public async Task GetEntry_ReturnsStableIdentityRevisionAndStrongEtagWithExistingFields()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        ConfirmedMeal meal = await ConfirmDemoAsync(client);

        Assert.Equal(3, meal.Entries.Select(entry => entry.Id).Distinct().Count());
        Assert.All(meal.Entries, entry =>
        {
            Assert.True(entry.Id > 0);
            Assert.Equal(0, entry.Revision);
        });
        MealEntryResponse entry = meal.Entries[0];
        using HttpResponseMessage response = await client.GetAsync($"/api/meal-entries/{entry.Id}");
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"0\"", response.Headers.ETag!.Tag);
        Assert.False(response.Headers.ETag!.IsWeak);
        Assert.Equal(entry, JsonSerializer.Deserialize<MealEntryResponse>(body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.All(new[] { "name", "weightInGrams", "nutrition", "quality", "id", "revision" },
            field => Assert.True(document.RootElement.TryGetProperty(field, out _)));
        Assert.Equal(meal.Entries, (await GetDayAsync(client)).Entries);
        using JsonDocument openApi = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        JsonElement operations = openApi.RootElement.GetProperty("paths").GetProperty("/api/meal-entries/{id}");
        foreach (string method in new[] { "put", "delete" })
        {
            JsonElement operation = operations.GetProperty(method);
            JsonElement header = Assert.Single(operation.GetProperty("parameters").EnumerateArray(),
                parameter => parameter.GetProperty("name").GetString() == "If-Match");
            Assert.Equal("header", header.GetProperty("in").GetString());
            Assert.True(operation.GetProperty("responses").TryGetProperty("428", out _));
            Assert.True(operation.GetProperty("responses").TryGetProperty("412", out _));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateWeight_RecalculatesDailyTotalsAndOptionalGoalWithoutParsing(bool setGoal)
    {
        CountingFakeParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        ConfirmedMeal meal = await ConfirmDemoAsync(client);
        MealEntryResponse original = meal.Entries[0];
        string immutableBefore = await ReadImmutableSnapshotAsync(factory);
        if (setGoal)
        {
            using HttpResponseMessage goal = await client.PutAsJsonAsync(
                $"/api/daily-goals/{MealDate:yyyy-MM-dd}", new SetDailyGoalRequest(500m, 30m, 30m, 50m));
            Assert.Equal(HttpStatusCode.OK, goal.StatusCode);
        }

        using HttpResponseMessage updated = await PutAsync(client, original.Id, "\"0\"", 100m);
        MealEntryResponse entry = await ReadAsync<MealEntryResponse>(updated);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("\"1\"", updated.Headers.ETag!.Tag);
        Assert.Equal(original.Id, entry.Id);
        Assert.Equal(1, entry.Revision);
        Assert.Equal(100m, entry.WeightInGrams);
        AssertNutrition(entry.Nutrition, 160m, 10m, 8m, 12m);
        Assert.Equal("Unknown", entry.Quality);
        DailyProgressResponse day = await GetDayAsync(client);
        AssertNutrition(day.Consumed, 360m, 22.5m, 18m, 27m);
        Assert.Equal(entry, Assert.Single(day.Entries, item => item.Id == entry.Id));
        if (setGoal)
        {
            AssertNutrition(day.Remaining!, 140m, 7.5m, 12m, 23m);
            AssertNutrition(day.Exceeded!, 0m, 0m, 0m, 0m);
        }
        else
        {
            Assert.Null(day.Goal);
            Assert.Null(day.Remaining);
            Assert.Null(day.Exceeded);
        }

        ConfirmMealSessionResponse repeated = await ConfirmAgainAsync(client, meal.Session);
        Assert.Equal("AlreadyConfirmed", repeated.Outcome);
        Assert.Equal(entry, Assert.Single(repeated.Entries, item => item.Id == entry.Id));
        Assert.Equal(immutableBefore, await ReadImmutableSnapshotAsync(factory));
        Assert.Equal(1, parser.CallCount);
    }

    [Fact]
    public async Task DeleteEntry_HidesItAndRepeatedConfirmationDoesNotRestoreIt()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        ConfirmedMeal meal = await ConfirmDemoAsync(client);
        MealEntryResponse removed = meal.Entries[0];
        string immutableBefore = await ReadImmutableSnapshotAsync(factory);

        using HttpResponseMessage deletion = await DeleteAsync(client, removed.Id, "\"0\"");
        Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
        string adjustmentAfter = await ReadAdjustmentsSnapshotAsync(factory);
        using HttpResponseMessage repeated = await DeleteAsync(client, removed.Id, "\"0\"");
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        Assert.Equal(adjustmentAfter, await ReadAdjustmentsSnapshotAsync(factory));
        using HttpResponseMessage get = await client.GetAsync($"/api/meal-entries/{removed.Id}");
        using HttpResponseMessage update = await PutAsync(client, removed.Id, "\"0\"", 100m);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        DailyProgressResponse day = await GetDayAsync(client);
        Assert.Equal(2, day.Entries.Count);
        Assert.DoesNotContain(day.Entries, entry => entry.Id == removed.Id);
        AssertNutrition(day.Consumed, 200m, 12.5m, 10m, 15m);
        ConfirmMealSessionResponse reconfirmed = await ConfirmAgainAsync(client, meal.Session);
        Assert.Equal("AlreadyConfirmed", reconfirmed.Outcome);
        Assert.Equal(day.Entries, reconfirmed.Entries);
        Assert.Equal(immutableBefore, await ReadImmutableSnapshotAsync(factory));

        foreach (MealEntryResponse entry in day.Entries)
        {
            using HttpResponseMessage result = await DeleteAsync(client, entry.Id, "\"0\"");
            Assert.Equal(HttpStatusCode.NoContent, result.StatusCode);
        }

        Assert.Empty((await ConfirmAgainAsync(client, meal.Session)).Entries);
        AssertNutrition((await GetDayAsync(client)).Consumed, 0m, 0m, 0m, 0m);
        Assert.Equal(immutableBefore, await ReadImmutableSnapshotAsync(factory));
    }

    [Fact]
    public async Task Restart_PreservesAdjustmentAndDeletionAndFrozenPreview()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"nutriflow-entry-{Guid.NewGuid():N}.db");
        ConfirmedMeal meal;
        MealEntryResponse updated;
        string frozenPreview;

        try
        {
            await using (TestApiFactory first = new(databasePath: databasePath))
            {
                using HttpClient client = first.CreateClient();
                meal = await ConfirmDemoAsync(client);
                frozenPreview = await client.GetStringAsync($"/api/meal-sessions/{meal.Session.Id}");
                using HttpResponseMessage edit = await PutAsync(client, meal.Entries[0].Id, "\"0\"", 100m);
                updated = await ReadAsync<MealEntryResponse>(edit);
                using HttpResponseMessage deletion = await DeleteAsync(client, meal.Entries[1].Id, "\"0\"");
                Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
            }

            await using (TestApiFactory second = new(databasePath: databasePath))
            {
                using HttpClient client = second.CreateClient();
                Assert.Equal(updated, await GetEntryAsync(client, updated.Id));
                using HttpResponseMessage deleted = await client.GetAsync($"/api/meal-entries/{meal.Entries[1].Id}");
                Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
                Assert.Equal(frozenPreview, await client.GetStringAsync($"/api/meal-sessions/{meal.Session.Id}"));
                ConfirmMealSessionResponse repeated = await ConfirmAgainAsync(client, meal.Session);
                Assert.Equal("AlreadyConfirmed", repeated.Outcome);
                Assert.Equal(2, repeated.Entries.Count);
                Assert.Equal(updated, Assert.Single(repeated.Entries, entry => entry.Id == updated.Id));
                AssertNutrition((await GetDayAsync(client)).Consumed, 260m, 16.25m, 13m, 19.5m);
            }
        }
        finally
        {
            TestDatabasePool.Clear(databasePath);
            File.Delete(databasePath);
            File.Delete($"{databasePath}-wal");
            File.Delete($"{databasePath}-shm");
        }
    }

    [Fact]
    public async Task UpdateWeight_AfterCatalogChanges_UsesOriginalConfirmedSnapshot()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        ConfirmedMeal meal = await ConfirmDemoAsync(client);
        string immutableBefore = await ReadImmutableSnapshotAsync(factory);
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            NutriFlowDbContext database = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
            await database.Database.ExecuteSqlRawAsync("""
                UPDATE Products SET Calories = '999', ProteinGrams = '99', SourceName = 'Changed source'
                WHERE Name = 'Демо-продукт A'
                """);
        }

        using HttpResponseMessage first = await PutAsync(client, meal.Entries[0].Id, "\"0\"", 100m);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        AssertNutrition((await ReadAsync<MealEntryResponse>(first)).Nutrition, 160m, 10m, 8m, 12m);
        using HttpResponseMessage second = await PutAsync(client, meal.Entries[0].Id, "\"1\"", 125m);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        AssertNutrition((await ReadAsync<MealEntryResponse>(second)).Nutrition, 200m, 12.5m, 10m, 15m);
        Assert.Equal(immutableBefore, await ReadImmutableSnapshotAsync(factory));
    }

    [Theory]
    [InlineData(DataQuality.Exact, "Estimated", "Estimated")]
    [InlineData(DataQuality.Estimated, "Exact", "Estimated")]
    public async Task UpdateWeight_DoesNotPromoteUncertainNutrition(
        DataQuality originalPortionQuality,
        string newWeightQuality,
        string expectedQuality)
    {
        await using TestApiFactory factory = new(seedDemoData: false,
            parser: new ExactProductParser(originalPortionQuality));
        using HttpClient client = factory.CreateClient();
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            LocalProductCatalog catalog = scope.ServiceProvider.GetRequiredService<LocalProductCatalog>();
            await catalog.AddAsync(new Product("Точный продукт", new NutritionValues(100m, 10m, 4m, 6m),
                new NutritionSource(NutritionSourceKind.ManualInput, DataQuality.Exact, "Manual input")));
        }

        ConfirmedMeal meal = await ConfirmDemoAsync(client);
        using HttpResponseMessage response = await PutAsync(
            client, Assert.Single(meal.Entries).Id, "\"0\"", 40m, newWeightQuality);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        MealEntryResponse updated = await ReadAsync<MealEntryResponse>(response);
        Assert.Equal(expectedQuality, updated.Quality);
        AssertNutrition(updated.Nutrition, 40m, 4m, 1.6m, 2.4m);
    }

    [Theory]
    [InlineData(null, 428)]
    [InlineData("0", 400)]
    [InlineData("W/\"0\"", 400)]
    [InlineData("*", 400)]
    [InlineData("\"00\"", 400)]
    [InlineData("\"-1\"", 400)]
    [InlineData("\"0\"\n\"1\"", 400)]
    [InlineData("\"2147483648\"", 400)]
    public async Task Mutation_WithInvalidIfMatch_RejectsWithoutChangingData(string? header, int status)
    {
        await using TestApiFactory factory = new(environment: "Production");
        using HttpClient client = factory.CreateClient();
        ConfirmedMeal meal = await ConfirmDemoAsync(client);
        string before = await ReadAdjustmentsSnapshotAsync(factory);

        using HttpResponseMessage update = await PutAsync(client, meal.Entries[0].Id, header, 100m);
        using HttpResponseMessage deletion = await DeleteAsync(client, meal.Entries[0].Id, header);

        AssertProblem(update, status);
        AssertProblem(deletion, status);
        Assert.Equal(before, await ReadAdjustmentsSnapshotAsync(factory));
        Assert.Equal(meal.Entries, (await GetDayAsync(client)).Entries);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"weightInGrams\":0}")]
    [InlineData("{\"weightInGrams\":-1}")]
    [InlineData("{\"weightInGrams\":100,\"weightQuality\":\"Verified\"}")]
    public async Task Update_WithInvalidBody_RejectsWithoutChangingData(string json)
    {
        await using TestApiFactory factory = new(environment: "Production");
        using HttpClient client = factory.CreateClient();
        ConfirmedMeal meal = await ConfirmDemoAsync(client);
        string before = await ReadAdjustmentsSnapshotAsync(factory);

        using HttpResponseMessage response = await SendMutationAsync(
            client, HttpMethod.Put, meal.Entries[0].Id, "\"0\"", json);

        AssertProblem(response, 400);
        Assert.Equal(before, await ReadAdjustmentsSnapshotAsync(factory));
        Assert.Equal(meal.Entries[0], await GetEntryAsync(client, meal.Entries[0].Id));
    }

    [Fact]
    public async Task StaleRevision_RejectsUpdateAndDeleteWithoutLosingCurrentWeight()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        ConfirmedMeal meal = await ConfirmDemoAsync(client);
        int id = meal.Entries[0].Id;
        using HttpResponseMessage successful = await PutAsync(client, id, "\"0\"", 100m);
        MealEntryResponse current = await ReadAsync<MealEntryResponse>(successful);
        string before = await ReadAdjustmentsSnapshotAsync(factory);

        using HttpResponseMessage update = await PutAsync(client, id, "\"0\"", 90m);
        using HttpResponseMessage deletion = await DeleteAsync(client, id, "\"0\"");

        AssertProblem(update, 412);
        AssertProblem(deletion, 412);
        Assert.Equal(current, await GetEntryAsync(client, id));
        Assert.Equal(before, await ReadAdjustmentsSnapshotAsync(factory));
    }

    [Fact]
    public async Task ForeignAndUnknownEntries_AreNotReadableEditableOrDeletable()
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient localClient = factory.CreateClient();
        Guid foreignOwner = Guid.NewGuid();
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            NutriFlowDbContext database = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Users (Id, CreatedAtUtc, IsLegacyLocal, LegacyLabelPhotosImported)
                VALUES ({foreignOwner}, {DateTimeOffset.UtcNow}, 0, 0)
                """);
            await AddDemoProductsAsync(scope.ServiceProvider.GetRequiredService<LocalProductCatalog>());
            await AddDemoProductsAsync(new LocalProductCatalog(database, foreignOwner));
        }

        ConfirmedMeal local = await ConfirmDemoAsync(localClient);
        await using var foreignFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<LocalProductCatalog>();
                services.AddScoped(provider => new LocalProductCatalog(
                    provider.GetRequiredService<NutriFlowDbContext>(), foreignOwner));
                services.RemoveAll<MealSessionStore>();
                services.AddScoped(provider => new MealSessionStore(
                    provider.GetRequiredService<NutriFlowDbContext>(), foreignOwner));
                services.RemoveAll<DailyDiaryStore>();
                services.AddScoped(provider => new DailyDiaryStore(
                    provider.GetRequiredService<NutriFlowDbContext>(), foreignOwner));
                services.RemoveAll<SavedDishStore>();
                services.AddScoped(provider => new SavedDishStore(
                    provider.GetRequiredService<NutriFlowDbContext>(), foreignOwner));
            }));
        using HttpClient foreignClient = foreignFactory.CreateClient();
        ConfirmedMeal foreign = await ConfirmDemoAsync(foreignClient);
        string before = await ReadAdjustmentsSnapshotAsync(factory);

        foreach ((HttpClient client, int id) in new[]
        {
            (localClient, foreign.Entries[0].Id),
            (foreignClient, local.Entries[0].Id),
            (localClient, int.MaxValue)
        })
        {
            using HttpResponseMessage get = await client.GetAsync($"/api/meal-entries/{id}");
            using HttpResponseMessage update = await PutAsync(client, id, "\"0\"", 100m);
            using HttpResponseMessage deletion = await DeleteAsync(client, id, "\"0\"");
            AssertProblem(get, 404);
            AssertProblem(update, 404);
            AssertProblem(deletion, 404);
        }

        Assert.Equal(before, await ReadAdjustmentsSnapshotAsync(factory));
        Assert.Equal(local.Entries, (await GetDayAsync(localClient)).Entries);
        Assert.Equal(foreign.Entries, (await GetDayAsync(foreignClient)).Entries);
    }

    [Fact]
    public async Task ConcurrentUpdates_WithSameRevision_DoNotOverwriteEachOther()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        ConfirmedMeal meal = await ConfirmDemoAsync(client);
        int id = meal.Entries[0].Id;
        HttpResponseMessage[] responses = await Task.WhenAll(
            PutAsync(client, id, "\"0\"", 100m), PutAsync(client, id, "\"0\"", 90m))
            .WaitAsync(TimeSpan.FromSeconds(20));

        try
        {
            HttpResponseMessage success = Assert.Single(responses, result => result.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, result => (int)result.StatusCode == 412);
            MealEntryResponse saved = await GetEntryAsync(client, id);
            Assert.Equal(await ReadAsync<MealEntryResponse>(success), saved);
            Assert.Equal(1, saved.Revision);
            Assert.Contains(saved.WeightInGrams, new[] { 100m, 90m });
            Assert.Equal(saved.WeightInGrams * 1.6m, saved.Nutrition.Calories);
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
    public async Task ConcurrentUpdateAndDelete_WithSameRevision_HaveOneConsistentOutcome()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        ConfirmedMeal meal = await ConfirmDemoAsync(client);
        int id = meal.Entries[0].Id;
        Task<HttpResponseMessage> putTask = PutAsync(client, id, "\"0\"", 100m);
        Task<HttpResponseMessage> deleteTask = DeleteAsync(client, id, "\"0\"");
        await Task.WhenAll(putTask, deleteTask).WaitAsync(TimeSpan.FromSeconds(20));
        using HttpResponseMessage update = await putTask;
        using HttpResponseMessage deletion = await deleteTask;

        if (update.StatusCode == HttpStatusCode.OK)
        {
            Assert.Equal(412, (int)deletion.StatusCode);
            Assert.Equal(100m, (await GetEntryAsync(client, id)).WeightInGrams);
            Assert.Equal(3, (await GetDayAsync(client)).Entries.Count);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
            using HttpResponseMessage hidden = await client.GetAsync($"/api/meal-entries/{id}");
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            Assert.Equal(2, (await GetDayAsync(client)).Entries.Count);
        }
    }

    [Fact]
    public async Task ConcurrentDeletes_AreIdempotentWithoutChangingOriginalRows()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        ConfirmedMeal meal = await ConfirmDemoAsync(client);
        string before = await ReadImmutableSnapshotAsync(factory);
        HttpResponseMessage[] responses = await Task.WhenAll(
            DeleteAsync(client, meal.Entries[0].Id, "\"0\""),
            DeleteAsync(client, meal.Entries[0].Id, "\"0\""))
            .WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
            Assert.Equal(2, (await GetDayAsync(client)).Entries.Count);
            Assert.Equal(before, await ReadImmutableSnapshotAsync(factory));
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    private static async Task<ConfirmedMeal> ConfirmDemoAsync(HttpClient client)
    {
        using HttpResponseMessage creation = await client.PostAsJsonAsync("/api/meal-sessions",
            new { messages = DemoMessages, mealDate = MealDate });
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        MealSessionResponse session = await ReadAsync<MealSessionResponse>(creation);
        Assert.Equal("Diary", session.Purpose);
        ConfirmMealSessionResponse confirmed = await ConfirmAgainAsync(client, session);
        Assert.Equal("Confirmed", confirmed.Outcome);
        return new ConfirmedMeal(confirmed.Session, confirmed.Entries);
    }

    private static async Task<ConfirmMealSessionResponse> ConfirmAgainAsync(
        HttpClient client,
        MealSessionResponse session)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{session.Id}/confirm", new ConfirmMealSessionRequest(session.PreviewToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<ConfirmMealSessionResponse>(response);
    }

    private static async Task<MealEntryResponse> GetEntryAsync(HttpClient client, int id)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/meal-entries/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        MealEntryResponse entry = await ReadAsync<MealEntryResponse>(response);
        Assert.Equal($"\"{entry.Revision}\"", response.Headers.ETag!.Tag);
        return entry;
    }

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client,
        int id,
        string? ifMatch,
        decimal weight,
        string quality = "Exact")
    {
        return SendMutationAsync(client, HttpMethod.Put, id, ifMatch,
            JsonSerializer.Serialize(new { weightInGrams = weight, weightQuality = quality }));
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, int id, string? ifMatch)
    {
        return SendMutationAsync(client, HttpMethod.Delete, id, ifMatch);
    }

    private static async Task<HttpResponseMessage> SendMutationAsync(
        HttpClient client,
        HttpMethod method,
        int id,
        string? ifMatch,
        string? json = null)
    {
        using HttpRequestMessage request = new(method, $"/api/meal-entries/{id}");
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch.Split('\n'));
        }
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    private static async Task<DailyProgressResponse> GetDayAsync(HttpClient client)
    {
        return await client.GetFromJsonAsync<DailyProgressResponse>($"/api/daily-progress/{MealDate:yyyy-MM-dd}")
            ?? throw new InvalidDataException();
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) where T : class
    {
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidDataException();
    }

    private static void AssertProblem(HttpResponseMessage response, int status)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    private static void AssertNutrition(NutritionResponse nutrition,
        decimal calories, decimal protein, decimal fat, decimal carbohydrates)
    {
        Assert.Equal(calories, nutrition.Calories);
        Assert.Equal(protein, nutrition.ProteinGrams);
        Assert.Equal(fat, nutrition.FatGrams);
        Assert.Equal(carbohydrates, nutrition.CarbohydratesGrams);
    }

    private static async Task<string> ReadImmutableSnapshotAsync(TestApiFactory factory)
    {
        string entries = await ReadRowsAsync(factory, "SELECT * FROM MealEntries ORDER BY Id");
        string sessions = await ReadRowsAsync(factory, "SELECT Id, PreviewJson FROM MealSessions ORDER BY Id");
        return entries + sessions;
    }

    private static Task<string> ReadAdjustmentsSnapshotAsync(TestApiFactory factory)
    {
        return ReadRowsAsync(factory, "SELECT * FROM MealEntryAdjustments ORDER BY 1");
    }

    private static async Task<string> ReadRowsAsync(TestApiFactory factory, string sql)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        NutriFlowDbContext database = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
        await using SqliteConnection connection = new(database.Database.GetConnectionString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        List<object[]> rows = [];
        while (await reader.ReadAsync())
        {
            object[] values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
        }

        return JsonSerializer.Serialize(rows);
    }

    private static async Task AddDemoProductsAsync(LocalProductCatalog catalog)
    {
        NutritionSource source = new(NutritionSourceKind.ManualInput, DataQuality.Exact, "Manual input");
        await catalog.AddAsync(new Product("Демо-продукт A", new NutritionValues(100m, 10m, 4m, 6m), source));
        await catalog.AddAsync(new Product("Демо-продукт B", new NutritionValues(200m, 5m, 12m, 18m), source));
    }

    private sealed record ConfirmedMeal(MealSessionResponse Session, IReadOnlyList<MealEntryResponse> Entries);

    private sealed class CountingFakeParser : IMealParser
    {
        public int CallCount { get; private set; }

        public Task<MealDraft> ParseAsync(CaptureSession session, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return new FakeMealParser().ParseAsync(session, cancellationToken);
        }
    }

    private sealed class ExactProductParser(DataQuality portionQuality) : IMealParser
    {
        public Task<MealDraft> ParseAsync(CaptureSession session, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new MealDraft(
                [new DishDraft("Блюдо", [new IngredientDraft("Точный продукт", 100m)],
                    100m, DataQuality.Exact, [new PortionDraft(50m, portionQuality)])], []));
        }
    }
}
