using System.Text.Json.Serialization;
using NutriFlow.Domain;

namespace NutriFlow.Api.Contracts;

public sealed record CreateLabelProductRequest(
    string? PhotoReference,
    [property: JsonConverter(typeof(JsonStringEnumConverter<NutritionBasis>))]
    NutritionBasis Basis,
    string? Name,
    decimal Calories,
    decimal ProteinGrams,
    decimal FatGrams,
    decimal CarbohydratesGrams,
    string? Barcode = null);
