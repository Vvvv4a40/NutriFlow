namespace NutriFlow.Api.Contracts;

public sealed record MealDraftResponse(
    string SessionState,
    IReadOnlyList<DishDraftResponse> Dishes,
    IReadOnlyList<string> ClarificationQuestions,
    bool RequiresClarification);
