namespace NutriFlow.Domain;

public sealed class DailyProgress
{
    private NutritionValues? _consumedNutrition;

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
        if (_consumedNutrition is not null)
        {
            return _consumedNutrition;
        }

        NutritionValues consumedNutrition = new NutritionValues(
            calories: 0m,
            proteinGrams: 0m,
            fatGrams: 0m,
            carbohydratesGrams: 0m);

        foreach (MealEntry mealEntry in MealEntries)
        {
            consumedNutrition = consumedNutrition.Add(mealEntry.Nutrition);
        }

        _consumedNutrition = consumedNutrition;
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
            calories: PositiveDifference(minuend.Calories, subtrahend.Calories),
            proteinGrams: PositiveDifference(minuend.ProteinGrams, subtrahend.ProteinGrams),
            fatGrams: PositiveDifference(minuend.FatGrams, subtrahend.FatGrams),
            carbohydratesGrams: PositiveDifference(minuend.CarbohydratesGrams, subtrahend.CarbohydratesGrams));
    }

    private static decimal PositiveDifference(decimal left, decimal right) => left > right ? left - right : 0m;
}
