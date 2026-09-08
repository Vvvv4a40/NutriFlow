namespace NutriFlow.Api.Contracts;

public sealed record PortionDraftResponse(
    decimal? WeightInGrams,
    decimal? FractionOfDish,
    string WeightQuality);
