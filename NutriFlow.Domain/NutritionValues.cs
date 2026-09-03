namespace NutriFlow.Domain;

public sealed class NutritionValues
{
    public NutritionValues(
        decimal calories,
        decimal proteinGrams,
        decimal fatGrams,
        decimal carbohydratesGrams)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(calories);
        ArgumentOutOfRangeException.ThrowIfNegative(proteinGrams);
        ArgumentOutOfRangeException.ThrowIfNegative(fatGrams);
        ArgumentOutOfRangeException.ThrowIfNegative(carbohydratesGrams);

        Calories = calories;
        ProteinGrams = proteinGrams;
        FatGrams = fatGrams;
        CarbohydratesGrams = carbohydratesGrams;
    }

    public decimal Calories { get; }
    public decimal ProteinGrams { get; }
    public decimal FatGrams { get; }
    public decimal CarbohydratesGrams { get; }

    public NutritionValues Add(NutritionValues other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new NutritionValues(
            calories: Calories + other.Calories,
            proteinGrams: ProteinGrams + other.ProteinGrams,
            fatGrams: FatGrams + other.FatGrams,
            carbohydratesGrams: CarbohydratesGrams + other.CarbohydratesGrams);
    }

    public NutritionValues ScaleBy(decimal factor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(factor);

        return new NutritionValues(
            calories: Calories * factor,
            proteinGrams: ProteinGrams * factor,
            fatGrams: FatGrams * factor,
            carbohydratesGrams: CarbohydratesGrams * factor);
    }
}
