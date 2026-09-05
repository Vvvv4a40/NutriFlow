namespace NutriFlow.Domain;

public sealed class IngredientDraft
{
    public IngredientDraft(string productName, decimal weightInGrams)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weightInGrams);

        ProductName = productName;
        WeightInGrams = weightInGrams;
    }

    public string ProductName { get; }
    public decimal WeightInGrams { get; }
}
