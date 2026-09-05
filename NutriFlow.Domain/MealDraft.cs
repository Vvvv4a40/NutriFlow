namespace NutriFlow.Domain;

public sealed class MealDraft
{
    public MealDraft(
        IReadOnlyList<DishDraft> dishes,
        IReadOnlyList<string> clarificationQuestions)
    {
        ArgumentNullException.ThrowIfNull(dishes);
        ArgumentNullException.ThrowIfNull(clarificationQuestions);

        if (dishes.Count == 0)
        {
            throw new ArgumentException(
                "A meal draft must contain at least one dish.",
                nameof(dishes));
        }

        foreach (DishDraft dish in dishes)
        {
            if (dish is null)
            {
                throw new ArgumentException(
                    "Draft dishes cannot contain null values.",
                    nameof(dishes));
            }
        }

        foreach (string question in clarificationQuestions)
        {
            if (string.IsNullOrWhiteSpace(question))
            {
                throw new ArgumentException(
                    "Clarification questions cannot be blank.",
                    nameof(clarificationQuestions));
            }
        }

        Dishes = new List<DishDraft>(dishes).AsReadOnly();
        ClarificationQuestions = new List<string>(
            clarificationQuestions).AsReadOnly();
    }

    public IReadOnlyList<DishDraft> Dishes { get; }
    public IReadOnlyList<string> ClarificationQuestions { get; }
    public bool RequiresClarification => ClarificationQuestions.Count > 0;
}
