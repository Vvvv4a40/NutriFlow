namespace NutriFlow.Infrastructure;

public enum MealSessionConfirmationResult
{
    Confirmed = 0,
    AlreadyConfirmed = 1,
    NotReady = 2,
    StalePreview = 3
}
