namespace NutriFlow.Api.Contracts;

public sealed record IngredientDraftResponse(
    string ProductName,
    decimal? WeightInGrams,
    string WeightQuality);
