namespace NutriFlow.Api.Contracts;

public sealed record CreateMealSessionRequest(
    IReadOnlyList<string?>? Messages,
    DateOnly? MealDate = null);

public sealed record AddMealSessionMessageRequest(string? Message);

public sealed record ConfirmMealSessionRequest(string? PreviewToken);

public sealed record ConfirmMealSessionResponse(
    string Outcome,
    string? Message,
    MealSessionResponse Session,
    IReadOnlyList<MealEntryResponse> Entries);

public sealed record MealSessionResponse(
    Guid Id,
    string Status,
    DateOnly MealDate,
    IReadOnlyList<string> Messages,
    string PreviewToken,
    bool CanConfirm,
    IReadOnlyList<string> ClarificationQuestions,
    IReadOnlyList<WorkflowIssueResponse> Issues,
    IReadOnlyList<DishPreviewResponse> Dishes);

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
    NutritionResponse? NutritionPer100Grams,
    IReadOnlyList<IngredientPreviewResponse> Ingredients,
    IReadOnlyList<PortionPreviewResponse> Portions);

public sealed record IngredientPreviewResponse(
    string ProductName,
    decimal? WeightInGrams,
    string WeightQuality,
    ProductResponse? ResolvedProduct,
    NutritionResponse? Nutrition);

public sealed record PortionPreviewResponse(
    decimal WeightInGrams,
    string WeightQuality,
    NutritionResponse? Nutrition);
