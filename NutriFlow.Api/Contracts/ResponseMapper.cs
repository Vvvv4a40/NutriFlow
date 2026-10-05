using NutriFlow.Domain;
using NutriFlow.Infrastructure;

namespace NutriFlow.Api.Contracts;

internal static class ResponseMapper
{
    public static SavedDishResponse ToSavedDishResponse(StoredSavedDish dish)
    {
        return new SavedDishResponse(
            dish.Id,
            dish.Product.Name,
            dish.SourceSessionId,
            dish.FinalWeightInGrams,
            ToNutritionResponse(dish.Product.NutritionPer100Grams),
            dish.Product.Source.Quality.ToString());
    }

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

    public static MealEntryResponse ToMealEntryResponse(StoredMealEntry storedEntry)
    {
        MealEntry entry = storedEntry.Entry;

        return new MealEntryResponse(
            entry.Name,
            entry.WeightInGrams,
            ToNutritionResponse(entry.Nutrition),
            entry.Quality.ToString(),
            storedEntry.Id,
            storedEntry.Revision);
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
