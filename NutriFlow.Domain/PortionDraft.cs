namespace NutriFlow.Domain;

public sealed class PortionDraft
{
    public PortionDraft(
        decimal weightInGrams,
        DataQuality weightQuality = DataQuality.Exact)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weightInGrams);

        if (weightQuality is not (DataQuality.Exact or DataQuality.Estimated))
        {
            throw new ArgumentException(
                "A portion weight must be exact or estimated.",
                nameof(weightQuality));
        }

        WeightInGrams = weightInGrams;
        WeightQuality = weightQuality;
    }

    public decimal WeightInGrams { get; }
    public DataQuality WeightQuality { get; }
}
