using System.Net;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Ai;
using NutriFlow.Mobile.Core;
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
    public void ALimitSaysHowLongToWaitAndADailyOneSaysWhatElseToDo()
    {
        Assert.StartsWith("Groq просит подождать 10 с", CaptureText.GroqError(new GroqRateLimitException(TimeSpan.FromSeconds(9.5), false)));
        Assert.StartsWith("Groq просит подождать 2 мин", CaptureText.GroqError(new GroqRateLimitException(TimeSpan.FromSeconds(100), false)));
        Assert.StartsWith("Дневной лимит этого ключа", CaptureText.GroqError(new GroqRateLimitException(TimeSpan.FromHours(3), true)));
        Assert.StartsWith("Groq временно ограничил запросы", CaptureText.GroqError(new GroqRateLimitException(null, false)));
    }

    [Fact]
    public void TheReviewSumsTheFoodsAndMarksWhatIsEstimated()
    {
        ProductResponse exact = new("Творог", 100m, 16m, 5m, 3m, "ManualInput", "Exact", "Ручной ввод", null, null);
        ProductResponse estimated = exact with { Name = "Гречка варёная", DataQuality = "Estimated" };
        MealSessionResponse session = Session("ReadyForConfirmation", true) with
        {
            Dishes =
            [
                Food("Творог", exact, 200m, "Exact", new NutritionResponse(200m, 32m, 10m, 6m), "Exact"),
                Food("Гречка варёная", estimated, 150m, "Estimated", new NutritionResponse(165m, 6m, 2m, 32m), "Estimated")
            ]
        };

        MealReviewSummary review = CaptureText.Review(session);

        Assert.Equal(new NutritionResponse(365m, 38m, 12m, 38m), review.Total);
        Assert.True(review.Estimated);
        Assert.False(review.LacksNutrition);
        Assert.False(review.Lines[0].Estimated);
        Assert.Equal("200 г", review.Lines[0].Detail);
        Assert.True(review.Lines[1].Estimated);
        Assert.Equal("≈ 150 г", review.Lines[1].Detail);
        Assert.Equal(1, review.Lines[1].DishIndex);
        Assert.Equal("Гречка варёная, 150 граммов, примерно 165 килокалорий", CaptureText.ReviewLineDescription(review.Lines[1]));
    }

    [Fact]
    public void TheReviewNamesAFoodWithoutNutritionAndADishByItsComposition()
    {
        ProductResponse known = new("Гречка", 110m, 4m, 1m, 21m, "ManualInput", "Exact", "Ручной ввод", null, null);
        DishPreviewResponse dish = new("Гречка с курицей", 270m, "Estimated", null, null, null, null,
            [
                new IngredientPreviewResponse("Гречка", 150m, "Exact", 0m, "Exact", 150m, known, null),
                new IngredientPreviewResponse("Курица", 120m, "Exact", 0m, "Exact", 120m, null, null)
            ],
            [new PortionPreviewResponse(270m, 1m, "Exact", null, null)]);

        MealReviewLine line = Assert.Single(CaptureText.Review(Session("NeedsProducts", false) with { Dishes = [dish] }).Lines);

        Assert.Equal("Курица", line.MissingProduct);
        Assert.Null(line.Nutrition);
        Assert.Equal("270 г · Гречка, Курица", line.Detail);
        Assert.Equal(270m, line.Grams);
    }

    private static DishPreviewResponse Food(string name, ProductResponse product, decimal grams, string weightQuality, NutritionResponse nutrition, string quality) =>
        new(name, grams, weightQuality, nutrition, quality, null, null,
            [new IngredientPreviewResponse(name, grams, weightQuality, 0m, "Exact", grams, product, nutrition)],
            [new PortionPreviewResponse(grams, null, weightQuality, nutrition, quality)]);

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
