using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class MealEntryWeightTests
{
    [Theory]
    [InlineData(50, 80, 5, 4, 6)]
    [InlineData(100, 160, 10, 8, 12)]
    [InlineData(250, 400, 25, 20, 30)]
    public void WithWeight_ScalesAllNutritionWithoutChangingOriginal(
        int weight, int calories, int protein, int fat, int carbohydrates)
    {
        MealEntry original = new MealEntry("Stew", 125m,
            new NutritionValues(200m, 12.5m, 10m, 15m), DataQuality.Verified);

        MealEntry updated = original.WithWeight(weight);

        Assert.Equal(original.Name, updated.Name);
        Assert.Equal(weight, updated.WeightInGrams);
        Assert.Equal(calories, updated.Nutrition.Calories);
        Assert.Equal(protein, updated.Nutrition.ProteinGrams);
        Assert.Equal(fat, updated.Nutrition.FatGrams);
        Assert.Equal(carbohydrates, updated.Nutrition.CarbohydratesGrams);
        Assert.Equal(125m, original.WeightInGrams);
        Assert.Equal(200m, original.Nutrition.Calories);
    }

    [Theory]
    [InlineData(DataQuality.Exact, DataQuality.Exact, DataQuality.Exact)]
    [InlineData(DataQuality.Verified, DataQuality.Exact, DataQuality.Verified)]
    [InlineData(DataQuality.Estimated, DataQuality.Exact, DataQuality.Estimated)]
    [InlineData(DataQuality.Unknown, DataQuality.Exact, DataQuality.Unknown)]
    [InlineData(DataQuality.Exact, DataQuality.Estimated, DataQuality.Estimated)]
    [InlineData(DataQuality.Verified, DataQuality.Estimated, DataQuality.Estimated)]
    [InlineData(DataQuality.Unknown, DataQuality.Estimated, DataQuality.Unknown)]
    public void WithWeight_DoesNotUpgradeSnapshotQuality(
        DataQuality originalQuality, DataQuality weightQuality, DataQuality expectedQuality)
    {
        MealEntry entry = new MealEntry("Stew", 125m,
            new NutritionValues(200m, 12.5m, 10m, 15m), originalQuality);

        Assert.Equal(expectedQuality, entry.WithWeight(100m, weightQuality).Quality);
    }

    [Fact]
    public void WithWeight_PreservesDecimalPrecisionAndZeroNutrients()
    {
        MealEntry entry = new MealEntry("Stew", 2m,
            new NutritionValues(1.234567890123456789012345678m, 0m, 0.125m, 0m));

        MealEntry updated = entry.WithWeight(1m);

        Assert.Equal(0.617283945061728394506172839m, updated.Nutrition.Calories);
        Assert.Equal(0m, updated.Nutrition.ProteinGrams);
        Assert.Equal(0.0625m, updated.Nutrition.FatGrams);
        Assert.Equal(0m, updated.Nutrition.CarbohydratesGrams);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WithWeight_RejectsNonPositiveWeight(int weight)
    {
        MealEntry entry = new MealEntry("Stew", 125m, new NutritionValues(200m, 0m, 0m, 0m));

        Assert.Throws<ArgumentOutOfRangeException>(() => entry.WithWeight(weight));
    }

    [Theory]
    [InlineData(DataQuality.Unknown)]
    [InlineData(DataQuality.Verified)]
    [InlineData((DataQuality)99)]
    public void WithWeight_RejectsUnsupportedWeightQuality(DataQuality quality)
    {
        MealEntry entry = new MealEntry("Stew", 125m, new NutritionValues(200m, 0m, 0m, 0m));

        Assert.Throws<ArgumentOutOfRangeException>(() => entry.WithWeight(100m, quality));
    }

    [Fact]
    public void WithWeight_DoesNotSilentlyClampOverflow()
    {
        MealEntry entry = new MealEntry("Stew", 1m, new NutritionValues(decimal.MaxValue, 0m, 0m, 0m));

        Assert.Throws<OverflowException>(() => entry.WithWeight(2m));
    }
}
