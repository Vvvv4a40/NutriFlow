namespace NutriFlow.Api.Contracts;

public sealed record DishDraftResponse(
    string Name,
    IReadOnlyList<IngredientDraftResponse> Ingredients,
    decimal FinalWeightInGrams,
    IReadOnlyList<decimal> PortionWeightsInGrams);
