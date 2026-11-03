using System.Net;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Ui.Components;
using NutriFlow.Ui.Pages;

namespace NutriFlow.Ui.Tests;

public sealed class CaptureTextTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);

    [Fact]
    public void ContextNamesTheDayOfTheEntry()
    {
        Assert.Equal("Запись попадёт в дневник за сегодня, 3 ноября. Перед сохранением вы увидите расчёт.",
            CaptureText.Context(MealSessionPurpose.Diary, Today, Today));
        Assert.StartsWith("Запись попадёт в дневник за вчера, 2 ноября.", CaptureText.Context(MealSessionPurpose.Diary, Today.AddDays(-1), Today));
        Assert.StartsWith("Запись попадёт в дневник за 30 октября.", CaptureText.Context(MealSessionPurpose.Diary, new DateOnly(2026, 10, 30), Today));
        Assert.StartsWith("Опишите ингредиенты", CaptureText.Context(MealSessionPurpose.CreateDish, Today, Today));
    }

    [Fact]
    public void CounterAppearsOnlyNearTheLimit()
    {
        Assert.Null(CaptureText.Counter(3200));
        Assert.Equal("3201/4000", CaptureText.Counter(3201));
    }

    [Fact]
    public void StatusTellsWhetherTheCalculationCanBeConfirmed()
    {
        PreviewStatusText ready = CaptureText.Status(Session("ReadyForConfirmation", true), Today);
        PreviewStatusText missing = CaptureText.Status(Session("NeedsProducts", false), Today);
        PreviewStatusText dish = CaptureText.Status(Session("ReadyForConfirmation", true, "CreateDish"), Today);

        Assert.Equal((Tone.Success, "Готово к подтверждению", "В дневник · сегодня, 3 ноября"), (ready.Tone, ready.Title, ready.Context));
        Assert.Equal((Tone.Warning, "Не хватает пищевой ценности"), (missing.Tone, missing.Title));
        Assert.StartsWith("Добавьте пищевую ценность", missing.Hint);
        Assert.Equal("Своё блюдо", dish.Context);
        Assert.StartsWith("Проверьте состав и готовый вес.", dish.Hint);
        Assert.Equal("Проверьте блюдо", CaptureText.Title(Session("ReadyForConfirmation", true, "CreateDish"), MealSessionPurpose.Diary));
        Assert.Equal("Новое блюдо", CaptureText.Title(null, MealSessionPurpose.CreateDish));
    }

    [Fact]
    public void WeightsSayWhatIsUnknownAndWhatWasRemoved()
    {
        Assert.Equal("вес не указан", CaptureText.Weight(null));
        Assert.Equal("Готовый вес не указан", CaptureText.FinalWeight(null));
        Assert.Equal("150 г", CaptureText.IngredientWeight(Ingredient(150m, 0m, 150m)));
        Assert.Equal("200 г · убрано 50 г · вошло 150 г", CaptureText.IngredientWeight(Ingredient(200m, 50m, 150m)));
        Assert.Equal("62,5 г · 25 % блюда", CaptureText.PortionWeight(new PortionPreviewResponse(62.5m, 0.25m, "Exact", null, null)));
        Assert.Equal("200 г · сколько убрали, не указано", CaptureText.IngredientWeight(new IngredientPreviewResponse("Рис", 200m, "Exact", null, "Unknown", null, null, null)));
        Assert.Equal("ручной ввод · точные данные", CaptureText.Source(new ProductResponse("Рис", 1m, 1m, 1m, 1m, "ManualInput", "Exact", "", null, null)));
    }

    [Fact]
    public void IssuesAreInRussianAndDoNotRepeatTheName()
    {
        WorkflowIssueResponse missing = new("product_not_found", "гречка", "гречка", "The product is not available in the local catalog.");
        WorkflowIssueResponse weight = new("ingredient_weight_missing", "Плов", "рис", "The ingredient weight is required for calculation.");
        WorkflowIssueResponse unknown = new("new_code", "Плов", null, "Новая проверка.");

        Assert.Equal("гречка", CaptureText.IssueTitle(missing));
        Assert.Equal("Этого продукта нет в каталоге. Добавьте его пищевую ценность.", CaptureText.Issue(missing));
        Assert.Equal("Плов · рис", CaptureText.IssueTitle(weight));
        Assert.Equal("Укажите вес в поле уточнения.", CaptureText.Issue(weight));
        Assert.Equal("Новая проверка.", CaptureText.Issue(unknown));
    }

    [Fact]
    public void HintNamesOnlyTheStepsThatAreLeft()
    {
        Assert.StartsWith("Добавьте пищевую ценность продуктов", CaptureText.Status(Session("NeedsProducts", false), Today).Hint);
        Assert.Equal("Ответьте на вопросы в поле уточнения ниже.", CaptureText.Status(Session("NeedsClarification", false), Today).Hint);
        MealSessionResponse both = Session("NeedsProducts", false) with { ClarificationQuestions = ["Сколько граммов «гречка»?"] };
        Assert.StartsWith("Ответьте на вопросы в поле уточнения и добавьте", CaptureText.Status(both, Today).Hint);
    }

    [Fact]
    public void ProductWithoutWeightAsksOnlyForTheWeight()
    {
        MealSessionResponse session = Session("NeedsClarification", false) with
        {
            Issues =
            [
                new("ingredient_weight_missing", "гречка", "гречка", ""),
                new("final_weight_missing", "гречка", null, ""),
                new("portion_missing", "гречка", null, ""),
                new("final_weight_missing", "Плов", null, "")
            ]
        };

        Assert.Equal(["ingredient_weight_missing", "final_weight_missing"], CaptureText.VisibleIssues(session).Select(issue => issue.Code));
        Assert.Equal("Плов", CaptureText.VisibleIssues(session)[1].DishName);
    }

    [Fact]
    public void ProductEatenOnItsOwnIsNotADishEvenWithoutNutrition()
    {
        DishPreviewResponse dish = new("огурец", 80m, "Exact", null, null, null, null,
            [new IngredientPreviewResponse("огурец", 80m, "Exact", 0m, "Exact", 80m, null, null)],
            [new PortionPreviewResponse(80m, null, "Exact", null, null)]);

        Assert.True(CaptureText.IsSingleProduct(dish));
        Assert.False(CaptureText.IsSingleProduct(dish with { Portions = [] }));
        Assert.Equal("Сохранить блюдо", CaptureText.ConfirmText(Session("ReadyForConfirmation", true, "CreateDish")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Groq не принял ключ.")]
    [InlineData(HttpStatusCode.TooManyRequests, "Groq временно ограничил запросы")]
    [InlineData(HttpStatusCode.NotFound, "Groq не принял запрос. Проверьте модель")]
    [InlineData(HttpStatusCode.BadGateway, "Groq сейчас не отвечает.")]
    public void GroqErrorsSayWhatToCheck(HttpStatusCode status, string start)
    {
        Assert.StartsWith(start, CaptureText.GroqError(new HttpRequestException("failed", null, status)));
    }

    [Fact]
    public void NoNetworkAndTimeoutAreTheSameForTheUser()
    {
        Assert.Equal(CaptureText.Unreachable, CaptureText.GroqError(new HttpRequestException("Failed to fetch")));
        Assert.Equal(CaptureText.Unreachable, CaptureText.GroqError(new TaskCanceledException()));
        Assert.Equal("Groq вернул неполный ответ. Повторите запрос.", CaptureText.GroqError(new InvalidDataException()));
    }

    private static MealSessionResponse Session(string status, bool canConfirm, string purpose = "Diary") =>
        new(Guid.NewGuid(), status, Today, ["гречка 150 г"], "token", canConfirm, [], [], [], purpose);

    private static IngredientPreviewResponse Ingredient(decimal weight, decimal removed, decimal included) =>
        new("Гречка", weight, "Exact", removed, "Exact", included, null, null);
}
