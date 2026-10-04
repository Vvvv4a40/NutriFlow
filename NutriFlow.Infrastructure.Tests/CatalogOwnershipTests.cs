using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.ExternalProducts;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class CatalogOwnershipTests
{
    private const string Barcode = "12345678";

    [Fact]
    public async Task AddAsync_SameBarcodeAndName_AreIndependentForEachOwner()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);
        Product first = CreatePersonalProduct("Молоко", 60m);
        Product second = CreatePersonalProduct("Молоко", 80m);

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            LocalProductCatalog firstCatalog = new LocalProductCatalog(context, firstOwner);
            LocalProductCatalog secondCatalog = new LocalProductCatalog(context, secondOwner);

            Assert.True(await firstCatalog.AddAsync(first));
            Assert.True(await secondCatalog.AddAsync(second));
            Assert.False(await firstCatalog.AddAsync(first));
            Assert.False(await secondCatalog.AddAsync(second));
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        LocalProductCatalog firstRead = new LocalProductCatalog(readContext, firstOwner);
        LocalProductCatalog secondRead = new LocalProductCatalog(readContext, secondOwner);

        AssertProduct(first, Assert.Single(await firstRead.FindByNameAsync("молоко")));
        AssertProduct(second, Assert.Single(await secondRead.FindByNameAsync("молоко")));
        AssertProduct(first, Assert.IsType<Product>(await firstRead.FindByBarcodeAsync(Barcode)));
        AssertProduct(second, Assert.IsType<Product>(await secondRead.FindByBarcodeAsync(Barcode)));
    }

    [Fact]
    public async Task AddAsync_IdenticalProductsWithoutBarcode_AreDeduplicatedOnlyWithinOwner()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);
        Product product = CreatePersonalProduct("Гречка", 320m, barcode: null);

        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog firstCatalog = new LocalProductCatalog(context, firstOwner);
        LocalProductCatalog secondCatalog = new LocalProductCatalog(context, secondOwner);

        Assert.True(await firstCatalog.AddAsync(product));
        Assert.True(await secondCatalog.AddAsync(product));
        Assert.False(await firstCatalog.AddAsync(product));
        Assert.False(await secondCatalog.AddAsync(product));
        Assert.Single(await firstCatalog.FindByNameAsync("Гречка"));
        Assert.Single(await secondCatalog.FindByNameAsync("Гречка"));
    }

    [Fact]
    public async Task FindByBarcodeAsync_ExternalCache_IsSharedAcrossOwnersAndRestarts()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);
        Product product = CreateExternalProduct("Public milk", 61m);
        StubExternalProductProvider provider = new StubExternalProductProvider(product);

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            ProductLookupService lookup = new ProductLookupService(
                new LocalProductCatalog(context, firstOwner), provider);

            AssertProduct(product, Assert.IsType<Product>(await lookup.FindByBarcodeAsync(Barcode)));
            Assert.Equal(1, provider.CallCount);
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        LocalProductCatalog secondCatalog = new LocalProductCatalog(readContext, secondOwner);
        ProductLookupService secondLookup = new ProductLookupService(secondCatalog, provider);

        AssertProduct(product, Assert.IsType<Product>(await secondLookup.FindByBarcodeAsync(Barcode)));
        AssertProduct(product, Assert.Single(await secondCatalog.FindByNameAsync("Public milk")));
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task AddAsync_PersonalOverride_DoesNotChangeSharedProduct()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);
        Product shared = CreateExternalProduct("Public milk", 61m);
        Product personal = CreatePersonalProduct("Моё молоко", 70m);

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            LocalProductCatalog catalog = new LocalProductCatalog(context, firstOwner);
            await CacheSharedAsync(catalog, shared);
            Assert.True(await catalog.AddAsync(personal));
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        LocalProductCatalog firstCatalog = new LocalProductCatalog(readContext, firstOwner);
        LocalProductCatalog secondCatalog = new LocalProductCatalog(readContext, secondOwner);

        AssertProduct(personal, Assert.IsType<Product>(await firstCatalog.FindByBarcodeAsync(Barcode)));
        AssertProduct(personal, Assert.Single(await firstCatalog.FindByNameAsync("Public milk")));
        AssertProduct(personal, Assert.Single(await firstCatalog.FindByNameAsync("Моё молоко")));
        AssertProduct(shared, Assert.IsType<Product>(await secondCatalog.FindByBarcodeAsync(Barcode)));
        AssertProduct(shared, Assert.Single(await secondCatalog.FindByNameAsync("Public milk")));
        Assert.Empty(await secondCatalog.FindByNameAsync("Моё молоко"));
    }

    [Fact]
    public async Task FindByNameAsync_SharedAndPersonalSameBarcode_ReturnsOnlyPersonalProduct()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid owner = await CreateOwnerAsync(database);
        Product shared = CreateExternalProduct("Молоко", 61m);
        Product personal = CreatePersonalProduct("Молоко", 70m);

        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new LocalProductCatalog(context, owner);
        await CacheSharedAsync(catalog, shared);
        await catalog.AddAsync(personal);

        AssertProduct(personal, Assert.Single(await catalog.FindByNameAsync("молоко")));
    }

    [Fact]
    public async Task FindByNameAsync_SharedAlias_UsesOnlyOwnersPersonalOverride()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);
        Product shared = CreateExternalProduct("Public milk", 61m);
        Product personal = CreatePersonalProduct("Моё молоко", 70m);

        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog firstCatalog = new LocalProductCatalog(context, firstOwner);
        LocalProductCatalog secondCatalog = new LocalProductCatalog(context, secondOwner);
        await CacheSharedAsync(firstCatalog, shared);
        await firstCatalog.AddAliasByBarcodeAsync(Barcode, "Для каши");
        await firstCatalog.AddAsync(personal);

        AssertProduct(personal, Assert.Single(await firstCatalog.FindByNameAsync("Для каши")));
        Assert.Empty(await secondCatalog.FindByNameAsync("Для каши"));
        AssertProduct(shared, Assert.Single(await secondCatalog.FindByNameAsync("Public milk")));
    }

    [Fact]
    public async Task AddAliasByBarcodeAsync_SharedAliases_ArePersonalAndMayCoincide()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);
        Product shared = CreateExternalProduct("Public milk", 61m);

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            LocalProductCatalog firstCatalog = new LocalProductCatalog(context, firstOwner);
            LocalProductCatalog secondCatalog = new LocalProductCatalog(context, secondOwner);
            await CacheSharedAsync(firstCatalog, shared);
            await firstCatalog.AddAliasByBarcodeAsync(Barcode, "Для каши");
            await secondCatalog.AddAliasByBarcodeAsync(Barcode, "Для кофе");
            await firstCatalog.AddAliasByBarcodeAsync(Barcode, "Молоко");
            await secondCatalog.AddAliasByBarcodeAsync(Barcode, "Молоко");
            await secondCatalog.AddAliasByBarcodeAsync(Barcode, " МОЛОКО ");
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        LocalProductCatalog firstRead = new LocalProductCatalog(readContext, firstOwner);
        LocalProductCatalog secondRead = new LocalProductCatalog(readContext, secondOwner);

        AssertProduct(shared, Assert.Single(await firstRead.FindByNameAsync("Для каши")));
        Assert.Empty(await secondRead.FindByNameAsync("Для каши"));
        AssertProduct(shared, Assert.Single(await secondRead.FindByNameAsync("Для кофе")));
        Assert.Empty(await firstRead.FindByNameAsync("Для кофе"));
        Assert.Single(await firstRead.FindByNameAsync("Молоко"));
        Assert.Single(await secondRead.FindByNameAsync("Молоко"));
    }

    [Fact]
    public async Task AddAliasByBarcodeAsync_PersonalAliases_DoNotResolveOtherOwnersProduct()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);
        Product first = CreatePersonalProduct("Первый продукт", 60m);
        Product second = CreatePersonalProduct("Второй продукт", 80m);

        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog firstCatalog = new LocalProductCatalog(context, firstOwner);
        LocalProductCatalog secondCatalog = new LocalProductCatalog(context, secondOwner);
        await firstCatalog.AddAsync(first);
        await secondCatalog.AddAsync(second);
        await firstCatalog.AddAliasByBarcodeAsync(Barcode, "Мой завтрак");
        await secondCatalog.AddAliasByBarcodeAsync(Barcode, "Мой завтрак");
        await firstCatalog.AddAliasByBarcodeAsync(Barcode, "Первый алиас");

        AssertProduct(first, Assert.Single(await firstCatalog.FindByNameAsync("Мой завтрак")));
        AssertProduct(second, Assert.Single(await secondCatalog.FindByNameAsync("Мой завтрак")));
        Assert.Empty(await secondCatalog.FindByNameAsync("Первый алиас"));
    }

    [Fact]
    public async Task AddAsync_PersonalQualityUpgrade_PreservesOldNameWithoutLeakingIt()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);
        Product initial = CreatePersonalProduct("Примерное молоко", 60m, DataQuality.Estimated);
        Product reviewed = CreatePersonalProduct("Проверенное молоко", 65m);
        Product other = CreatePersonalProduct("Другое молоко", 80m, DataQuality.Estimated);

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            LocalProductCatalog firstCatalog = new LocalProductCatalog(context, firstOwner);
            LocalProductCatalog secondCatalog = new LocalProductCatalog(context, secondOwner);
            await firstCatalog.AddAsync(initial);
            await secondCatalog.AddAsync(other);
            Assert.True(await firstCatalog.AddAsync(reviewed));
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        LocalProductCatalog firstRead = new LocalProductCatalog(readContext, firstOwner);
        LocalProductCatalog secondRead = new LocalProductCatalog(readContext, secondOwner);

        AssertProduct(reviewed, Assert.Single(await firstRead.FindByNameAsync(initial.Name)));
        AssertProduct(reviewed, Assert.Single(await firstRead.FindByNameAsync(reviewed.Name)));
        AssertProduct(other, Assert.IsType<Product>(await secondRead.FindByBarcodeAsync(Barcode)));
        Assert.Empty(await secondRead.FindByNameAsync(initial.Name));
        Assert.Empty(await secondRead.FindByNameAsync(reviewed.Name));
    }

    [Fact]
    public async Task AddAliasByBarcodeAsync_OnlyForeignPersonalProductExists_ThrowsNotFound()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);

        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog firstCatalog = new LocalProductCatalog(context, firstOwner);
        LocalProductCatalog secondCatalog = new LocalProductCatalog(context, secondOwner);
        await firstCatalog.AddAsync(CreatePersonalProduct("Личный продукт", 60m));

        Assert.Null(await secondCatalog.FindByBarcodeAsync(Barcode));
        Assert.Empty(await secondCatalog.FindByNameAsync("Личный продукт"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            secondCatalog.AddAliasByBarcodeAsync(Barcode, "Чужой алиас"));
        Assert.Empty(await firstCatalog.FindByNameAsync("Чужой алиас"));
    }

    [Fact]
    public async Task FindByBarcodeAsync_ForeignPersonalProduct_DoesNotSuppressExternalLookup()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);
        Product personal = CreatePersonalProduct("Личное молоко", 80m);
        Product shared = CreateExternalProduct("Public milk", 61m);
        StubExternalProductProvider provider = new StubExternalProductProvider(shared);

        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog firstCatalog = new LocalProductCatalog(context, firstOwner);
        LocalProductCatalog secondCatalog = new LocalProductCatalog(context, secondOwner);
        await firstCatalog.AddAsync(personal);
        ProductLookupService secondLookup = new ProductLookupService(secondCatalog, provider);

        AssertProduct(shared, Assert.IsType<Product>(await secondLookup.FindByBarcodeAsync(Barcode)));
        Assert.Equal(1, provider.CallCount);
        AssertProduct(personal, Assert.IsType<Product>(await firstCatalog.FindByBarcodeAsync(Barcode)));
        Assert.Empty(await secondCatalog.FindByNameAsync(personal.Name));
    }

    [Fact]
    public async Task FindByBarcodeAsync_PersonalOverrideSavedDuringLookup_ReturnsPersonalProduct()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);
        Product personal = CreatePersonalProduct("Моё молоко", 70m);
        Product shared = CreateExternalProduct("Public milk", 61m);
        CallbackExternalProductProvider provider = new CallbackExternalProductProvider(
            async cancellationToken =>
            {
                await using NutriFlowDbContext writeContext = database.CreateContext();
                LocalProductCatalog writeCatalog = new LocalProductCatalog(writeContext, firstOwner);
                Assert.True(await writeCatalog.AddAsync(personal, cancellationToken));
                return shared;
            });

        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog firstCatalog = new LocalProductCatalog(context, firstOwner);
        ProductLookupService lookup = new ProductLookupService(firstCatalog, provider);

        AssertProduct(personal, Assert.IsType<Product>(await lookup.FindByBarcodeAsync(Barcode)));

        await using NutriFlowDbContext readContext = database.CreateContext();
        LocalProductCatalog secondCatalog = new LocalProductCatalog(readContext, secondOwner);
        AssertProduct(shared, Assert.IsType<Product>(await secondCatalog.FindByBarcodeAsync(Barcode)));
        Assert.Empty(await secondCatalog.FindByNameAsync(personal.Name));
    }

    [Fact]
    public async Task FindByBarcodeAsync_ExternalBarcodeMismatch_RejectsResultWithoutCaching()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid owner = await CreateOwnerAsync(database);
        Product mismatchedProduct = CreateExternalProduct("Wrong milk", 61m, "87654321");
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new LocalProductCatalog(context, owner);
        ProductLookupService lookup = new ProductLookupService(
            catalog, new StubExternalProductProvider(mismatchedProduct));

        await Assert.ThrowsAsync<InvalidDataException>(() => lookup.FindByBarcodeAsync(Barcode));
        Assert.Null(await catalog.FindByBarcodeAsync(Barcode));
        Assert.Null(await catalog.FindByBarcodeAsync(mismatchedProduct.Barcode!));
        Assert.Empty(await catalog.FindByNameAsync(mismatchedProduct.Name));
    }

    [Fact]
    public async Task FindByBarcodeAsync_ConcurrentOwnersCacheMiss_StoresOneSharedProduct()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);
        Product shared = CreateExternalProduct("Public milk", 61m);
        TaskCompletionSource bothRequestsArrived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int arrivals = 0;
        CallbackExternalProductProvider provider = new CallbackExternalProductProvider(
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref arrivals) == 2)
                {
                    bothRequestsArrived.TrySetResult();
                }

                await bothRequestsArrived.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                return shared;
            });
        await using NutriFlowDbContext firstContext = database.CreateContext();
        await using NutriFlowDbContext secondContext = database.CreateContext();
        ProductLookupService firstLookup = new ProductLookupService(
            new LocalProductCatalog(firstContext, firstOwner), provider);
        ProductLookupService secondLookup = new ProductLookupService(
            new LocalProductCatalog(secondContext, secondOwner), provider);

        Task<Product?> firstRequest = firstLookup.FindByBarcodeAsync(Barcode);
        Task<Product?> secondRequest = secondLookup.FindByBarcodeAsync(Barcode);
        Product?[] results = await Task.WhenAll(firstRequest, secondRequest)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, arrivals);
        Assert.All(results, result => AssertProduct(shared, Assert.IsType<Product>(result)));

        await using NutriFlowDbContext readContext = database.CreateContext();
        int sharedCount = await readContext.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS "Value" FROM "Products"
            WHERE "UserId" IS NULL AND "Barcode" = {Barcode}
            """).SingleAsync();
        Assert.Equal(1, sharedCount);
    }

    [Theory]
    [InlineData(NutritionSourceKind.ManualInput, "https://example.com/product/12345678")]
    [InlineData(NutritionSourceKind.LabelPhoto, "label-photo:12345678.webp")]
    [InlineData(NutritionSourceKind.ExternalService, "label-photo:12345678.webp")]
    public async Task FindByBarcodeAsync_NonPublicExternalResult_IsNotSavedAsShared(
        NutritionSourceKind sourceKind,
        string sourceReference)
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid owner = await CreateOwnerAsync(database);
        Product invalidImport = new Product(
            "Private result",
            new NutritionValues(60m, 3m, 3m, 4m),
            new NutritionSource(sourceKind, DataQuality.Unknown, "Test source", sourceReference),
            Barcode);
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new LocalProductCatalog(context, owner);
        ProductLookupService lookup = new ProductLookupService(
            catalog, new StubExternalProductProvider(invalidImport));

        await Assert.ThrowsAsync<InvalidDataException>(() => lookup.FindByBarcodeAsync(Barcode));
        Assert.Null(await catalog.FindByBarcodeAsync(Barcode));
        Assert.Empty(await catalog.FindByNameAsync(invalidImport.Name));
    }

    [Fact]
    public async Task Constructor_EmptyOwner_ThrowsArgumentException()
    {
        await using TestDatabase database = new TestDatabase();
        using NutriFlowDbContext context = database.CreateContext();

        Assert.Throws<ArgumentException>(() => new LocalProductCatalog(context, Guid.Empty));
    }

    [Fact]
    public async Task Database_SameSharedBarcode_RejectsDuplicateEvenWithNullOwner()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid owner = await CreateOwnerAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        await CacheSharedAsync(new LocalProductCatalog(context, owner), CreateExternalProduct("Public milk", 61m));

        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Products"
                    ("UserId", "Name", "NormalizedName", "Barcode", "Calories",
                     "ProteinGrams", "FatGrams", "CarbohydratesGrams", "SourceKind",
                     "SourceQuality", "SourceName", "SourceReference")
                SELECT "UserId", "Name", "NormalizedName", "Barcode", "Calories",
                       "ProteinGrams", "FatGrams", "CarbohydratesGrams", "SourceKind",
                       "SourceQuality", "SourceName", "SourceReference"
                FROM "Products" WHERE "UserId" IS NULL AND "Barcode" = {Barcode}
                """));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task Database_SamePersonalBarcode_RejectsDuplicateWithinOwner()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid owner = await CreateOwnerAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        await new LocalProductCatalog(context, owner).AddAsync(CreatePersonalProduct("Молоко", 60m));

        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Products"
                    ("UserId", "Name", "NormalizedName", "Barcode", "Calories",
                     "ProteinGrams", "FatGrams", "CarbohydratesGrams", "SourceKind",
                     "SourceQuality", "SourceName", "SourceReference")
                SELECT "UserId", "Name", "NormalizedName", "Barcode", "Calories",
                       "ProteinGrams", "FatGrams", "CarbohydratesGrams", "SourceKind",
                       "SourceQuality", "SourceName", "SourceReference"
                FROM "Products" WHERE "UserId" = {owner} AND "Barcode" = {Barcode}
                """));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task Database_ProductAndAliasOwner_MustReferenceExistingUser()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid owner = await CreateOwnerAsync(database);
        Guid missingOwner = Guid.NewGuid();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new LocalProductCatalog(context, owner);
        await catalog.AddAsync(CreatePersonalProduct("Молоко", 60m));
        await catalog.AddAliasByBarcodeAsync(Barcode, "Для каши");

        SqliteException productException = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "Products" SET "UserId" = {missingOwner}
                WHERE "UserId" = {owner} AND "Barcode" = {Barcode}
                """));
        SqliteException aliasException = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "ProductAliases" SET "UserId" = {missingOwner}
                WHERE "UserId" = {owner}
                """));

        Assert.Equal(19, productException.SqliteErrorCode);
        Assert.Equal(19, aliasException.SqliteErrorCode);
    }

    private static async Task<Guid> CreateOwnerAsync(TestDatabase database)
    {
        Guid owner = Guid.NewGuid();
        await using NutriFlowDbContext context = database.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Users" ("Id", "CreatedAtUtc", "IsLegacyLocal")
            VALUES ({owner}, {DateTimeOffset.UtcNow}, {false})
            """);
        return owner;
    }

    private static async Task CacheSharedAsync(LocalProductCatalog catalog, Product product)
    {
        ProductLookupService lookup = new ProductLookupService(
            catalog, new StubExternalProductProvider(product));
        AssertProduct(product, Assert.IsType<Product>(await lookup.FindByBarcodeAsync(product.Barcode!)));
    }

    private static Product CreatePersonalProduct(
        string name,
        decimal calories,
        DataQuality quality = DataQuality.Exact,
        string? barcode = Barcode)
    {
        return new Product(
            name,
            new NutritionValues(calories, 3m, 3m, 4m),
            new NutritionSource(NutritionSourceKind.ManualInput, quality, "Personal input"),
            barcode);
    }

    private static Product CreateExternalProduct(
        string name,
        decimal calories,
        string barcode = Barcode)
    {
        return new Product(
            name,
            new NutritionValues(calories, 3m, 3.2m, 4.7m),
            new NutritionSource(
                NutritionSourceKind.ExternalService,
                DataQuality.Unknown,
                "Public test provider",
                $"https://example.com/product/{barcode}"),
            barcode);
    }

    private static void AssertProduct(Product expected, Product actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Barcode, actual.Barcode);
        Assert.Equal(expected.NutritionPer100Grams.Calories, actual.NutritionPer100Grams.Calories);
        Assert.Equal(expected.NutritionPer100Grams.ProteinGrams, actual.NutritionPer100Grams.ProteinGrams);
        Assert.Equal(expected.NutritionPer100Grams.FatGrams, actual.NutritionPer100Grams.FatGrams);
        Assert.Equal(expected.NutritionPer100Grams.CarbohydratesGrams, actual.NutritionPer100Grams.CarbohydratesGrams);
        Assert.Equal(expected.Source.Kind, actual.Source.Kind);
        Assert.Equal(expected.Source.Quality, actual.Source.Quality);
        Assert.Equal(expected.Source.Name, actual.Source.Name);
        Assert.Equal(expected.Source.Reference, actual.Source.Reference);
    }

    private sealed class StubExternalProductProvider : IExternalProductProvider
    {
        private readonly Product _product;

        public StubExternalProductProvider(Product product)
        {
            _product = product;
        }

        public int CallCount { get; private set; }

        public Task<Product?> FindByBarcodeAsync(
            string barcode,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<Product?>(_product);
        }
    }

    private sealed class CallbackExternalProductProvider : IExternalProductProvider
    {
        private readonly Func<CancellationToken, Task<Product?>> _callback;

        public CallbackExternalProductProvider(Func<CancellationToken, Task<Product?>> callback)
        {
            _callback = callback;
        }

        public Task<Product?> FindByBarcodeAsync(
            string barcode,
            CancellationToken cancellationToken = default)
        {
            return _callback(cancellationToken);
        }
    }
}
