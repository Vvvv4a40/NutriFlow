namespace NutriFlow.Domain;

public sealed class DailyGoal
{
    public DailyGoal(NutritionValues targetNutrition)
    {
        ArgumentNullException.ThrowIfNull(targetNutrition);

        TargetNutrition = targetNutrition;
    }

    public NutritionValues TargetNutrition { get; }
}
