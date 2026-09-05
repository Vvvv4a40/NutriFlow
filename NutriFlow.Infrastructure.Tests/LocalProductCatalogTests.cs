using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class LocalProductCatalogTests
{
    [Fact]
    public async Task AddAsync_ThenFindByNameAsync_RestoresProduct()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Product expected = new Product(
            "Йогурт 'Домашний'",
            new NutritionValues(
                123.4567890123456789012345678m,
                8.25m,
                4.75m,
                12.5m),
            new NutritionSource(
                NutritionSourceKind.WebPage,
                DataQuality.Verified,
                "Manufacturer website",
                "https://example.com/yogurt"));

        await using (NutriFlowDbContext writeContext = database.CreateContext())
        {
            LocalProductCatalog writeCatalog = new LocalProductCatalog(writeContext);
            await writeCatalog.AddAsync(expected);
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        LocalProductCatalog readCatalog = new LocalProductCatalog(readContext);

        Product actual = Assert.Single(
            await readCatalog.FindByNameAsync(expected.Name));

        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(
            expected.NutritionPer100Grams.Calories,
            actual.NutritionPer100Grams.Calories);
        Assert.Equal(
            expected.NutritionPer100Grams.ProteinGrams,
            actual.NutritionPer100Grams.ProteinGrams);
        Assert.Equal(
            expected.NutritionPer100Grams.FatGrams,
            actual.NutritionPer100Grams.FatGrams);
        Assert.Equal(
            expected.NutritionPer100Grams.CarbohydratesGrams,
            actual.NutritionPer100Grams.CarbohydratesGrams);
        Assert.Equal(expected.Source.Kind, actual.Source.Kind);
        Assert.Equal(expected.Source.Quality, actual.Source.Quality);
        Assert.Equal(expected.Source.Name, actual.Source.Name);
        Assert.Equal(expected.Source.Reference, actual.Source.Reference);
    }

    [Fact]
    public async Task FindByNameAsync_IgnoresOuterSpacesAndLetterCase()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Product product = CreateManualProduct("Гречка", 320m);

        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new LocalProductCatalog(context);
        await catalog.AddAsync(product);

        Product actual = Assert.Single(
            await catalog.FindByNameAsync("  гРеЧкА  "));

        Assert.Equal("Гречка", actual.Name);
        Assert.Null(actual.Source.Reference);
    }

    [Fact]
    public async Task FindByNameAsync_WithPartialName_ReturnsEmptyList()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();

        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new LocalProductCatalog(context);
        await catalog.AddAsync(CreateManualProduct("Гречка", 320m));

        IReadOnlyList<Product> products = await catalog.FindByNameAsync("Греч");

        Assert.Empty(products);
    }

    [Fact]
    public async Task FindByNameAsync_WithSameNormalizedName_ReturnsAllProducts()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();

        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new LocalProductCatalog(context);
        await catalog.AddAsync(CreateManualProduct("Кефир", 50m));
        await catalog.AddAsync(CreateManualProduct(" кЕфИр ", 60m));

        IReadOnlyList<Product> products = await catalog.FindByNameAsync("кефир");

        Assert.Collection(
            products,
            product => Assert.Equal(50m, product.NutritionPer100Grams.Calories),
            product => Assert.Equal(60m, product.NutritionPer100Grams.Calories));
    }

    [Fact]
    public async Task FindByNameAsync_NormalizesEquivalentUnicode()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();

        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new LocalProductCatalog(context);
        await catalog.AddAsync(CreateManualProduct("Caf\u00e9", 40m));

        IReadOnlyList<Product> products =
            await catalog.FindByNameAsync("Cafe\u0301");

        Assert.Single(products);
    }

    [Fact]
    public async Task AddAsync_WithIdenticalProduct_DoesNotAddDuplicate()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();

        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new LocalProductCatalog(context);
        Product product = CreateManualProduct("Творог", 120m);

        bool firstWasAdded = await catalog.AddAsync(product);
        bool secondWasAdded = await catalog.AddAsync(product);

        Assert.True(firstWasAdded);
        Assert.False(secondWasAdded);
        Assert.Single(await catalog.FindByNameAsync(product.Name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task FindByNameAsync_WithBlankName_ThrowsArgumentException(
        string? name)
    {
        await using TestDatabase database = new TestDatabase();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new LocalProductCatalog(context);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => catalog.FindByNameAsync(name!));
    }

    [Fact]
    public async Task AddAsync_WithNullProduct_ThrowsArgumentNullException()
    {
        await using TestDatabase database = new TestDatabase();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new LocalProductCatalog(context);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => catalog.AddAsync(null!));
    }

    [Fact]
    public async Task MigrateAsync_WhenCalledTwice_LeavesDatabaseUsable()
    {
        await using TestDatabase database = new TestDatabase();

        await database.MigrateAsync();
        await database.MigrateAsync();

        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new LocalProductCatalog(context);
        await catalog.AddAsync(CreateManualProduct("Рис", 340m));

        Assert.Single(await catalog.FindByNameAsync("Рис"));
    }

    private static Product CreateManualProduct(string name, decimal calories)
    {
        return new Product(
            name,
            new NutritionValues(calories, 10m, 2m, 60m),
            new NutritionSource(
                NutritionSourceKind.ManualInput,
                DataQuality.Exact,
                "Test manual input"));
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"nutriflow-{Guid.NewGuid():N}.db");

        public NutriFlowDbContext CreateContext()
        {
            DbContextOptions<NutriFlowDbContext> options =
                new DbContextOptionsBuilder<NutriFlowDbContext>()
                    .UseSqlite($"Data Source={_databasePath};Pooling=False")
                    .Options;

            return new NutriFlowDbContext(options);
        }

        public async Task MigrateAsync()
        {
            await using NutriFlowDbContext context = CreateContext();
            await context.Database.MigrateAsync();
        }

        public ValueTask DisposeAsync()
        {
            File.Delete(_databasePath);
            File.Delete($"{_databasePath}-shm");
            File.Delete($"{_databasePath}-wal");

            return ValueTask.CompletedTask;
        }
    }
}
