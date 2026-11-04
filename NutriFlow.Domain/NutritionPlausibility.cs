namespace NutriFlow.Domain;

// checks numbers that came from a model against the energy of the macros (4 kcal/g protein and carbohydrates, 9 kcal/g fat)
public static class NutritionPlausibility
{
    private const decimal MaximumCalories = 900m;
    private const decimal MaximumGrams = 100m;

    // a food cannot hold more than 100 g of macros in 100 g or much less energy than its macros give.
    // the energy can be above them: alcohol and polyols are not among the macros
    public static bool IsPossible(decimal calories, decimal protein, decimal fat, decimal carbohydrates)
    {
        if (calories is < 0m or > MaximumCalories ||
            protein < 0m || fat < 0m || carbohydrates < 0m ||
            protein + fat + carbohydrates > MaximumGrams)
        {
            return false;
        }

        decimal energy = EnergyOfMacros(protein, fat, carbohydrates);

        return energy > 0m ? calories >= energy * 0.6m : calories >= 0m;
    }

    // the printed energy and the printed macros disagree: a digit was probably misread
    public static bool MacrosMatchCalories(decimal calories, decimal protein, decimal fat, decimal carbohydrates)
    {
        decimal difference = Math.Abs(calories - EnergyOfMacros(protein, fat, carbohydrates));

        return difference <= Math.Max(25m, calories * 0.2m);
    }

    private static decimal EnergyOfMacros(decimal protein, decimal fat, decimal carbohydrates) =>
        protein * 4m + fat * 9m + carbohydrates * 4m;
}
