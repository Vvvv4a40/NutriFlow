namespace NutriFlow.Domain;

public sealed class Product
{
    public Product(
        string name,
        NutritionValues nutritionPer100Grams,
        NutritionSource source,
        string? barcode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(nutritionPer100Grams);
        ArgumentNullException.ThrowIfNull(source);

        string normalizedName = name.Trim();

        if (normalizedName.Length > 200)
        {
            throw new ArgumentException(
                "A product name cannot exceed 200 characters.",
                nameof(name));
        }

        ValidateNutritionPer100Grams(nutritionPer100Grams);

        Name = normalizedName;
        NutritionPer100Grams = nutritionPer100Grams;
        Source = source;
        Barcode = barcode is null
            ? null
            : ProductBarcode.Normalize(barcode);
    }

    public string Name { get; }
    public NutritionValues NutritionPer100Grams { get; }
    public NutritionSource Source { get; }
    public string? Barcode { get; }

    private static void ValidateNutritionPer100Grams(NutritionValues nutrition)
    {
        if (nutrition.Calories > 1000m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nutrition),
                "Calories per 100 grams cannot exceed 1000 kcal.");
        }

        if (nutrition.ProteinGrams > 100m ||
            nutrition.FatGrams > 100m ||
            nutrition.CarbohydratesGrams > 100m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nutrition),
                "Each macronutrient per 100 grams cannot exceed 100 grams.");
        }
    }
}
