namespace NutriFlow.Api.Contracts;

public sealed record ProductResponse(
    string Name,
    decimal Calories,
    decimal ProteinGrams,
    decimal FatGrams,
    decimal CarbohydratesGrams,
    string SourceKind,
    string DataQuality,
    string SourceName,
    string? SourceReference,
    string? Barcode,
    // absent for a saved dish used as an ingredient: it is not a catalog product
    int? Id = null);
