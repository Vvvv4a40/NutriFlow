using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class DailyDiaryStoreTests
{
    private const string PreviewToken =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string DifferentToken =
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    [Fact]
    public async Task SetGoalAsync_InsertsUpdatesAndKeepsDatesIndependent()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        DateOnly firstDate = new DateOnly(2026, 9, 6);
        DateOnly secondDate = firstDate.AddDays(1);

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            DailyDiaryStore store = new DailyDiaryStore(context);
            await store.SetGoalAsync(
                firstDate,
                CreateGoal(2200m, 150m, 70m, 230m));
            await store.SetGoalAsync(
                secondDate,
                CreateGoal(1800m, 110m, 55m, 190m));
            await store.SetGoalAsync(
                firstDate,
                CreateGoal(2100.5m, 145.25m, 65.75m, 225.125m));
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        DailyDiaryStore readStore = new DailyDiaryStore(readContext);
        DailyGoal first = Assert.IsType<DailyGoal>(
            await readStore.FindGoalAsync(firstDate));
        DailyGoal second = Assert.IsType<DailyGoal>(
            await readStore.FindGoalAsync(secondDate));

        AssertNutrition(
            first.TargetNutrition,
            calories: 2100.5m,
            protein: 145.25m,
            fat: 65.75m,
            carbohydrates: 225.125m);
        AssertNutrition(
            second.TargetNutrition,
            calories: 1800m,
            protein: 110m,
            fat: 55m,
            carbohydrates: 190m);
        Assert.Null(await readStore.FindGoalAsync(secondDate.AddDays(1)));
    }

    [Fact]
    public async Task Owners_HaveIndependentGoalsAndEntries_AndCannotConfirmForeignSessions()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid otherOwnerId = Guid.NewGuid();
        DateOnly mealDate = new DateOnly(2026, 9, 6);

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Users" ("Id", "CreatedAtUtc", "IsLegacyLocal")
                VALUES ({otherOwnerId}, {DateTimeOffset.UtcNow}, {false})
                """);

            DailyDiaryStore legacyDiary = new DailyDiaryStore(context);
            DailyDiaryStore otherDiary = new DailyDiaryStore(context, otherOwnerId);
            await legacyDiary.SetGoalAsync(
                mealDate,
                CreateGoal(2200m, 150m, 70m, 230m));
            await otherDiary.SetGoalAsync(
                mealDate,
                CreateGoal(1800m, 110m, 55m, 190m));
        }

        StoredMealSession legacySession = await CreateSessionAsync(
            database,
            MealSessionStatus.ReadyForConfirmation,
            mealDate);
        StoredMealSession otherSession = await CreateSessionAsync(
            database,
            MealSessionStatus.ReadyForConfirmation,
            mealDate,
            otherOwnerId);
        MealEntry legacyEntry = MealSessionStoreTests.CreateEntry(
            "Обед первого владельца",
            200m,
            250m);
        MealEntry otherEntry = MealSessionStoreTests.CreateEntry(
            "Обед второго владельца",
            150m,
            190m);

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            DailyDiaryStore legacyDiary = new DailyDiaryStore(context);
            DailyDiaryStore otherDiary = new DailyDiaryStore(context, otherOwnerId);

            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                legacyDiary.ConfirmSessionAsync(
                    otherSession.Id,
                    PreviewToken,
                    [otherEntry]));
            Assert.Empty(await legacyDiary.GetSessionEntriesAsync(otherSession.Id));
            Assert.Equal(
                MealSessionConfirmationResult.Confirmed,
                await legacyDiary.ConfirmSessionAsync(
                    legacySession.Id,
                    PreviewToken,
                    [legacyEntry]));
            Assert.Equal(
                MealSessionConfirmationResult.Confirmed,
                await otherDiary.ConfirmSessionAsync(
                    otherSession.Id,
                    PreviewToken,
                    [otherEntry]));
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                otherDiary.ConfirmSessionAsync(
                    legacySession.Id,
                    PreviewToken,
                    [legacyEntry]));
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        DailyDiaryStore legacyReadDiary = new DailyDiaryStore(readContext);
        DailyDiaryStore otherReadDiary = new DailyDiaryStore(readContext, otherOwnerId);
        DailyGoal legacyGoal = Assert.IsType<DailyGoal>(
            await legacyReadDiary.FindGoalAsync(mealDate));
        DailyGoal otherGoal = Assert.IsType<DailyGoal>(
            await otherReadDiary.FindGoalAsync(mealDate));

        Assert.Equal(2200m, legacyGoal.TargetNutrition.Calories);
        Assert.Equal(1800m, otherGoal.TargetNutrition.Calories);
        AssertEntry(legacyEntry, Assert.Single(await legacyReadDiary.GetEntriesAsync(mealDate)));
        AssertEntry(otherEntry, Assert.Single(await otherReadDiary.GetEntriesAsync(mealDate)));
        AssertEntry(
            legacyEntry,
            Assert.Single(await legacyReadDiary.GetSessionEntriesAsync(legacySession.Id)));
        AssertEntry(
            otherEntry,
            Assert.Single(await otherReadDiary.GetSessionEntriesAsync(otherSession.Id)));
        Assert.Empty(await legacyReadDiary.GetSessionEntriesAsync(otherSession.Id));
        Assert.Empty(await otherReadDiary.GetSessionEntriesAsync(legacySession.Id));
    }

    [Fact]
    public async Task ConfirmSessionAsync_WritesSnapshotsAndIsIdempotent()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        DateOnly mealDate = new DateOnly(2026, 9, 6);
        StoredMealSession session = await CreateSessionAsync(
            database,
            MealSessionStatus.ReadyForConfirmation,
            mealDate);
        MealEntry firstEntry = new MealEntry(
            "Рагу, порция 1",
            200.125m,
            new NutritionValues(
                251.1234567890123456789012345m,
                20.25m,
                13.5m,
                7.75m),
            DataQuality.Estimated);
        MealEntry secondEntry = new MealEntry(
            "Рагу, порция 2",
            100.25m,
            new NutritionValues(125.5m, 10.125m, 6.75m, 3.875m),
            DataQuality.Verified);

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            DailyDiaryStore store = new DailyDiaryStore(context);
            MealSessionConfirmationResult result = await store.ConfirmSessionAsync(
                session.Id,
                PreviewToken,
                [firstEntry, secondEntry]);

            Assert.Equal(MealSessionConfirmationResult.Confirmed, result);
        }

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            DailyDiaryStore store = new DailyDiaryStore(context);
            MealSessionConfirmationResult repeated = await store.ConfirmSessionAsync(
                session.Id,
                PreviewToken,
                [MealSessionStoreTests.CreateEntry("Дубликат", 50m, 50m)]);

            Assert.Equal(MealSessionConfirmationResult.AlreadyConfirmed, repeated);
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        DailyDiaryStore readStore = new DailyDiaryStore(readContext);
        IReadOnlyList<MealEntry> dailyEntries =
            await readStore.GetEntriesAsync(mealDate);
        IReadOnlyList<MealEntry> sessionEntries =
            await readStore.GetSessionEntriesAsync(session.Id);
        StoredMealSession confirmed = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(readContext).FindAsync(session.Id));

        Assert.Equal(MealSessionStatus.Confirmed, confirmed.Status);
        Assert.NotNull(confirmed.ConfirmedAtUtc);
        Assert.Equal(confirmed.ConfirmedAtUtc, confirmed.UpdatedAtUtc);
        Assert.Equal(2, dailyEntries.Count);
        Assert.Equal(2, sessionEntries.Count);
        AssertEntry(firstEntry, dailyEntries[0]);
        AssertEntry(secondEntry, dailyEntries[1]);
        AssertEntry(firstEntry, sessionEntries[0]);
        AssertEntry(secondEntry, sessionEntries[1]);
        Assert.Empty(await readStore.GetEntriesAsync(mealDate.AddDays(1)));
    }

    [Fact]
    public async Task ConfirmSessionAsync_WhenSessionIsNotReady_WritesNothing()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        DateOnly mealDate = new DateOnly(2026, 9, 6);
        StoredMealSession session = await CreateSessionAsync(
            database,
            MealSessionStatus.NeedsProducts,
            mealDate);

        await using NutriFlowDbContext context = database.CreateContext();
        DailyDiaryStore store = new DailyDiaryStore(context);
        MealSessionConfirmationResult result = await store.ConfirmSessionAsync(
            session.Id,
            PreviewToken,
            [MealSessionStoreTests.CreateEntry("Рагу", 200m, 250m)]);

        Assert.Equal(MealSessionConfirmationResult.NotReady, result);
        Assert.Empty(await store.GetSessionEntriesAsync(session.Id));
        StoredMealSession unchanged = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(context).FindAsync(session.Id));
        Assert.Equal(MealSessionStatus.NeedsProducts, unchanged.Status);
        Assert.Null(unchanged.ConfirmedAtUtc);
    }

    [Fact]
    public async Task ConfirmSessionAsync_WithStalePreview_WritesNothing()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        StoredMealSession session = await CreateSessionAsync(
            database,
            MealSessionStatus.ReadyForConfirmation,
            new DateOnly(2026, 9, 6));

        await using NutriFlowDbContext context = database.CreateContext();
        DailyDiaryStore store = new DailyDiaryStore(context);
        MealSessionConfirmationResult result = await store.ConfirmSessionAsync(
            session.Id,
            DifferentToken,
            [MealSessionStoreTests.CreateEntry("Рагу", 200m, 250m)]);

        Assert.Equal(MealSessionConfirmationResult.StalePreview, result);
        Assert.Empty(await store.GetSessionEntriesAsync(session.Id));
        StoredMealSession unchanged = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(context).FindAsync(session.Id));
        Assert.Equal(MealSessionStatus.ReadyForConfirmation, unchanged.Status);
        Assert.Null(unchanged.ConfirmedAtUtc);
    }

    [Fact]
    public async Task ConfirmSessionAsync_WithEmptyEntries_ThrowsAndWritesNothing()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        StoredMealSession session = await CreateSessionAsync(
            database,
            MealSessionStatus.ReadyForConfirmation,
            new DateOnly(2026, 9, 6));

        await using NutriFlowDbContext context = database.CreateContext();
        DailyDiaryStore store = new DailyDiaryStore(context);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ConfirmSessionAsync(session.Id, PreviewToken, []));
        Assert.Empty(await store.GetSessionEntriesAsync(session.Id));
        StoredMealSession unchanged = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(context).FindAsync(session.Id));
        Assert.Equal(MealSessionStatus.ReadyForConfirmation, unchanged.Status);
    }

    [Fact]
    public async Task ConfirmSessionAsync_WhenSessionDoesNotExist_ThrowsKeyNotFoundException()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();

        await using NutriFlowDbContext context = database.CreateContext();
        DailyDiaryStore store = new DailyDiaryStore(context);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.ConfirmSessionAsync(
                Guid.NewGuid(),
                PreviewToken,
                [MealSessionStoreTests.CreateEntry("Рагу", 200m, 250m)]));
    }

    private static async Task<StoredMealSession> CreateSessionAsync(
        TestDatabase database,
        MealSessionStatus status,
        DateOnly mealDate,
        Guid? ownerId = null)
    {
        await using NutriFlowDbContext context = database.CreateContext();
        MealSessionStore store = ownerId is Guid id
            ? new MealSessionStore(context, id)
            : new MealSessionStore(context);

        return await store.CreateAsync(
            ["Готовое рагу весит 800 г, съел 200 г"],
            MealSessionStoreTests.CreateReadyDraft(),
            """{"canConfirm":true}""",
            PreviewToken,
            status,
            mealDate);
    }

    private static DailyGoal CreateGoal(
        decimal calories,
        decimal protein,
        decimal fat,
        decimal carbohydrates)
    {
        return new DailyGoal(
            new NutritionValues(calories, protein, fat, carbohydrates));
    }

    private static void AssertEntry(MealEntry expected, MealEntry actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.WeightInGrams, actual.WeightInGrams);
        Assert.Equal(expected.Quality, actual.Quality);
        AssertNutrition(
            actual.Nutrition,
            expected.Nutrition.Calories,
            expected.Nutrition.ProteinGrams,
            expected.Nutrition.FatGrams,
            expected.Nutrition.CarbohydratesGrams);
    }

    private static void AssertNutrition(
        NutritionValues actual,
        decimal calories,
        decimal protein,
        decimal fat,
        decimal carbohydrates)
    {
        Assert.Equal(calories, actual.Calories);
        Assert.Equal(protein, actual.ProteinGrams);
        Assert.Equal(fat, actual.FatGrams);
        Assert.Equal(carbohydrates, actual.CarbohydratesGrams);
    }
}
