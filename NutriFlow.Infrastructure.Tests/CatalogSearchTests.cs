using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.ExternalProducts;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class CatalogSearchTests
{
    [Fact]
    public async Task SearchAsync_ReturnsOnlyPersonalAndSharedProductsInStableOrder()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new(context, firstOwner);
        await catalog.AddAsync(CreatePersonal("Яблоко", 50m));
        await catalog.AddAsync(CreatePersonal("Гречка", 320m));
        await new LocalProductCatalog(context, secondOwner).AddAsync(CreatePersonal("Чужая гречка", 340m));
        Product shared = CreateShared("Молоко", "12345678");
        await CacheSharedAsync(catalog, shared);

        Assert.Equal(["Гречка", "Молоко", "Яблоко"],
            (await catalog.SearchAsync()).Select(product => product.Name));
        Assert.Equal(["Гречка", "Молоко", "Яблоко"],
            (await catalog.SearchAsync(" ")).Select(product => product.Name));
        Assert.Equal(["Гречка", "Молоко"],
            (await catalog.SearchAsync(limit: 2)).Select(product => product.Name));
        Assert.Empty(await catalog.SearchAsync("чужая"));
    }

    [Fact]
    public async Task SearchAsync_NormalizesSubstringAndIncludesOwnAliasesAndBarcodes()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new(context, firstOwner);
        await catalog.AddAsync(CreatePersonal("Йогурт", 60m, "12345678"));
        await catalog.AddAliasByBarcodeAsync("12345678", "Для завтрака");
        Product shared = CreateShared("Молоко", "87654321");
        await CacheSharedAsync(catalog, shared);
        await new LocalProductCatalog(context, secondOwner).AddAliasByBarcodeAsync("87654321", "Чужой завтрак");

        Assert.Equal("Йогурт", Assert.Single(await catalog.SearchAsync("  И\u0306ОГУ  ")).Name);
        Assert.Equal("Йогурт", Assert.Single(await catalog.SearchAsync("завтра")).Name);
        Assert.Equal("Йогурт", Assert.Single(await catalog.SearchAsync("2345")).Name);
        Assert.Empty(await catalog.SearchAsync("чужой"));
        Assert.Equal("Молоко", Assert.Single(await catalog.SearchAsync("лок")).Name);
    }

    [Fact]
    public async Task SearchAsync_UsesPersonalOverrideForSharedNameAndAliasWithoutDuplicates()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        Guid firstOwner = await CreateOwnerAsync(database);
        Guid secondOwner = await CreateOwnerAsync(database);
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog first = new(context, firstOwner);
        await CacheSharedAsync(first, CreateShared("Молоко фермерское", "12345678"));
        await first.AddAliasByBarcodeAsync("12345678", "Для каши");
        await first.AddAsync(CreatePersonal("Мой напиток", 80.125m, "12345678"));
        LocalProductCatalog second = new(context, secondOwner);
        await second.AddAsync(CreatePersonal("Другой напиток", 90.875m, "12345678"));

        Product own = Assert.Single(await first.SearchAsync());
        Assert.Equal("Мой напиток", own.Name);
        Assert.Equal(80.125m, own.NutritionPer100Grams.Calories);
        Assert.Equal("Мой напиток", Assert.Single(await first.SearchAsync("фермер")).Name);
        Assert.Equal("Мой напиток", Assert.Single(await first.SearchAsync("каши")).Name);
        Assert.Equal("Мой напиток", Assert.Single(await first.SearchAsync("12345678")).Name);
        Assert.Equal("Другой напиток", Assert.Single(await second.SearchAsync("фермер")).Name);
        Assert.Empty(await second.SearchAsync("каши"));
    }

    [Fact]
    public async Task SearchAsync_KeepsDistinctNutritionForProductsWithoutBarcodes()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new(context);
        await catalog.AddAsync(CreatePersonal("Яблоко", 50m));
        await catalog.AddAsync(CreatePersonal("Яблоко", 60m));

        Assert.Equal([50m, 60m],
            (await catalog.SearchAsync("ябл")).Select(product => product.NutritionPer100Grams.Calories));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public async Task SearchAsync_RejectsInvalidLimit(int limit)
    {
        await using TestDatabase database = new();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new(context, Guid.NewGuid());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => catalog.SearchAsync(limit: limit));
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

    private static Product CreatePersonal(string name, decimal calories, string? barcode = null) =>
        new(name, new NutritionValues(calories, 3m, 4m, 5m),
            new NutritionSource(NutritionSourceKind.ManualInput, DataQuality.Exact, "Personal input"), barcode);

    private static Product CreateShared(string name, string barcode) =>
        new(name, new NutritionValues(60m, 3m, 4m, 5m),
            new NutritionSource(NutritionSourceKind.ExternalService, DataQuality.Unknown,
                "Public test provider", $"https://example.com/product/{barcode}"), barcode);

    private static async Task CacheSharedAsync(LocalProductCatalog catalog, Product product)
    {
        ProductLookupService lookup = new(catalog, new StubExternalProductProvider(product));
        Assert.NotNull(await lookup.FindByBarcodeAsync(product.Barcode!));
    }

    private sealed class StubExternalProductProvider(Product product) : IExternalProductProvider
    {
        public Task<Product?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken = default) =>
            Task.FromResult<Product?>(product);
    }
}
