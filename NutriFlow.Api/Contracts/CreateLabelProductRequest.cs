namespace NutriFlow.Api.Contracts;

public sealed record CreateLabelProductRequest(
    string? PhotoReference,
    string? Name,
    decimal Calories,
    decimal ProteinGrams,
    decimal FatGrams,
    decimal CarbohydratesGrams,
    string? Barcode = null);
