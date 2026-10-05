namespace NutriFlow.Infrastructure;

public enum MealEntryChangeKind
{
    Updated = 0,
    Deleted = 1,
    NotFound = 2,
    RevisionConflict = 3
}

public sealed record MealEntryChangeResult(
    MealEntryChangeKind Kind,
    StoredMealEntry? Entry = null);
