using NutriFlow.Domain;

namespace NutriFlow.Infrastructure;

public sealed record StoredMealEntry(
    int Id,
    int Revision,
    DateOnly MealDate,
    Guid MealSessionId,
    MealEntry Entry);
