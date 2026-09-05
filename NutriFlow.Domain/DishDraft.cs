namespace NutriFlow.Domain;

public sealed class DishDraft
{
    public DishDraft(
        string name,
        IReadOnlyList<IngredientDraft> ingredients,
        decimal finalWeightInGrams,
        IReadOnlyList<decimal> portionWeightsInGrams)
        : this(
            name,
            ingredients,
            finalWeightInGrams,
            DataQuality.Exact,
            CreateExactPortions(portionWeightsInGrams))
    {
    }

    public DishDraft(
        string name,
        IReadOnlyList<IngredientDraft> ingredients,
        decimal? finalWeightInGrams,
        DataQuality finalWeightQuality,
        IReadOnlyList<PortionDraft> portions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(ingredients);
        ArgumentNullException.ThrowIfNull(portions);

        ValidateFinalWeight(finalWeightInGrams, finalWeightQuality);

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

        foreach (PortionDraft portion in portions)
        {
            if (portion is null)
            {
                throw new ArgumentException(
                    "Draft portions cannot contain null values.",
                    nameof(portions));
            }

            if (finalWeightInGrams is not null &&
                portion.WeightInGrams > finalWeightInGrams.Value)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(portions),
                    portion.WeightInGrams,
                    "A portion must be positive and cannot exceed the dish weight.");
            }

            totalPortionWeightInGrams += portion.WeightInGrams;
        }

        if (finalWeightInGrams is not null &&
            totalPortionWeightInGrams > finalWeightInGrams.Value)
        {
            throw new ArgumentException(
                "The total portion weight cannot exceed the dish weight.",
                nameof(portions));
        }

        Name = name.Trim();
        Ingredients = new List<IngredientDraft>(ingredients).AsReadOnly();
        FinalWeightInGrams = finalWeightInGrams;
        FinalWeightQuality = finalWeightQuality;
        Portions = new List<PortionDraft>(portions).AsReadOnly();
        PortionWeightsInGrams = Portions
            .Select(portion => portion.WeightInGrams)
            .ToArray();
    }

    public string Name { get; }
    public IReadOnlyList<IngredientDraft> Ingredients { get; }
    public decimal? FinalWeightInGrams { get; }
    public DataQuality FinalWeightQuality { get; }
    public IReadOnlyList<PortionDraft> Portions { get; }
    public IReadOnlyList<decimal> PortionWeightsInGrams { get; }

    private static IReadOnlyList<PortionDraft> CreateExactPortions(
        IReadOnlyList<decimal> portionWeightsInGrams)
    {
        ArgumentNullException.ThrowIfNull(portionWeightsInGrams);

        return portionWeightsInGrams
            .Select(weight => new PortionDraft(weight))
            .ToArray();
    }

    private static void ValidateFinalWeight(
        decimal? finalWeightInGrams,
        DataQuality finalWeightQuality)
    {
        if (!Enum.IsDefined(finalWeightQuality))
        {
            throw new ArgumentOutOfRangeException(nameof(finalWeightQuality));
        }

        if (finalWeightInGrams is null)
        {
            if (finalWeightQuality != DataQuality.Unknown)
            {
                throw new ArgumentException(
                    "An unknown final weight must have unknown quality.",
                    nameof(finalWeightQuality));
            }

            return;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            finalWeightInGrams.Value);

        if (finalWeightQuality is not (DataQuality.Exact or DataQuality.Estimated))
        {
            throw new ArgumentException(
                "A known final weight must be exact or estimated.",
                nameof(finalWeightQuality));
        }
    }
}
