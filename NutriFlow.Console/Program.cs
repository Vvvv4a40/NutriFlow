using NutriFlow.Domain;

Console.WriteLine("NutriFlow — расчёт блюда, порций и дневного прогресса");
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
decimal secondPortionWeightInGrams = 62.5m;

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
NutritionValues secondPortionNutrition = dish.CalculatePortionNutrition(
    secondPortionWeightInGrams);

Console.WriteLine();
Console.WriteLine($"Итоговый вес блюда: {dish.FinalWeightInGrams:0.##} г");
Console.WriteLine($"Всё блюдо: {FormatNutrition(dishTotalNutrition)}");
Console.WriteLine($"На 100 г готового блюда: {FormatNutrition(nutritionPer100Grams)}");
Console.WriteLine(
    $"Порция {portionWeightInGrams:0.##} г: " +
    FormatNutrition(portionNutrition));
Console.WriteLine();

List<MealEntry> mealEntries = new List<MealEntry>
{
    new MealEntry(
        name: $"{dish.Name}, первая порция",
        weightInGrams: portionWeightInGrams,
        nutrition: portionNutrition),
    new MealEntry(
        name: $"{dish.Name}, вторая порция",
        weightInGrams: secondPortionWeightInGrams,
        nutrition: secondPortionNutrition)
};

DailyGoal dailyGoal = new DailyGoal(
    new NutritionValues(
        calories: 2000m,
        proteinGrams: 100m,
        fatGrams: 70m,
        carbohydratesGrams: 250m));
DailyProgress dailyProgress = new DailyProgress(dailyGoal, mealEntries);
NutritionValues consumedNutrition = dailyProgress.CalculateConsumedNutrition();
NutritionValues remainingNutrition = dailyProgress.CalculateRemainingNutrition();
NutritionValues exceededNutrition = dailyProgress.CalculateExceededNutrition();

Console.WriteLine("Дневной прогресс:");
Console.WriteLine($"Цель: {FormatNutrition(dailyGoal.TargetNutrition)}");
Console.WriteLine("Съеденные порции:");

foreach (MealEntry mealEntry in dailyProgress.MealEntries)
{
    Console.WriteLine(
        $"  {mealEntry.Name}, {mealEntry.WeightInGrams:0.##} г: " +
        FormatNutrition(mealEntry.Nutrition));
}

Console.WriteLine($"Съедено: {FormatNutrition(consumedNutrition)}");
Console.WriteLine($"Осталось: {FormatNutrition(remainingNutrition)}");

if (HasAnyValue(exceededNutrition))
{
    Console.WriteLine($"Превышено: {FormatNutrition(exceededNutrition)}");
}

Console.WriteLine();
Console.WriteLine("Демонстрационные значения заданы вручную и не являются справочными.");

static string FormatNutrition(NutritionValues nutrition)
{
    return $"{nutrition.Calories:0.##} ккал, " +
           $"Б {nutrition.ProteinGrams:0.##} г, " +
           $"Ж {nutrition.FatGrams:0.##} г, " +
           $"У {nutrition.CarbohydratesGrams:0.##} г";
}

static bool HasAnyValue(NutritionValues nutrition)
{
    return nutrition.Calories > 0m ||
           nutrition.ProteinGrams > 0m ||
           nutrition.FatGrams > 0m ||
           nutrition.CarbohydratesGrams > 0m;
}
