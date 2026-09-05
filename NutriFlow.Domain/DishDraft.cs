namespace NutriFlow.Domain;

public sealed class DishDraft
{
    public DishDraft(
        string name,
        IReadOnlyList<IngredientDraft> ingredients,
        decimal finalWeightInGrams,
        IReadOnlyList<decimal> portionWeightsInGrams)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(ingredients);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(finalWeightInGrams);
        ArgumentNullException.ThrowIfNull(portionWeightsInGrams);

        if (ingredients.Count == 0)
        {
            throw new ArgumentException(
                "A dish draft must contain at least one ingredient.",
                nameof(ingredients));
        }

        foreach (IngredientDraft ingredient in ingredients)
        {
            if (ingredient is null)
            {
                throw new ArgumentException(
                    "Draft ingredients cannot contain null values.",
                    nameof(ingredients));
            }
        }

        decimal totalPortionWeightInGrams = 0m;

        foreach (decimal portionWeightInGrams in portionWeightsInGrams)
        {
            if (portionWeightInGrams <= 0m ||
                portionWeightInGrams > finalWeightInGrams)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(portionWeightsInGrams),
                    portionWeightInGrams,
                    "A portion must be positive and cannot exceed the dish weight.");
            }

            totalPortionWeightInGrams += portionWeightInGrams;
        }

        if (totalPortionWeightInGrams > finalWeightInGrams)
        {
            throw new ArgumentException(
                "The total portion weight cannot exceed the dish weight.",
                nameof(portionWeightsInGrams));
        }

        Name = name;
        Ingredients = new List<IngredientDraft>(ingredients).AsReadOnly();
        FinalWeightInGrams = finalWeightInGrams;
        PortionWeightsInGrams = new List<decimal>(
            portionWeightsInGrams).AsReadOnly();
    }

    public string Name { get; }
    public IReadOnlyList<IngredientDraft> Ingredients { get; }
    public decimal FinalWeightInGrams { get; }
    public IReadOnlyList<decimal> PortionWeightsInGrams { get; }
}
