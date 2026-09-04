using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class DailyProgressTests
{
    [Fact]
    public void CalculateConsumedNutrition_SumsMealEntries()
    {
        DailyProgress progress = new DailyProgress(
            CreateGoal(),
            new List<MealEntry>
            {
                CreateMealEntry("Breakfast", 300m, 20m, 10m, 40m),
                CreateMealEntry("Lunch", 500m, 30m, 20m, 60m)
            });

        NutritionValues result = progress.CalculateConsumedNutrition();

        AssertNutrition(result, 800m, 50m, 30m, 100m);
    }

    [Fact]
    public void CalculateConsumedNutrition_WithNoEntries_ReturnsZero()
    {
        DailyProgress progress = new DailyProgress(
            CreateGoal(),
            new List<MealEntry>());

        NutritionValues result = progress.CalculateConsumedNutrition();

        AssertNutrition(result, 0m, 0m, 0m, 0m);
    }

    [Fact]
    public void CalculateRemainingNutrition_SubtractsConsumedFromGoal()
    {
        DailyProgress progress = new DailyProgress(
            CreateGoal(),
            new List<MealEntry>
            {
                CreateMealEntry("Breakfast", 300m, 20m, 10m, 40m),
                CreateMealEntry("Lunch", 500m, 30m, 20m, 60m)
            });

        NutritionValues result = progress.CalculateRemainingNutrition();

        AssertNutrition(result, 1200m, 50m, 40m, 150m);
    }

    [Fact]
    public void CalculateRemainingNutrition_WhenGoalIsExceeded_StopsAtZero()
    {
        DailyProgress progress = new DailyProgress(
            CreateGoal(),
            new List<MealEntry>
            {
                CreateMealEntry("Meal", 2100m, 110m, 65m, 260m)
            });

        NutritionValues result = progress.CalculateRemainingNutrition();

        AssertNutrition(result, 0m, 0m, 5m, 0m);
    }

    [Fact]
    public void CalculateExceededNutrition_ReturnsOnlyExceededAmounts()
    {
        DailyProgress progress = new DailyProgress(
            CreateGoal(),
            new List<MealEntry>
            {
                CreateMealEntry("Meal", 2100m, 110m, 65m, 260m)
            });

        NutritionValues result = progress.CalculateExceededNutrition();

        AssertNutrition(result, 100m, 10m, 0m, 10m);
    }

    [Fact]
    public void CalculateExceededNutrition_WhenGoalIsNotExceeded_ReturnsZero()
    {
        DailyProgress progress = new DailyProgress(
            CreateGoal(),
            new List<MealEntry>
            {
                CreateMealEntry("Meal", 800m, 50m, 30m, 100m)
            });

        NutritionValues result = progress.CalculateExceededNutrition();

        AssertNutrition(result, 0m, 0m, 0m, 0m);
    }

    [Fact]
    public void Constructor_WithNullGoal_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DailyProgress(null!, new List<MealEntry>()));
    }

    [Fact]
    public void Constructor_WithNullMealEntries_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DailyProgress(CreateGoal(), null!));
    }

    [Fact]
    public void Constructor_WithNullMealEntry_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new DailyProgress(
                CreateGoal(),
                new List<MealEntry> { null! }));
    }

    [Fact]
    public void Constructor_CopiesMealEntries()
    {
        List<MealEntry> mealEntries = new List<MealEntry>
        {
            CreateMealEntry("Breakfast", 300m, 20m, 10m, 40m)
        };
        DailyProgress progress = new DailyProgress(CreateGoal(), mealEntries);

        mealEntries.Clear();

        Assert.Single(progress.MealEntries);
    }

    private static DailyGoal CreateGoal()
    {
        return new DailyGoal(
            new NutritionValues(2000m, 100m, 70m, 250m));
    }

    private static MealEntry CreateMealEntry(
        string name,
        decimal calories,
        decimal proteinGrams,
        decimal fatGrams,
        decimal carbohydratesGrams)
    {
        return new MealEntry(
            name,
            100m,
            new NutritionValues(
                calories,
                proteinGrams,
                fatGrams,
                carbohydratesGrams));
    }

    private static void AssertNutrition(
        NutritionValues actual,
        decimal calories,
        decimal proteinGrams,
        decimal fatGrams,
        decimal carbohydratesGrams)
    {
        Assert.Equal(calories, actual.Calories);
        Assert.Equal(proteinGrams, actual.ProteinGrams);
        Assert.Equal(fatGrams, actual.FatGrams);
        Assert.Equal(carbohydratesGrams, actual.CarbohydratesGrams);
    }
}
