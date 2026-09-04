namespace NutriFlow.Domain;

public sealed class DailyProgress
{
    public DailyProgress(
        DailyGoal goal,
        IReadOnlyList<MealEntry> mealEntries)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(mealEntries);

        foreach (MealEntry mealEntry in mealEntries)
        {
            if (mealEntry is null)
            {
                throw new ArgumentException(
                    "Meal entries cannot contain null values.",
                    nameof(mealEntries));
            }
        }

        Goal = goal;
        MealEntries = new List<MealEntry>(mealEntries).AsReadOnly();
    }

    public DailyGoal Goal { get; }
    public IReadOnlyList<MealEntry> MealEntries { get; }

    public NutritionValues CalculateConsumedNutrition()
    {
        NutritionValues consumedNutrition = new NutritionValues(
            calories: 0m,
            proteinGrams: 0m,
            fatGrams: 0m,
            carbohydratesGrams: 0m);

        foreach (MealEntry mealEntry in MealEntries)
        {
            consumedNutrition = consumedNutrition.Add(mealEntry.Nutrition);
        }

        return consumedNutrition;
    }

    public NutritionValues CalculateRemainingNutrition()
    {
        return CalculatePositiveDifference(
            Goal.TargetNutrition,
            CalculateConsumedNutrition());
    }

    public NutritionValues CalculateExceededNutrition()
    {
        return CalculatePositiveDifference(
            CalculateConsumedNutrition(),
            Goal.TargetNutrition);
    }

    private static NutritionValues CalculatePositiveDifference(
        NutritionValues minuend,
        NutritionValues subtrahend)
    {
        return new NutritionValues(
            calories: Math.Max(minuend.Calories - subtrahend.Calories, 0m),
            proteinGrams: Math.Max(
                minuend.ProteinGrams - subtrahend.ProteinGrams,
                0m),
            fatGrams: Math.Max(minuend.FatGrams - subtrahend.FatGrams, 0m),
            carbohydratesGrams: Math.Max(
                minuend.CarbohydratesGrams - subtrahend.CarbohydratesGrams,
                0m));
    }
}
