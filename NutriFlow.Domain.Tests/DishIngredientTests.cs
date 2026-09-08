using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class DishIngredientTests
{
    [Fact]
    public void CalculateNutrition_UsesIngredientWeight()
    {
        Product product = new Product(
            "Product",
            new NutritionValues(200m, 10m, 8m, 24m),
            CreateSource());
        DishIngredient ingredient = new DishIngredient(product, 175m);

        NutritionValues result = ingredient.CalculateNutrition();

        Assert.Equal(350m, result.Calories);
        Assert.Equal(17.5m, result.ProteinGrams);
        Assert.Equal(14m, result.FatGrams);
        Assert.Equal(42m, result.CarbohydratesGrams);
    }

    [Fact]
    public void CalculateNutrition_SubtractsExplicitlyRemovedWeight()
    {
        DishIngredient ingredient = new DishIngredient(
            CreateProduct(),
            200m,
            50m);

        NutritionValues result = ingredient.CalculateNutrition();

        Assert.Equal(150m, result.Calories);
        Assert.Equal(15m, result.ProteinGrams);
        Assert.Equal(6m, result.FatGrams);
        Assert.Equal(9m, result.CarbohydratesGrams);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithNonPositiveWeight_ThrowsArgumentOutOfRangeException(
        int weightInGrams)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DishIngredient(CreateProduct(), weightInGrams));
    }

    [Fact]
    public void Constructor_WithNullProduct_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DishIngredient(null!, 100m));
    }

    private static Product CreateProduct()
    {
        return new Product(
            "Product",
            new NutritionValues(100m, 10m, 4m, 6m),
            CreateSource());
    }

    private static NutritionSource CreateSource()
    {
        return new NutritionSource(
            NutritionSourceKind.ManualInput,
            DataQuality.Exact,
            "Test data");
    }
}
