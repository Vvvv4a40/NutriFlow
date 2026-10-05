using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NutriFlow.Api.Contracts;
using NutriFlow.Infrastructure.LabelPhotos;

namespace NutriFlow.Api.Tests;

public sealed class OpenApiMetadataApiTests
{
    [Fact]
    public async Task ConfirmationConflict_DescribesReviewOutcomeAndProblemDetails()
    {
        await using TestApiFactory factory = new(applyMigrations: false, seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        using JsonDocument document = await ReadOpenApiAsync(factory, client);
        JsonElement content = GetOperation(document, "/api/meal-sessions/{id}/confirm", "post")
            .GetProperty("responses").GetProperty("409").GetProperty("content");

        AssertReference(content.GetProperty("application/json").GetProperty("schema"), "ConfirmMealSessionResponse");
        JsonElement problem = ResolveSchema(document, content.GetProperty("application/problem+json").GetProperty("schema"));
        Assert.Equal("object", problem.GetProperty("type").GetString());
        Assert.True(JsonElement.DeepEquals(GetSchema(document, "ProblemDetails").GetProperty("properties"),
            problem.GetProperty("properties")));
    }

    [Theory]
    [InlineData("200")]
    [InlineData("206")]
    public async Task LabelPhotoResponse_DescribesEachImageMediaTypeAsBinary(string status)
    {
        await using TestApiFactory factory = new(applyMigrations: false, seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        using JsonDocument document = await ReadOpenApiAsync(factory, client);
        JsonElement content = GetOperation(document, "/api/label-photos/{fileName}", "get")
            .GetProperty("responses").GetProperty(status).GetProperty("content");

        Assert.Equal(new[] { "image/jpeg", "image/png", "image/webp" },
            content.EnumerateObject().Select(mediaType => mediaType.Name).Order());
        foreach (JsonProperty mediaType in content.EnumerateObject())
        {
            JsonElement schema = ResolveSchema(document, mediaType.Value.GetProperty("schema"));
            Assert.Equal("string", schema.GetProperty("type").GetString());
            Assert.Equal("binary", schema.GetProperty("format").GetString());
        }
    }

    [Fact]
    public async Task UnsatisfiablePhotoRange_DescribesAnEmptyResponseWithoutAnImageBody()
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        byte[] image = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        string reference;
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            LabelPhotoStore store = scope.ServiceProvider.GetRequiredService<LabelPhotoStore>();
            reference = await store.SaveAsync(LabelPhotoValidator.Validate(image, "image/png"));
        }
        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/label-photos/{reference["label-photo:".Length..]}");
        request.Headers.Range = new RangeHeaderValue(image.Length, image.Length + 10);
        using HttpResponseMessage response = await client.SendAsync(request);

        await ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.RequestedRangeNotSatisfiable);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, response.Content.Headers.ContentLength);
        Assert.Equal(image.Length, response.Content.Headers.ContentRange?.Length);
        Assert.Null(response.Content.Headers.ContentRange?.From);
        Assert.Null(response.Content.Headers.ContentRange?.To);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);

        await using TestApiFactory documentFactory = new(applyMigrations: false, seedDemoData: false);
        using HttpClient documentClient = documentFactory.CreateClient();
        using JsonDocument document = await ReadOpenApiAsync(documentFactory, documentClient);
        JsonElement rangeResponse = GetOperation(document, "/api/label-photos/{fileName}", "get")
            .GetProperty("responses").GetProperty("416");
        Assert.False(rangeResponse.TryGetProperty("content", out _));
    }

    [Theory]
    [InlineData("/api/products/manual")]
    [InlineData("/api/products/from-label")]
    [InlineData("/api/products/aliases")]
    [InlineData("/api/meal-drafts/parse")]
    public async Task JsonOnlyRequest_DescribesItsActualUnsupportedMediaTypeProblem(string path)
    {
        await using TestApiFactory factory = new(applyMigrations: false, seedDemoData: false, environment: "Production");
        using HttpClient client = factory.CreateClient();
        using StringContent body = new("{}", Encoding.UTF8, "text/plain");
        using HttpResponseMessage response = await client.PostAsync(path, body);
        await ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.UnsupportedMediaType);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(415, problem.RootElement.GetProperty("status").GetInt32());

        await using TestApiFactory documentFactory = new(applyMigrations: false, seedDemoData: false);
        using HttpClient documentClient = documentFactory.CreateClient();
        using JsonDocument document = await ReadOpenApiAsync(documentFactory, documentClient);
        JsonElement schema = GetOperation(document, path, "post").GetProperty("responses")
            .GetProperty("415").GetProperty("content").GetProperty("application/problem+json").GetProperty("schema");
        AssertReference(schema, "ProblemDetails");
        AssertDatabaseWasNotCreated(factory);
    }

    [Theory]
    [InlineData("CreateMealSessionRequest", "purpose", "MealSessionPurpose", "Diary")]
    [InlineData("UpdateMealEntryRequest", "weightQuality", "DataQuality", "Exact")]
    public async Task StringEnumDefault_MatchesTheRequestJsonFormat(
        string requestName, string propertyName, string enumName, string defaultValue)
    {
        await using TestApiFactory factory = new(applyMigrations: false, seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        using JsonDocument document = await ReadOpenApiAsync(factory, client);
        JsonElement property = GetSchema(document, requestName).GetProperty("properties").GetProperty(propertyName);

        AssertReference(property, enumName);
        JsonElement schema = ResolveSchema(document, property);
        JsonElement value = property.GetProperty("default");
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        Assert.Equal(defaultValue, value.GetString());
        Assert.Contains(defaultValue, schema.GetProperty("enum").EnumerateArray().Select(member => member.GetString()));
        Assert.False(schema.TryGetProperty("default", out _));
        JsonSerializerOptions options = factory.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        string actualDefault = requestName switch
        {
            nameof(CreateMealSessionRequest) => JsonSerializer.Deserialize<CreateMealSessionRequest>("{}", options)!.Purpose.ToString(),
            nameof(UpdateMealEntryRequest) => JsonSerializer.Deserialize<UpdateMealEntryRequest>("{}", options)!.WeightQuality.ToString(),
            _ => throw new InvalidOperationException($"Unsupported request: {requestName}.")
        };
        Assert.Equal(defaultValue, actualDefault);
    }

    [Fact]
    public async Task ResponseDecimals_DescribeNumericValuesAndNullWithoutStringPatterns()
    {
        await using TestApiFactory factory = new(applyMigrations: false, seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        using JsonDocument document = await ReadOpenApiAsync(factory, client);
        (string Schema, string[] Properties, bool Nullable)[] responseFields =
        [
            ("NutritionResponse", ["calories", "proteinGrams", "fatGrams", "carbohydratesGrams"], false),
            ("ProductResponse", ["calories", "proteinGrams", "fatGrams", "carbohydratesGrams"], false),
            ("MealEntryResponse", ["weightInGrams"], false),
            ("SavedDishResponse", ["finalWeightInGrams"], false),
            ("DishPreviewResponse", ["finalWeightInGrams"], true),
            ("IngredientPreviewResponse", ["weightInGrams", "removedWeightInGrams", "includedWeightInGrams"], true),
            ("PortionPreviewResponse", ["weightInGrams", "fractionOfDish"], true),
            ("NutritionLabelDraftResponse", ["calories", "proteinGrams", "fatGrams", "carbohydratesGrams"], true),
            ("DishDraftResponse", ["finalWeightInGrams"], true),
            ("IngredientDraftResponse", ["weightInGrams", "removedWeightInGrams", "includedWeightInGrams"], true),
            ("PortionDraftResponse", ["weightInGrams", "fractionOfDish"], true)
        ];

        foreach ((string schemaName, string[] properties, bool nullable) in responseFields)
        {
            foreach (string property in properties)
            {
                JsonElement schema = GetSchema(document, schemaName).GetProperty("properties").GetProperty(property);
                AssertTypes(schema, nullable ? ["null", "number"] : ["number"]);
                Assert.Equal("decimal", schema.GetProperty("format").GetString());
                Assert.False(schema.TryGetProperty("pattern", out _));
            }
        }
    }

    [Fact]
    public async Task RequestDecimals_PreserveNumericStringCompatibilityAndNullableWeights()
    {
        await using TestApiFactory factory = new(applyMigrations: false, seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        using JsonDocument document = await ReadOpenApiAsync(factory, client);

        foreach (string requestName in new[] { "CreateManualProductRequest", "CreateLabelProductRequest", "SetDailyGoalRequest" })
        {
            foreach (string property in new[] { "calories", "proteinGrams", "fatGrams", "carbohydratesGrams" })
            {
                JsonElement schema = GetSchema(document, requestName).GetProperty("properties").GetProperty(property);
                AssertTypes(schema, ["number", "string"]);
                Assert.Equal("decimal", schema.GetProperty("format").GetString());
                Assert.False(string.IsNullOrWhiteSpace(schema.GetProperty("pattern").GetString()));
            }
        }
        JsonElement weight = GetSchema(document, "UpdateMealEntryRequest").GetProperty("properties").GetProperty("weightInGrams");
        AssertTypes(weight, ["null", "number", "string"]);
        Assert.Equal("decimal", weight.GetProperty("format").GetString());
        Assert.False(string.IsNullOrWhiteSpace(weight.GetProperty("pattern").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NumericAndNumericStringRequests_StillReturnJsonNumbers(bool useStrings)
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        object Encode(decimal value) => useStrings ? value.ToString(CultureInfo.InvariantCulture) : value;
        string values = JsonSerializer.Serialize(new
        {
            name = "Decimal contract product",
            calories = Encode(121.25m),
            proteinGrams = Encode(17.125m),
            fatGrams = Encode(5.5m),
            carbohydratesGrams = Encode(3.75m),
            isEstimated = false
        });
        using StringContent productBody = new(values, Encoding.UTF8, "application/json");
        using HttpResponseMessage product = await client.PostAsync("/api/products/manual", productBody);
        await ApiTestAssertions.AssertStatusAsync(factory, product, HttpStatusCode.Created);
        using JsonDocument productDocument = JsonDocument.Parse(await product.Content.ReadAsStringAsync());
        AssertNutritionNumbers(productDocument.RootElement);

        string goalValues = JsonSerializer.Serialize(new
        {
            calories = Encode(121.25m),
            proteinGrams = Encode(17.125m),
            fatGrams = Encode(5.5m),
            carbohydratesGrams = Encode(3.75m)
        });
        using StringContent goalBody = new(goalValues, Encoding.UTF8, "application/json");
        using HttpResponseMessage goal = await client.PutAsync("/api/daily-goals/2026-10-06", goalBody);
        await ApiTestAssertions.AssertStatusAsync(factory, goal, HttpStatusCode.OK);
        using JsonDocument goalDocument = JsonDocument.Parse(await goal.Content.ReadAsStringAsync());
        AssertNutritionNumbers(goalDocument.RootElement.GetProperty("goal"));
    }

    private static void AssertNutritionNumbers(JsonElement nutrition)
    {
        (string Name, decimal Value)[] values =
        [
            ("calories", 121.25m),
            ("proteinGrams", 17.125m),
            ("fatGrams", 5.5m),
            ("carbohydratesGrams", 3.75m)
        ];
        foreach ((string name, decimal expected) in values)
        {
            JsonElement value = nutrition.GetProperty(name);
            Assert.Equal(JsonValueKind.Number, value.ValueKind);
            Assert.Equal(expected, value.GetDecimal());
        }
    }

    private static void AssertTypes(JsonElement schema, string[] expected)
    {
        JsonElement type = schema.GetProperty("type");
        string?[] types = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(value => value.GetString()).ToArray()
            : [type.GetString()];
        Assert.Equal(expected.Order(), types.Order());
    }

    private static async Task<JsonDocument> ReadOpenApiAsync(TestApiFactory factory, HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        await ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.OK);
        AssertDatabaseWasNotCreated(factory);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static void AssertDatabaseWasNotCreated(TestApiFactory factory)
    {
        string databasePath = Assert.IsType<string>(factory.Services.GetRequiredService<IConfiguration>()["Database:Path"]);
        Assert.True(Path.IsPathFullyQualified(databasePath));
        Assert.False(File.Exists(databasePath));
    }

    private static JsonElement GetOperation(JsonDocument document, string path, string method) =>
        document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

    private static JsonElement GetSchema(JsonDocument document, string name) =>
        document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(name);

    private static JsonElement ResolveSchema(JsonDocument document, JsonElement schema) =>
        schema.TryGetProperty("$ref", out JsonElement reference)
            ? GetSchema(document, reference.GetString()!["#/components/schemas/".Length..])
            : schema;

    private static void AssertReference(JsonElement schema, string name) =>
        Assert.Equal($"#/components/schemas/{name}", schema.GetProperty("$ref").GetString());
}
