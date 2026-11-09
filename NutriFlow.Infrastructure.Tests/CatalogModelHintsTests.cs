using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.ExternalProducts;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class CatalogModelHintsTests
{
    [Fact]
    public async Task GetModelHintsAsync_EmptyTextReturns100NewestOwnProducts()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new(context);
        string[] names = Enumerable.Range(0, 120).Select(index => $"Продукт {index:D3}").ToArray();
        foreach (string name in names)
        {
            Assert.True(await catalog.AddAsync(CreateProduct(name)));
        }

        Guid otherOwner = await CreateOwnerAsync(context);
        await new LocalProductCatalog(context, otherOwner).AddAsync(CreateProduct("Чужой продукт"));
        Product shared = CreateSharedProduct();
        await new ProductLookupService(catalog, new StubExternalProductProvider(shared)).FindByBarcodeAsync(shared.Barcode!);

        IReadOnlyList<string> hints = await catalog.GetModelHintsAsync("");

        Assert.Equal(names.Skip(20).Reverse(), hints);
        Assert.DoesNotContain("Чужой продукт", hints);
        Assert.DoesNotContain(shared.Name, hints);
    }

    [Fact]
    public async Task GetModelHintsAsync_AppendsAtMost50WordMatchesTo100RecentProducts()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new(context);
        string[] matches = Enumerable.Range(0, 60).Select(index => $"Гречка варёная {index:D2}").ToArray();
        string[] recent = Enumerable.Range(0, 100).Select(index => $"Продукт {index:D3}").ToArray();
        foreach (string name in matches.Concat(recent))
        {
            Assert.True(await catalog.AddAsync(CreateProduct(name)));
        }

        IReadOnlyList<string> hints = await catalog.GetModelHintsAsync("гречки 150 г");

        Assert.Equal(150, hints.Count);
        Assert.Equal(recent.Reverse(), hints.Take(100));
        Assert.Equal(matches.Skip(10).Reverse(), hints.Skip(100));
        Assert.Equal(hints.Count, hints.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("гречки", "Гречка варёная")]
    [InlineData("яйца", "Яйцо куриное")]
    public async Task GetModelHintsAsync_WordStemFindsOtherFormOutsideRecentProducts(string text, string productName)
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new(context);
        await catalog.AddAsync(CreateProduct(productName));
        foreach (int index in Enumerable.Range(0, 100))
        {
            await catalog.AddAsync(CreateProduct($"Продукт {index:D3}"));
        }

        IReadOnlyList<string> hints = await catalog.GetModelHintsAsync(text);

        Assert.Equal(101, hints.Count);
        Assert.Equal(productName, hints[^1]);
        Assert.Equal(hints.Count, hints.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task GetModelHintsAsync_RepeatedWordsDoNotDuplicateRecentOrMatchedNames()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new(context);
        await catalog.AddAsync(CreateProduct("Гречка варёная"));
        foreach (int index in Enumerable.Range(0, 100))
        {
            await catalog.AddAsync(CreateProduct($"Продукт {index:D3}"));
        }

        await catalog.AddAsync(CreateProduct("Гречка домашняя"));

        IReadOnlyList<string> hints = await catalog.GetModelHintsAsync("гречки гречка гречки");

        Assert.Equal(101, hints.Count);
        Assert.Contains("Гречка варёная", hints);
        Assert.Contains("Гречка домашняя", hints);
        Assert.Equal(hints.Count, hints.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task GetModelHintsAsync_RecentNamesakesAreIncludedOnlyOnce()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new(context);
        Assert.True(await catalog.AddAsync(CreateProduct("Яблоко", 50m)));
        Assert.True(await catalog.AddAsync(CreateProduct("Яблоко", 80m)));

        IReadOnlyList<string> hints = await catalog.GetModelHintsAsync("яблоки");

        Assert.Equal("Яблоко", Assert.Single(hints));
    }

    [Fact]
    public async Task FindByIdAsync_SharedRecordHiddenByPersonalBarcodeReturnsNull()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new(context);
        Product shared = CreateSharedProduct();
        await new ProductLookupService(catalog, new StubExternalProductProvider(shared)).FindByBarcodeAsync(shared.Barcode!);
        StoredProduct storedShared = Assert.IsType<StoredProduct>(await catalog.FindStoredByBarcodeAsync(shared.Barcode!));
        Assert.NotNull(await catalog.FindByIdAsync(storedShared.Id));
        Product personal = CreateProduct("Моё молоко", 80m, shared.Barcode);

        StoredProduct storedPersonal = Assert.IsType<StoredProduct>(await catalog.AddStoredAsync(personal));

        Assert.Null(await catalog.FindByIdAsync(storedShared.Id));
        StoredProduct visible = Assert.IsType<StoredProduct>(await catalog.FindByIdAsync(storedPersonal.Id));
        Assert.Equal(personal.Name, visible.Product.Name);
        Assert.Equal(80m, visible.Product.NutritionPer100Grams.Calories);
        Assert.Equal(storedPersonal.Id, (await catalog.FindStoredByBarcodeAsync(shared.Barcode!))!.Id);
    }

    [Fact]
    public async Task FindByIdAsync_AnotherOwnersProductReturnsNull()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog local = new(context);
        Guid otherOwner = await CreateOwnerAsync(context);
        LocalProductCatalog other = new(context, otherOwner);
        StoredProduct stored = Assert.IsType<StoredProduct>(await other.AddStoredAsync(CreateProduct("Чужое молоко")));

        Assert.NotNull(await other.FindByIdAsync(stored.Id));
        Assert.Null(await local.FindByIdAsync(stored.Id));
    }

    [Fact]
    public async Task CreateSelectionAliasAsync_UnknownIdThrowsWithoutAddingAlias()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new(context);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => catalog.CreateSelectionAliasAsync(int.MaxValue));

        Assert.Equal(0, await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM \"ProductAliases\"").SingleAsync());
    }

    [Fact]
    public async Task AddStoredAsync_ReturnsPersistentIdAndNullForIdenticalDuplicate()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        int id;
        await using (NutriFlowDbContext context = database.CreateContext())
        {
            LocalProductCatalog catalog = new(context);

            StoredProduct stored = Assert.IsType<StoredProduct>(await catalog.AddStoredAsync(CreateProduct("Молоко", 60.125m)));

            Assert.True(stored.Id > 0);
            id = stored.Id;
            Assert.Equal("Молоко", stored.Product.Name);
            Assert.Equal(60.125m, stored.Product.NutritionPer100Grams.Calories);
            Assert.Null(await catalog.AddStoredAsync(CreateProduct("Молоко", 60.125m)));
            Assert.Equal(id, Assert.Single(await catalog.FindStoredByNameAsync("Молоко")).Id);
        }

        await using NutriFlowDbContext reopened = database.CreateContext();
        StoredProduct found = Assert.IsType<StoredProduct>(await new LocalProductCatalog(reopened).FindByIdAsync(id));
        Assert.Equal(id, found.Id);
        Assert.Equal("Молоко", found.Product.Name);
        Assert.Equal(60.125m, found.Product.NutritionPer100Grams.Calories);
    }

    private static Product CreateProduct(string name, decimal calories = 100m, string? barcode = null) =>
        new(name, new NutritionValues(calories, 3m, 4m, 5m),
            new NutritionSource(NutritionSourceKind.ManualInput, DataQuality.Exact, "Personal input"), barcode);

    private static Product CreateSharedProduct() =>
        new("Общее молоко", new NutritionValues(60m, 3m, 4m, 5m),
            new NutritionSource(NutritionSourceKind.ExternalService, DataQuality.Unknown,
                "Public test provider", "https://example.com/product/12345678"), "12345678");

    private static async Task<Guid> CreateOwnerAsync(NutriFlowDbContext context)
    {
        Guid owner = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Users" ("Id", "CreatedAtUtc", "IsLegacyLocal")
            VALUES ({owner}, {DateTimeOffset.UtcNow}, {false})
            """);
        return owner;
    }

    private sealed class StubExternalProductProvider(Product product) : IExternalProductProvider
    {
        public Task<Product?> FindByBarcodeAsync(string barcode, CancellationToken cancellationToken = default) =>
            Task.FromResult<Product?>(product);
    }
}
