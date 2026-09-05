namespace NutriFlow.Domain;

public sealed class IngredientDraft
{
    public IngredientDraft(string productName, decimal weightInGrams)
        : this(productName, weightInGrams, DataQuality.Exact)
    {
    }

    public IngredientDraft(
        string productName,
        decimal? weightInGrams,
        DataQuality weightQuality)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);

        ValidateWeight(weightInGrams, weightQuality);

        ProductName = productName.Trim();
        WeightInGrams = weightInGrams;
        WeightQuality = weightQuality;
    }

    public string ProductName { get; }
    public decimal? WeightInGrams { get; }
    public DataQuality WeightQuality { get; }

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
}
