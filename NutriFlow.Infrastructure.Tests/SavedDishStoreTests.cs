using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class SavedDishStoreTests
{
    private const string PreviewToken =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string OtherToken =
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    [Fact]
    public async Task ConfirmSessionAsync_SavesSnapshotWithoutDiaryEntriesAndSurvivesRestart()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealSession session = await CreateSessionAsync(database);
        NutritionValues nutrition = new(123.1234567890123456789012345m, 14.25m, 5.75m, 7.125m);
        StoredSavedDish saved;

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            SavedDishStore store = new(context);
            Assert.Equal(MealSessionConfirmationResult.Confirmed, await store.ConfirmSessionAsync(
                session.Id, PreviewToken, "  Моё рагу  ", 801.125m, nutrition, DataQuality.Estimated));
            saved = Assert.IsType<StoredSavedDish>(await store.FindBySessionIdAsync(session.Id));
            Assert.NotEqual(Guid.Empty, saved.Id);
            Assert.Equal(session.Id, saved.SourceSessionId);
            Assert.Equal("Моё рагу", saved.Product.Name);
            Assert.Equal(801.125m, saved.FinalWeightInGrams);
            AssertNutrition(nutrition, saved.Product.NutritionPer100Grams);
            Assert.Equal(NutritionSourceKind.SavedDish, saved.Product.Source.Kind);
            Assert.Equal(DataQuality.Estimated, saved.Product.Source.Quality);
            Assert.Equal("Saved NutriFlow dish", saved.Product.Source.Name);
            Assert.Equal($"saved-dish:{saved.Id:N}", saved.Product.Source.Reference);
            Assert.Null(saved.Product.Barcode);
            Assert.Empty(await new DailyDiaryStore(context).GetSessionEntriesAsync(session.Id));
            Assert.Equal(0, await CountAsync(context, "Products"));
        }

        await using NutriFlowDbContext restarted = database.CreateContext();
        StoredSavedDish restored = Assert.IsType<StoredSavedDish>(
            await new SavedDishStore(restarted).FindAsync(saved.Id));
        Assert.Equal(saved.Id, restored.Id);
        Assert.Equal(saved.CreatedAtUtc, restored.CreatedAtUtc);
        AssertNutrition(nutrition, restored.Product.NutritionPer100Grams);
        StoredMealSession confirmed = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(restarted).FindAsync(session.Id));
        Assert.Equal(MealSessionStatus.Confirmed, confirmed.Status);
        Assert.Equal(MealSessionPurpose.CreateDish, confirmed.Purpose);
        Assert.Equal(saved.CreatedAtUtc, confirmed.ConfirmedAtUtc);
        Assert.Equal(confirmed.ConfirmedAtUtc, confirmed.UpdatedAtUtc);
        Assert.Equal(session.Messages, confirmed.Messages);
        Assert.Equal(session.PreviewJson, confirmed.PreviewJson);
        Assert.Equal(session.PreviewToken, confirmed.PreviewToken);
        Assert.Equal(session.Draft.Dishes.Single().Name, confirmed.Draft.Dishes.Single().Name);
    }

    [Fact]
    public async Task ConfirmSessionAsync_RepeatedConfirmationKeepsOriginalDish()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealSession session = await CreateSessionAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        SavedDishStore store = new(context);

        Assert.Equal(MealSessionConfirmationResult.Confirmed, await ConfirmAsync(store, session.Id));
        StoredSavedDish first = Assert.IsType<StoredSavedDish>(await store.FindBySessionIdAsync(session.Id));
        Assert.Equal(MealSessionConfirmationResult.AlreadyConfirmed, await ConfirmAsync(
            store, session.Id, "Не должно заменить рагу", OtherToken));
        StoredSavedDish repeated = Assert.IsType<StoredSavedDish>(await store.FindBySessionIdAsync(session.Id));

        Assert.Equal(first.Id, repeated.Id);
        Assert.Equal(first.CreatedAtUtc, repeated.CreatedAtUtc);
        Assert.Equal(first.Product.Name, repeated.Product.Name);
        Assert.Equal(1, await CountAsync(context, "SavedDishes"));
        Assert.Equal(0, await CountAsync(context, "MealEntries"));
    }

    [Theory]
    [InlineData(MealSessionStatus.NeedsProducts, MealSessionPurpose.CreateDish, PreviewToken, MealSessionConfirmationResult.NotReady)]
    [InlineData(MealSessionStatus.NeedsClarification, MealSessionPurpose.CreateDish, PreviewToken, MealSessionConfirmationResult.NotReady)]
    [InlineData(MealSessionStatus.ReadyForConfirmation, MealSessionPurpose.CreateDish, OtherToken, MealSessionConfirmationResult.StalePreview)]
    [InlineData(MealSessionStatus.ReadyForConfirmation, MealSessionPurpose.Diary, PreviewToken, MealSessionConfirmationResult.NotReady)]
    public async Task ConfirmSessionAsync_NotReadyStaleOrWrongPurpose_DoesNotWrite(
        MealSessionStatus status,
        MealSessionPurpose purpose,
        string token,
        MealSessionConfirmationResult expected)
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealSession session = await CreateSessionAsync(database, status: status, purpose: purpose);
        await using NutriFlowDbContext context = database.CreateContext();

        Assert.Equal(expected, await ConfirmAsync(new SavedDishStore(context), session.Id, token: token));
        StoredMealSession unchanged = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(context).FindAsync(session.Id));
        Assert.Equal(status, unchanged.Status);
        Assert.Equal(session.UpdatedAtUtc, unchanged.UpdatedAtUtc);
        Assert.Null(unchanged.ConfirmedAtUtc);
        Assert.Equal(0, await CountAsync(context, "SavedDishes"));
        Assert.Equal(0, await CountAsync(context, "MealEntries"));
    }

    [Fact]
    public async Task ConfirmSessionAsync_ConfirmedDiarySession_IsNotReady()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealSession session = await CreateSessionAsync(database, purpose: MealSessionPurpose.Diary);
        await using NutriFlowDbContext context = database.CreateContext();
        await new DailyDiaryStore(context).ConfirmSessionAsync(
            session.Id, PreviewToken, [MealSessionStoreTests.CreateEntry("Обед", 100m, 100m)]);

        Assert.Equal(MealSessionConfirmationResult.NotReady,
            await ConfirmAsync(new SavedDishStore(context), session.Id));
        Assert.Equal(0, await CountAsync(context, "SavedDishes"));
        Assert.Equal(1, await CountAsync(context, "MealEntries"));
    }

    [Fact]
    public async Task Owners_CannotReadOrConfirmEachOthersDishesAndCanReuseNames()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        Guid secondOwner;
        await using (NutriFlowDbContext context = database.CreateContext())
        {
            secondOwner = await AddOwnerAsync(context);
        }

        StoredMealSession first = await CreateSessionAsync(database);
        StoredMealSession second = await CreateSessionAsync(database, ownerId: secondOwner);
        await using NutriFlowDbContext readContext = database.CreateContext();
        SavedDishStore firstStore = new(readContext);
        SavedDishStore secondStore = new(readContext, secondOwner);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => ConfirmAsync(firstStore, second.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => ConfirmAsync(secondStore, first.Id));
        Assert.Equal(MealSessionConfirmationResult.Confirmed, await ConfirmAsync(firstStore, first.Id));
        Assert.Equal(MealSessionConfirmationResult.Confirmed, await ConfirmAsync(secondStore, second.Id));
        StoredSavedDish firstDish = Assert.IsType<StoredSavedDish>(await firstStore.FindBySessionIdAsync(first.Id));
        StoredSavedDish secondDish = Assert.IsType<StoredSavedDish>(await secondStore.FindBySessionIdAsync(second.Id));

        Assert.Null(await firstStore.FindAsync(secondDish.Id));
        Assert.Null(await secondStore.FindAsync(firstDish.Id));
        Assert.Null(await firstStore.FindBySessionIdAsync(second.Id));
        Assert.Null(await secondStore.FindBySessionIdAsync(first.Id));
        Assert.Equal(firstDish.Id, Assert.IsType<StoredSavedDish>(await firstStore.FindByNameAsync("рагу")).Id);
        Assert.Equal(secondDish.Id, Assert.IsType<StoredSavedDish>(await secondStore.FindByNameAsync("рагу")).Id);
        Assert.Equal(firstDish.Id, Assert.Single(await firstStore.ListAsync()).Id);
        Assert.Equal(secondDish.Id, Assert.Single(await secondStore.ListAsync()).Id);
        Assert.Equal(["Рагу"], await firstStore.GetNamesAsync());
        Assert.Equal(["Рагу"], await secondStore.GetNamesAsync());
    }

    [Fact]
    public async Task FindByNameAsync_NormalizesUnicodeWhitespaceAndCase()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealSession session = await CreateSessionAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        SavedDishStore store = new(context);
        await ConfirmAsync(store, session.Id, "  Café  ");

        Assert.Equal("Café", Assert.IsType<StoredSavedDish>(await store.FindByNameAsync("  CAFE\u0301  ")).Product.Name);
        Assert.Null(await store.FindByNameAsync("Каша"));
        Assert.Null(await store.FindAsync(Guid.NewGuid()));
        Assert.Null(await store.FindBySessionIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlySavedDishesInNameOrder()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealSession first = await CreateSessionAsync(database);
        StoredMealSession second = await CreateSessionAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        SavedDishStore store = new(context);
        await ConfirmAsync(store, first.Id, "Рагу");
        await ConfirmAsync(store, second.Id, "Каша");

        Assert.Equal(["Каша", "Рагу"], await store.GetNamesAsync());
        Assert.Equal(["Каша", "Рагу"], (await store.ListAsync()).Select(dish => dish.Product.Name));
    }

    [Fact]
    public async Task ConfirmSessionAsync_NameConflictRollsBackClaimAndAllowsRetryWithAnotherName()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealSession first = await CreateSessionAsync(database);
        StoredMealSession second = await CreateSessionAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        SavedDishStore store = new(context);
        await ConfirmAsync(store, first.Id);

        MealSessionConflictException exception = await Assert.ThrowsAsync<MealSessionConflictException>(() =>
            ConfirmAsync(store, second.Id, "  рАГУ  "));
        Assert.Contains("another name", exception.Message);
        StoredMealSession unchanged = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(context).FindAsync(second.Id));
        Assert.Equal(MealSessionStatus.ReadyForConfirmation, unchanged.Status);
        Assert.Null(unchanged.ConfirmedAtUtc);
        Assert.Equal(second.UpdatedAtUtc, unchanged.UpdatedAtUtc);
        Assert.Equal(1, await CountAsync(context, "SavedDishes"));
        Assert.DoesNotContain(context.ChangeTracker.Entries(), entry => entry.State == EntityState.Added);

        Assert.Equal(MealSessionConfirmationResult.Confirmed, await ConfirmAsync(store, second.Id, "Другое рагу"));
        Assert.Equal(2, await CountAsync(context, "SavedDishes"));
    }

    [Fact]
    public async Task ConfirmSessionAsync_InsertFailureRollsBackSessionClaim()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealSession session = await CreateSessionAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER "RejectSavedDish" BEFORE INSERT ON "SavedDishes"
            BEGIN SELECT RAISE(ABORT, 'saved dish insert rejected'); END;
            """);

        await Assert.ThrowsAsync<DbUpdateException>(() => ConfirmAsync(new SavedDishStore(context), session.Id));
        StoredMealSession unchanged = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(context).FindAsync(session.Id));
        Assert.Equal(MealSessionStatus.ReadyForConfirmation, unchanged.Status);
        Assert.Null(unchanged.ConfirmedAtUtc);
        Assert.Equal(session.UpdatedAtUtc, unchanged.UpdatedAtUtc);
        Assert.Equal(0, await CountAsync(context, "SavedDishes"));
        Assert.DoesNotContain(context.ChangeTracker.Entries(), entry => entry.State == EntityState.Added);
    }

    [Fact]
    public async Task ConfirmSessionAsync_ConcurrentSameSession_CreatesOneDish()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealSession session = await CreateSessionAsync(database);
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<MealSessionConfirmationResult> first = ConfirmAfterSignalAsync(database, session.Id, start.Task);
        Task<MealSessionConfirmationResult> second = ConfirmAfterSignalAsync(database, session.Id, start.Task);
        start.SetResult();
        MealSessionConfirmationResult[] results = await Task.WhenAll(first, second);

        Assert.Contains(MealSessionConfirmationResult.Confirmed, results);
        Assert.Contains(MealSessionConfirmationResult.AlreadyConfirmed, results);
        await using NutriFlowDbContext context = database.CreateContext();
        Assert.Equal(1, await CountAsync(context, "SavedDishes"));
        Assert.Equal(0, await CountAsync(context, "MealEntries"));
    }

    [Fact]
    public async Task ConfirmSessionAsync_ConcurrentSameNameDifferentSessions_KeepsLosingSessionEditable()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealSession firstSession = await CreateSessionAsync(database);
        StoredMealSession secondSession = await CreateSessionAsync(database);
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> first = ConfirmNameAfterSignalAsync(database, firstSession.Id, start.Task);
        Task<bool> second = ConfirmNameAfterSignalAsync(database, secondSession.Id, start.Task);
        start.SetResult();
        bool[] results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
        await using NutriFlowDbContext context = database.CreateContext();
        Assert.Equal(1, await CountAsync(context, "SavedDishes"));
        MealSessionStore sessions = new(context);
        StoredMealSession firstRestored = Assert.IsType<StoredMealSession>(await sessions.FindAsync(firstSession.Id));
        StoredMealSession secondRestored = Assert.IsType<StoredMealSession>(await sessions.FindAsync(secondSession.Id));
        Assert.Contains(MealSessionStatus.Confirmed, new[] { firstRestored.Status, secondRestored.Status });
        Assert.Contains(MealSessionStatus.ReadyForConfirmation, new[] { firstRestored.Status, secondRestored.Status });
    }

    [Fact]
    public async Task Schema_EnforcesUniqueSourceSessionAndProtectsSourceFromDeletion()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealSession session = await CreateSessionAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        await ConfirmAsync(new SavedDishStore(context), session.Id);
        Guid duplicateId = Guid.NewGuid();

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "SavedDishes"
                    ("Id", "UserId", "SourceSessionId", "Name", "NormalizedName", "FinalWeightInGrams",
                     "Calories", "ProteinGrams", "FatGrams", "CarbohydratesGrams", "Quality", "CreatedAtUtc")
                SELECT {duplicateId}, "UserId", "SourceSessionId", {"Другое"}, {"ДРУГОЕ"}, "FinalWeightInGrams",
                    "Calories", "ProteinGrams", "FatGrams", "CarbohydratesGrams", "Quality", "CreatedAtUtc"
                FROM "SavedDishes" WHERE "SourceSessionId" = {session.Id}
                """));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM "MealSessions" WHERE "Id" = {session.Id}
                """));
        Assert.Equal(1, await CountAsync(context, "SavedDishes"));
        Assert.NotNull(await new MealSessionStore(context).FindAsync(session.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ConfirmSessionAsync_NonpositiveFinalWeight_DoesNotWrite(int weight)
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealSession session = await CreateSessionAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new SavedDishStore(context).ConfirmSessionAsync(
            session.Id, PreviewToken, "Рагу", weight, CreateNutrition(), DataQuality.Exact));
        Assert.Equal(0, await CountAsync(context, "SavedDishes"));
        Assert.Equal(MealSessionStatus.ReadyForConfirmation,
            Assert.IsType<StoredMealSession>(await new MealSessionStore(context).FindAsync(session.Id)).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ConfirmSessionAsync_InvalidName_DoesNotWrite(string? name)
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealSession session = await CreateSessionAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => new SavedDishStore(context).ConfirmSessionAsync(
            session.Id, PreviewToken, name!, 800m, CreateNutrition(), DataQuality.Exact));
        Assert.Equal(0, await CountAsync(context, "SavedDishes"));
    }

    [Fact]
    public async Task ConfirmSessionAsync_InvalidQualityOrNutrition_DoesNotClaimSession()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealSession session = await CreateSessionAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        SavedDishStore store = new(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ConfirmSessionAsync(
            session.Id, PreviewToken, "Рагу", 800m, CreateNutrition(), (DataQuality)999));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ConfirmSessionAsync(
            session.Id, PreviewToken, "Рагу", 800m, new NutritionValues(1001m, 0m, 0m, 0m), DataQuality.Exact));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.ConfirmSessionAsync(
            session.Id, PreviewToken, "Рагу", 800m, null!, DataQuality.Exact));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ConfirmSessionAsync(
            session.Id, PreviewToken, new string('a', 201), 800m, CreateNutrition(), DataQuality.Exact));
        Assert.Equal(0, await CountAsync(context, "SavedDishes"));
        Assert.Null(Assert.IsType<StoredMealSession>(await new MealSessionStore(context).FindAsync(session.Id)).ConfirmedAtUtc);
    }

    [Fact]
    public async Task ConfirmSessionAsync_MissingSessionThrowsAndCancelledRequestDoesNotWrite()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealSession session = await CreateSessionAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        SavedDishStore store = new(context);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => ConfirmAsync(store, Guid.NewGuid()));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ConfirmSessionAsync(
            session.Id, PreviewToken, "Рагу", 800m, CreateNutrition(), DataQuality.Exact, cancellation.Token));
        Assert.Equal(0, await CountAsync(context, "SavedDishes"));
        Assert.Null(Assert.IsType<StoredMealSession>(await new MealSessionStore(context).FindAsync(session.Id)).ConfirmedAtUtc);
    }

    [Fact]
    public async Task Constructor_RejectsNullContextAndEmptyOwner()
    {
        Assert.Throws<ArgumentNullException>(() => new SavedDishStore(null!));
        Assert.Throws<ArgumentNullException>(() => new SavedDishStore(null!, Guid.NewGuid()));
        await using TestDatabase database = new();
        await using NutriFlowDbContext context = database.CreateContext();
        Assert.Throws<ArgumentException>(() => new SavedDishStore(context, Guid.Empty));
    }

    private static Task<MealSessionConfirmationResult> ConfirmAsync(
        SavedDishStore store,
        Guid sessionId,
        string name = "Рагу",
        string token = PreviewToken)
    {
        return store.ConfirmSessionAsync(sessionId, token, name, 800m, CreateNutrition(), DataQuality.Verified);
    }

    private static Task<MealSessionConfirmationResult> ConfirmAfterSignalAsync(
        TestDatabase database,
        Guid sessionId,
        Task signal)
    {
        return Task.Run(async () =>
        {
            await signal;
            await using NutriFlowDbContext context = database.CreateContext();
            return await ConfirmAsync(new SavedDishStore(context), sessionId);
        });
    }

    private static Task<bool> ConfirmNameAfterSignalAsync(TestDatabase database, Guid sessionId, Task signal)
    {
        return Task.Run(async () =>
        {
            await signal;
            await using NutriFlowDbContext context = database.CreateContext();
            try
            {
                await ConfirmAsync(new SavedDishStore(context), sessionId);
                return true;
            }
            catch (MealSessionConflictException)
            {
                return false;
            }
        });
    }

    private static async Task<StoredMealSession> CreateSessionAsync(
        TestDatabase database,
        Guid? ownerId = null,
        MealSessionStatus status = MealSessionStatus.ReadyForConfirmation,
        MealSessionPurpose purpose = MealSessionPurpose.CreateDish)
    {
        await using NutriFlowDbContext context = database.CreateContext();
        MealSessionStore sessions = ownerId.HasValue
            ? new MealSessionStore(context, ownerId.Value)
            : new MealSessionStore(context);

        return await sessions.CreateAsync(
            ["Готовлю своё рагу, готовая масса 800 г"],
            MealSessionStoreTests.CreateReadyDraft(),
            """{"dishes":[{"name":"Рагу"}]}""",
            PreviewToken,
            status,
            new DateOnly(2026, 10, 5),
            purpose: purpose);
    }

    private static async Task<Guid> AddOwnerAsync(NutriFlowDbContext context)
    {
        Guid owner = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Users" ("Id", "CreatedAtUtc", "IsLegacyLocal")
            VALUES ({owner}, {DateTimeOffset.UtcNow}, {false})
            """);
        return owner;
    }

    private static NutritionValues CreateNutrition() => new(150.125m, 10.25m, 8.5m, 9.75m);

    private static Task<int> CountAsync(NutriFlowDbContext context, string table)
    {
        string query = table switch
        {
            "SavedDishes" => "SELECT COUNT(*) AS Value FROM \"SavedDishes\"",
            "Products" => "SELECT COUNT(*) AS Value FROM \"Products\"",
            "MealEntries" => "SELECT COUNT(*) AS Value FROM \"MealEntries\"",
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        return context.Database.SqlQueryRaw<int>(query).SingleAsync();
    }

    private static void AssertNutrition(NutritionValues expected, NutritionValues actual)
    {
        Assert.Equal(expected.Calories, actual.Calories);
        Assert.Equal(expected.ProteinGrams, actual.ProteinGrams);
        Assert.Equal(expected.FatGrams, actual.FatGrams);
        Assert.Equal(expected.CarbohydratesGrams, actual.CarbohydratesGrams);
    }
}
