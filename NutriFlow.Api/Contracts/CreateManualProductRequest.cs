namespace NutriFlow.Api.Contracts;

public sealed record CreateManualProductRequest(
    string? Name,
    decimal Calories,
    decimal ProteinGrams,
    decimal FatGrams,
    decimal CarbohydratesGrams,
    bool? IsEstimated,
    string? Barcode = null);
