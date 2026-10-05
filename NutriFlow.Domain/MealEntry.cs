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

    public MealEntry WithWeight(decimal weightInGrams, DataQuality weightQuality = DataQuality.Exact)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weightInGrams);

        if (weightQuality is not (DataQuality.Exact or DataQuality.Estimated))
        {
            throw new ArgumentOutOfRangeException(nameof(weightQuality));
        }

        DataQuality quality = Quality == DataQuality.Unknown
            ? DataQuality.Unknown
            : Quality == DataQuality.Estimated || weightQuality == DataQuality.Estimated
                ? DataQuality.Estimated
                : Quality;

        return new MealEntry(Name, weightInGrams,
            Nutrition.ScaleBy(weightInGrams / WeightInGrams), quality);
    }
}
