namespace NutriFlow.Domain;

public sealed class MealEntry
{
    public MealEntry(
        string name,
        decimal weightInGrams,
        NutritionValues nutrition,
        DataQuality quality = DataQuality.Unknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weightInGrams);
        ArgumentNullException.ThrowIfNull(nutrition);

        if (!Enum.IsDefined(quality))
        {
            throw new ArgumentOutOfRangeException(nameof(quality));
        }

        Name = name;
        WeightInGrams = weightInGrams;
        Nutrition = nutrition;
        Quality = quality;
    }

    public string Name { get; }
    public decimal WeightInGrams { get; }
    public NutritionValues Nutrition { get; }
    public DataQuality Quality { get; }
}
