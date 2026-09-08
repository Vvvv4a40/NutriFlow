namespace NutriFlow.Infrastructure;

public sealed class MealSessionConflictException : InvalidOperationException
{
    public MealSessionConflictException(string message)
        : base(message)
    {
    }
}
