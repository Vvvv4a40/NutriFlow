using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Api.Tests;

public sealed class MealPreviewValidationTests
{
    private static readonly string[] DemoMessages =
    [
        "Добавил 200 г демо-продукта A.",
        "Потом добавил 100 г демо-продукта B.",
        "Готовое блюдо весит 250 г.",
        "Съел 125 г, потом ещё две порции по 62,5 г."
    ];

    [Theory]
    [InlineData("Dishes/0", "null", "get")]
    [InlineData("Dishes", "empty-array", "get")]
    [InlineData("Dishes/0/Ingredients/0", "null", "get")]
    [InlineData("Dishes/0/Portions/0", "null", "get")]
    [InlineData("Issues/0", "null", "get")]
    [InlineData("ClarificationQuestions/0", "null", "get")]
    [InlineData("Dishes/0/Ingredients", "remove", "get")]
    [InlineData("Dishes/0/Portions", "remove", "get")]
    [InlineData("Issues", "remove", "get")]
    [InlineData("Dishes/0/Name", "blank", "get")]
    [InlineData("Dishes/0/Ingredients/0/ProductName", "blank", "get")]
    [InlineData("Dishes/0/Ingredients/0/ResolvedProduct/SourceName", "blank", "get")]
    [InlineData("Issues/0/Message", "blank", "get")]
    [InlineData("Dishes/0/TotalNutrition/Calories", "negative", "get")]
    [InlineData("Dishes/0/NutritionPer100Grams/ProteinGrams", "negative", "get")]
    [InlineData("Dishes/0/Ingredients/0/Nutrition/FatGrams", "negative", "get")]
    [InlineData("Dishes/0/Portions/0/Nutrition/CarbohydratesGrams", "negative", "reconfirm")]
    [InlineData("Dishes/0/Ingredients/0/ResolvedProduct/Calories", "negative", "reconfirm")]
    [InlineData("Dishes/0/Portions/0/Nutrition/Calories", "remove", "get")]
    [InlineData("Dishes/0/FinalWeightQuality", "invalid-quality", "get")]
    [InlineData("Dishes/0/Ingredients/0/WeightQuality", "numeric-quality", "get")]
    [InlineData("Dishes/0/Ingredients/0/ResolvedProduct/SourceKind", "invalid-quality", "get")]
    [InlineData("Dishes/0/Ingredients/0/WeightInGrams", "negative", "reconfirm")]
    [InlineData("Dishes/0/Portions/0/FractionOfDish", "negative", "stale")]
    [InlineData("Dishes/0/FinalWeightInGrams", "zero", "stale")]
    public async Task MalformedStoredPreview_ReturnsRedactedInternalErrorWithoutWriting(
        string path,
        string mutation,
        string operation)
    {
        await using TestApiFactory factory = new(environment: "Production");
        using HttpClient client = factory.CreateClient();
        MealSessionResponse session = await CreateSessionAsync(client, DemoMessages);
        if (operation != "stale")
        {
            await ConfirmSessionAsync(client, session);
        }

        JsonObject preview = await ReadPreviewAsync(factory, session.Id);
        ApplyMutation(preview, path, mutation);
        await SavePreviewAsync(factory, session.Id, preview);
        DatabaseSnapshot before = await ReadSnapshotAsync(factory);

        using HttpResponseMessage response = operation switch
        {
            "reconfirm" => await client.PostAsJsonAsync(
                $"/api/meal-sessions/{session.Id}/confirm",
                new ConfirmMealSessionRequest(session.PreviewToken)),
            "stale" => await client.PostAsJsonAsync(
                $"/api/meal-sessions/{session.Id}/confirm",
                new ConfirmMealSessionRequest(new string('0', 64))),
            _ => await client.GetAsync($"/api/meal-sessions/{session.Id}")
        };

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(body);
        Assert.Equal(500, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(problem.RootElement.TryGetProperty("exception", out _));
        Assert.DoesNotContain("Stored", body, StringComparison.Ordinal);
        Assert.DoesNotContain("MealPreview", body, StringComparison.Ordinal);
        Assert.DoesNotContain("AI provider", body, StringComparison.Ordinal);
        Assert.DoesNotContain(path, body, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid-preview-value", body, StringComparison.Ordinal);
        Assert.Equal(before, await ReadSnapshotAsync(factory));
    }

    [Theory]
    [InlineData(false, "get")]
    [InlineData(false, "reconfirm")]
    [InlineData(true, "get")]
    [InlineData(true, "reconfirm")]
    public async Task ValidConfirmedPreview_PreservesCurrentAndLegacySnapshots(
        bool legacy,
        string operation)
    {
        await using TestApiFactory factory = new(environment: "Production");
        using HttpClient client = factory.CreateClient();
        MealSessionResponse session = await CreateSessionAsync(client, DemoMessages);
        await ConfirmSessionAsync(client, session);
        if (legacy)
        {
            JsonObject preview = await ReadPreviewAsync(factory, session.Id);
            RemoveNewFields(preview);
            await SavePreviewAsync(factory, session.Id, LowercaseProperties(preview).AsObject());
        }

        DatabaseSnapshot before = await ReadSnapshotAsync(factory);
        MealSessionResponse restored;
        if (operation == "reconfirm")
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/api/meal-sessions/{session.Id}/confirm",
                new ConfirmMealSessionRequest(session.PreviewToken));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            ConfirmMealSessionResponse confirmation = await ReadAsync<ConfirmMealSessionResponse>(response);
            Assert.Equal("AlreadyConfirmed", confirmation.Outcome);
            Assert.Equal(3, confirmation.Entries.Count);
            restored = confirmation.Session;
        }
        else
        {
            using HttpResponseMessage response = await client.GetAsync($"/api/meal-sessions/{session.Id}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            restored = await ReadAsync<MealSessionResponse>(response);
        }

        Assert.Equal("Confirmed", restored.Status);
        Assert.Equal(session.PreviewToken, restored.PreviewToken);
        DishPreviewResponse expected = Assert.Single(session.Dishes);
        DishPreviewResponse actual = Assert.Single(restored.Dishes);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.TotalNutrition, actual.TotalNutrition);
        Assert.Equal(expected.NutritionPer100Grams, actual.NutritionPer100Grams);
        Assert.Equal(expected.Portions.Select(portion => portion.Nutrition), actual.Portions.Select(portion => portion.Nutrition));
        if (legacy)
        {
            Assert.Null(actual.TotalNutritionQuality);
            Assert.All(actual.Ingredients, ingredient => Assert.Null(ingredient.RemovedWeightInGrams));
            Assert.All(actual.Portions, portion => Assert.Null(portion.FractionOfDish));
        }

        Assert.Equal(before, await ReadSnapshotAsync(factory));
    }

    [Fact]
    public async Task ConfirmedFractionPreview_PreservesResolvedMassAndOriginalFraction()
    {
        MealDraft draft = new(
            [new DishDraft(
                "Блюдо",
                [new IngredientDraft("Демо-продукт A", 200m)],
                160m,
                DataQuality.Exact,
                [PortionDraft.FromFraction(0.25m)])],
            []);
        await using TestApiFactory factory = new(parser: new StaticParser(draft), environment: "Production");
        using HttpClient client = factory.CreateClient();
        MealSessionResponse session = await CreateSessionAsync(client, ["Съел четверть блюда."]);
        await ConfirmSessionAsync(client, session);
        DatabaseSnapshot before = await ReadSnapshotAsync(factory);

        using HttpResponseMessage response = await client.GetAsync($"/api/meal-sessions/{session.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        MealSessionResponse restored = await ReadAsync<MealSessionResponse>(response);
        PortionPreviewResponse portion = Assert.Single(Assert.Single(restored.Dishes).Portions);
        Assert.Equal(40m, portion.WeightInGrams);
        Assert.Equal(0.25m, portion.FractionOfDish);
        Assert.Equal(Assert.Single(Assert.Single(session.Dishes).Portions).Nutrition, portion.Nutrition);
        Assert.Equal(before, await ReadSnapshotAsync(factory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompletePreview_StaleConfirmationPreservesOptionalNullsAndMissingLegacyFields(bool legacy)
    {
        MealDraft draft = new(
            [new DishDraft(
                "Незавершённое блюдо",
                [new IngredientDraft("Неизвестный продукт", null, DataQuality.Unknown)],
                null,
                DataQuality.Unknown,
                [])],
            ["Какова масса продукта?"]);
        await using TestApiFactory factory = new(parser: new StaticParser(draft), environment: "Production");
        using HttpClient client = factory.CreateClient();
        MealSessionResponse session = await CreateSessionAsync(client, ["Начал готовить."]);
        Assert.Equal("NeedsClarification", session.Status);
        if (legacy)
        {
            JsonObject preview = await ReadPreviewAsync(factory, session.Id);
            RemoveNewFields(preview);
            await SavePreviewAsync(factory, session.Id, preview);
        }

        DatabaseSnapshot before = await ReadSnapshotAsync(factory);
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{session.Id}/confirm",
            new ConfirmMealSessionRequest(new string('0', 64)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        ConfirmMealSessionResponse confirmation = await ReadAsync<ConfirmMealSessionResponse>(response);
        Assert.Equal("StalePreview", confirmation.Outcome);
        Assert.Equal("NeedsClarification", confirmation.Session.Status);
        Assert.Empty(confirmation.Entries);
        DishPreviewResponse dish = Assert.Single(confirmation.Session.Dishes);
        Assert.Null(dish.FinalWeightInGrams);
        Assert.Null(dish.TotalNutrition);
        Assert.Null(dish.NutritionPer100Grams);
        Assert.Empty(dish.Portions);
        IngredientPreviewResponse ingredient = Assert.Single(dish.Ingredients);
        Assert.Null(ingredient.WeightInGrams);
        Assert.Null(ingredient.ResolvedProduct);
        Assert.Null(ingredient.Nutrition);
        Assert.Equal(before, await ReadSnapshotAsync(factory));
    }

    [Fact]
    public async Task UnresolvedFractionPreview_PreservesRoundedZeroMassWithoutRecalculation()
    {
        decimal tinyValue = 0.0000000000000000000000000001m;
        MealDraft draft = new(
            [new DishDraft(
                "Блюдо",
                [new IngredientDraft("Неизвестный продукт", 100m)],
                tinyValue,
                DataQuality.Exact,
                [PortionDraft.FromFraction(tinyValue)])],
            []);
        await using TestApiFactory factory = new(
            seedDemoData: false,
            parser: new StaticParser(draft),
            environment: "Production");
        using HttpClient client = factory.CreateClient();
        MealSessionResponse session = await CreateSessionAsync(client, ["Съел долю блюда."]);
        Assert.Equal("NeedsProducts", session.Status);
        DatabaseSnapshot before = await ReadSnapshotAsync(factory);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{session.Id}/confirm",
            new ConfirmMealSessionRequest(new string('0', 64)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        ConfirmMealSessionResponse confirmation = await ReadAsync<ConfirmMealSessionResponse>(response);
        Assert.Equal("StalePreview", confirmation.Outcome);
        Assert.Equal("NeedsProducts", confirmation.Session.Status);
        Assert.Empty(confirmation.Entries);
        PortionPreviewResponse portion = Assert.Single(Assert.Single(confirmation.Session.Dishes).Portions);
        Assert.Equal(0m, portion.WeightInGrams);
        Assert.Equal(tinyValue, portion.FractionOfDish);
        Assert.Null(portion.Nutrition);
        Assert.Equal(before, await ReadSnapshotAsync(factory));
    }

    private static void ApplyMutation(JsonObject preview, string path, string mutation)
    {
        if (path.StartsWith("Issues/", StringComparison.Ordinal))
        {
            preview["Issues"] = new JsonArray(new JsonObject
            {
                ["Code"] = "product_not_found",
                ["DishName"] = "Блюдо",
                ["IngredientName"] = null,
                ["Message"] = "Продукт не найден."
            });
        }

        if (path.StartsWith("ClarificationQuestions/", StringComparison.Ordinal))
        {
            preview["ClarificationQuestions"] = new JsonArray("Какова масса?");
        }

        string[] segments = path.Split('/');
        JsonNode parent = preview;
        foreach (string segment in segments[..^1])
        {
            parent = parent is JsonArray array
                ? array[int.Parse(segment)]!
                : parent[segment]!;
        }

        string property = segments[^1];
        if (mutation == "remove")
        {
            Assert.True(parent.AsObject().Remove(property));
            return;
        }

        JsonNode? replacement = mutation switch
        {
            "null" => null,
            "empty-array" => new JsonArray(),
            "blank" => JsonValue.Create(" "),
            "negative" => JsonValue.Create(-1m),
            "zero" => JsonValue.Create(0m),
            "numeric-quality" => JsonValue.Create("0"),
            _ => JsonValue.Create("invalid-preview-value")
        };
        if (parent is JsonArray parentArray)
        {
            parentArray[int.Parse(property)] = replacement;
        }
        else
        {
            parent[property] = replacement;
        }
    }

    private static void RemoveNewFields(JsonObject preview)
    {
        foreach (JsonNode? dishNode in preview["Dishes"]!.AsArray())
        {
            JsonObject dish = dishNode!.AsObject();
            dish.Remove("TotalNutritionQuality");
            dish.Remove("NutritionPer100GramsQuality");
            foreach (JsonNode? ingredientNode in dish["Ingredients"]!.AsArray())
            {
                JsonObject ingredient = ingredientNode!.AsObject();
                ingredient.Remove("RemovedWeightInGrams");
                ingredient.Remove("RemovedWeightQuality");
                ingredient.Remove("IncludedWeightInGrams");
            }

            foreach (JsonNode? portionNode in dish["Portions"]!.AsArray())
            {
                JsonObject portion = portionNode!.AsObject();
                portion.Remove("FractionOfDish");
                portion.Remove("NutritionQuality");
            }
        }
    }

    private static JsonNode LowercaseProperties(JsonNode node)
    {
        return node switch
        {
            JsonObject value => new JsonObject(value.Select(property => KeyValuePair.Create(
                property.Key.ToLowerInvariant(),
                property.Value is null ? null : LowercaseProperties(property.Value)))),
            JsonArray value => new JsonArray(value.Select(item => item is null ? null : LowercaseProperties(item)).ToArray()),
            _ => node.DeepClone()
        };
    }

    private static async Task<MealSessionResponse> CreateSessionAsync(HttpClient client, string[] messages)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/meal-sessions",
            new CreateMealSessionRequest(messages, new DateOnly(2026, 10, 5)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<MealSessionResponse>(response);
    }

    private static async Task ConfirmSessionAsync(HttpClient client, MealSessionResponse session)
    {
        Assert.True(session.CanConfirm);
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{session.Id}/confirm",
            new ConfirmMealSessionRequest(session.PreviewToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ConfirmMealSessionResponse confirmation = await ReadAsync<ConfirmMealSessionResponse>(response);
        Assert.Equal("Confirmed", confirmation.Outcome);
        Assert.Equal(session.Dishes.Sum(dish => dish.Portions.Count), confirmation.Entries.Count);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        return await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidDataException();
    }

    private static async Task<JsonObject> ReadPreviewAsync(TestApiFactory factory, Guid sessionId)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        NutriFlowDbContext dbContext = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
        DbConnection connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT \"PreviewJson\" FROM \"MealSessions\" WHERE \"Id\" = @id";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = sessionId;
        command.Parameters.Add(parameter);
        string preview = (string)(await command.ExecuteScalarAsync() ?? throw new InvalidDataException());
        return JsonNode.Parse(preview)!.AsObject();
    }

    private static async Task SavePreviewAsync(TestApiFactory factory, Guid sessionId, JsonObject preview)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        NutriFlowDbContext dbContext = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
        string json = preview.ToJsonString();
        int changed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"MealSessions\" SET \"PreviewJson\" = {json} WHERE \"Id\" = {sessionId}");
        Assert.Equal(1, changed);
    }

    private static async Task<DatabaseSnapshot> ReadSnapshotAsync(TestApiFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        NutriFlowDbContext dbContext = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
        DbConnection connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        return new DatabaseSnapshot(
            await ReadRowsAsync(connection, "SELECT * FROM \"MealSessions\" ORDER BY \"Id\""),
            await ReadRowsAsync(connection, "SELECT * FROM \"MealEntries\" ORDER BY \"Id\""));
    }

    private static async Task<string> ReadRowsAsync(DbConnection connection, string query)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = query;
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        List<Dictionary<string, object?>> rows = [];
        while (await reader.ReadAsync())
        {
            Dictionary<string, object?> row = [];
            for (int index = 0; index < reader.FieldCount; index++)
            {
                row.Add(reader.GetName(index), reader.IsDBNull(index) ? null : reader.GetValue(index));
            }

            rows.Add(row);
        }

        return JsonSerializer.Serialize(rows);
    }

    private sealed record DatabaseSnapshot(string SessionsJson, string EntriesJson);

    private sealed class StaticParser(MealDraft draft) : IMealParser
    {
        public Task<MealDraft> ParseAsync(CaptureSession session, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(draft);
        }
    }
}
