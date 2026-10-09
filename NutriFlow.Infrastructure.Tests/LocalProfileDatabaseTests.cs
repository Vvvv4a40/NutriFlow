using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class LocalProfileDatabaseTests
{
    private const string PreviewToken =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OriginalRequestHash =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateOnly MealDate = new(2026, 10, 9);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative.db")]
    [InlineData(":memory:")]
    public async Task OpenAsync_RejectsMissingOrRelativePath(string? path)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            LocalProfileDatabase.OpenAsync(path!, Guid.NewGuid()));
    }

    [Fact]
    public async Task OpenAsync_RejectsDirectoryAndEmptyOwnerBeforeCreatingDatabase()
    {
        using TemporaryProfiles profiles = new();
        string directory = Path.GetDirectoryName(profiles.FirstPath)!;
        Directory.CreateDirectory(directory);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            LocalProfileDatabase.OpenAsync(directory, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            LocalProfileDatabase.OpenAsync(profiles.FirstPath, Guid.Empty));

        Assert.False(File.Exists(profiles.FirstPath));
    }

    [Fact]
    public async Task OpenAsync_PreCanceledRequestDoesNotCreateDirectoryOrDatabase()
    {
        using TemporaryProfiles profiles = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LocalProfileDatabase.OpenAsync(profiles.FirstPath, Guid.NewGuid(), cancellation.Token));

        Assert.False(Directory.Exists(Path.GetDirectoryName(profiles.FirstPath)));
    }

    [Fact]
    public async Task OpenAsync_ConcurrentRequestsCreateOneOwnerAndPreserveCreationTime()
    {
        using TemporaryProfiles profiles = new();
        await using (NutriFlowDbContext initialized =
            await LocalProfileDatabase.OpenAsync(profiles.FirstPath, Guid.NewGuid()))
        {
        }

        Guid owner = Guid.NewGuid();
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] requests = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            await using NutriFlowDbContext context =
                await LocalProfileDatabase.OpenAsync(profiles.FirstPath, owner);
            Assert.Equal(1, await OwnerCountAsync(context, owner));
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(requests);

        string createdAt;
        await using (NutriFlowDbContext context =
            await LocalProfileDatabase.OpenAsync(profiles.FirstPath, owner))
        {
            createdAt = await context.Database.SqlQuery<string>($"""
                SELECT "CreatedAtUtc" AS "Value" FROM "Users" WHERE "Id" = {owner}
                """).SingleAsync();
            Assert.Equal(0, await context.Database.SqlQuery<int>($"""
                SELECT "IsLegacyLocal" AS "Value" FROM "Users" WHERE "Id" = {owner}
                """).SingleAsync());
        }

        await using NutriFlowDbContext reopened =
            await LocalProfileDatabase.OpenAsync(profiles.FirstPath, owner);
        Assert.Equal(createdAt, await reopened.Database.SqlQuery<string>($"""
            SELECT "CreatedAtUtc" AS "Value" FROM "Users" WHERE "Id" = {owner}
            """).SingleAsync());
    }

    [Fact]
    public async Task OpenAsync_SeparateProfilesKeepProductsGoalsAndSessionsAfterReopen()
    {
        using TemporaryProfiles profiles = new();
        Guid firstOwner = Guid.NewGuid();
        Guid secondOwner = Guid.NewGuid();
        Guid idempotencyKey = Guid.NewGuid();
        StoredMealSession firstSession = await WriteProfileAsync(
            profiles.FirstPath, firstOwner, idempotencyKey, 60.125m, 2100.125m);
        StoredMealSession secondSession = await WriteProfileAsync(
            profiles.SecondPath, secondOwner, idempotencyKey, 80.875m, 2400.875m);

        Assert.NotEqual(firstSession.Id, secondSession.Id);
        await using NutriFlowDbContext first =
            await LocalProfileDatabase.OpenAsync(profiles.FirstPath, firstOwner);
        await using NutriFlowDbContext second =
            await LocalProfileDatabase.OpenAsync(profiles.SecondPath, secondOwner);
        await AssertProfileAsync(first, firstOwner, firstSession, 60.125m, 2100.125m);
        await AssertProfileAsync(second, secondOwner, secondSession, 80.875m, 2400.875m);

        Assert.Null(await new MealSessionStore(first, firstOwner).FindAsync(secondSession.Id));
        Assert.Null(await new MealSessionStore(second, secondOwner).FindAsync(firstSession.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            new DailyDiaryStore(second, secondOwner).ConfirmSessionAsync(
                firstSession.Id, PreviewToken, [CreateEntry(60.125m)]));
        Assert.Equal(1, await OwnerCountAsync(first, firstOwner));
        Assert.Equal(0, await OwnerCountAsync(first, secondOwner));
        Assert.Equal(1, await OwnerCountAsync(second, secondOwner));
        Assert.Equal(0, await OwnerCountAsync(second, firstOwner));
    }

    [Fact]
    public async Task OpenAsync_ExplicitOwnerScopeRejectsForeignDataInSameDatabase()
    {
        using TemporaryProfiles profiles = new();
        Guid firstOwner = Guid.NewGuid();
        Guid secondOwner = Guid.NewGuid();
        StoredMealSession firstSession = await WriteProfileAsync(
            profiles.FirstPath, firstOwner, Guid.NewGuid(), 60.125m, 2100.125m);
        await using NutriFlowDbContext context =
            await LocalProfileDatabase.OpenAsync(profiles.FirstPath, secondOwner);
        DailyDiaryStore firstDiary = new(context, firstOwner);
        StoredMealEntry firstEntry = Assert.Single(await firstDiary.GetStoredEntriesAsync(MealDate));
        DailyDiaryStore secondDiary = new(context, secondOwner);

        Assert.Null(await new LocalProductCatalog(context, secondOwner).FindByBarcodeAsync("12345678"));
        Assert.Null(await secondDiary.FindGoalAsync(MealDate));
        Assert.Empty(await secondDiary.GetStoredEntriesAsync(MealDate));
        Assert.Null(await new MealSessionStore(context, secondOwner).FindAsync(firstSession.Id));
        Assert.Null(await secondDiary.FindEntryAsync(firstEntry.Id));
        Assert.Equal(MealEntryChangeKind.NotFound,
            (await secondDiary.UpdateEntryWeightAsync(firstEntry.Id, firstEntry.Revision, 200m)).Kind);
        Assert.Equal(MealEntryChangeKind.NotFound,
            (await secondDiary.DeleteEntryAsync(firstEntry.Id, firstEntry.Revision)).Kind);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            secondDiary.ConfirmSessionAsync(firstSession.Id, PreviewToken, [CreateEntry(60.125m)]));
        Assert.Equal(100m, (await firstDiary.FindEntryAsync(firstEntry.Id))!.Entry.WeightInGrams);
    }

    [Fact]
    public async Task OpenAsync_MigratesExistingDatabaseWithoutClaimingLegacyGoal()
    {
        using TemporaryProfiles profiles = new();
        Directory.CreateDirectory(Path.GetDirectoryName(profiles.FirstPath)!);
        DbContextOptions<NutriFlowDbContext> options =
            new DbContextOptionsBuilder<NutriFlowDbContext>()
                .UseSqlite($"Data Source={profiles.FirstPath};Pooling=False")
                .Options;
        await using (NutriFlowDbContext oldContext = new(options))
        {
            await oldContext.Database.MigrateAsync("20261001185045_AddMealSessionMessageReceipts");
            await oldContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "DailyGoals" ("Date", "Calories", "ProteinGrams", "FatGrams", "CarbohydratesGrams")
                VALUES ({MealDate.ToString("yyyy-MM-dd")}, {"2100.125"}, {"120.25"}, {"60.5"}, {"200.75"})
                """);
        }

        Guid owner = Guid.NewGuid();
        await using NutriFlowDbContext upgraded =
            await LocalProfileDatabase.OpenAsync(profiles.FirstPath, owner);
        Assert.Null(await new DailyDiaryStore(upgraded, owner).FindGoalAsync(MealDate));
        DailyGoal legacyGoal = Assert.IsType<DailyGoal>(
            await new DailyDiaryStore(upgraded).FindGoalAsync(MealDate));
        Assert.Equal(2100.125m, legacyGoal.TargetNutrition.Calories);
        Assert.Equal(120.25m, legacyGoal.TargetNutrition.ProteinGrams);
        Assert.Equal(60.5m, legacyGoal.TargetNutrition.FatGrams);
        Assert.Equal(200.75m, legacyGoal.TargetNutrition.CarbohydratesGrams);
        Assert.Equal(1, await upgraded.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS \"Value\" FROM \"Users\" WHERE \"IsLegacyLocal\" = 1").SingleAsync());
        Assert.Equal(1, await OwnerCountAsync(upgraded, owner));
        Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task OpenAsync_CorruptDatabaseFailurePreservesFileAndReleasesConnection()
    {
        using TemporaryProfiles profiles = new();
        Directory.CreateDirectory(Path.GetDirectoryName(profiles.FirstPath)!);
        const string original = "not a SQLite database";
        await File.WriteAllTextAsync(profiles.FirstPath, original);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            LocalProfileDatabase.OpenAsync(profiles.FirstPath, Guid.NewGuid()));

        Assert.Equal(original, await File.ReadAllTextAsync(profiles.FirstPath));
        File.Move(profiles.FirstPath, profiles.SecondPath);
        Assert.True(File.Exists(profiles.SecondPath));
    }

    private static async Task<StoredMealSession> WriteProfileAsync(
        string path, Guid owner, Guid idempotencyKey, decimal calories, decimal goalCalories)
    {
        await using NutriFlowDbContext context = await LocalProfileDatabase.OpenAsync(path, owner);
        Product product = new("Молоко", new NutritionValues(calories, 3m, 4m, 5m),
            new NutritionSource(NutritionSourceKind.ManualInput, DataQuality.Exact,
                "Личная этикетка"), "12345678");
        Assert.True(await new LocalProductCatalog(context, owner).AddAsync(product));
        DailyDiaryStore diary = new(context, owner);
        await diary.SetGoalAsync(MealDate, new DailyGoal(new NutritionValues(goalCalories, 120m, 60m, 200m)));
        MealDraft draft = new([
            new DishDraft("Молоко", [new IngredientDraft("Молоко", 100m, DataQuality.Exact, 0m,
                DataQuality.Exact)], 100m, DataQuality.Exact, [new PortionDraft(100m)])
        ], []);
        (StoredMealSession session, bool created) = await new MealSessionStore(context, owner)
            .CreateWithIdempotencyKeyAsync(["Выпил 100 г молока"], draft, "{}", PreviewToken,
                MealSessionStatus.ReadyForConfirmation, MealDate, idempotencyKey, OriginalRequestHash);
        Assert.True(created);
        Assert.Equal(MealSessionConfirmationResult.Confirmed,
            await diary.ConfirmSessionAsync(session.Id, PreviewToken, [CreateEntry(calories)]));
        return session;
    }

    private static async Task AssertProfileAsync(
        NutriFlowDbContext context, Guid owner, StoredMealSession session, decimal calories, decimal goalCalories)
    {
        Product product = Assert.IsType<Product>(
            await new LocalProductCatalog(context, owner).FindByBarcodeAsync("12345678"));
        Assert.Equal(calories, product.NutritionPer100Grams.Calories);
        DailyDiaryStore diary = new(context, owner);
        DailyGoal goal = Assert.IsType<DailyGoal>(await diary.FindGoalAsync(MealDate));
        Assert.Equal(goalCalories, goal.TargetNutrition.Calories);
        StoredMealEntry entry = Assert.Single(await diary.GetStoredEntriesAsync(MealDate));
        Assert.Equal(calories, entry.Entry.Nutrition.Calories);
        Assert.Equal(session.Id, entry.MealSessionId);
        StoredMealSession restored = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(context, owner).FindByIdempotencyKeyAsync(session.IdempotencyKey!.Value));
        Assert.Equal(session.Id, restored.Id);
        Assert.Equal(MealSessionStatus.Confirmed, restored.Status);
        Assert.Equal(MealSessionConfirmationResult.AlreadyConfirmed,
            await diary.ConfirmSessionAsync(session.Id, PreviewToken, [CreateEntry(calories)]));
        Assert.Single(await diary.GetStoredEntriesAsync(MealDate));
    }

    private static MealEntry CreateEntry(decimal calories) =>
        new("Молоко", 100m, new NutritionValues(calories, 3m, 4m, 5m), DataQuality.Exact);

    private static Task<int> OwnerCountAsync(NutriFlowDbContext context, Guid owner) =>
        context.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS "Value" FROM "Users" WHERE "Id" = {owner}
            """).SingleAsync();

    private sealed class TemporaryProfiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nutriflow-profiles-{Guid.NewGuid():N}");

        public string FirstPath => Path.Combine(_directory, "first.db");
        public string SecondPath => Path.Combine(_directory, "second.db");

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
