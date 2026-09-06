namespace NutriFlow.Api.Contracts;

public sealed record NutritionLabelDraftResponse(
    string PhotoReference,
    string? ProductName,
    string Basis,
    decimal? Calories,
    decimal? ProteinGrams,
    decimal? FatGrams,
    decimal? CarbohydratesGrams,
    IReadOnlyList<string> ClarificationQuestions,
    bool CanCreateProduct);
