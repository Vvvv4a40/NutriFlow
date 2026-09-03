namespace NutriFlow.Domain;

public sealed class DishIngredient
{
    public DishIngredient(Product product, decimal weightInGrams)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weightInGrams);

        Product = product;
        WeightInGrams = weightInGrams;
    }

    public Product Product { get; }
    public decimal WeightInGrams { get; }

    public NutritionValues CalculateNutrition()
    {
        decimal massFactor = WeightInGrams / 100m;

        return Product.NutritionPer100Grams.ScaleBy(massFactor);
    }
}
