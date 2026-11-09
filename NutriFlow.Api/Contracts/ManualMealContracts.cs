using System.Text.Json.Serialization;
using NutriFlow.Domain;

namespace NutriFlow.Api.Contracts;

public sealed record CreateManualMealRequest(
    int? ProductId,
    decimal? WeightInGrams,
    DateOnly? MealDate,
    [property: JsonConverter(typeof(JsonStringEnumConverter<DataQuality>))]
    DataQuality WeightQuality = DataQuality.Exact);

public sealed record ManualIngredientRequest(
    int? ProductId,
    decimal? WeightInGrams,
    [property: JsonConverter(typeof(JsonStringEnumConverter<DataQuality>))]
    DataQuality WeightQuality = DataQuality.Exact,
    decimal RemovedWeightInGrams = 0m,
    [property: JsonConverter(typeof(JsonStringEnumConverter<DataQuality>))]
    DataQuality RemovedWeightQuality = DataQuality.Exact);

public sealed record CreateManualDishRequest(
    string? Name,
    IReadOnlyList<ManualIngredientRequest?>? Ingredients,
    decimal? FinalWeightInGrams,
    DateOnly? MealDate,
    [property: JsonConverter(typeof(JsonStringEnumConverter<DataQuality>))]
    DataQuality FinalWeightQuality = DataQuality.Exact);

public sealed record CreateSavedDishMealRequest(
    Guid? SavedDishId,
    decimal? WeightInGrams,
    DateOnly? MealDate,
    [property: JsonConverter(typeof(JsonStringEnumConverter<DataQuality>))]
    DataQuality WeightQuality = DataQuality.Exact);

public sealed record SetDishWeightRequest(decimal? WeightInGrams);
