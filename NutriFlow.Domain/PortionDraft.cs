namespace NutriFlow.Domain;

public sealed class PortionDraft
{
    public PortionDraft(
        decimal weightInGrams,
        DataQuality weightQuality = DataQuality.Exact)
        : this(weightInGrams, fractionOfDish: null, weightQuality)
    {
    }

    private PortionDraft(
        decimal? weightInGrams,
        decimal? fractionOfDish,
        DataQuality weightQuality)
    {
        if ((weightInGrams is null) == (fractionOfDish is null))
        {
            throw new ArgumentException(
                "A portion must have either a weight or a fraction.");
        }

        if (weightInGrams is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                weightInGrams.Value);
        }

        if (fractionOfDish is not null &&
            fractionOfDish is <= 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fractionOfDish),
                "A portion fraction must be greater than zero and at most one.");
        }

        ValidateQuality(weightQuality);

        WeightInGrams = weightInGrams;
        FractionOfDish = fractionOfDish;
        WeightQuality = weightQuality;
    }

    public decimal? WeightInGrams { get; }
    public decimal? FractionOfDish { get; }
    public DataQuality WeightQuality { get; }

    public static PortionDraft FromFraction(
        decimal fractionOfDish,
        DataQuality quality = DataQuality.Exact)
    {
        return new PortionDraft(
            weightInGrams: null,
            fractionOfDish,
            quality);
    }

    public decimal ResolveWeightInGrams(decimal finalWeightInGrams)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(finalWeightInGrams);

        return WeightInGrams ?? finalWeightInGrams * FractionOfDish!.Value;
    }

    private static void ValidateQuality(DataQuality quality)
    {
        if (quality is not (DataQuality.Exact or DataQuality.Estimated))
        {
            throw new ArgumentException(
                "A portion weight or fraction must be exact or estimated.",
                nameof(quality));
        }
    }
}
