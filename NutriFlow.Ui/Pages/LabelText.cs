using System.Net;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;

namespace NutriFlow.Ui.Pages;

// texts of the label photo and the rules of its product (LabelSourcePageAsync and ProductEditorPageAsync on android)
public static class LabelText
{
    public const string UnreadablePhoto = "Не удалось прочитать фото. Сфотографируйте этикетку ещё раз или выберите другое фото.";
    public const string NoKey = "Чтобы распознать этикетку, добавьте личный ключ Groq в настройках профиля или заполните продукт вручную.";
    public const string ManualReason = "Этикетка не распознана. Перенесите значения с упаковки сами.";
    public const string CheckedTitle = "Значения сверены с этикеткой";
    public const string MustCheck = "Пересчитайте значения на 100 г и отметьте, что они сверены.";
    public const string Disagree = "Калории не сходятся с белками, жирами и углеводами: похоже, какая-то цифра прочитана неверно. Сверьте внимательно.";

    // all numbers are there, but they cannot all be true
    public static bool ValuesDisagree(NutritionLabelDraft draft) =>
        draft is { Calories: { } calories, ProteinGrams: { } protein, FatGrams: { } fat, CarbohydratesGrams: { } carbohydrates } &&
        !NutritionPlausibility.MacrosMatchCalories(calories, protein, fat, carbohydrates);

    public static string Error(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.RequestEntityTooLarge } =>
            "Фото слишком большое для Groq. Выберите фото поменьше или заполните продукт вручную.",
        _ => CaptureText.GroqError(exception, "для этикеток", "заполните продукт вручную")
    };

    // another photo is what helps with a file that is not a photo; Groq can be asked again with the same one
    public static bool CanRetry(Exception exception) => exception is not ArgumentException;

    public static string Basis(NutritionBasis basis) => basis switch
    {
        NutritionBasis.Per100Grams => "на 100 г",
        NutritionBasis.Per100Milliliters => "на 100 мл",
        NutritionBasis.PerServing => "на порцию",
        _ => "с этикетки"
    };

    public static string Note(NutritionBasis basis) => basis switch
    {
        NutritionBasis.Per100Grams => "Распознаны значения на 100 г. Сверьте каждое поле с упаковкой.",
        NutritionBasis.Unknown => "Не удалось определить, на 100 г или на порцию указаны значения. Проверьте на упаковке и при необходимости пересчитайте на 100 г.",
        _ => $"На этикетке значения {Basis(basis)}. Пересчитайте их на 100 г перед сохранением: сохраняются только значения на 100 г."
    };

    // stays true while the numbers under it are not yet per 100 g
    public static string ValuesTitle(NutritionBasis basis) => basis switch
    {
        NutritionBasis.Per100Grams => "Пищевая ценность на 100 г",
        NutritionBasis.Unknown => "Значения с этикетки — проверьте, что они на 100 г",
        _ => $"Значения {Basis(basis)} — пересчитайте на 100 г"
    };

    public static string CheckedHint(NutritionBasis basis) => basis == NutritionBasis.Per100Grams
        ? "Без этой отметки продукт сохранится как оценка"
        : "Они пересчитаны и указаны на 100 г";

    // numbers per a serving or per 100 ml would count as per 100 g, an estimate does not make that true
    public static string? CheckProblem(NutritionBasis basis, bool isChecked) =>
        basis != NutritionBasis.Per100Grams && !isChecked ? MustCheck : null;

    public static ProductResponse Product(string name, decimal calories, decimal protein, decimal fat, decimal carbohydrates,
        bool isChecked, string photoReference, string? barcode) => new(name, calories, protein, fat, carbohydrates,
        nameof(NutritionSourceKind.LabelPhoto),
        isChecked ? nameof(DataQuality.Exact) : nameof(DataQuality.Estimated),
        isChecked ? "Этикетка, проверена пользователем" : "Этикетка, не сверена",
        photoReference, barcode);
}
