namespace NutriFlow.Domain;

public sealed class DishBatch
{
    public DishBatch(
        string name,
        IReadOnlyList<DishIngredient> ingredients,
        decimal finalWeightInGrams)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(ingredients);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(finalWeightInGrams);

        if (ingredients.Count == 0)
        {
            throw new ArgumentException(
                "A dish must contain at least one ingredient.",
                nameof(ingredients));
        }

        Name = name;
        Ingredients = new List<DishIngredient>(ingredients).AsReadOnly();
        FinalWeightInGrams = finalWeightInGrams;
    }

    public string Name { get; }
    public IReadOnlyList<DishIngredient> Ingredients { get; }
    public decimal FinalWeightInGrams { get; }

    public NutritionValues CalculateTotalNutrition()
    {
        NutritionValues totalNutrition = new NutritionValues(
            calories: 0m,
            proteinGrams: 0m,
            fatGrams: 0m,
            carbohydratesGrams: 0m);

        foreach (DishIngredient ingredient in Ingredients)
        {
            totalNutrition = totalNutrition.Add(ingredient.CalculateNutrition());
        }

        return totalNutrition;
    }

    public NutritionValues CalculateNutritionPer100Grams()
    {
        decimal finalWeightFactor = 100m / FinalWeightInGrams;

        return CalculateTotalNutrition().ScaleBy(finalWeightFactor);
    }

    public NutritionValues CalculatePortionNutrition(decimal portionWeightInGrams)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(portionWeightInGrams);

        if (portionWeightInGrams > FinalWeightInGrams)
        {
            throw new ArgumentOutOfRangeException(
                nameof(portionWeightInGrams),
                portionWeightInGrams,
                "Portion weight cannot exceed the final dish weight.");
        }

        decimal portionFactor = portionWeightInGrams / FinalWeightInGrams;

        return CalculateTotalNutrition().ScaleBy(portionFactor);
    }
}
