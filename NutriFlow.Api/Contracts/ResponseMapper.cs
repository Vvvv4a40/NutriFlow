using NutriFlow.Domain;

namespace NutriFlow.Api.Contracts;

internal static class ResponseMapper
{
    public static ProductResponse ToProductResponse(Product product)
    {
        return new ProductResponse(
            product.Name,
            product.NutritionPer100Grams.Calories,
            product.NutritionPer100Grams.ProteinGrams,
            product.NutritionPer100Grams.FatGrams,
            product.NutritionPer100Grams.CarbohydratesGrams,
            product.Source.Kind.ToString(),
            product.Source.Quality.ToString(),
            product.Source.Name,
            product.Source.Reference,
            product.Barcode);
    }

    public static MealEntryResponse ToMealEntryResponse(MealEntry entry)
    {
        return new MealEntryResponse(
            entry.Name,
            entry.WeightInGrams,
            ToNutritionResponse(entry.Nutrition),
            entry.Quality.ToString());
    }

    public static NutritionResponse ToNutritionResponse(NutritionValues nutrition)
    {
        return new NutritionResponse(
            nutrition.Calories,
            nutrition.ProteinGrams,
            nutrition.FatGrams,
            nutrition.CarbohydratesGrams);
    }
}
