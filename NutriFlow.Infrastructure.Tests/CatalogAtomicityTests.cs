using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class CatalogAtomicityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddAsync_AliasSaveFails_RollsBackUpgradeAndAllowsRetry(bool cancelSave)
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Product original = CreateProduct("Original milk", 61m, DataQuality.Estimated);
        Product reviewed = CreateProduct("Reviewed milk", 60m, DataQuality.Verified);
        await using NutriFlowDbContext initialContext = database.CreateContext();
        await new LocalProductCatalog(initialContext).AddAsync(original);

        using CancellationTokenSource cancellation = new CancellationTokenSource();
        AliasSaveFailureInterceptor interceptor = new AliasSaveFailureInterceptor(
            cancelSave ? cancellation : null);
        DbContextOptions<NutriFlowDbContext> options =
            new DbContextOptionsBuilder<NutriFlowDbContext>()
                .UseSqlite(initialContext.Database.GetDbConnection().ConnectionString)
                .AddInterceptors(interceptor)
                .Options;
        await using NutriFlowDbContext context = new NutriFlowDbContext(options);
        LocalProductCatalog catalog = new LocalProductCatalog(context);
        await catalog.AddAsync(new Product(
            "Unrelated product",
            new NutritionValues(100m, 10m, 5m, 4m),
            new NutritionSource(NutritionSourceKind.ManualInput, DataQuality.Exact, "Test input")));
        int trackedCount = context.ChangeTracker.Entries().Count();

        if (cancelSave)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => catalog.AddAsync(reviewed, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<DbUpdateException>(
                () => catalog.AddAsync(reviewed));
        }

        Product unchanged = Assert.IsType<Product>(
            await catalog.FindByBarcodeAsync(original.Barcode!));
        Assert.Equal(original.Name, unchanged.Name);
        Assert.Equal(original.NutritionPer100Grams.Calories, unchanged.NutritionPer100Grams.Calories);
        Assert.Equal(original.Source.Quality, unchanged.Source.Quality);
        Assert.Empty(await catalog.FindByNameAsync(reviewed.Name));
        Assert.Equal(trackedCount, context.ChangeTracker.Entries().Count());
        Assert.Null(context.Database.CurrentTransaction);

        Assert.True(await catalog.AddAsync(reviewed));
        Product updated = Assert.IsType<Product>(
            await catalog.FindByBarcodeAsync(reviewed.Barcode!));
        Product byOldName = Assert.Single(await catalog.FindByNameAsync(original.Name));
        Assert.Equal(reviewed.Name, updated.Name);
        Assert.Equal(reviewed.NutritionPer100Grams.Calories, updated.NutritionPer100Grams.Calories);
        Assert.Equal(reviewed.Source.Quality, updated.Source.Quality);
        Assert.Equal(reviewed.Name, byOldName.Name);
        Assert.Null(context.Database.CurrentTransaction);

        await using NutriFlowDbContext readContext = database.CreateContext();
        Product persisted = Assert.Single(
            await new LocalProductCatalog(readContext).FindByNameAsync(original.Name));
        Assert.Equal(reviewed.Name, persisted.Name);
    }

    [Fact]
    public async Task AddAsync_RepeatedRenames_PreservesExistingAliasesWithoutDuplicates()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new LocalProductCatalog(context);

        await catalog.AddAsync(CreateProduct("First name", 61m, DataQuality.Unknown));
        Assert.True(await catalog.AddAsync(CreateProduct("Second name", 62m, DataQuality.Estimated)));
        Assert.True(await catalog.AddAsync(CreateProduct("First name", 63m, DataQuality.Verified)));
        Assert.True(await catalog.AddAsync(CreateProduct("Second name", 64m, DataQuality.Exact)));

        Product firstName = Assert.Single(await catalog.FindByNameAsync("First name"));
        Product secondName = Assert.Single(await catalog.FindByNameAsync("Second name"));
        Assert.Equal("Second name", firstName.Name);
        Assert.Equal(64m, firstName.NutritionPer100Grams.Calories);
        Assert.Equal(firstName.Name, secondName.Name);
    }

    [Theory]
    [InlineData(DataQuality.Verified, DataQuality.Exact, true)]
    [InlineData(DataQuality.Exact, DataQuality.Verified, false)]
    public async Task AddAsync_ConcurrentQualityUpgrade_RetriesWithoutDowngrading(
        DataQuality concurrentQuality,
        DataQuality incomingQuality,
        bool expectedUpgrade)
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        await using NutriFlowDbContext concurrentContext = database.CreateContext();
        LocalProductCatalog concurrentCatalog = new LocalProductCatalog(concurrentContext);
        await concurrentCatalog.AddAsync(CreateProduct("Original milk", 61m, DataQuality.Unknown));
        Product concurrent = CreateProduct("Concurrent milk", 62m, concurrentQuality);
        Product incoming = CreateProduct("Incoming milk", 63m, incomingQuality);
        BeforeFirstTransactionInterceptor interceptor = new BeforeFirstTransactionInterceptor(
            async cancellationToken =>
            {
                await concurrentCatalog.AddAsync(concurrent, cancellationToken);
            });
        DbContextOptions<NutriFlowDbContext> options =
            new DbContextOptionsBuilder<NutriFlowDbContext>()
                .UseSqlite(concurrentContext.Database.GetDbConnection().ConnectionString)
                .AddInterceptors(interceptor)
                .Options;
        await using NutriFlowDbContext context = new NutriFlowDbContext(options);
        LocalProductCatalog catalog = new LocalProductCatalog(context);

        Assert.Equal(expectedUpgrade, await catalog.AddAsync(incoming));

        Product expected = expectedUpgrade ? incoming : concurrent;
        Product actual = Assert.IsType<Product>(await catalog.FindByBarcodeAsync(expected.Barcode!));
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Source.Quality, actual.Source.Quality);
        Assert.Equal(expected.NutritionPer100Grams.Calories, actual.NutritionPer100Grams.Calories);
        Assert.Equal(expected.Name, Assert.Single(await catalog.FindByNameAsync("Original milk")).Name);
        Assert.Null(context.Database.CurrentTransaction);
    }

    private static Product CreateProduct(string name, decimal calories, DataQuality quality)
    {
        return new Product(
            name,
            new NutritionValues(calories, 3m, 3.2m, 4.7m),
            new NutritionSource(NutritionSourceKind.ManualInput, quality, "Test input"),
            "12345678");
    }

    private sealed class AliasSaveFailureInterceptor : SaveChangesInterceptor
    {
        private readonly CancellationTokenSource? _cancellation;
        private bool _failed;

        public AliasSaveFailureInterceptor(CancellationTokenSource? cancellation)
        {
            _cancellation = cancellation;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            bool hasNewAlias = eventData.Context!.ChangeTracker.Entries().Any(entry =>
                entry.State == EntityState.Added && entry.Metadata.GetTableName() == "ProductAliases");
            if (!_failed && hasNewAlias)
            {
                _failed = true;
                if (_cancellation is not null)
                {
                    _cancellation.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                throw new DbUpdateException("Simulated alias save failure.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class BeforeFirstTransactionInterceptor : DbTransactionInterceptor
    {
        private readonly Func<CancellationToken, Task> _callback;
        private bool _executed;

        public BeforeFirstTransactionInterceptor(Func<CancellationToken, Task> callback)
        {
            _callback = callback;
        }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (!_executed)
            {
                _executed = true;
                await _callback(cancellationToken);
            }

            return result;
        }
    }
}
