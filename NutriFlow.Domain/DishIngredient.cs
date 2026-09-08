namespace NutriFlow.Domain;

public sealed class DishIngredient
{
    public DishIngredient(Product product, decimal weightInGrams)
        : this(product, weightInGrams, removedWeightInGrams: 0m)
    {
    }

    public DishIngredient(
        Product product,
        decimal weightInGrams,
        decimal removedWeightInGrams)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weightInGrams);
        ArgumentOutOfRangeException.ThrowIfNegative(removedWeightInGrams);

        if (removedWeightInGrams > weightInGrams)
        {
            throw new ArgumentOutOfRangeException(
                nameof(removedWeightInGrams),
                "Removed weight cannot exceed the original ingredient weight.");
        }

        Product = product;
        WeightInGrams = weightInGrams;
        RemovedWeightInGrams = removedWeightInGrams;
    }

    public Product Product { get; }
    public decimal WeightInGrams { get; }
    public decimal RemovedWeightInGrams { get; }
    public decimal IncludedWeightInGrams => WeightInGrams - RemovedWeightInGrams;

    public NutritionValues CalculateNutrition()
    {
        decimal massFactor = IncludedWeightInGrams / 100m;

        return Product.NutritionPer100Grams.ScaleBy(massFactor);
    }
}
