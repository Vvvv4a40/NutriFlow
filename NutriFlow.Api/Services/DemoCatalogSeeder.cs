using NutriFlow.Domain;
using NutriFlow.Infrastructure;

namespace NutriFlow.Api.Services;

public static class DemoCatalogSeeder
{
    public static async Task SeedAsync(
        LocalProductCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        NutritionSource source = new NutritionSource(
            NutritionSourceKind.NutriFlowCatalog,
            DataQuality.Unknown,
            "NutriFlow demo dataset");
        Product[] products =
        {
            new Product(
                "Демо-продукт A",
                new NutritionValues(100m, 10m, 4m, 6m),
                source),
            new Product(
                "Демо-продукт B",
                new NutritionValues(200m, 5m, 12m, 18m),
                source)
        };

        foreach (Product product in products)
        {
            await catalog.AddAsync(product, cancellationToken);
        }
    }
}
