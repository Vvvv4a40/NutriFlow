using NutriFlow.Api.Contracts;
using NutriFlow.Ui.Pages;

namespace NutriFlow.Ui.Tests;

public sealed class DiaryTextTests
{
    private static readonly DateOnly Today = new(2026, 11, 2);

    [Fact]
    public void DateNamesTodayAndYesterdayAndShortensOtherDays()
    {
        Assert.Equal("Сегодня, 2 ноября", DiaryText.DateLabel(Today, Today));
        Assert.Equal("Вчера, 1 ноября", DiaryText.DateLabel(Today.AddDays(-1), Today));
        Assert.Equal("сб, 31 октября", DiaryText.DateLabel(Today.AddDays(-2), Today));
        Assert.Equal("вт, 3 ноября", DiaryText.DateLabel(Today.AddDays(1), Today));
    }

    [Fact]
    public void CentreOfTheArcShowsWhatIsLeftOrTheExcess()
    {
        NutritionResponse eaten = new(1240.4m, 60m, 40m, 150m);

        // ru-RU groups digits with a no-break space
        Assert.Equal(new DiaryHeadline("1 240", "ккал съедено", "Съедено 1240,4 килокалорий", false), DiaryText.Headline(eaten, null));
        Assert.Equal(new DiaryHeadline("760", "ккал осталось", "Осталось 759,6 килокалорий", false),
            DiaryText.Headline(eaten, new NutritionResponse(2000m, 150m, 67m, 200m)));
        Assert.Equal(new DiaryHeadline("+240", "ккал сверх цели", "Сверх цели 240,4 килокалорий", true),
            DiaryText.Headline(eaten, new NutritionResponse(1000m, 75m, 33m, 100m)));
        Assert.Equal(new DiaryHeadline("0", "ккал осталось", "Осталось 0 килокалорий", false),
            DiaryText.Headline(new NutritionResponse(1000m, 0m, 0m, 0m), new NutritionResponse(1000m, 0m, 0m, 0m)));
    }

    [Fact]
    public void GoalOfZeroCaloriesCountsAsNoGoal()
    {
        NutritionResponse zero = new(0m, 0m, 0m, 0m);

        Assert.Null(DiaryText.EffectiveGoal(zero));
        Assert.Null(DiaryText.EffectiveGoal(null));
        Assert.Equal("ккал съедено", DiaryText.Headline(new NutritionResponse(300m, 0m, 0m, 0m), zero).Caption);
    }

    [Fact]
    public void MacroBarsSayTheGoalAndAVisibleExcess()
    {
        Assert.Equal(new MacroProgressText("45", " г", null, "Белки: съедено 45 граммов. Цель не задана"),
            DiaryText.Macro("Белки", 45.2m, null));
        Assert.Equal(new MacroProgressText("45", " / 120 г", null, "Белки: съедено 45 граммов. Цель 120 граммов"),
            DiaryText.Macro("Белки", 45.2m, 120m));
        Assert.Equal(new MacroProgressText("32", " / 9 г", "+23", "Углеводы: съедено 32 граммов. Цель 9 граммов. Превышение на 23 граммов"),
            DiaryText.Macro("Углеводы", 32m, 9m));
        // under half a gram over rounds to «+0», which is not worth a red mark
        Assert.Null(DiaryText.Macro("Жиры", 40.3m, 40m).Excess);
    }

    [Theory]
    [InlineData(1, "1 запись")]
    [InlineData(2, "2 записи")]
    [InlineData(4, "4 записи")]
    [InlineData(5, "5 записей")]
    [InlineData(11, "11 записей")]
    [InlineData(14, "14 записей")]
    [InlineData(21, "21 запись")]
    [InlineData(22, "22 записи")]
    [InlineData(112, "112 записей")]
    public void EntryCountUsesRussianPlurals(int count, string expected) =>
        Assert.Equal(expected, DiaryText.Plural(count, "запись", "записи", "записей"));

    [Fact]
    public void EntryRowMarksEstimatesAndSpeaksNutritionInWords()
    {
        MealEntryResponse estimated = new("Овсянка", 200.5m, new NutritionResponse(176.44m, 6m, 3.4m, 30.1m), "Estimated", 7, 2);
        MealEntryResponse exact = estimated with { Quality = "Exact" };
        MealEntryResponse unknown = estimated with { Quality = "Unknown" };

        Assert.Equal("≈ 176 ккал", DiaryText.EntryCalories(estimated));
        Assert.Equal("176 ккал", DiaryText.EntryCalories(exact));
        Assert.Equal("? 176 ккал", DiaryText.EntryCalories(unknown));
        Assert.Equal("Овсянка. 200,5 граммов. 176,44 килокалорий, белки 6, жиры 3, углеводы 30 граммов. оценочные данные",
            DiaryText.EntryDescription(estimated));
        Assert.Equal("Овсянка · 200,5 г\nоценочные данные", DiaryText.EntryActionsTitle(estimated));
        Assert.Equal("2 записи · 353 ккал", DiaryText.EntriesSummary([estimated, exact]));
    }
}
