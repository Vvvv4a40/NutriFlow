using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class MealSessionStoreTests
{
    private const string InitialToken =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string UpdatedToken =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task CreateAsync_ThenFindAsync_RestoresCompleteDraftAndPreview()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        DateOnly mealDate = new DateOnly(2026, 9, 6);
        MealDraft draft = CreateIncompleteDraft();
        string previewJson = """{"version":1,"canConfirm":false}""";
        StoredMealSession created;

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            MealSessionStore store = new MealSessionStore(context);
            created = await store.CreateAsync(
                ["Добавил примерно 600 г говядины", "Вес масла не помню"],
                draft,
                previewJson,
                InitialToken,
                MealSessionStatus.NeedsClarification,
                mealDate);
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        MealSessionStore readStore = new MealSessionStore(readContext);
        StoredMealSession restored = Assert.IsType<StoredMealSession>(
            await readStore.FindAsync(created.Id));

        Assert.Equal(mealDate, restored.MealDate);
        Assert.Equal(MealSessionStatus.NeedsClarification, restored.Status);
        Assert.Equal(previewJson, restored.PreviewJson);
        Assert.Equal(InitialToken, restored.PreviewToken);
        Assert.Null(restored.ConfirmedAtUtc);
        Assert.Equal(created.CreatedAtUtc, restored.CreatedAtUtc);
        Assert.Equal(created.UpdatedAtUtc, restored.UpdatedAtUtc);
        Assert.Equal(
            ["Добавил примерно 600 г говядины", "Вес масла не помню"],
            restored.Messages);
        Assert.Equal(
            ["Сколько масла вошло в блюдо?"],
            restored.Draft.ClarificationQuestions);

        DishDraft dish = Assert.Single(restored.Draft.Dishes);
        Assert.Equal("Рагу", dish.Name);
        Assert.Null(dish.FinalWeightInGrams);
        Assert.Equal(DataQuality.Unknown, dish.FinalWeightQuality);
        Assert.Collection(
            dish.Ingredients,
            ingredient =>
            {
                Assert.Equal("Говядина", ingredient.ProductName);
                Assert.Equal(600m, ingredient.WeightInGrams);
                Assert.Equal(DataQuality.Estimated, ingredient.WeightQuality);
            },
            ingredient =>
            {
                Assert.Equal("Масло", ingredient.ProductName);
                Assert.Null(ingredient.WeightInGrams);
                Assert.Equal(DataQuality.Unknown, ingredient.WeightQuality);
            });
        PortionDraft portion = Assert.Single(dish.Portions);
        Assert.Equal(350m, portion.WeightInGrams);
        Assert.Equal(DataQuality.Estimated, portion.WeightQuality);
    }

    [Fact]
    public async Task ReplaceDraftAsync_UpdatesMessagesDraftPreviewAndStatus()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        StoredMealSession initial;

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            MealSessionStore store = new MealSessionStore(context);
            initial = await store.CreateAsync(
                ["Начал готовить"],
                CreateIncompleteDraft(),
                """{"version":1}""",
                InitialToken,
                MealSessionStatus.NeedsClarification,
                new DateOnly(2026, 9, 6));
        }

        MealDraft replacement = CreateReadyDraft();

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            MealSessionStore store = new MealSessionStore(context);
            await store.ReplaceDraftAsync(
                initial.Id,
                InitialToken,
                ["Начал готовить", "Готовое блюдо весит 800 г"],
                replacement,
                """{"version":2,"canConfirm":true}""",
                UpdatedToken,
                MealSessionStatus.ReadyForConfirmation);
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        StoredMealSession restored = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(readContext).FindAsync(initial.Id));

        Assert.Equal(
            ["Начал готовить", "Готовое блюдо весит 800 г"],
            restored.Messages);
        Assert.Equal(MealSessionStatus.ReadyForConfirmation, restored.Status);
        Assert.Equal("""{"version":2,"canConfirm":true}""", restored.PreviewJson);
        Assert.Equal(UpdatedToken, restored.PreviewToken);
        Assert.Empty(restored.Draft.ClarificationQuestions);
        Assert.Equal(800m, Assert.Single(restored.Draft.Dishes).FinalWeightInGrams);
        Assert.True(restored.UpdatedAtUtc >= initial.UpdatedAtUtc);
    }

    [Fact]
    public async Task UpdatePreviewAsync_LeavesMessagesAndDraftUnchanged()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        StoredMealSession initial;

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            MealSessionStore store = new MealSessionStore(context);
            initial = await store.CreateAsync(
                ["Готовлю рагу"],
                CreateReadyDraft(),
                """{"products":"missing"}""",
                InitialToken,
                MealSessionStatus.NeedsProducts,
                new DateOnly(2026, 9, 6));
        }

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            MealSessionStore store = new MealSessionStore(context);
            await store.UpdatePreviewAsync(
                initial.Id,
                InitialToken,
                """{"products":"resolved"}""",
                UpdatedToken,
                MealSessionStatus.ReadyForConfirmation);
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        StoredMealSession restored = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(readContext).FindAsync(initial.Id));

        Assert.Equal(initial.Messages, restored.Messages);
        Assert.Equal(
            initial.Draft.Dishes[0].FinalWeightInGrams,
            restored.Draft.Dishes[0].FinalWeightInGrams);
        Assert.Equal("""{"products":"resolved"}""", restored.PreviewJson);
        Assert.Equal(UpdatedToken, restored.PreviewToken);
        Assert.Equal(MealSessionStatus.ReadyForConfirmation, restored.Status);
    }

    [Fact]
    public async Task ReplaceDraftAndUpdatePreview_WhenConfirmed_RejectChanges()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        StoredMealSession session;

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            MealSessionStore store = new MealSessionStore(context);
            session = await store.CreateAsync(
                ["Готовое рагу 800 г, съел 200 г"],
                CreateReadyDraft(),
                """{"canConfirm":true}""",
                InitialToken,
                MealSessionStatus.ReadyForConfirmation,
                new DateOnly(2026, 9, 6));
        }

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            DailyDiaryStore diary = new DailyDiaryStore(context);
            MealSessionConfirmationResult result = await diary.ConfirmSessionAsync(
                session.Id,
                InitialToken,
                [CreateEntry("Рагу", 200m, 250m)]);

            Assert.Equal(MealSessionConfirmationResult.Confirmed, result);
        }

        await using NutriFlowDbContext editContext = database.CreateContext();
        MealSessionStore editStore = new MealSessionStore(editContext);

        await Assert.ThrowsAsync<MealSessionConflictException>(() =>
            editStore.ReplaceDraftAsync(
                session.Id,
                InitialToken,
                ["Изменённое сообщение"],
                CreateReadyDraft(),
                """{"changed":true}""",
                UpdatedToken,
                MealSessionStatus.ReadyForConfirmation));
        await Assert.ThrowsAsync<MealSessionConflictException>(() =>
            editStore.UpdatePreviewAsync(
                session.Id,
                InitialToken,
                """{"changed":true}""",
                UpdatedToken,
                MealSessionStatus.ReadyForConfirmation));
    }

    [Fact]
    public async Task UpdatePreviewAsync_WithStaleToken_DoesNotOverwriteNewerPreview()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        StoredMealSession initial;

        await using (NutriFlowDbContext createContext = database.CreateContext())
        {
            initial = await new MealSessionStore(createContext).CreateAsync(
                ["Готовлю рагу"],
                CreateReadyDraft(),
                """{"version":1}""",
                InitialToken,
                MealSessionStatus.NeedsProducts,
                new DateOnly(2026, 9, 6));
        }

        await using (NutriFlowDbContext firstContext = database.CreateContext())
        {
            await new MealSessionStore(firstContext).UpdatePreviewAsync(
                initial.Id,
                InitialToken,
                """{"version":2}""",
                UpdatedToken,
                MealSessionStatus.ReadyForConfirmation);
        }

        await using (NutriFlowDbContext staleContext = database.CreateContext())
        {
            MealSessionStore staleStore = new MealSessionStore(staleContext);

            await Assert.ThrowsAsync<MealSessionConflictException>(() =>
                staleStore.UpdatePreviewAsync(
                    initial.Id,
                    InitialToken,
                    """{"version":"stale"}""",
                    new string('c', 64),
                    MealSessionStatus.NeedsClarification));
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        StoredMealSession actual = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(readContext).FindAsync(initial.Id));

        Assert.Equal("""{"version":2}""", actual.PreviewJson);
        Assert.Equal(UpdatedToken, actual.PreviewToken);
        Assert.Equal(MealSessionStatus.ReadyForConfirmation, actual.Status);
    }

    private static MealDraft CreateIncompleteDraft()
    {
        return new MealDraft(
            [
                new DishDraft(
                    "Рагу",
                    [
                        new IngredientDraft(
                            "Говядина",
                            600m,
                            DataQuality.Estimated),
                        new IngredientDraft(
                            "Масло",
                            null,
                            DataQuality.Unknown)
                    ],
                    null,
                    DataQuality.Unknown,
                    [new PortionDraft(350m, DataQuality.Estimated)])
            ],
            ["Сколько масла вошло в блюдо?"]);
    }

    internal static MealDraft CreateReadyDraft()
    {
        return new MealDraft(
            [
                new DishDraft(
                    "Рагу",
                    [
                        new IngredientDraft(
                            "Говядина",
                            600m,
                            DataQuality.Estimated),
                        new IngredientDraft("Масло", 20m, DataQuality.Exact)
                    ],
                    800m,
                    DataQuality.Exact,
                    [new PortionDraft(200m, DataQuality.Exact)])
            ],
            []);
    }

    internal static MealEntry CreateEntry(
        string name,
        decimal weight,
        decimal calories)
    {
        return new MealEntry(
            name,
            weight,
            new NutritionValues(calories, 20.25m, 13.5m, 7.75m));
    }
}
