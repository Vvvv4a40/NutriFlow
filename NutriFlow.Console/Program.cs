


Console.WriteLine("NutriFlow — расчёт КБЖУ одного продукта");
Console.WriteLine();

Product product = new Product(
    name: "Демо-продукт",
    weightInGrams: 250m,
    nutritionPer100Grams: new NutritionPer100Grams(
        calories: 200m,
        proteinGrams: 10m,
        fatGrams: 8m,
        carbohydratesGrams: 24m));

NutritionTotal totalNutrition = product.CalculateTotalNutrition();

Console.WriteLine($"Продукт: {product.Name}");
Console.WriteLine($"Масса: {product.WeightInGrams:0.##} г");
Console.WriteLine(
    $"На 100 г: {product.NutritionPer100Grams.Calories:0.##} ккал, " +
    $"Б {product.NutritionPer100Grams.ProteinGrams:0.##} г, " +
    $"Ж {product.NutritionPer100Grams.FatGrams:0.##} г, " +
    $"У {product.NutritionPer100Grams.CarbohydratesGrams:0.##} г");
Console.WriteLine(
    $"Всего: {totalNutrition.Calories:0.##} ккал, " +
    $"Б {totalNutrition.ProteinGrams:0.##} г, " +
    $"Ж {totalNutrition.FatGrams:0.##} г, " +
    $"У {totalNutrition.CarbohydratesGrams:0.##} г");
Console.WriteLine();
Console.WriteLine("Демонстрационные значения заданы вручную и не являются справочными.");

public class NutritionPer100Grams
{
    public NutritionPer100Grams(
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
}

public class Product
{
    public Product(
        string name,
        decimal weightInGrams,
        NutritionPer100Grams nutritionPer100Grams)
    {
        Name = name;
        WeightInGrams = weightInGrams;
        NutritionPer100Grams = nutritionPer100Grams;
    }

    public string Name { get; }
    public decimal WeightInGrams { get; }
    public NutritionPer100Grams NutritionPer100Grams { get; }

    public NutritionTotal CalculateTotalNutrition()
    {
        decimal massFactor = WeightInGrams / 100m;

        return new NutritionTotal(
            calories: NutritionPer100Grams.Calories * massFactor,
            proteinGrams: NutritionPer100Grams.ProteinGrams * massFactor,
            fatGrams: NutritionPer100Grams.FatGrams * massFactor,
            carbohydratesGrams: NutritionPer100Grams.CarbohydratesGrams * massFactor);
    }
}

public class NutritionTotal
{
    public NutritionTotal(
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
}

