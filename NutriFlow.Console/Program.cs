using NutriFlow.Domain;

Console.WriteLine("NutriFlow — сессия ввода и расчёт дневного прогресса");
Console.WriteLine();

CaptureSession captureSession = new CaptureSession();
captureSession.AddEvent(new InputEvent("Добавил 200 г демо-продукта A."));
captureSession.AddEvent(new InputEvent("Потом добавил 100 г демо-продукта B."));
captureSession.AddEvent(new InputEvent("Готовое блюдо весит 250 г."));
captureSession.AddEvent(
    new InputEvent("Съел 125 г, потом ещё две порции по 62,5 г."));
captureSession.FinishCollecting();

Console.WriteLine("Сессия исходного ввода:");

for (int index = 0; index < captureSession.InputEvents.Count; index++)
{
    InputEvent inputEvent = captureSession.InputEvents[index];
    Console.WriteLine($"  {index + 1}. {inputEvent.Text}");
}

Console.WriteLine($"Состояние: {captureSession.State}");
Console.WriteLine("События сохранены по порядку.");
Console.WriteLine();

FakeMealParser fakeMealParser = new FakeMealParser();
MealDraft mealDraft = fakeMealParser.Parse(captureSession);

Console.WriteLine("Структурированный черновик FakeMealParser:");

foreach (DishDraft parsedDish in mealDraft.Dishes)
{
    Console.WriteLine($"  Блюдо: {parsedDish.Name}");
    Console.WriteLine("  Ингредиенты:");

    foreach (IngredientDraft ingredient in parsedDish.Ingredients)
    {
        Console.WriteLine(
            $"    {ingredient.ProductName} — {ingredient.WeightInGrams:0.##} г");
    }

    Console.WriteLine($"  Итоговый вес: {parsedDish.FinalWeightInGrams:0.##} г");
    Console.WriteLine("  Порции:");

    foreach (decimal portionWeight in parsedDish.PortionWeightsInGrams)
    {
        Console.WriteLine($"    {portionWeight:0.##} г");
    }
}

if (mealDraft.RequiresClarification)
{
    Console.WriteLine("  Уточнения:");

    foreach (string question in mealDraft.ClarificationQuestions)
    {
        Console.WriteLine($"    {question}");
    }
}
else
{
    Console.WriteLine("  Уточнения: не требуются");
}

Console.WriteLine("  КБЖУ в результате парсера отсутствуют.");
Console.WriteLine("  Это заранее заданный fake-сценарий, а не настоящий AI.");
Console.WriteLine();

if (mealDraft.RequiresClarification)
{
    Console.WriteLine("Сессию нельзя подтвердить до получения уточнений.");
    return;
}

if (mealDraft.Dishes.Count != 1)
{
    throw new NotSupportedException(
        "Консольный демо-сценарий поддерживает ровно одно блюдо.");
}

NutritionSource demoNutritionSource = new NutritionSource(
    NutritionSourceKind.ManualInput,
    DataQuality.Unknown,
    "Демонстрационный ручной ввод");

Product productA = new Product(
    name: "Демо-продукт A",
    nutritionPer100Grams: new NutritionValues(
        calories: 100m,
        proteinGrams: 10m,
        fatGrams: 4m,
        carbohydratesGrams: 6m),
    source: demoNutritionSource);

Product productB = new Product(
    name: "Демо-продукт B",
    nutritionPer100Grams: new NutritionValues(
        calories: 200m,
        proteinGrams: 5m,
        fatGrams: 12m,
        carbohydratesGrams: 18m),
    source: demoNutritionSource);

Dictionary<string, Product> productsByName = new Dictionary<string, Product>
{
    [productA.Name] = productA,
    [productB.Name] = productB
};
DishDraft dishDraft = mealDraft.Dishes[0];
List<DishIngredient> ingredients = new List<DishIngredient>();

foreach (IngredientDraft ingredientDraft in dishDraft.Ingredients)
{
    if (!productsByName.TryGetValue(
            ingredientDraft.ProductName,
            out Product? product))
    {
        throw new InvalidOperationException(
            $"Демо-продукт '{ingredientDraft.ProductName}' не найден.");
    }

    ingredients.Add(
        new DishIngredient(product, ingredientDraft.WeightInGrams));
}

DishBatch dish = new DishBatch(
    name: dishDraft.Name,
    ingredients: ingredients,
    finalWeightInGrams: dishDraft.FinalWeightInGrams);

Console.WriteLine("Предпросмотр, рассчитанный C#-кодом:");
Console.WriteLine($"Блюдо: {dish.Name}");
Console.WriteLine("Ингредиенты:");

foreach (DishIngredient ingredient in dish.Ingredients)
{
    NutritionValues ingredientNutrition = ingredient.CalculateNutrition();

    Console.WriteLine(
        $"  {ingredient.Product.Name}, {ingredient.WeightInGrams:0.##} г: " +
        FormatNutrition(ingredientNutrition));
    Console.WriteLine(
        $"    источник: {ingredient.Product.Source.Name}; " +
        $"качество: {ingredient.Product.Source.Quality}");
}

NutritionValues dishTotalNutrition = dish.CalculateTotalNutrition();
NutritionValues nutritionPer100Grams = dish.CalculateNutritionPer100Grams();
List<NutritionValues> portionNutritionValues = new List<NutritionValues>();

foreach (decimal portionWeight in dishDraft.PortionWeightsInGrams)
{
    portionNutritionValues.Add(dish.CalculatePortionNutrition(portionWeight));
}

Console.WriteLine();
Console.WriteLine($"Итоговый вес блюда: {dish.FinalWeightInGrams:0.##} г");
Console.WriteLine($"Всё блюдо: {FormatNutrition(dishTotalNutrition)}");
Console.WriteLine($"На 100 г готового блюда: {FormatNutrition(nutritionPer100Grams)}");

for (int index = 0; index < dishDraft.PortionWeightsInGrams.Count; index++)
{
    Console.WriteLine(
        $"Порция {dishDraft.PortionWeightsInGrams[index]:0.##} г: " +
        FormatNutrition(portionNutritionValues[index]));
}

Console.WriteLine();

captureSession.Confirm();
Console.WriteLine($"Сессия после имитации подтверждения: {captureSession.State}");
Console.WriteLine();

List<MealEntry> mealEntries = new List<MealEntry>();

for (int index = 0; index < dishDraft.PortionWeightsInGrams.Count; index++)
{
    mealEntries.Add(
        new MealEntry(
            name: $"{dish.Name}, порция {index + 1}",
            weightInGrams: dishDraft.PortionWeightsInGrams[index],
            nutrition: portionNutritionValues[index]));
}

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
