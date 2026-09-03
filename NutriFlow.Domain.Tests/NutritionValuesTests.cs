using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class NutritionValuesTests
{
    [Fact]
    public void Add_ReturnsComponentWiseSum()
    {
        NutritionValues left = new NutritionValues(100m, 10m, 4m, 6m);
        NutritionValues right = new NutritionValues(200m, 5m, 12m, 18m);

        NutritionValues result = left.Add(right);

        Assert.Equal(300m, result.Calories);
        Assert.Equal(15m, result.ProteinGrams);
        Assert.Equal(16m, result.FatGrams);
        Assert.Equal(24m, result.CarbohydratesGrams);
    }

    [Fact]
    public void ScaleBy_ReturnsScaledValues()
    {
        NutritionValues nutrition = new NutritionValues(200m, 10m, 8m, 24m);

        NutritionValues result = nutrition.ScaleBy(1.75m);

        Assert.Equal(350m, result.Calories);
        Assert.Equal(17.5m, result.ProteinGrams);
        Assert.Equal(14m, result.FatGrams);
        Assert.Equal(42m, result.CarbohydratesGrams);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, -1, 0, 0)]
    [InlineData(0, 0, -1, 0)]
    [InlineData(0, 0, 0, -1)]
    public void Constructor_WithNegativeValue_ThrowsArgumentOutOfRangeException(
        int calories,
        int proteinGrams,
        int fatGrams,
        int carbohydratesGrams)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NutritionValues(
                calories,
                proteinGrams,
                fatGrams,
                carbohydratesGrams));
    }

    [Fact]
    public void Add_WithNull_ThrowsArgumentNullException()
    {
        NutritionValues nutrition = new NutritionValues(100m, 10m, 4m, 6m);

        Assert.Throws<ArgumentNullException>(() => nutrition.Add(null!));
    }

    [Fact]
    public void ScaleBy_WithNegativeFactor_ThrowsArgumentOutOfRangeException()
    {
        NutritionValues nutrition = new NutritionValues(100m, 10m, 4m, 6m);

        Assert.Throws<ArgumentOutOfRangeException>(() => nutrition.ScaleBy(-0.5m));
    }
}
