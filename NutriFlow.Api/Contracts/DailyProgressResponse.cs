using System.Text.Json.Serialization;
using NutriFlow.Domain;

namespace NutriFlow.Api.Contracts;

public sealed record SetDailyGoalRequest(
    decimal Calories,
    decimal ProteinGrams,
    decimal FatGrams,
    decimal CarbohydratesGrams);

public sealed record DailyProgressResponse(
    DateOnly Date,
    NutritionResponse? Goal,
    NutritionResponse Consumed,
    NutritionResponse? Remaining,
    NutritionResponse? Exceeded,
    IReadOnlyList<MealEntryResponse> Entries);

public sealed record MealEntryResponse(
    string Name,
    decimal WeightInGrams,
    NutritionResponse Nutrition,
    string Quality,
    int Id = 0,
    int Revision = 0);

public sealed record UpdateMealEntryRequest(
    decimal? WeightInGrams,
    [property: JsonConverter(typeof(JsonStringEnumConverter<DataQuality>))]
    DataQuality WeightQuality = DataQuality.Exact);
