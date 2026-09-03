namespace NutriFlow.Domain;

public sealed class Product
{
    public Product(string name, NutritionValues nutritionPer100Grams)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(nutritionPer100Grams);

        Name = name;
        NutritionPer100Grams = nutritionPer100Grams;
    }

    public string Name { get; }
    public NutritionValues NutritionPer100Grams { get; }
}
