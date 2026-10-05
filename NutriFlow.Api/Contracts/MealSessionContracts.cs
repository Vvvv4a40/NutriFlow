using System.Text.Json.Serialization;
using NutriFlow.Domain;

namespace NutriFlow.Api.Contracts;

public sealed record CreateMealSessionRequest(
    IReadOnlyList<string?>? Messages,
    DateOnly? MealDate = null,
    [property: JsonConverter(typeof(JsonStringEnumConverter<MealSessionPurpose>))]
    MealSessionPurpose Purpose = MealSessionPurpose.Diary);

public sealed record AddMealSessionMessageRequest(string? Message);

public sealed record ConfirmMealSessionRequest(string? PreviewToken);

public sealed record ConfirmMealSessionResponse(
    string Outcome,
    string? Message,
    MealSessionResponse Session,
    IReadOnlyList<MealEntryResponse> Entries,
    SavedDishResponse? SavedDish = null);

public sealed record MealSessionResponse(
    Guid Id,
    string Status,
    DateOnly MealDate,
    IReadOnlyList<string> Messages,
    string PreviewToken,
    bool CanConfirm,
    IReadOnlyList<string> ClarificationQuestions,
    IReadOnlyList<WorkflowIssueResponse> Issues,
    IReadOnlyList<DishPreviewResponse> Dishes,
    string Purpose = "Diary");

public sealed record SavedDishResponse(
    Guid Id,
    string Name,
    Guid SourceSessionId,
    decimal FinalWeightInGrams,
    NutritionResponse NutritionPer100Grams,
    string NutritionQuality);

public sealed record SavedDishDetailResponse(
    SavedDishResponse SavedDish,
    DishPreviewResponse Dish);

public sealed record WorkflowIssueResponse(
    string Code,
    string DishName,
    string? IngredientName,
    string Message);

public sealed record DishPreviewResponse(
    string Name,
    decimal? FinalWeightInGrams,
    string FinalWeightQuality,
    NutritionResponse? TotalNutrition,
    string? TotalNutritionQuality,
    NutritionResponse? NutritionPer100Grams,
    string? NutritionPer100GramsQuality,
    IReadOnlyList<IngredientPreviewResponse> Ingredients,
    IReadOnlyList<PortionPreviewResponse> Portions);

public sealed record IngredientPreviewResponse(
    string ProductName,
    decimal? WeightInGrams,
    string WeightQuality,
    decimal? RemovedWeightInGrams,
    string RemovedWeightQuality,
    decimal? IncludedWeightInGrams,
    ProductResponse? ResolvedProduct,
    NutritionResponse? Nutrition);

public sealed record PortionPreviewResponse(
    decimal? WeightInGrams,
    decimal? FractionOfDish,
    string WeightQuality,
    NutritionResponse? Nutrition,
    string? NutritionQuality);
