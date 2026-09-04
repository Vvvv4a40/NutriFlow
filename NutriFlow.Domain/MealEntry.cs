namespace NutriFlow.Domain;

public sealed class MealEntry
{
    public MealEntry(
        string name,
        decimal weightInGrams,
        NutritionValues nutrition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weightInGrams);
        ArgumentNullException.ThrowIfNull(nutrition);

        Name = name;
        WeightInGrams = weightInGrams;
        Nutrition = nutrition;
    }

    public string Name { get; }
    public decimal WeightInGrams { get; }
    public NutritionValues Nutrition { get; }
}
