using NutriFlow.Api.Contracts;
using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Pages;

namespace NutriFlow.Ui.Tests;

public sealed class GoalEditorTests
{
    [Fact]
    public void WithoutAGoalTheFormStartsAt2000CaloriesWithABalancedSplit()
    {
        GoalEditor editor = new(null);

        Assert.Equal("2000", editor.CaloriesText);
        Assert.Equal((30m, 30m, 40m), Shares(editor));
        Assert.Same(GoalEditor.Presets[0], editor.Preset);
        Assert.Equal(new NutritionResponse(2000m, 150m, 2000m * 0.3m / 9m, 200m), editor.ToGoal());
    }

    [Fact]
    public void ExistingGoalKeepsItsCaloriesAndShares()
    {
        GoalEditor editor = new(new NutritionResponse(1800m, 180m, 60m, 135m));

        Assert.Equal("1800", editor.CaloriesText);
        Assert.Same(GoalEditor.Presets[1], editor.Preset);
        Assert.Equal(40m, Math.Round(editor.Percent(NutritionMacro.Protein), 6));
        Assert.Equal(30m, Math.Round(editor.Percent(NutritionMacro.Fat), 6));
        Assert.Equal(180m, Math.Round(editor.Grams(NutritionMacro.Protein)!.Value, 6));
    }

    [Fact]
    public void PresetSetsEnergySharesAndASliderLeavesIt()
    {
        GoalEditor editor = new(null) { CaloriesText = "1800" };

        editor.ApplyPreset(GoalEditor.Presets[1]);
        Assert.Same(GoalEditor.Presets[1], editor.Preset);
        Assert.Equal((40m, 30m, 30m), Shares(editor));
        Assert.Equal(180m, Math.Round(editor.Grams(NutritionMacro.Protein)!.Value, 6));

        editor.SetShare(NutritionMacro.Fat, 20m);

        Assert.Null(editor.Preset);
        Assert.Equal(20m, editor.Percent(NutritionMacro.Fat));
        // the other two keep their proportion 40 : 30 within the remaining 80 percent
        Assert.Equal(45.714286m, Math.Round(editor.Percent(NutritionMacro.Protein), 6));
        Assert.Equal(100m, editor.Percent(NutritionMacro.Protein) + editor.Percent(NutritionMacro.Fat) + editor.Percent(NutritionMacro.Carbohydrates));
    }

    [Fact]
    public void SliderValuesOutsideTheRangeAreClamped()
    {
        GoalEditor editor = new(null);

        editor.SetShare(NutritionMacro.Protein, 140m);

        Assert.Equal((100m, 0m, 0m), Shares(editor));
    }

    [Fact]
    public void OwnSharesSelectNoPreset() =>
        Assert.Null(new GoalEditor(new NutritionResponse(2000m, 100m, 100m, 200m)).Preset);

    // zero would silently remove the goal: the diary treats it as no goal
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-100")]
    [InlineData("0")]
    public void InvalidCaloriesGiveNoGramsAndNoGoal(string calories)
    {
        GoalEditor editor = new(null) { CaloriesText = calories };

        Assert.Null(editor.Grams(NutritionMacro.Carbohydrates));
        Assert.Null(editor.ToGoal());
    }

    [Fact]
    public void CaloriesTakeACommaAndKeepTheExactDecimalGrams()
    {
        GoalEditor editor = new(null) { CaloriesText = "1750,5" };

        NutritionResponse goal = editor.ToGoal()!;

        Assert.Equal(1750.5m, goal.Calories);
        Assert.Equal(1750.5m * 0.3m / 4m, goal.ProteinGrams);
    }

    private static (decimal Protein, decimal Fat, decimal Carbohydrates) Shares(GoalEditor editor) =>
        (Math.Round(editor.Percent(NutritionMacro.Protein), 6), Math.Round(editor.Percent(NutritionMacro.Fat), 6),
            Math.Round(editor.Percent(NutritionMacro.Carbohydrates), 6));
}
