using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.ExternalProducts;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class CatalogSelectionTests
{
    [Fact]
    public async Task CreateSelectionAliasAsync_SelectsEstimatedProductWithoutChoosingExactNamesake()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new(context);
        Product exact = CreateProduct("Молоко", 60m);
        Product estimated = CreateProduct("Молоко", 80.125m, DataQuality.Estimated);
        await catalog.AddAsync(exact);
        await catalog.AddAsync(estimated);

        string alias = await catalog.CreateSelectionAliasAsync(estimated);

        Product selected = Assert.Single(await catalog.FindByNameAsync(alias));
        Assert.Equal(estimated.Name, selected.Name);
        Assert.Equal(80.125m, selected.NutritionPer100Grams.Calories);
        Assert.Equal(DataQuality.Estimated, selected.Source.Quality);
        Assert.Equal(2, (await catalog.FindByNameAsync("Молоко")).Count);
    }

    [Fact]
    public async Task CreateSelectionAliasAsync_PreservesSameQualityNutritionSelectionAfterReopen()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        Product first = CreateProduct("Яблоко", 50.125m);
        Product second = CreateProduct("Яблоко", 60.875m);
        string alias;
        await using (NutriFlowDbContext context = database.CreateContext())
        {
            LocalProductCatalog catalog = new(context);
            await catalog.AddAsync(first);
            await catalog.AddAsync(second);
            alias = await catalog.CreateSelectionAliasAsync(second);
        }

        await using NutriFlowDbContext reopened = database.CreateContext();
        Product selected = Assert.Single(await new LocalProductCatalog(reopened).FindByNameAsync(alias));
        Assert.Equal(second.Name, selected.Name);
        Assert.Equal(second.NutritionPer100Grams.Calories, selected.NutritionPer100Grams.Calories);
        Assert.Equal(second.Source.Quality, selected.Source.Quality);
        Assert.StartsWith("selected-", alias);
    }

    [Fact]
    public async Task CreateSelectionAliasAsync_RejectsForeignProductAndDoesNotCreateAlias()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        Product product = CreateProduct("Личный продукт", 80m);
        await new LocalProductCatalog(context, firstOwner).AddAsync(product);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            new LocalProductCatalog(context, secondOwner).CreateSelectionAliasAsync(product));

        Assert.Equal(0, await AliasCountAsync(context));
    }

    [Fact]
    public async Task CreateSelectionAliasAsync_SharedProductAliasIsPrivateToItsOwner()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog first = new(context, firstOwner);
        LocalProductCatalog second = new(context, secondOwner);
        Product shared = CreateSharedProduct();
        await new ProductLookupService(first, new StubExternalProductProvider(shared))
            .FindByBarcodeAsync(shared.Barcode!);

        string alias = await first.CreateSelectionAliasAsync(shared);

        Assert.Equal(shared.Name, Assert.Single(await first.FindByNameAsync(alias)).Name);
        Assert.Empty(await second.FindByNameAsync(alias));
        Assert.NotNull(await second.FindByBarcodeAsync(shared.Barcode!));
    }

    [Fact]
    public async Task CreateSelectionAliasAsync_RejectsSharedProductHiddenByPersonalOverride()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new(context);
        Product shared = CreateSharedProduct();
        await new ProductLookupService(catalog, new StubExternalProductProvider(shared))
            .FindByBarcodeAsync(shared.Barcode!);
        Product personal = CreateProduct("Моё молоко", 80m, barcode: shared.Barcode);
        await catalog.AddAsync(personal);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => catalog.CreateSelectionAliasAsync(shared));
        string alias = await catalog.CreateSelectionAliasAsync(personal);

        Assert.Equal(personal.Name, Assert.Single(await catalog.FindByNameAsync(alias)).Name);
        Assert.Equal(1, await AliasCountAsync(context));
    }

    [Fact]
    public async Task CreateSelectionAliasAsync_RequiresMatchingNutritionAndCompleteSource()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new(context);
        Product original = CreateProduct("Молоко", 60m);
        await catalog.AddAsync(original);
        Product[] altered =
        [
            new("Молоко", new NutritionValues(61m, 3m, 4m, 5m), original.Source),
            new("Молоко", new NutritionValues(60m, 4m, 4m, 5m), original.Source),
            new("Молоко", new NutritionValues(60m, 3m, 5m, 5m), original.Source),
            new("Молоко", new NutritionValues(60m, 3m, 4m, 6m), original.Source),
            CreateProduct("Молоко", 60m, DataQuality.Estimated),
            new("Молоко", original.NutritionPer100Grams,
                new NutritionSource(NutritionSourceKind.NutriFlowCatalog, DataQuality.Exact, original.Source.Name)),
            new("Молоко", original.NutritionPer100Grams,
                new NutritionSource(NutritionSourceKind.ManualInput, DataQuality.Exact, "Другой источник")),
            new("Молоко", original.NutritionPer100Grams,
                new NutritionSource(NutritionSourceKind.ManualInput, DataQuality.Exact, original.Source.Name, "changed-reference")),
            CreateProduct("Молоко", 60m, barcode: "12345678")
        ];
        foreach (Product product in altered)
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(() => catalog.CreateSelectionAliasAsync(product));
        }

        Assert.Equal(0, await AliasCountAsync(context));
        Assert.Equal(original.Name,
            Assert.Single(await catalog.FindByNameAsync(await catalog.CreateSelectionAliasAsync(original))).Name);
    }

    [Fact]
    public async Task CreateSelectionAliasAsync_NumericScaleDoesNotChangeSelectedNutrition()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new(context);
        await catalog.AddAsync(CreateProduct("Молоко", 60.00m));
        Product sameValue = CreateProduct("Молоко", 60m);

        string alias = await catalog.CreateSelectionAliasAsync(sameValue);

        Assert.Equal(60m, Assert.Single(await catalog.FindByNameAsync(alias)).NutritionPer100Grams.Calories);
    }

    private static Product CreateProduct(
        string name, decimal calories, DataQuality quality = DataQuality.Exact, string? barcode = null) =>
        new(name, new NutritionValues(calories, 3m, 4m, 5m),
            new NutritionSource(NutritionSourceKind.ManualInput, quality, "Personal input"), barcode);

    private static Product CreateSharedProduct() =>
        new("Общее молоко", new NutritionValues(60m, 3m, 4m, 5m),
            new NutritionSource(NutritionSourceKind.ExternalService, DataQuality.Unknown,
                "Public test provider", "https://example.com/product/12345678"), "12345678");

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

    private static Task<int> AliasCountAsync(NutriFlowDbContext context) =>
        context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM \"ProductAliases\"").SingleAsync();

    private sealed class StubExternalProductProvider(Product product) : IExternalProductProvider
    {
        public Task<Product?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken = default) =>
            Task.FromResult<Product?>(product);
    }
}
