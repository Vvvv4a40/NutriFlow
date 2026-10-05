using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class MealEntryAdjustmentTests
{
    private const string PreviewToken =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly DateOnly MealDate = new(2026, 10, 5);

    [Fact]
    public async Task UpdateEntryWeightAsync_PersistsAdjustmentWithoutChangingOriginalSnapshots()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        MealEntry original = new("Рагу", 120.125m,
            new NutritionValues(240.1234567890123456789012345m, 20.25m, 8.75m, 12.125m), DataQuality.Verified);
        StoredMealEntry stored = await CreateEntryAsync(database, original);
        string entrySnapshot;
        string sessionSnapshot;

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            entrySnapshot = await EntrySnapshotAsync(context, stored.Id);
            sessionSnapshot = await SessionSnapshotAsync(context, stored.MealSessionId);
            MealEntryChangeResult result = await new DailyDiaryStore(context).UpdateEntryWeightAsync(
                stored.Id, 0, 80.0625m, DataQuality.Estimated);
            Assert.Equal(MealEntryChangeKind.Updated, result.Kind);
            StoredMealEntry adjusted = Assert.IsType<StoredMealEntry>(result.Entry);
            Assert.Equal(stored.Id, adjusted.Id);
            Assert.Equal(stored.MealSessionId, adjusted.MealSessionId);
            Assert.Equal(stored.MealDate, adjusted.MealDate);
            Assert.Equal(1, adjusted.Revision);
            AssertEntry(original.WithWeight(80.0625m, DataQuality.Estimated), adjusted.Entry);
            Assert.Equal(entrySnapshot, await EntrySnapshotAsync(context, stored.Id));
            Assert.Equal(sessionSnapshot, await SessionSnapshotAsync(context, stored.MealSessionId));
        }

        await using NutriFlowDbContext restarted = database.CreateContext();
        DailyDiaryStore store = new(restarted);
        StoredMealEntry restored = Assert.IsType<StoredMealEntry>(await store.FindEntryAsync(stored.Id));
        Assert.Equal(1, restored.Revision);
        AssertEntry(original.WithWeight(80.0625m, DataQuality.Estimated), restored.Entry);
        AssertEntry(restored.Entry, Assert.Single(await store.GetEntriesAsync(MealDate)));
        AssertEntry(restored.Entry, Assert.Single(await store.GetSessionEntriesAsync(stored.MealSessionId)));
        Assert.Equal(restored.Id, Assert.Single(await store.GetStoredEntriesAsync(MealDate)).Id);
        Assert.Equal(restored.Id, Assert.Single(await store.GetStoredSessionEntriesAsync(stored.MealSessionId)).Id);
        Assert.Equal(entrySnapshot, await EntrySnapshotAsync(restarted, stored.Id));
        Assert.Equal(sessionSnapshot, await SessionSnapshotAsync(restarted, stored.MealSessionId));
    }

    [Fact]
    public async Task UpdateEntryWeightAsync_AlwaysScalesOriginalAndDoesNotAccumulateRounding()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        MealEntry original = new("Порция", 3m, new NutritionValues(1m, 1m, 1m, 1m), DataQuality.Exact);
        StoredMealEntry entry = await CreateEntryAsync(database, original);
        await using NutriFlowDbContext context = database.CreateContext();
        DailyDiaryStore store = new(context);
        MealEntryChangeResult first = await store.UpdateEntryWeightAsync(entry.Id, 0, 2m, DataQuality.Estimated);
        MealEntryChangeResult second = await store.UpdateEntryWeightAsync(entry.Id, 1, 1m);
        MealEntryChangeResult third = await store.UpdateEntryWeightAsync(entry.Id, 2, 3m);

        AssertEntry(original.WithWeight(2m, DataQuality.Estimated), first.Entry!.Entry);
        AssertEntry(original.WithWeight(1m), second.Entry!.Entry);
        AssertEntry(original, third.Entry!.Entry);
        Assert.Equal(3, third.Entry.Revision);
        AssertEntry(original, Assert.IsType<StoredMealEntry>(await store.FindEntryAsync(entry.Id)).Entry);
    }

    [Theory]
    [InlineData(DataQuality.Unknown, DataQuality.Exact, DataQuality.Unknown)]
    [InlineData(DataQuality.Unknown, DataQuality.Estimated, DataQuality.Unknown)]
    [InlineData(DataQuality.Estimated, DataQuality.Exact, DataQuality.Estimated)]
    [InlineData(DataQuality.Estimated, DataQuality.Estimated, DataQuality.Estimated)]
    [InlineData(DataQuality.Verified, DataQuality.Exact, DataQuality.Verified)]
    [InlineData(DataQuality.Verified, DataQuality.Estimated, DataQuality.Estimated)]
    [InlineData(DataQuality.Exact, DataQuality.Exact, DataQuality.Exact)]
    [InlineData(DataQuality.Exact, DataQuality.Estimated, DataQuality.Estimated)]
    public async Task UpdateEntryWeightAsync_PreservesConservativeQuality(
        DataQuality originalQuality, DataQuality weightQuality, DataQuality expectedQuality)
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database, CreateMealEntry(originalQuality));
        await using NutriFlowDbContext context = database.CreateContext();
        DailyDiaryStore store = new(context);
        MealEntryChangeResult result = await store.UpdateEntryWeightAsync(entry.Id, 0, 80m, weightQuality);

        Assert.Equal(expectedQuality, result.Entry!.Entry.Quality);
        Assert.Equal(expectedQuality, Assert.IsType<StoredMealEntry>(await store.FindEntryAsync(entry.Id)).Entry.Quality);
    }

    [Fact]
    public async Task StaleUpdateAndDeletion_ReturnCurrentRevisionWithoutMutation()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        DailyDiaryStore store = new(context);
        Assert.Equal(0, entry.Revision);
        await store.UpdateEntryWeightAsync(entry.Id, 0, 75m);
        string snapshot = await AdjustmentSnapshotAsync(context, entry.Id);

        MealEntryChangeResult update = await store.UpdateEntryWeightAsync(entry.Id, 0, 50m);
        MealEntryChangeResult deletion = await store.DeleteEntryAsync(entry.Id, 0);

        Assert.Equal(MealEntryChangeKind.RevisionConflict, update.Kind);
        Assert.Equal(MealEntryChangeKind.RevisionConflict, deletion.Kind);
        Assert.Equal(1, update.Entry!.Revision);
        Assert.Equal(75m, update.Entry.Entry.WeightInGrams);
        AssertEntry(update.Entry.Entry, deletion.Entry!.Entry);
        Assert.Equal(snapshot, await AdjustmentSnapshotAsync(context, entry.Id));
    }

    [Fact]
    public async Task DeleteEntryAsync_IsSoftIdempotentAndConfirmationDoesNotResurrectEntry()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database);
        string originalSnapshot;
        string tombstoneSnapshot;

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            DailyDiaryStore store = new(context);
            originalSnapshot = await EntrySnapshotAsync(context, entry.Id);
            MealEntryChangeResult deleted = await store.DeleteEntryAsync(entry.Id, 0);
            Assert.Equal(MealEntryChangeKind.Deleted, deleted.Kind);
            Assert.Null(deleted.Entry);
            tombstoneSnapshot = await AdjustmentSnapshotAsync(context, entry.Id);
            Assert.Equal(MealEntryChangeKind.Deleted, (await store.DeleteEntryAsync(entry.Id, 0)).Kind);
            Assert.Equal(MealEntryChangeKind.Deleted, (await store.DeleteEntryAsync(entry.Id, 99)).Kind);
            Assert.Equal(MealEntryChangeKind.NotFound, (await store.UpdateEntryWeightAsync(entry.Id, 1, 90m)).Kind);
            Assert.Null(await store.FindEntryAsync(entry.Id));
            Assert.Empty(await store.GetEntriesAsync(MealDate));
            Assert.Empty(await store.GetStoredEntriesAsync(MealDate));
            Assert.Empty(await store.GetSessionEntriesAsync(entry.MealSessionId));
            Assert.Empty(await store.GetStoredSessionEntriesAsync(entry.MealSessionId));
            Assert.Equal(MealSessionConfirmationResult.AlreadyConfirmed,
                await store.ConfirmSessionAsync(entry.MealSessionId, PreviewToken, [CreateMealEntry()]));
            Assert.Empty(await store.GetSessionEntriesAsync(entry.MealSessionId));
            Assert.Equal(originalSnapshot, await EntrySnapshotAsync(context, entry.Id));
            Assert.Equal(tombstoneSnapshot, await AdjustmentSnapshotAsync(context, entry.Id));
        }

        await using NutriFlowDbContext restarted = database.CreateContext();
        Assert.Null(await new DailyDiaryStore(restarted).FindEntryAsync(entry.Id));
        Assert.Equal(originalSnapshot, await EntrySnapshotAsync(restarted, entry.Id));
        Assert.Equal(tombstoneSnapshot, await AdjustmentSnapshotAsync(restarted, entry.Id));
        Assert.Equal(1, await EntryCountAsync(restarted));
        Assert.Equal(1, await AdjustmentCountAsync(restarted));
    }

    [Fact]
    public async Task DeleteEntryAsync_AfterWeightCorrection_PreservesAdjustmentAndOriginal()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        DailyDiaryStore store = new(context);
        await store.UpdateEntryWeightAsync(entry.Id, 0, 80.125m, DataQuality.Estimated);
        await store.DeleteEntryAsync(entry.Id, 1);

        Assert.Equal(2, await context.Database.SqlQuery<int>($"""
            SELECT Revision AS Value FROM MealEntryAdjustments WHERE MealEntryId = {entry.Id}
            """).SingleAsync());
        Assert.Equal("80.125", await context.Database.SqlQuery<string>($"""
            SELECT WeightInGrams AS Value FROM MealEntryAdjustments WHERE MealEntryId = {entry.Id}
            """).SingleAsync());
        Assert.Equal((int)DataQuality.Estimated, await context.Database.SqlQuery<int>($"""
            SELECT WeightQuality AS Value FROM MealEntryAdjustments WHERE MealEntryId = {entry.Id}
            """).SingleAsync());
        Assert.Null(await store.FindEntryAsync(entry.Id));
    }

    [Fact]
    public async Task Owners_CannotReadUpdateOrDeleteForeignEntries()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        Guid foreignOwner = Guid.NewGuid();
        await using (NutriFlowDbContext context = database.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Users (Id, CreatedAtUtc, IsLegacyLocal)
                VALUES ({foreignOwner}, {DateTimeOffset.UtcNow}, 0)
                """);
        }

        StoredMealEntry local = await CreateEntryAsync(database);
        StoredMealEntry foreign = await CreateEntryAsync(database, ownerId: foreignOwner);
        await using NutriFlowDbContext read = database.CreateContext();
        DailyDiaryStore localStore = new(read);
        DailyDiaryStore foreignStore = new(read, foreignOwner);

        Assert.Null(await localStore.FindEntryAsync(foreign.Id));
        Assert.Null(await foreignStore.FindEntryAsync(local.Id));
        Assert.Equal(MealEntryChangeKind.NotFound, (await localStore.UpdateEntryWeightAsync(foreign.Id, 0, 50m)).Kind);
        Assert.Equal(MealEntryChangeKind.NotFound, (await foreignStore.UpdateEntryWeightAsync(local.Id, 0, 50m)).Kind);
        Assert.Equal(MealEntryChangeKind.NotFound, (await localStore.DeleteEntryAsync(foreign.Id, 0)).Kind);
        Assert.Equal(MealEntryChangeKind.NotFound, (await foreignStore.DeleteEntryAsync(local.Id, 0)).Kind);
        Assert.Equal(local.Id, Assert.Single(await localStore.GetStoredEntriesAsync(MealDate)).Id);
        Assert.Equal(foreign.Id, Assert.Single(await foreignStore.GetStoredEntriesAsync(MealDate)).Id);
        Assert.Empty(await localStore.GetStoredSessionEntriesAsync(foreign.MealSessionId));
        Assert.Empty(await foreignStore.GetStoredSessionEntriesAsync(local.MealSessionId));
        Assert.Equal(0, await AdjustmentCountAsync(read));
    }

    [Theory]
    [InlineData(MealSessionStatus.ReadyForConfirmation, MealSessionPurpose.Diary)]
    [InlineData(MealSessionStatus.NeedsProducts, MealSessionPurpose.Diary)]
    [InlineData(MealSessionStatus.Confirmed, MealSessionPurpose.CreateDish)]
    public async Task EntriesOutsideConfirmedDiary_AreNotReadableOrMutable(
        MealSessionStatus status, MealSessionPurpose purpose)
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE MealSessions SET Status = {(int)status}, Purpose = {(int)purpose}
            WHERE Id = {entry.MealSessionId}
            """);
        DailyDiaryStore store = new(context);

        Assert.Null(await store.FindEntryAsync(entry.Id));
        Assert.Empty(await store.GetStoredEntriesAsync(MealDate));
        Assert.Empty(await store.GetStoredSessionEntriesAsync(entry.MealSessionId));
        Assert.Equal(MealEntryChangeKind.NotFound, (await store.UpdateEntryWeightAsync(entry.Id, 0, 50m)).Kind);
        Assert.Equal(MealEntryChangeKind.NotFound, (await store.DeleteEntryAsync(entry.Id, 0)).Kind);
        Assert.Equal(0, await AdjustmentCountAsync(context));
    }

    [Fact]
    public async Task MissingEntry_IsNotFoundAndDoesNotWrite()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        DailyDiaryStore store = new(context);

        Assert.Null(await store.FindEntryAsync(99));
        Assert.Equal(MealEntryChangeKind.NotFound, (await store.UpdateEntryWeightAsync(99, 0, 50m)).Kind);
        Assert.Equal(MealEntryChangeKind.NotFound, (await store.DeleteEntryAsync(99, 0)).Kind);
        Assert.Equal(0, await AdjustmentCountAsync(context));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidWeight_IsRejectedBeforeWriting(int weight)
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        DailyDiaryStore store = new(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.UpdateEntryWeightAsync(entry.Id, 0, weight));
        Assert.Equal(0, await AdjustmentCountAsync(context));
    }

    [Theory]
    [InlineData(DataQuality.Unknown)]
    [InlineData(DataQuality.Verified)]
    [InlineData((DataQuality)99)]
    public async Task InvalidWeightQuality_IsRejectedBeforeWriting(DataQuality quality)
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        DailyDiaryStore store = new(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.UpdateEntryWeightAsync(entry.Id, 0, 50m, quality));
        Assert.Equal(0, await AdjustmentCountAsync(context));
    }

    [Fact]
    public async Task NegativeRevision_IsRejectedBeforeWriting()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        DailyDiaryStore store = new(context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.UpdateEntryWeightAsync(entry.Id, -1, 50m));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.DeleteEntryAsync(entry.Id, -1));
        Assert.Equal(0, await AdjustmentCountAsync(context));
    }

    [Fact]
    public async Task ProposedWeightOverflow_IsAnArgumentErrorAndDoesNotWrite()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database,
            new MealEntry("Большая порция", 100m, new NutritionValues(decimal.MaxValue, 1m, 1m, 1m)));
        await using NutriFlowDbContext context = database.CreateContext();
        string original = await EntrySnapshotAsync(context, entry.Id);

        ArgumentOutOfRangeException exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new DailyDiaryStore(context).UpdateEntryWeightAsync(entry.Id, 0, 200m));
        Assert.Equal("weightInGrams", exception.ParamName);
        Assert.Equal(0, await AdjustmentCountAsync(context));
        Assert.Equal(original, await EntrySnapshotAsync(context, entry.Id));
    }

    [Theory]
    [InlineData("original_weight")]
    [InlineData("original_nutrition")]
    [InlineData("original_quality")]
    [InlineData("adjustment_weight")]
    [InlineData("adjustment_quality")]
    [InlineData("adjustment_revision")]
    [InlineData("adjustment_overflow")]
    public async Task CorruptStoredEntry_IsInvalidDataRatherThanAUserArgumentError(string corruption)
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        DailyDiaryStore store = new(context);
        if (corruption.StartsWith("adjustment", StringComparison.Ordinal))
        {
            await store.UpdateEntryWeightAsync(entry.Id, 0, 80m);
        }

        string update = corruption switch
        {
            "original_weight" => "UPDATE MealEntries SET WeightInGrams = '0' WHERE Id = {0}",
            "original_nutrition" => "UPDATE MealEntries SET Calories = '-1' WHERE Id = {0}",
            "original_quality" => "UPDATE MealEntries SET Quality = 99 WHERE Id = {0}",
            "adjustment_weight" => "UPDATE MealEntryAdjustments SET WeightInGrams = '0' WHERE MealEntryId = {0}",
            "adjustment_quality" => "UPDATE MealEntryAdjustments SET WeightQuality = 2 WHERE MealEntryId = {0}",
            "adjustment_revision" => "UPDATE MealEntryAdjustments SET Revision = 0 WHERE MealEntryId = {0}",
            "adjustment_overflow" => "UPDATE MealEntryAdjustments SET WeightInGrams = '79228162514264337593543950335' WHERE MealEntryId = {0}",
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        await context.Database.ExecuteSqlRawAsync(update, entry.Id);
        string original = await EntrySnapshotAsync(context, entry.Id);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.FindEntryAsync(entry.Id));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetStoredEntriesAsync(MealDate));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateEntryWeightAsync(
            entry.Id, corruption.StartsWith("adjustment", StringComparison.Ordinal) ? 1 : 0, 50m));
        Assert.Equal(original, await EntrySnapshotAsync(context, entry.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentUpdates_ClaimOneRevision(bool alreadyAdjusted)
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database);
        int revision = 0;
        if (alreadyAdjusted)
        {
            await using NutriFlowDbContext setup = database.CreateContext();
            await new DailyDiaryStore(setup).UpdateEntryWeightAsync(entry.Id, 0, 90m);
            revision = 1;
        }

        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<MealEntryChangeResult> first = ChangeAfterSignalAsync(database, entry.Id, revision, false, start.Task, 75m);
        Task<MealEntryChangeResult> second = ChangeAfterSignalAsync(database, entry.Id, revision, false, start.Task, 50m);
        start.SetResult();
        MealEntryChangeResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Single(results, result => result.Kind == MealEntryChangeKind.Updated);
        Assert.Single(results, result => result.Kind == MealEntryChangeKind.RevisionConflict);
        await using NutriFlowDbContext read = database.CreateContext();
        StoredMealEntry current = Assert.IsType<StoredMealEntry>(await new DailyDiaryStore(read).FindEntryAsync(entry.Id));
        Assert.Equal(revision + 1, current.Revision);
        Assert.Equal(1, await AdjustmentCountAsync(read));
    }

    [Fact]
    public async Task ConcurrentUpdateAndDelete_ApplyOnlyOneMutation()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database);
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<MealEntryChangeResult> first = ChangeAfterSignalAsync(database, entry.Id, 0, false, start.Task);
        Task<MealEntryChangeResult> second = ChangeAfterSignalAsync(database, entry.Id, 0, true, start.Task);
        start.SetResult();
        MealEntryChangeResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Single(results, result => result.Kind is MealEntryChangeKind.Updated or MealEntryChangeKind.Deleted);
        Assert.Single(results, result => result.Kind is MealEntryChangeKind.RevisionConflict or MealEntryChangeKind.NotFound);
        await using NutriFlowDbContext read = database.CreateContext();
        Assert.Equal(1, await AdjustmentCountAsync(read));
        Assert.Equal(1, await read.Database.SqlQuery<int>($"""
            SELECT Revision AS Value FROM MealEntryAdjustments WHERE MealEntryId = {entry.Id}
            """).SingleAsync());
    }

    [Fact]
    public async Task ConcurrentDeletes_KeepOneTombstoneAndAreBothSuccessful()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database);
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<MealEntryChangeResult> first = ChangeAfterSignalAsync(database, entry.Id, 0, true, start.Task);
        Task<MealEntryChangeResult> second = ChangeAfterSignalAsync(database, entry.Id, 0, true, start.Task);
        start.SetResult();
        MealEntryChangeResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.All(results, result => Assert.Equal(MealEntryChangeKind.Deleted, result.Kind));
        await using NutriFlowDbContext read = database.CreateContext();
        Assert.Equal(1, await AdjustmentCountAsync(read));
        Assert.Null(await new DailyDiaryStore(read).FindEntryAsync(entry.Id));
        Assert.Equal(1, await read.Database.SqlQuery<int>($"""
            SELECT Revision AS Value FROM MealEntryAdjustments WHERE MealEntryId = {entry.Id}
            """).SingleAsync());
    }

    [Fact]
    public async Task InsertFailure_RollsBackAndDetachesOnlyFailedAdjustment()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        DailyDiaryStore store = new(context);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectAdjustment BEFORE INSERT ON MealEntryAdjustments
            BEGIN SELECT RAISE(ABORT, 'adjustment rejected'); END;
            """);

        await Assert.ThrowsAsync<DbUpdateException>(() => store.UpdateEntryWeightAsync(entry.Id, 0, 80m));
        Assert.Equal(0, await AdjustmentCountAsync(context));
        Assert.Equal(0, Assert.IsType<StoredMealEntry>(await store.FindEntryAsync(entry.Id)).Revision);
        Assert.DoesNotContain(context.ChangeTracker.Entries(), tracked => tracked.State == EntityState.Added);
        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectAdjustment");
        Assert.Equal(MealEntryChangeKind.Updated, (await store.UpdateEntryWeightAsync(entry.Id, 0, 80m)).Kind);
    }

    [Fact]
    public async Task ExistingAdjustmentUpdateFailure_RollsBackDeletion()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        DailyDiaryStore store = new(context);
        await store.UpdateEntryWeightAsync(entry.Id, 0, 80m);
        string adjustment = await AdjustmentSnapshotAsync(context, entry.Id);
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectAdjustmentUpdate BEFORE UPDATE ON MealEntryAdjustments
            BEGIN SELECT RAISE(ABORT, 'adjustment update rejected'); END;
            """);

        await Assert.ThrowsAsync<SqliteException>(() => store.DeleteEntryAsync(entry.Id, 1));
        Assert.Equal(adjustment, await AdjustmentSnapshotAsync(context, entry.Id));
        Assert.Equal(1, Assert.IsType<StoredMealEntry>(await store.FindEntryAsync(entry.Id)).Revision);
    }

    [Fact]
    public async Task CancelledOperations_DoNotWriteAdjustments()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        DailyDiaryStore store = new(context);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpdateEntryWeightAsync(
            entry.Id, 0, 80m, cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteEntryAsync(
            entry.Id, 0, cancellation.Token));
        Assert.Equal(0, await AdjustmentCountAsync(context));
    }

    [Fact]
    public async Task Schema_RequiresExistingOriginalEntryAndPreventsItsPhysicalDeletion()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        StoredMealEntry entry = await CreateEntryAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        await new DailyDiaryStore(context).DeleteEntryAsync(entry.Id, 0);

        await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM MealEntries WHERE Id = {entry.Id}
            """));
        await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("""
            INSERT INTO MealEntryAdjustments (MealEntryId, WeightInGrams, WeightQuality, Revision, IsDeleted, UpdatedAtUtc)
            VALUES (999999, '100', 1, 1, 0, '2026-10-05T00:00:00+00:00')
            """));
        Assert.Equal(1, await EntryCountAsync(context));
        Assert.Equal(1, await AdjustmentCountAsync(context));
    }

    private static async Task<StoredMealEntry> CreateEntryAsync(
        TestDatabase database, MealEntry? entry = null, Guid? ownerId = null)
    {
        await using NutriFlowDbContext context = database.CreateContext();
        MealSessionStore sessions = ownerId.HasValue ? new(context, ownerId.Value) : new(context);
        DailyDiaryStore diary = ownerId.HasValue ? new(context, ownerId.Value) : new(context);
        StoredMealSession session = await sessions.CreateAsync(
            ["Готовое рагу весит 800 г, съел 100 г"],
            MealSessionStoreTests.CreateReadyDraft(), """{"canConfirm":true}""", PreviewToken,
            MealSessionStatus.ReadyForConfirmation, MealDate);
        await diary.ConfirmSessionAsync(session.Id, PreviewToken, [entry ?? CreateMealEntry()]);
        return Assert.Single(await diary.GetStoredSessionEntriesAsync(session.Id));
    }

    private static MealEntry CreateMealEntry(DataQuality quality = DataQuality.Verified)
    {
        return new MealEntry("Рагу", 100m, new NutritionValues(200m, 20m, 10m, 15m), quality);
    }

    private static Task<MealEntryChangeResult> ChangeAfterSignalAsync(
        TestDatabase database, int id, int revision, bool deletion, Task signal, decimal weight = 80m)
    {
        return Task.Run(async () =>
        {
            await signal;
            await using NutriFlowDbContext context = database.CreateContext();
            DailyDiaryStore store = new(context);
            return deletion ? await store.DeleteEntryAsync(id, revision)
                : await store.UpdateEntryWeightAsync(id, revision, weight);
        });
    }

    private static Task<string> EntrySnapshotAsync(NutriFlowDbContext context, int id)
    {
        return context.Database.SqlQuery<string>($"""
            SELECT json_object('Id', Id, 'MealSessionId', MealSessionId, 'Sequence', Sequence,
                'MealDate', MealDate, 'Name', Name, 'WeightInGrams', WeightInGrams, 'Calories', Calories,
                'ProteinGrams', ProteinGrams, 'FatGrams', FatGrams, 'CarbohydratesGrams', CarbohydratesGrams,
                'Quality', Quality, 'CreatedAtUtc', CreatedAtUtc) AS Value FROM MealEntries WHERE Id = {id}
            """).SingleAsync();
    }

    private static Task<string> SessionSnapshotAsync(NutriFlowDbContext context, Guid id)
    {
        return context.Database.SqlQuery<string>($"""
            SELECT json_object('MessagesJson', MessagesJson, 'DraftJson', DraftJson, 'PreviewJson', PreviewJson,
                'PreviewToken', PreviewToken, 'Status', Status, 'Purpose', Purpose,
                'UpdatedAtUtc', UpdatedAtUtc, 'ConfirmedAtUtc', ConfirmedAtUtc) AS Value
            FROM MealSessions WHERE Id = {id}
            """).SingleAsync();
    }

    private static Task<string> AdjustmentSnapshotAsync(NutriFlowDbContext context, int id)
    {
        return context.Database.SqlQuery<string>($"""
            SELECT json_object('MealEntryId', MealEntryId, 'WeightInGrams', WeightInGrams,
                'WeightQuality', WeightQuality, 'Revision', Revision, 'IsDeleted', IsDeleted,
                'UpdatedAtUtc', UpdatedAtUtc) AS Value FROM MealEntryAdjustments WHERE MealEntryId = {id}
            """).SingleAsync();
    }

    private static Task<int> EntryCountAsync(NutriFlowDbContext context) =>
        context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM MealEntries").SingleAsync();

    private static Task<int> AdjustmentCountAsync(NutriFlowDbContext context) =>
        context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM MealEntryAdjustments").SingleAsync();

    private static void AssertEntry(MealEntry expected, MealEntry actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.WeightInGrams, actual.WeightInGrams);
        Assert.Equal(expected.Quality, actual.Quality);
        Assert.Equal(expected.Nutrition.Calories, actual.Nutrition.Calories);
        Assert.Equal(expected.Nutrition.ProteinGrams, actual.Nutrition.ProteinGrams);
        Assert.Equal(expected.Nutrition.FatGrams, actual.Nutrition.FatGrams);
        Assert.Equal(expected.Nutrition.CarbohydratesGrams, actual.Nutrition.CarbohydratesGrams);
    }
}
