using System.Globalization;
using NutriFlow.Api.Contracts;

namespace NutriFlow.Ui.Formatting;

// numbers and labels shared by every screen; same rules as Ui.cs of the android app
public static class NutritionText
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static string Amount(decimal value) => value.ToString("0.##", Russian);

    public static string MacroAmount(decimal value) => value.ToString("0", Russian);

    public static string Kilocalories(decimal value) => value.ToString("#,0", Russian);

    public static string Nutrition(NutritionResponse value) =>
        Nutrition(value.Calories, value.ProteinGrams, value.FatGrams, value.CarbohydratesGrams);

    public static string Nutrition(ProductResponse value) =>
        Nutrition(value.Calories, value.ProteinGrams, value.FatGrams, value.CarbohydratesGrams);

    public static string Nutrition(decimal calories, decimal protein, decimal fat, decimal carbs) =>
        $"{Amount(calories)} ккал · Б {MacroAmount(protein)} · Ж {MacroAmount(fat)} · У {MacroAmount(carbs)} г";

    // screen readers spell out abbreviations like «Б» and «ккал», so spoken descriptions use whole words
    public static string SpokenNutrition(decimal calories, decimal protein, decimal fat, decimal carbs) =>
        $"{Amount(calories)} килокалорий, {SpokenMacros(protein, fat, carbs)}";

    public static string SpokenNutrition(NutritionResponse value) =>
        SpokenNutrition(value.Calories, value.ProteinGrams, value.FatGrams, value.CarbohydratesGrams);

    public static string SpokenNutrition(ProductResponse value) =>
        SpokenNutrition(value.Calories, value.ProteinGrams, value.FatGrams, value.CarbohydratesGrams);

    public static string SpokenMacros(decimal protein, decimal fat, decimal carbs) =>
        $"белки {MacroAmount(protein)}, жиры {MacroAmount(fat)}, углеводы {MacroAmount(carbs)} граммов";

    public static string Quality(string? value) => value switch
    {
        "Exact" => "точные данные",
        "Verified" => "проверенные данные",
        "Estimated" => "оценочные данные",
        _ => "качество не установлено"
    };

    public static string Source(string value) => value switch
    {
        "ManualInput" => "ручной ввод",
        "LabelPhoto" => "фото этикетки",
        "ExternalService" => "внешний каталог",
        "SavedDish" => "сохранённое блюдо",
        "WebPage" => "веб-страница",
        _ => value
    };

    // fields accept a comma or a dot as the decimal separator
    public static decimal ParseNumber(string? text, string name, bool positive = false)
    {
        string value = (text ?? "").Trim().Replace(',', '.');
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out decimal number) || number < 0 || (positive && number == 0))
        {
            throw new ArgumentException($"{name}: введите {(positive ? "положительное" : "неотрицательное")} число.");
        }

        return number;
    }

    public static string RequireName(string? text, string label)
    {
        string value = (text ?? "").Trim();
        if (value.Length == 0)
        {
            throw new ArgumentException($"Заполните поле «{label}».");
        }

        return value;
    }
}
