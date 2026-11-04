using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Ui.Catalog;
using NutriFlow.Ui.Formatting;

namespace NutriFlow.Ui.Pages;

// texts of the products and dishes sections (MainPage.Products.cs, MainPage.Dishes.cs and their cards on android)
public static class CatalogText
{
    public const string OpenFoodFactsUnreachable =
        "Не удалось проверить штрихкод: нет связи с Open Food Facts. Повторите позже или добавьте продукт вручную.";

    public const string BarcodeNotFound =
        "В Open Food Facts нет этого продукта или его полной пищевой ценности. Добавьте продукт вручную по упаковке.";

    public const string BarcodeUnread =
        "Не получилось прочитать штрихкод. Снимите его крупнее, ровно и без бликов или введите цифры.";

    // the current calculation or the typed text gives way to the new one
    public static Func<Task<bool>> ConfirmReplace(ActionRunner runner) => () => runner.ConfirmAsync("Заменить текущий расчёт?",
        "Неподтверждённый расчёт и набранный текст заменятся новым расчётом. Дневник не изменится.", "Заменить", "Оставить");

    // what a screen reader hears while typing a search: the query too, so the same count for a longer query is heard again
    public static string SearchSummary(ProductSearch search) => search switch
    {
        { Failed: true } => "Не удалось открыть каталог",
        { Results: null } => "",
        { Results.Count: > 0, ResultsQuery.Length: > 0 } => $"«{search.ResultsQuery}»: {Products(search.Results.Count)}",
        { Results.Count: > 0 } => Products(search.Results.Count),
        { ResultsQuery.Length: > 0 } => $"«{search.ResultsQuery}»: ничего не найдено",
        _ => "Нет продуктов"
    };

    public static string Products(int count) => DiaryText.Plural(count, "продукт", "продукта", "продуктов");

    public static string Dishes(int count) => DiaryText.Plural(count, "блюдо", "блюда", "блюд");

    // an estimate never passes for exact data, even in a list
    public static string Per100Calories(decimal calories, string? quality) =>
        $"{(quality == nameof(DataQuality.Estimated) ? "≈ " : "")}{NutritionText.Kilocalories(calories)}";

    public static string ProductDescription(ProductResponse product) =>
        $"{product.Name}. На 100 граммов: {NutritionText.SpokenNutrition(product)}. {NutritionText.Quality(product.DataQuality)}";

    public static string DishDescription(SavedDishResponse dish) =>
        $"{dish.Name}. На 100 граммов: {NutritionText.SpokenNutrition(dish.NutritionPer100Grams)}. {NutritionText.Quality(dish.NutritionQuality)}";

    public static string NothingFound(string query) => $"В каталоге нет «{query}». Добавьте продукт вручную или по штрихкоду.";

    public static string MealDay(DateOnly date, DateOnly today) => $"Запись попадёт в дневник за {CaptureText.DiaryDay(date, today)}.";

    public static string IngredientWeight(decimal weight, decimal removed) => removed > 0
        ? $"{NutritionText.Amount(weight)} г, не вошло {NutritionText.Amount(removed)} г"
        : $"{NutritionText.Amount(weight)} г";

    public static string SavedIngredientWeight(IngredientPreviewResponse ingredient) =>
        ingredient.RemovedWeightInGrams is > 0
            ? $"{NutritionText.Amount(ingredient.WeightInGrams ?? 0m)} г · не вошло {NutritionText.Amount(ingredient.RemovedWeightInGrams.Value)} г"
            : $"{NutritionText.Amount(ingredient.WeightInGrams ?? 0m)} г";

    // the name of the source already says what kind it is («Ручной ввод пользователя», «Open Food Facts»)
    public static string IngredientSource(ProductResponse product) => string.Join(" · ", new[]
    {
        string.IsNullOrWhiteSpace(product.SourceName)
            ? string.IsNullOrWhiteSpace(product.SourceKind) ? null : NutritionText.Source(product.SourceKind)
            : product.SourceName,
        NutritionText.Quality(product.DataQuality)
    }.OfType<string>());

    // 8, 12, 13 or 14 digits, as ProductBarcode accepts; the field says what to type before anything is sent
    public static string? BarcodeProblem(string? text)
    {
        string value = (text ?? "").Trim();
        return value.Length == 0 ? "Введите цифры под штрихкодом, например 4600000000000."
            : value.Any(character => !char.IsAsciiDigit(character)) ? "Введите только цифры, без пробелов и букв."
            : value.Length is not (8 or 12 or 13 or 14) ? $"В штрихкоде 8, 12, 13 или 14 цифр, а введено {value.Length}. Проверьте цифры под штрихкодом."
            : null;
    }

    // a missing product or one without the whole nutrition is the same for the user: add it by hand
    public static bool IsBarcodeMissing(Exception exception) => exception is InvalidDataException;

    public static string BarcodeError(Exception exception) => exception switch
    {
        HttpRequestException or OperationCanceledException => OpenFoodFactsUnreachable,
        _ => UiErrors.Describe(exception)
    };
}
