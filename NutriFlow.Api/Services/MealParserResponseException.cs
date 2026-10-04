namespace NutriFlow.Api.Services;

internal sealed class MealParserResponseException : Exception
{
    public MealParserResponseException(InvalidDataException innerException)
        : base("The meal parser returned an invalid structured draft.", innerException)
    {
    }
}
