using System.Globalization;
using System.Net;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Components;
using NutriFlow.Ui.Formatting;

namespace NutriFlow.Ui.Pages;

public sealed record PreviewStatusText(Tone Tone, AppIconKind Icon, string Context, string Title, string Hint);

// texts of the food input and the preview, as MainPage.Input.cs and MainPage.Preview.cs build them on android
public static class CaptureText
{
    public const string Saved = "Сохранено на устройстве";
    public const string SaveFailed = "Не сохранено. Повторите сохранение.";
    public const string SessionMissing = "Расчёт не найден. Начните новую запись.";
    // the text is kept in the field and on the device; a catalog product or a saved dish needs no Groq
    public const string Unreachable = "Не удалось связаться с Groq. Проверьте интернет и повторите или добавьте запись вручную.";
    public const string Blocked = "Сначала восстановите или удалите сохранённый текст в записи еды.";
    public const string NoKey = "Чтобы разобрать описание, добавьте личный ключ Groq в настройках профиля. Продукт из каталога и своё блюдо можно записать и без него.";
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static bool IsDish(MealSessionResponse session) => session.Purpose == nameof(MealSessionPurpose.CreateDish);

    public static string ConfirmWithoutText(MealSessionResponse? session) =>
        session is not null && IsDish(session) ? "Сохранить без уточнения?" : "Записать без уточнения?";

    public static string ConfirmText(MealSessionResponse? session) =>
        session is not null && IsDish(session) ? "Сохранить блюдо" : "Записать в дневник";

    public static string Title(MealSessionResponse? session, MealSessionPurpose purpose) => session is not null
        ? IsDish(session) ? "Проверьте блюдо" : "Проверьте запись"
        : purpose == MealSessionPurpose.CreateDish ? "Новое блюдо" : "Запись еды";

    public static string DiaryDay(DateOnly date, DateOnly today) =>
        date == today ? $"сегодня, {date.ToString("d MMMM", Russian)}"
        : date == today.AddDays(-1) ? $"вчера, {date.ToString("d MMMM", Russian)}"
        : date.ToString("d MMMM", Russian);

    public static string Context(MealSessionPurpose purpose, DateOnly date, DateOnly today) =>
        purpose == MealSessionPurpose.CreateDish
            ? "Опишите ингредиенты с весом и сколько весит готовое блюдо. Оно сохранится в разделе «Блюда»."
            : $"Запись попадёт в дневник за {DiaryDay(date, today)}. Перед сохранением вы увидите расчёт.";

    public static string Placeholder(bool clarifying, MealSessionPurpose purpose) =>
        clarifying ? "Ответьте на вопросы или поправьте состав…"
        : purpose == MealSessionPurpose.CreateDish ? "Например: фарш 600 г, лук 150 г, рис 200 г сухого. Готовое блюдо 1180 г"
        : "Например: гречка 200 г, куриная грудка 150 г и огурец";

    // the counter only appears near the limit, where it helps
    public static string? Counter(int length) =>
        length > MealInputText.MaximumLength * 0.8 ? $"{length}/{MealInputText.MaximumLength}" : null;

    public static PreviewStatusText Status(MealSessionResponse session, DateOnly today)
    {
        (Tone tone, AppIconKind icon, string title) = session.Status switch
        {
            "ReadyForConfirmation" => (Tone.Success, AppIconKind.Check, "Готово к подтверждению"),
            "NeedsClarification" => (Tone.Warning, AppIconKind.Warning, "Нужны уточнения"),
            "NeedsProducts" => (Tone.Warning, AppIconKind.Warning, "Не хватает пищевой ценности"),
            "Confirmed" => (Tone.Muted, AppIconKind.Check, "Уже подтверждено"),
            _ => (Tone.Warning, AppIconKind.Warning, "Проверьте расчёт")
        };
        string hint = session.Status switch
        {
            "Confirmed" => "Этот расчёт уже подтверждён.",
            _ when session.CanConfirm => IsDish(session)
                ? "Проверьте состав и готовый вес. Блюдо сохранится только после подтверждения."
                : "Проверьте состав и порцию. До подтверждения запись не попадёт в дневник.",
            "NeedsProducts" when session.ClarificationQuestions.Count == 0 =>
                "Добавьте пищевую ценность продуктов, которых нет в каталоге, — расчёт обновится сам.",
            "NeedsProducts" => "Ответьте на вопросы в поле уточнения и добавьте пищевую ценность продуктов, которых нет в каталоге.",
            _ => "Ответьте на вопросы в поле уточнения ниже."
        };
        string context = IsDish(session) ? "Своё блюдо" : $"В дневник · {DiaryDay(session.MealDate, today)}";
        return new PreviewStatusText(tone, icon, context, title, hint);
    }

    public static string Weight(decimal? grams) => grams is decimal value ? $"{NutritionText.Amount(value)} г" : "вес не указан";

    public static string FinalWeight(decimal? grams) => grams is decimal value ? $"Готовый вес {Weight(value)}" : "Готовый вес не указан";

    public static string PortionWeight(PortionPreviewResponse portion) =>
        portion.FractionOfDish is decimal fraction
            ? portion.WeightInGrams is null
                ? $"{NutritionText.Amount(fraction * 100m)} % блюда"
                : $"{Weight(portion.WeightInGrams)} · {NutritionText.Amount(fraction * 100m)} % блюда"
            : Weight(portion.WeightInGrams);

    public static string IngredientWeight(IngredientPreviewResponse ingredient) => ingredient.RemovedWeightInGrams switch
    {
        null => $"{Weight(ingredient.WeightInGrams)} · сколько убрали, не указано",
        > 0 => $"{Weight(ingredient.WeightInGrams)} · убрано {Weight(ingredient.RemovedWeightInGrams)} · вошло {Weight(ingredient.IncludedWeightInGrams)}",
        _ => Weight(ingredient.WeightInGrams)
    };

    public static string DraftContext(LocalInputDraft draft, DateOnly today) =>
        draft.Purpose == MealSessionPurpose.CreateDish ? "Новое блюдо" : $"Запись за {DiaryDay(draft.MealDate, today)}";

    // a single product eaten on its own has no meaningful "dish": its weight, total and composition repeat the portion
    public static bool IsSingleProduct(DishPreviewResponse dish) =>
        dish.Ingredients is [{ } only] && dish.Portions.Count > 0 &&
        string.Equals(only.ResolvedProduct?.Name ?? only.ProductName, dish.Name, StringComparison.CurrentCultureIgnoreCase);

    // a product eaten on its own without a weight also lacks the dish weight and the portion: one question is enough
    public static IReadOnlyList<WorkflowIssueResponse> VisibleIssues(MealSessionResponse session)
    {
        HashSet<string> weightless = session.Issues
            .Where(issue => issue.Code == "ingredient_weight_missing" &&
                string.Equals(issue.IngredientName, issue.DishName, StringComparison.CurrentCultureIgnoreCase))
            .Select(issue => issue.DishName)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        return session.Issues
            .Where(issue => issue.Code is not ("final_weight_missing" or "portion_missing") || !weightless.Contains(issue.DishName))
            .ToArray();
    }

    public static bool IsWholeDishEaten(DishPreviewResponse dish) =>
        dish.Portions is [{ WeightInGrams: { } weight }] && weight == dish.FinalWeightInGrams;

    // a product eaten on its own is a dish of one ingredient with the same name
    public static string IssueTitle(WorkflowIssueResponse issue) =>
        issue.IngredientName is null || string.Equals(issue.IngredientName, issue.DishName, StringComparison.CurrentCultureIgnoreCase)
            ? issue.DishName
            : $"{issue.DishName} · {issue.IngredientName}";

    // the workflow explains problems in english for the api; the screen names the next step
    public static string Issue(WorkflowIssueResponse issue) => issue.Code switch
    {
        "product_not_found" => "Этого продукта нет в каталоге. Добавьте его пищевую ценность.",
        "product_ambiguous" => "В каталоге несколько таких продуктов с разной пищевой ценностью. Уточните название в поле уточнения.",
        "ingredient_weight_missing" => "Укажите вес в поле уточнения.",
        "removed_weight_missing" => "Укажите в поле уточнения, сколько граммов убрали.",
        "final_weight_missing" => "Укажите в поле уточнения вес готового блюда.",
        "portion_missing" => "Укажите в поле уточнения, сколько вы съели.",
        "single_dish_required" => "Опишите одно блюдо с названием, чтобы сохранить его состав.",
        "saved_dish_invalid" => "Проверьте название и готовый вес: пищевая ценность получилась больше возможной.",
        _ => issue.Message
    };

    public static string HistoryTitle(int index) => index == 0 ? "Описание" : $"Уточнение {index}";

    // unknown parts are left out instead of being named as unknown
    public static string Source(ProductResponse product) => string.Join(" · ", new[]
    {
        string.IsNullOrWhiteSpace(product.SourceKind) ? null : NutritionText.Source(product.SourceKind),
        string.IsNullOrWhiteSpace(product.SourceName) ? null : product.SourceName,
        NutritionText.Quality(product.DataQuality)
    }.OfType<string>());

    // what a failed request to Groq tells the user; the text stays in the field and in the saved input
    public static string GroqError(Exception exception) => GroqError(exception, "для текста", "добавьте запись вручную");

    // the model of this kind of request and the way that works without Groq, both as they read inside a sentence
    public static string GroqError(Exception exception, string model, string manualWay) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            $"Groq не принял ключ. Проверьте ключ в настройках или {manualWay}.",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
            "Groq временно ограничил запросы по этому ключу. Подождите минуту и повторите.",
        HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.NotFound } =>
            $"Groq не принял запрос. Проверьте модель {model} в настройках и повторите.",
        HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError } =>
            $"Groq сейчас не отвечает. Повторите позже или {manualWay}.",
        HttpRequestException or OperationCanceledException =>
            $"Не удалось связаться с Groq. Проверьте интернет и повторите или {manualWay}.",
        InvalidDataException => "Groq вернул неполный ответ. Повторите запрос.",
        _ => UiErrors.Describe(exception)
    };
}
