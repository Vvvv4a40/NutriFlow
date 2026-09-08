using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class MealEntryTests
{
    [Fact]
    public void Constructor_WithQuality_StoresQuality()
    {
        MealEntry entry = new MealEntry(
            "Breakfast",
            250m,
            new NutritionValues(300m, 20m, 10m, 40m),
            DataQuality.Estimated);

        Assert.Equal(DataQuality.Estimated, entry.Quality);
    }

    [Fact]
    public void Constructor_StoresNameWeightAndNutrition()
    {
        NutritionValues nutrition = new NutritionValues(300m, 20m, 10m, 40m);

        MealEntry mealEntry = new MealEntry("Breakfast", 250m, nutrition);

        Assert.Equal("Breakfast", mealEntry.Name);
        Assert.Equal(250m, mealEntry.WeightInGrams);
        Assert.Same(nutrition, mealEntry.Nutrition);
    }

    [Fact]
    public void Constructor_WithNullName_ThrowsArgumentNullException()
    {
        NutritionValues nutrition = new NutritionValues(300m, 20m, 10m, 40m);

        Assert.Throws<ArgumentNullException>(() => new MealEntry(null!, 250m, nutrition));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithBlankName_ThrowsArgumentException(string name)
    {
        NutritionValues nutrition = new NutritionValues(300m, 20m, 10m, 40m);

        Assert.Throws<ArgumentException>(() => new MealEntry(name, 250m, nutrition));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithNonPositiveWeight_ThrowsArgumentOutOfRangeException(
        int weightInGrams)
    {
        NutritionValues nutrition = new NutritionValues(300m, 20m, 10m, 40m);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MealEntry("Breakfast", weightInGrams, nutrition));
    }

    [Fact]
    public void Constructor_WithNullNutrition_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new MealEntry("Breakfast", 250m, null!));
    }
}
