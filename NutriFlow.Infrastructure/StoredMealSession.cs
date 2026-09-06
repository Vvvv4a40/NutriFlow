using NutriFlow.Domain;

namespace NutriFlow.Infrastructure;

public sealed record StoredMealSession(
    Guid Id,
    IReadOnlyList<string> Messages,
    MealDraft Draft,
    string PreviewJson,
    string PreviewToken,
    MealSessionStatus Status,
    DateOnly MealDate,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ConfirmedAtUtc);
