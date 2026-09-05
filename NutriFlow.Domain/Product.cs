namespace NutriFlow.Domain;

public sealed class Product
{
    public Product(
        string name,
        NutritionValues nutritionPer100Grams,
        NutritionSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(nutritionPer100Grams);
        ArgumentNullException.ThrowIfNull(source);

        Name = name.Trim();
        NutritionPer100Grams = nutritionPer100Grams;
        Source = source;
    }

    public string Name { get; }
    public NutritionValues NutritionPer100Grams { get; }
    public NutritionSource Source { get; }
}
