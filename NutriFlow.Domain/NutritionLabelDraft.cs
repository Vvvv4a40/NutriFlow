namespace NutriFlow.Domain;

public sealed class NutritionLabelDraft
{
    public NutritionLabelDraft(
        string? productName,
        NutritionBasis basis,
        decimal? calories,
        decimal? proteinGrams,
        decimal? fatGrams,
        decimal? carbohydratesGrams,
        IReadOnlyList<string> clarificationQuestions)
    {
        if (!Enum.IsDefined(basis))
        {
            throw new ArgumentOutOfRangeException(nameof(basis));
        }

        ArgumentNullException.ThrowIfNull(clarificationQuestions);

        ValidateNutritionValue(calories, 1000m, nameof(calories));
        ValidateNutritionValue(proteinGrams, 100m, nameof(proteinGrams));
        ValidateNutritionValue(fatGrams, 100m, nameof(fatGrams));
        ValidateNutritionValue(
            carbohydratesGrams,
            100m,
            nameof(carbohydratesGrams));

        foreach (string question in clarificationQuestions)
        {
            if (string.IsNullOrWhiteSpace(question))
            {
                throw new ArgumentException(
                    "Clarification questions cannot be blank.",
                    nameof(clarificationQuestions));
            }
        }

        ProductName = string.IsNullOrWhiteSpace(productName)
            ? null
            : productName.Trim();
        Basis = basis;
        Calories = calories;
        ProteinGrams = proteinGrams;
        FatGrams = fatGrams;
        CarbohydratesGrams = carbohydratesGrams;
        ClarificationQuestions = new List<string>(
            clarificationQuestions).AsReadOnly();
    }

    public string? ProductName { get; }
    public NutritionBasis Basis { get; }
    public decimal? Calories { get; }
    public decimal? ProteinGrams { get; }
    public decimal? FatGrams { get; }
    public decimal? CarbohydratesGrams { get; }
    public IReadOnlyList<string> ClarificationQuestions { get; }
    public bool RequiresClarification => ClarificationQuestions.Count > 0;

    public bool CanCreateProduct =>
        ProductName is not null &&
        Basis == NutritionBasis.Per100Grams &&
        Calories is not null &&
        ProteinGrams is not null &&
        FatGrams is not null &&
        CarbohydratesGrams is not null &&
        !RequiresClarification;

    public NutritionValues CreateNutritionValues()
    {
        if (!CanCreateProduct)
        {
            throw new InvalidOperationException(
                "The label draft must be complete, per 100 grams, and clarified before creating nutrition values.");
        }

        return new NutritionValues(
            Calories!.Value,
            ProteinGrams!.Value,
            FatGrams!.Value,
            CarbohydratesGrams!.Value);
    }

    private static void ValidateNutritionValue(
        decimal? value,
        decimal maximum,
        string parameterName)
    {
        if (value is < 0m || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"A nutrition value must be between 0 and {maximum}.");
        }
    }
}
