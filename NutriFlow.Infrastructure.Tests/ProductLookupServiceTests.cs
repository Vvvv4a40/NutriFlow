using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.ExternalProducts;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class ProductLookupServiceTests
{
    [Fact]
    public async Task FindByBarcodeAsync_WhenProductIsLocal_DoesNotCallExternalProvider()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Product localProduct = CreateProduct("Local product", "12345678");

        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new LocalProductCatalog(context);
        await catalog.AddAsync(localProduct);
        StubExternalProductProvider provider = new StubExternalProductProvider(null);
        ProductLookupService service = new ProductLookupService(catalog, provider);

        Product product = Assert.IsType<Product>(
            await service.FindByBarcodeAsync("12345678"));

        Assert.Equal("Local product", product.Name);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task FindByBarcodeAsync_WhenProductIsExternal_CachesProduct()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Product externalProduct = CreateProduct("External product", "12345678");
        StubExternalProductProvider provider = new StubExternalProductProvider(
            externalProduct);

        await using NutriFlowDbContext firstContext = database.CreateContext();
        LocalProductCatalog firstCatalog = new LocalProductCatalog(firstContext);
        ProductLookupService firstService = new ProductLookupService(
            firstCatalog,
            provider);

        Product firstResult = Assert.IsType<Product>(
            await firstService.FindByBarcodeAsync("12345678"));

        Assert.Equal("External product", firstResult.Name);
        Assert.Equal(1, provider.CallCount);

        await using NutriFlowDbContext secondContext = database.CreateContext();
        LocalProductCatalog secondCatalog = new LocalProductCatalog(secondContext);
        ProductLookupService secondService = new ProductLookupService(
            secondCatalog,
            provider);

        Product secondResult = Assert.IsType<Product>(
            await secondService.FindByBarcodeAsync("12345678"));

        Assert.Equal("External product", secondResult.Name);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task FindByBarcodeAsync_WhenProductIsNowhere_DoesNotCacheAnything()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();

        await using NutriFlowDbContext context = database.CreateContext();
        LocalProductCatalog catalog = new LocalProductCatalog(context);
        StubExternalProductProvider provider = new StubExternalProductProvider(null);
        ProductLookupService service = new ProductLookupService(catalog, provider);

        Product? product = await service.FindByBarcodeAsync("12345678");

        Assert.Null(product);
        Assert.Null(await catalog.FindByBarcodeAsync("12345678"));
        Assert.Equal(1, provider.CallCount);
    }

    private static Product CreateProduct(string name, string barcode)
    {
        return new Product(
            name,
            new NutritionValues(100m, 10m, 5m, 4m),
            new NutritionSource(
                NutritionSourceKind.ExternalService,
                DataQuality.Unknown,
                "External provider",
                $"https://example.com/product/{barcode}"),
            barcode);
    }

    private sealed class StubExternalProductProvider : IExternalProductProvider
    {
        private readonly Product? _product;

        public StubExternalProductProvider(Product? product)
        {
            _product = product;
        }

        public int CallCount { get; private set; }

        public Task<Product?> FindByBarcodeAsync(
            string barcode,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_product);
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"nutriflow-lookup-{Guid.NewGuid():N}.db");

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
