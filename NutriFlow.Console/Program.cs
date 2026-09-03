Console.WriteLine("NutriFlow — расчёт блюда и порции");
Console.WriteLine();

Product productA = new Product(
    name: "Демо-продукт A",
    nutritionPer100Grams: new NutritionValues(
        calories: 100m,
        proteinGrams: 10m,
        fatGrams: 4m,
        carbohydratesGrams: 6m));

Product productB = new Product(
    name: "Демо-продукт B",
    nutritionPer100Grams: new NutritionValues(
        calories: 200m,
        proteinGrams: 5m,
        fatGrams: 12m,
        carbohydratesGrams: 18m));

List<DishIngredient> ingredients = new List<DishIngredient>
{
    new DishIngredient(product: productA, weightInGrams: 200m),
    new DishIngredient(product: productB, weightInGrams: 100m)
};

DishBatch dish = new DishBatch(
    name: "Демо-блюдо",
    ingredients: ingredients,
    finalWeightInGrams: 250m);

decimal portionWeightInGrams = 125m;

Console.WriteLine($"Блюдо: {dish.Name}");
Console.WriteLine("Ингредиенты:");

foreach (DishIngredient ingredient in dish.Ingredients)
{
    NutritionValues ingredientNutrition = ingredient.CalculateNutrition();

    Console.WriteLine(
        $"  {ingredient.Product.Name}, {ingredient.WeightInGrams:0.##} г: " +
        FormatNutrition(ingredientNutrition));
}

NutritionValues dishTotalNutrition = dish.CalculateTotalNutrition();
NutritionValues nutritionPer100Grams = dish.CalculateNutritionPer100Grams();
NutritionValues portionNutrition = dish.CalculatePortionNutrition(portionWeightInGrams);

Console.WriteLine();
Console.WriteLine($"Итоговый вес блюда: {dish.FinalWeightInGrams:0.##} г");
Console.WriteLine($"Всё блюдо: {FormatNutrition(dishTotalNutrition)}");
Console.WriteLine($"На 100 г готового блюда: {FormatNutrition(nutritionPer100Grams)}");
Console.WriteLine(
    $"Порция {portionWeightInGrams:0.##} г: " +
    FormatNutrition(portionNutrition));
Console.WriteLine();
Console.WriteLine("Демонстрационные значения заданы вручную и не являются справочными.");

static string FormatNutrition(NutritionValues nutrition)
{
    return $"{nutrition.Calories:0.##} ккал, " +
           $"Б {nutrition.ProteinGrams:0.##} г, " +
           $"Ж {nutrition.FatGrams:0.##} г, " +
           $"У {nutrition.CarbohydratesGrams:0.##} г";
}

public class NutritionValues
{
    public NutritionValues(
        decimal calories,
        decimal proteinGrams,
        decimal fatGrams,
        decimal carbohydratesGrams)
    {
        Calories = calories;
        ProteinGrams = proteinGrams;
        FatGrams = fatGrams;
        CarbohydratesGrams = carbohydratesGrams;
    }

    public decimal Calories { get; }
    public decimal ProteinGrams { get; }
    public decimal FatGrams { get; }
    public decimal CarbohydratesGrams { get; }

    public NutritionValues Add(NutritionValues other)
    {
        return new NutritionValues(
            calories: Calories + other.Calories,
            proteinGrams: ProteinGrams + other.ProteinGrams,
            fatGrams: FatGrams + other.FatGrams,
            carbohydratesGrams: CarbohydratesGrams + other.CarbohydratesGrams);
    }

    public NutritionValues ScaleBy(decimal factor)
    {
        return new NutritionValues(
            calories: Calories * factor,
            proteinGrams: ProteinGrams * factor,
            fatGrams: FatGrams * factor,
            carbohydratesGrams: CarbohydratesGrams * factor);
    }
}

public class Product
{
    public Product(string name, NutritionValues nutritionPer100Grams)
    {
        Name = name;
        NutritionPer100Grams = nutritionPer100Grams;
    }

    public string Name { get; }
    public NutritionValues NutritionPer100Grams { get; }
}

public class DishIngredient
{
    public DishIngredient(Product product, decimal weightInGrams)
    {
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

public class DishBatch
{
    public DishBatch(
        string name,
        IReadOnlyList<DishIngredient> ingredients,
        decimal finalWeightInGrams)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(finalWeightInGrams);

        Name = name;
        Ingredients = new List<DishIngredient>(ingredients);
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
        decimal portionFactor = portionWeightInGrams / FinalWeightInGrams;

        return CalculateTotalNutrition().ScaleBy(portionFactor);
    }
}
