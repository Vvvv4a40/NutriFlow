using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NutriFlow.Api.Tests;

public sealed class MobileOpenApiContractTests
{
    [Fact]
    public async Task OpenApi_ListsEveryMobileOperationWithStableRouteMethodAndName()
    {
        await using TestApiFactory factory = new(applyMigrations: false, seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        using JsonDocument document = await ReadOpenApiAsync(factory, client);
        string[] expectedOperations =
        [
            "get /api/capabilities GetApplicationCapabilities",
            "post /api/meal-sessions CreateMealSession",
            "get /api/meal-sessions/{id} GetMealSession",
            "post /api/meal-sessions/{id}/messages AddMealSessionMessage",
            "post /api/meal-sessions/{id}/confirm ConfirmMealSession",
            "post /api/meal-drafts/parse ParseMealDraft",
            "put /api/daily-goals/{date} SetDailyGoal",
            "get /api/daily-progress/{date} GetDailyProgress",
            "get /api/meal-entries/{id} GetMealEntry",
            "put /api/meal-entries/{id} UpdateMealEntry",
            "delete /api/meal-entries/{id} DeleteMealEntry",
            "post /api/products/manual CreateManualProduct",
            "post /api/products/from-label CreateLabelProduct",
            "post /api/products/aliases AddProductAlias",
            "get /api/products FindLocalProducts",
            "get /api/products/barcode/{barcode} FindProductByBarcode",
            "post /api/audio/transcribe TranscribeSpeech",
            "post /api/labels/analyze AnalyzeNutritionLabel",
            "get /api/label-photos/{fileName} GetLabelPhoto",
            "get /api/saved-dishes GetSavedDishes",
            "get /api/saved-dishes/{id} GetSavedDish"
        ];
        string[] methods = ["get", "post", "put", "patch", "delete", "head", "options", "trace"];
        string[] operations = document.RootElement.GetProperty("paths").EnumerateObject()
            .Where(path => path.Name.StartsWith("/api/", StringComparison.Ordinal))
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => methods.Contains(operation.Name))
                .Select(operation =>
                    $"{operation.Name} {path.Name} {operation.Value.GetProperty("operationId").GetString()}"))
            .ToArray();

        Assert.StartsWith("3.1.", document.RootElement.GetProperty("openapi").GetString());
        Assert.Equal(expectedOperations.Order(StringComparer.Ordinal), operations.Order(StringComparer.Ordinal));
        Assert.Equal(operations.Length, operations.Select(operation => operation.Split(' ')[2]).Distinct().Count());
        JsonElement search = Assert.Single(GetOperation(document, "/api/products", "get").GetProperty("parameters").EnumerateArray());
        Assert.Equal("name", search.GetProperty("name").GetString());
        Assert.Equal("query", search.GetProperty("in").GetString());
        AssertType(search.GetProperty("schema"), "string");
    }

    [Fact]
    public async Task Capabilities_UsesTheSameRequiredCamelCaseFieldsInJsonAndOpenApi()
    {
        await using TestApiFactory factory = new(applyMigrations: false, seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/capabilities");
        await ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.OK);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument capabilities = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using JsonDocument document = await ReadOpenApiAsync(factory, client);
        JsonElement schema = GetSchema(document, "ApplicationCapabilitiesResponse");
        string[] fields = ["aiProvider", "supportsFreeText", "supportsLabelPhotos", "supportsSpeechTranscription"];

        Assert.Equal(fields.Order(), capabilities.RootElement.EnumerateObject().Select(field => field.Name).Order());
        Assert.Equal(fields.Order(), schema.GetProperty("properties").EnumerateObject().Select(field => field.Name).Order());
        Assert.Equal(fields.Order(), schema.GetProperty("required").EnumerateArray().Select(field => field.GetString()).Order());
        Assert.Equal(JsonValueKind.String, capabilities.RootElement.GetProperty("aiProvider").ValueKind);
        AssertType(GetProperty(document, "ApplicationCapabilitiesResponse", "aiProvider"), "string");
        foreach (string field in fields.Skip(1))
        {
            Assert.Contains(capabilities.RootElement.GetProperty(field).ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
            AssertType(GetProperty(document, "ApplicationCapabilitiesResponse", field), "boolean");
        }
        AssertReference(GetOperation(document, "/api/capabilities", "get").GetProperty("responses")
            .GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema"),
            "ApplicationCapabilitiesResponse");
    }

    [Theory]
    [InlineData("CreateMealSessionRequest", "purpose", "MealSessionPurpose", new[] { "Diary", "CreateDish" })]
    [InlineData("CreateLabelProductRequest", "basis", "NutritionBasis", new[] { "Unknown", "Per100Grams", "Per100Milliliters", "PerServing" })]
    [InlineData("UpdateMealEntryRequest", "weightQuality", "DataQuality", new[] { "Unknown", "Exact", "Verified", "Estimated" })]
    public async Task RequestEnums_UseReferencesToNamedStringMembers(
        string requestName, string propertyName, string enumName, string[] members)
    {
        await using TestApiFactory factory = new(applyMigrations: false, seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        using JsonDocument document = await ReadOpenApiAsync(factory, client);

        AssertReference(GetProperty(document, requestName, propertyName), enumName);
        JsonElement values = GetSchema(document, enumName).GetProperty("enum");
        Assert.All(values.EnumerateArray(), value => Assert.Equal(JsonValueKind.String, value.ValueKind));
        Assert.Equal(members, values.EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task SessionAndPreviewSchemas_PreserveIdentifiersDatesAndUnknownNutrition()
    {
        await using TestApiFactory factory = new(applyMigrations: false, seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        using JsonDocument document = await ReadOpenApiAsync(factory, client);

        Assert.Equal("uuid", GetProperty(document, "MealSessionResponse", "id").GetProperty("format").GetString());
        Assert.Equal("date", GetProperty(document, "MealSessionResponse", "mealDate").GetProperty("format").GetString());
        AssertType(GetProperty(document, "CreateMealSessionRequest", "mealDate"), "string", nullable: true);
        AssertType(GetProperty(document, "MealSessionResponse", "previewToken"), "string");
        AssertType(GetProperty(document, "MealSessionResponse", "canConfirm"), "boolean");
        Assert.Equal("Diary", GetProperty(document, "MealSessionResponse", "purpose").GetProperty("default").GetString());
        AssertReference(GetProperty(document, "MealSessionResponse", "dishes").GetProperty("items"), "DishPreviewResponse");
        AssertReference(GetProperty(document, "DishPreviewResponse", "ingredients").GetProperty("items"), "IngredientPreviewResponse");
        AssertReference(GetProperty(document, "DishPreviewResponse", "portions").GetProperty("items"), "PortionPreviewResponse");
        AssertType(GetProperty(document, "DishPreviewResponse", "finalWeightInGrams"), "number", nullable: true);
        AssertNullableReference(GetProperty(document, "DishPreviewResponse", "totalNutrition"), "NutritionResponse");
        AssertNullableReference(GetProperty(document, "DishPreviewResponse", "nutritionPer100Grams"), "NutritionResponse");
        AssertNullableReference(GetProperty(document, "IngredientPreviewResponse", "resolvedProduct"), "ProductResponse");
        AssertNullableReference(GetProperty(document, "IngredientPreviewResponse", "nutrition"), "NutritionResponse");
        AssertType(GetProperty(document, "PortionPreviewResponse", "weightInGrams"), "number", nullable: true);
        AssertType(GetProperty(document, "PortionPreviewResponse", "fractionOfDish"), "number", nullable: true);
        AssertNullableReference(GetProperty(document, "PortionPreviewResponse", "nutrition"), "NutritionResponse");
        AssertType(GetProperty(document, "WorkflowIssueResponse", "ingredientName"), "string", nullable: true);
        JsonElement id = Assert.Single(GetOperation(document, "/api/meal-sessions/{id}", "get")
            .GetProperty("parameters").EnumerateArray());
        Assert.Equal("path", id.GetProperty("in").GetString());
        Assert.True(id.GetProperty("required").GetBoolean());
        Assert.Equal("uuid", id.GetProperty("schema").GetProperty("format").GetString());
    }

    [Fact]
    public async Task ConfirmationDiaryAndProductSchemas_KeepSnapshotsAndOptionalValuesDistinct()
    {
        await using TestApiFactory factory = new(applyMigrations: false, seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        using JsonDocument document = await ReadOpenApiAsync(factory, client);

        AssertReference(GetProperty(document, "ConfirmMealSessionResponse", "session"), "MealSessionResponse");
        AssertReference(GetProperty(document, "ConfirmMealSessionResponse", "entries").GetProperty("items"), "MealEntryResponse");
        AssertNullableReference(GetProperty(document, "ConfirmMealSessionResponse", "savedDish"), "SavedDishResponse");
        AssertType(GetProperty(document, "ConfirmMealSessionResponse", "message"), "string", nullable: true);
        AssertReference(GetProperty(document, "SavedDishDetailResponse", "savedDish"), "SavedDishResponse");
        AssertReference(GetProperty(document, "SavedDishDetailResponse", "dish"), "DishPreviewResponse");
        Assert.Equal("uuid", GetProperty(document, "SavedDishResponse", "id").GetProperty("format").GetString());
        Assert.Equal("uuid", GetProperty(document, "SavedDishResponse", "sourceSessionId").GetProperty("format").GetString());
        Assert.Equal("date", GetProperty(document, "DailyProgressResponse", "date").GetProperty("format").GetString());
        AssertReference(GetProperty(document, "DailyProgressResponse", "consumed"), "NutritionResponse");
        foreach (string field in new[] { "goal", "remaining", "exceeded" })
        {
            AssertNullableReference(GetProperty(document, "DailyProgressResponse", field), "NutritionResponse");
        }
        AssertReference(GetProperty(document, "DailyProgressResponse", "entries").GetProperty("items"), "MealEntryResponse");
        AssertType(GetProperty(document, "MealEntryResponse", "id"), "integer");
        AssertType(GetProperty(document, "MealEntryResponse", "revision"), "integer");
        foreach (string field in new[] { "calories", "proteinGrams", "fatGrams", "carbohydratesGrams" })
        {
            AssertType(GetProperty(document, "NutritionResponse", field), "number");
            AssertType(GetProperty(document, "ProductResponse", field), "number");
        }
        foreach (string field in new[] { "sourceKind", "dataQuality", "sourceName" })
        {
            AssertType(GetProperty(document, "ProductResponse", field), "string");
        }
        AssertType(GetProperty(document, "ProductResponse", "sourceReference"), "string", nullable: true);
        AssertType(GetProperty(document, "ProductResponse", "barcode"), "string", nullable: true);
        JsonElement date = Assert.Single(GetOperation(document, "/api/daily-progress/{date}", "get")
            .GetProperty("parameters").EnumerateArray());
        Assert.Equal("path", date.GetProperty("in").GetString());
        Assert.True(date.GetProperty("required").GetBoolean());
        Assert.Equal("date", date.GetProperty("schema").GetProperty("format").GetString());
    }

    [Fact]
    public async Task LabelUpload_DescribesPhotoBinaryAndReviewableJsonInsteadOfCreatingAProduct()
    {
        await using TestApiFactory factory = new(applyMigrations: false, seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        using JsonDocument document = await ReadOpenApiAsync(factory, client);
        JsonElement operation = GetOperation(document, "/api/labels/analyze", "post");
        JsonElement body = operation.GetProperty("requestBody");
        JsonElement upload = body.GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema");

        Assert.True(body.GetProperty("required").GetBoolean());
        Assert.Equal("photo", Assert.Single(upload.GetProperty("required").EnumerateArray()).GetString());
        AssertReference(upload.GetProperty("properties").GetProperty("photo"), "IFormFile");
        AssertType(GetSchema(document, "IFormFile"), "string");
        Assert.Equal("binary", GetSchema(document, "IFormFile").GetProperty("format").GetString());
        JsonElement responses = operation.GetProperty("responses");
        Assert.Equal(new[] { "200", "400", "429", "502", "503", "504" },
            responses.EnumerateObject().Select(response => response.Name).Order());
        AssertReference(responses.GetProperty("200").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema"), "NutritionLabelDraftResponse");
        foreach (string status in new[] { "400", "429", "502", "503", "504" })
        {
            AssertReference(responses.GetProperty(status).GetProperty("content").GetProperty("application/problem+json")
                .GetProperty("schema"), status == "400" ? "HttpValidationProblemDetails" : "ProblemDetails");
        }
        AssertType(GetProperty(document, "NutritionLabelDraftResponse", "photoReference"), "string");
        AssertType(GetProperty(document, "NutritionLabelDraftResponse", "productName"), "string", nullable: true);
        AssertType(GetProperty(document, "NutritionLabelDraftResponse", "canCreateProduct"), "boolean");
        foreach (string field in new[] { "calories", "proteinGrams", "fatGrams", "carbohydratesGrams" })
        {
            AssertType(GetProperty(document, "NutritionLabelDraftResponse", field), "number", nullable: true);
        }
    }

    private static async Task<JsonDocument> ReadOpenApiAsync(TestApiFactory factory, HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        await ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.OK);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        string databasePath = Assert.IsType<string>(factory.Services.GetRequiredService<IConfiguration>()["Database:Path"]);
        Assert.True(Path.IsPathFullyQualified(databasePath));
        Assert.False(File.Exists(databasePath));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement GetOperation(JsonDocument document, string path, string method) =>
        document.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method);

    private static JsonElement GetSchema(JsonDocument document, string name) =>
        document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(name);

    private static JsonElement GetProperty(JsonDocument document, string schemaName, string propertyName) =>
        GetSchema(document, schemaName).GetProperty("properties").GetProperty(propertyName);

    private static void AssertReference(JsonElement schema, string name) =>
        Assert.Equal($"#/components/schemas/{name}", schema.GetProperty("$ref").GetString());

    private static void AssertNullableReference(JsonElement schema, string name)
    {
        JsonElement[] variants = schema.GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.Equal(2, variants.Length);
        Assert.Single(variants, variant => variant.TryGetProperty("type", out JsonElement type) && type.GetString() == "null");
        AssertReference(Assert.Single(variants, variant => variant.TryGetProperty("$ref", out _)), name);
    }

    private static void AssertType(JsonElement schema, string expected, bool nullable = false)
    {
        JsonElement type = schema.GetProperty("type");
        string?[] types = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(value => value.GetString()).ToArray()
            : [type.GetString()];
        Assert.Contains(expected, types);
        Assert.Equal(nullable, types.Contains("null"));
    }
}
