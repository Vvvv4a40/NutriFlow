namespace NutriFlow.Api.Contracts;

public sealed record PortionDraftResponse(
    decimal WeightInGrams,
    string WeightQuality);
