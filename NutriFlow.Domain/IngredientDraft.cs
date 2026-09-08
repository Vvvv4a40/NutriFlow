namespace NutriFlow.Domain;

public sealed class IngredientDraft
{
    public IngredientDraft(string productName, decimal weightInGrams)
        : this(
            productName,
            weightInGrams,
            DataQuality.Exact,
            removedWeightInGrams: 0m,
            removedWeightQuality: DataQuality.Exact)
    {
    }

    public IngredientDraft(
        string productName,
        decimal? weightInGrams,
        DataQuality weightQuality)
        : this(
            productName,
            weightInGrams,
            weightQuality,
            removedWeightInGrams: 0m,
            removedWeightQuality: DataQuality.Exact)
    {
    }

    public IngredientDraft(
        string productName,
        decimal? weightInGrams,
        DataQuality weightQuality,
        decimal? removedWeightInGrams,
        DataQuality removedWeightQuality)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);

        ValidateWeight(weightInGrams, weightQuality);
        ValidateRemovedWeight(
            weightInGrams,
            removedWeightInGrams,
            removedWeightQuality);

        ProductName = productName.Trim();
        WeightInGrams = weightInGrams;
        WeightQuality = weightQuality;
        RemovedWeightInGrams = removedWeightInGrams;
        RemovedWeightQuality = removedWeightQuality;
    }

    public string ProductName { get; }
    public decimal? WeightInGrams { get; }
    public DataQuality WeightQuality { get; }
    public decimal? RemovedWeightInGrams { get; }
    public DataQuality RemovedWeightQuality { get; }
    public decimal? IncludedWeightInGrams =>
        WeightInGrams is null || RemovedWeightInGrams is null
            ? null
            : WeightInGrams.Value - RemovedWeightInGrams.Value;

    private static void ValidateWeight(
        decimal? weightInGrams,
        DataQuality weightQuality)
    {
        if (!Enum.IsDefined(weightQuality))
        {
            throw new ArgumentOutOfRangeException(nameof(weightQuality));
        }

        if (weightInGrams is null)
        {
            if (weightQuality != DataQuality.Unknown)
            {
                throw new ArgumentException(
                    "An unknown weight must have unknown quality.",
                    nameof(weightQuality));
            }

            return;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            weightInGrams.Value);

        if (weightQuality is not (DataQuality.Exact or DataQuality.Estimated))
        {
            throw new ArgumentException(
                "A known weight must be exact or estimated.",
                nameof(weightQuality));
        }
    }

    private static void ValidateRemovedWeight(
        decimal? weightInGrams,
        decimal? removedWeightInGrams,
        DataQuality removedWeightQuality)
    {
        if (!Enum.IsDefined(removedWeightQuality))
        {
            throw new ArgumentOutOfRangeException(nameof(removedWeightQuality));
        }

        if (removedWeightInGrams is null)
        {
            if (removedWeightQuality != DataQuality.Unknown)
            {
                throw new ArgumentException(
                    "An unknown removed weight must have unknown quality.",
                    nameof(removedWeightQuality));
            }

            return;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(removedWeightInGrams.Value);

        if (removedWeightQuality is not (DataQuality.Exact or DataQuality.Estimated))
        {
            throw new ArgumentException(
                "A known removed weight must be exact or estimated.",
                nameof(removedWeightQuality));
        }

        if (weightInGrams is not null &&
            removedWeightInGrams > weightInGrams)
        {
            throw new ArgumentOutOfRangeException(
                nameof(removedWeightInGrams),
                "Removed weight cannot exceed the original ingredient weight.");
        }
    }
}
