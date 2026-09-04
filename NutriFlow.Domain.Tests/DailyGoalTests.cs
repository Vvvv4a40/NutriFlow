using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class DailyGoalTests
{
    [Fact]
    public void Constructor_StoresTargetNutrition()
    {
        NutritionValues targetNutrition = new NutritionValues(2000m, 100m, 70m, 250m);

        DailyGoal goal = new DailyGoal(targetNutrition);

        Assert.Same(targetNutrition, goal.TargetNutrition);
    }

    [Fact]
    public void Constructor_WithNullTargetNutrition_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new DailyGoal(null!));
    }
}
