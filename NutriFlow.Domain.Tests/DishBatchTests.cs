using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class DishBatchTests
{
    [Fact]
    public void CalculateTotalNutrition_SumsIngredientNutrition()
    {
        DishBatch dish = CreateDish();

        NutritionValues result = dish.CalculateTotalNutrition();

        Assert.Equal(400m, result.Calories);
        Assert.Equal(25m, result.ProteinGrams);
        Assert.Equal(20m, result.FatGrams);
        Assert.Equal(30m, result.CarbohydratesGrams);
    }

    [Fact]
    public void CalculateNutritionPer100Grams_UsesFinalDishWeight()
    {
        DishBatch dish = CreateDish();

        NutritionValues result = dish.CalculateNutritionPer100Grams();

        Assert.Equal(160m, result.Calories);
        Assert.Equal(10m, result.ProteinGrams);
        Assert.Equal(8m, result.FatGrams);
        Assert.Equal(12m, result.CarbohydratesGrams);
    }

    [Fact]
    public void CalculatePortionNutrition_UsesPortionShareOfFinalWeight()
    {
        DishBatch dish = CreateDish();

        NutritionValues result = dish.CalculatePortionNutrition(125m);

        Assert.Equal(200m, result.Calories);
        Assert.Equal(12.5m, result.ProteinGrams);
        Assert.Equal(10m, result.FatGrams);
        Assert.Equal(15m, result.CarbohydratesGrams);
    }

    [Fact]
    public void CalculatePortionNutrition_WithWholeDishWeight_ReturnsTotalNutrition()
    {
        DishBatch dish = CreateDish();

        NutritionValues result = dish.CalculatePortionNutrition(250m);

        Assert.Equal(400m, result.Calories);
        Assert.Equal(25m, result.ProteinGrams);
        Assert.Equal(20m, result.FatGrams);
        Assert.Equal(30m, result.CarbohydratesGrams);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithNonPositiveFinalWeight_ThrowsArgumentOutOfRangeException(
        int finalWeightInGrams)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DishBatch(
                "Dish",
                CreateIngredients(),
                finalWeightInGrams));
    }

    [Fact]
    public void Constructor_WithEmptyIngredients_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new DishBatch(
                "Dish",
                new List<DishIngredient>(),
                250m));
    }

    [Fact]
    public void Constructor_WithNullIngredients_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DishBatch("Dish", null!, 250m));
    }

    [Fact]
    public void Constructor_WithNullIngredient_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new DishBatch(
                "Dish",
                new List<DishIngredient> { null! },
                250m));
    }

    [Fact]
    public void Constructor_WithNullName_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DishBatch(null!, CreateIngredients(), 250m));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithBlankName_ThrowsArgumentException(string name)
    {
        Assert.Throws<ArgumentException>(
            () => new DishBatch(name, CreateIngredients(), 250m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(251)]
    public void CalculatePortionNutrition_WithInvalidWeight_ThrowsArgumentOutOfRangeException(
        int portionWeightInGrams)
    {
        DishBatch dish = CreateDish();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => dish.CalculatePortionNutrition(portionWeightInGrams));
    }

    private static DishBatch CreateDish()
    {
        return new DishBatch(
            name: "Dish",
            ingredients: CreateIngredients(),
            finalWeightInGrams: 250m);
    }

    private static IReadOnlyList<DishIngredient> CreateIngredients()
    {
        Product productA = new Product(
            "Product A",
            new NutritionValues(100m, 10m, 4m, 6m),
            CreateSource());
        Product productB = new Product(
            "Product B",
            new NutritionValues(200m, 5m, 12m, 18m),
            CreateSource());

        return new List<DishIngredient>
        {
            new DishIngredient(productA, 200m),
            new DishIngredient(productB, 100m)
        };
    }

    private static NutritionSource CreateSource()
    {
        return new NutritionSource(
            NutritionSourceKind.ManualInput,
            DataQuality.Exact,
            "Test data");
    }
}
