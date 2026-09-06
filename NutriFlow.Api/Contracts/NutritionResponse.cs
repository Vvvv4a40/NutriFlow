namespace NutriFlow.Api.Contracts;

public sealed record NutritionResponse(
    decimal Calories,
    decimal ProteinGrams,
    decimal FatGrams,
    decimal CarbohydratesGrams);
