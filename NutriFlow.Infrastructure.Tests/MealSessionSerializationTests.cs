using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class MealSessionSerializationTests
{
    private const string PreviewToken =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string ValidDraftJson = """
        {
          "Dishes": [
            {
              "Name": "Рагу",
              "Ingredients": [
                {
                  "ProductName": "Говядина",
                  "WeightInGrams": 300,
                  "WeightQuality": 1,
                  "RemovedWeightInGrams": 0,
                  "RemovedWeightQuality": 1,
                  "RemovalSpecified": true
                }
              ],
              "FinalWeightInGrams": 500,
              "FinalWeightQuality": 1,
              "Portions": [
                { "WeightInGrams": 200, "FractionOfDish": null, "WeightQuality": 1 }
              ]
            }
          ],
          "ClarificationQuestions": []
        }
        """;

    public static TheoryData<string> CorruptDrafts => new()
    {
        "not-json",
        "null",
        "{}",
        """{"Dishes":null,"ClarificationQuestions":[]}""",
        ValidDraftJson.Replace("\"ClarificationQuestions\": []", "\"ClarificationQuestions\": null"),
        """{"Dishes":[null],"ClarificationQuestions":[]}""",
        ModifyDraft(dish => dish["Ingredients"] = null),
        ModifyDraft(dish => dish["Ingredients"] = JsonNode.Parse("[null]")),
        ModifyDraft(dish => dish["Portions"] = null),
        ModifyDraft(dish => dish["Portions"] = JsonNode.Parse("[null]")),
        ModifyDraft(dish => dish["Portions"] = JsonNode.Parse(
            """[{"WeightInGrams":200,"FractionOfDish":0.25,"WeightQuality":1}]""")),
        ModifyDraft(dish => dish["Portions"] = JsonNode.Parse(
            """[{"WeightInGrams":null,"FractionOfDish":null,"WeightQuality":1}]""")),
        ModifyDraft(dish =>
        {
            dish["FinalWeightInGrams"] = null;
            dish["FinalWeightQuality"] = (int)DataQuality.Unknown;
            dish["Portions"] = JsonNode.Parse(
                """
                [
                  {"WeightInGrams":79228162514264337593543950335,"FractionOfDish":null,"WeightQuality":1},
                  {"WeightInGrams":1,"FractionOfDish":null,"WeightQuality":1}
                ]
                """);
        }),
        ModifyDraft(dish => dish["Ingredients"]![0]!["WeightInGrams"] = -1m),
        """{"Dishes":[],"ClarificationQuestions":[]}""",
        ValidDraftJson.Replace("\"ClarificationQuestions\": []", "\"ClarificationQuestions\": [\" \"]"),
        ModifyDraft(dish => dish["FinalWeightQuality"] = 77),
        ModifyDraft(dish => dish["Ingredients"]![0]!["RemovedWeightInGrams"] = null)
    };

    [Theory]
    [MemberData(nameof(CorruptDrafts))]
    public async Task FindAsync_RejectsCorruptDraftWithInvalidDataException(string draftJson)
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        MealSessionStore store = new MealSessionStore(context);
        StoredMealSession session = await CreateSessionAsync(store);

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MealSessions" SET "DraftJson" = {draftJson}
            WHERE "Id" = {session.Id}
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.FindAsync(session.Id));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("[123]")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"  \"]")]
    public async Task FindAsync_RejectsCorruptMessagesWithInvalidDataException(string messagesJson)
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        MealSessionStore store = new MealSessionStore(context);
        StoredMealSession session = await CreateSessionAsync(store);

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MealSessions" SET "MessagesJson" = {messagesJson}
            WHERE "Id" = {session.Id}
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.FindAsync(session.Id));
    }

    [Fact]
    public async Task CreateAndFindAsync_PreserveDraftValuesAndStoredJsonLayout()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid sessionId;
        string originalJson;
        MealDraft draft = new MealDraft(
            [
                new DishDraft(
                    "Рагу",
                    [
                        new IngredientDraft("Говядина", 300.125m, DataQuality.Estimated, 25.25m, DataQuality.Estimated),
                        new IngredientDraft("Масло", 20m, DataQuality.Exact, null, DataQuality.Unknown),
                        new IngredientDraft("Овощи", null, DataQuality.Unknown)
                    ],
                    500.5m,
                    DataQuality.Estimated,
                    [new PortionDraft(100.125m), PortionDraft.FromFraction(0.25m, DataQuality.Estimated)]),
                new DishDraft(
                    "Коктейль",
                    [new IngredientDraft("Молоко", 200m)],
                    null,
                    DataQuality.Unknown,
                    [PortionDraft.FromFraction(0.5m)])
            ],
            ["Сколько масла осталось?"]);

        await using (NutriFlowDbContext writeContext = database.CreateContext())
        {
            StoredMealSession created = await CreateSessionAsync(new MealSessionStore(writeContext), draft);
            sessionId = created.Id;
            originalJson = await ReadDraftJsonAsync(writeContext, sessionId);
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        MealSessionStore readStore = new MealSessionStore(readContext);
        StoredMealSession restored = Assert.IsType<StoredMealSession>(await readStore.FindAsync(sessionId));
        DishDraft stew = restored.Draft.Dishes[0];

        Assert.Equal(["Готовлю рагу", "Съел часть блюда"], restored.Messages);
        Assert.Equal(["Сколько масла осталось?"], restored.Draft.ClarificationQuestions);
        Assert.Equal(300.125m, stew.Ingredients[0].WeightInGrams);
        Assert.Equal(DataQuality.Estimated, stew.Ingredients[0].WeightQuality);
        Assert.Equal(25.25m, stew.Ingredients[0].RemovedWeightInGrams);
        Assert.Equal(DataQuality.Estimated, stew.Ingredients[0].RemovedWeightQuality);
        Assert.Null(stew.Ingredients[1].RemovedWeightInGrams);
        Assert.Equal(DataQuality.Unknown, stew.Ingredients[1].RemovedWeightQuality);
        Assert.Null(stew.Ingredients[2].WeightInGrams);
        Assert.Equal(DataQuality.Unknown, stew.Ingredients[2].WeightQuality);
        Assert.Equal(500.5m, stew.FinalWeightInGrams);
        Assert.Equal(DataQuality.Estimated, stew.FinalWeightQuality);
        Assert.Equal(100.125m, stew.Portions[0].WeightInGrams);
        Assert.Null(stew.Portions[0].FractionOfDish);
        Assert.Equal(0.25m, stew.Portions[1].FractionOfDish);
        Assert.Null(stew.Portions[1].WeightInGrams);
        Assert.Equal(DataQuality.Estimated, stew.Portions[1].WeightQuality);
        Assert.Null(restored.Draft.Dishes[1].FinalWeightInGrams);
        Assert.Equal(DataQuality.Unknown, restored.Draft.Dishes[1].FinalWeightQuality);
        Assert.Equal(0.5m, Assert.Single(restored.Draft.Dishes[1].Portions).FractionOfDish);

        StoredMealSession rewritten = await CreateSessionAsync(readStore, restored.Draft);
        Assert.Equal(originalJson, await ReadDraftJsonAsync(readContext, rewritten.Id));

        using JsonDocument document = JsonDocument.Parse(originalJson);
        Assert.Equal(["Dishes", "ClarificationQuestions"], PropertyNames(document.RootElement));
        JsonElement storedDish = document.RootElement.GetProperty("Dishes")[0];
        Assert.Equal(
            ["Name", "Ingredients", "FinalWeightInGrams", "FinalWeightQuality", "Portions"],
            PropertyNames(storedDish));
        JsonElement storedIngredient = storedDish.GetProperty("Ingredients")[0];
        Assert.Equal(
            ["ProductName", "WeightInGrams", "WeightQuality", "RemovedWeightInGrams", "RemovedWeightQuality", "RemovalSpecified"],
            PropertyNames(storedIngredient));
        Assert.True(storedIngredient.GetProperty("RemovalSpecified").GetBoolean());
        Assert.Equal((int)DataQuality.Estimated, storedIngredient.GetProperty("WeightQuality").GetInt32());
        Assert.Equal(
            ["WeightInGrams", "FractionOfDish", "WeightQuality"],
            PropertyNames(storedDish.GetProperty("Portions")[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindAsync_RestoresLegacyDraftWithoutSpecifiedRemoval(bool includeFalseRemovalFlag)
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        MealSessionStore store = new MealSessionStore(context);
        StoredMealSession session = await CreateSessionAsync(store);
        string legacyJson = ModifyDraft(dish =>
        {
            JsonObject ingredient = dish["Ingredients"]![0]!.AsObject();
            if (includeFalseRemovalFlag)
            {
                ingredient["RemovalSpecified"] = false;
                ingredient["RemovedWeightInGrams"] = null;
                ingredient["RemovedWeightQuality"] = (int)DataQuality.Unknown;
            }
            else
            {
                ingredient.Remove("RemovalSpecified");
                ingredient.Remove("RemovedWeightInGrams");
                ingredient.Remove("RemovedWeightQuality");
            }
        });

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MealSessions" SET "DraftJson" = {legacyJson}
            WHERE "Id" = {session.Id}
            """);

        StoredMealSession restored = Assert.IsType<StoredMealSession>(await store.FindAsync(session.Id));
        IngredientDraft ingredient = Assert.Single(Assert.Single(restored.Draft.Dishes).Ingredients);
        Assert.Equal(0m, ingredient.RemovedWeightInGrams);
        Assert.Equal(DataQuality.Exact, ingredient.RemovedWeightQuality);
        Assert.Equal(300m, ingredient.IncludedWeightInGrams);
    }

    [Fact]
    public async Task FindAsync_ReadsDraftFieldNamesCaseInsensitively()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        MealSessionStore store = new MealSessionStore(context);
        StoredMealSession session = await CreateSessionAsync(store);
        string lowerCaseJson = ValidDraftJson.ToLowerInvariant();

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MealSessions" SET "DraftJson" = {lowerCaseJson}
            WHERE "Id" = {session.Id}
            """);

        StoredMealSession restored = Assert.IsType<StoredMealSession>(await store.FindAsync(session.Id));
        DishDraft dish = Assert.Single(restored.Draft.Dishes);
        Assert.Equal(500m, dish.FinalWeightInGrams);
        Assert.Equal(200m, Assert.Single(dish.Portions).WeightInGrams);
        Assert.Equal(300m, Assert.Single(dish.Ingredients).IncludedWeightInGrams);
    }

    private static Task<StoredMealSession> CreateSessionAsync(MealSessionStore store, MealDraft? draft = null)
    {
        return store.CreateAsync(
            ["Готовлю рагу", "Съел часть блюда"],
            draft ?? MealSessionStoreTests.CreateReadyDraft(),
            "{}",
            PreviewToken,
            MealSessionStatus.NeedsClarification,
            new DateOnly(2026, 9, 6));
    }

    private static Task<string> ReadDraftJsonAsync(NutriFlowDbContext context, Guid id)
    {
        return context.Database.SqlQuery<string>($"""
            SELECT "DraftJson" AS "Value" FROM "MealSessions" WHERE "Id" = {id}
            """).SingleAsync();
    }

    private static string[] PropertyNames(JsonElement element)
    {
        return element.EnumerateObject().Select(property => property.Name).ToArray();
    }

    private static string ModifyDraft(Action<JsonObject> changeDish)
    {
        JsonNode document = JsonNode.Parse(ValidDraftJson)!;
        changeDish(document["Dishes"]![0]!.AsObject());
        return document.ToJsonString();
    }
}
