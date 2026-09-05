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
    string? SourceReference);
