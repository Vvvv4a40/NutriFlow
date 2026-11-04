using NutriFlow.Domain;

namespace NutriFlow.Mobile.Core.Tests;

public sealed class DiaryDraftTests
{
    private static IngredientDraft Ingredient(string name, decimal? grams, DataQuality quality = DataQuality.Exact) =>
        new(name, grams, grams is null ? DataQuality.Unknown : quality);

    [Fact]
    public void ADishWithoutFinalWeightOrPortionWasEatenWholeAndNothingIsAsked()
    {
        DishDraft dish = new("Гречка с курицей", [Ingredient("Гречка", 150m), Ingredient("Курица", 120m)], null, DataQuality.Unknown, []);

        MealDraft draft = DiaryDraftDefaults.Apply(new MealDraft([dish], ["Сколько весит готовое блюдо?"]));

        DishDraft result = Assert.Single(draft.Dishes);
        Assert.Equal(270m, result.FinalWeightInGrams);
        Assert.Equal(DataQuality.Estimated, result.FinalWeightQuality);
        Assert.Equal(1m, Assert.Single(result.Portions).FractionOfDish);
        Assert.Empty(draft.ClarificationQuestions);
    }

    [Fact]
    public void AFoodOnItsOwnKeepsTheQualityOfItsWeight()
    {
        DishDraft dish = new("Яблоко", [Ingredient("Яблоко", 150m, DataQuality.Estimated)], null, DataQuality.Unknown, []);

        DishDraft result = DiaryDraftDefaults.Apply(new MealDraft([dish], [])).Dishes[0];

        Assert.Equal(DataQuality.Estimated, result.FinalWeightQuality);
    }

    [Fact]
    public void WhatTheUserSaidStaysAsItIs()
    {
        DishDraft dish = new("Суп", [Ingredient("Вода", 800m), Ingredient("Курица", 200m)], 900m, DataQuality.Exact, [new PortionDraft(300m)]);

        DishDraft result = DiaryDraftDefaults.Apply(new MealDraft([dish], [])).Dishes[0];

        Assert.Equal(900m, result.FinalWeightInGrams);
        Assert.Equal(300m, Assert.Single(result.Portions).WeightInGrams);
    }

    [Fact]
    public void AMissingIngredientWeightStillNeedsTheQuestion()
    {
        DishDraft dish = new("Гречка с курицей", [Ingredient("Гречка", 150m), Ingredient("Курица", null)], null, DataQuality.Unknown, []);

        MealDraft draft = DiaryDraftDefaults.Apply(new MealDraft([dish], ["Сколько курицы?"]));

        Assert.Null(draft.Dishes[0].FinalWeightInGrams);
        Assert.Equal("Сколько курицы?", Assert.Single(draft.ClarificationQuestions));
    }

    [Fact]
    public void ANewWeightOfAFoodOnItsOwnChangesTheWholeLine()
    {
        DishDraft dish = new("Гречка", [Ingredient("Гречка", 200m, DataQuality.Estimated)], 200m, DataQuality.Estimated, [new PortionDraft(200m, DataQuality.Estimated)]);

        DishDraft result = DraftEdits.SetWeight(new MealDraft([dish], []), 0, 150m).Dishes[0];

        Assert.Equal(150m, result.Ingredients[0].WeightInGrams);
        Assert.Equal(DataQuality.Exact, result.Ingredients[0].WeightQuality);
        Assert.Equal(150m, result.FinalWeightInGrams);
        Assert.Equal(150m, Assert.Single(result.Portions).WeightInGrams);
    }

    [Fact]
    public void ANewPortionOfADishCannotBeLargerThanTheDish()
    {
        DishDraft dish = new("Суп", [Ingredient("Вода", 800m), Ingredient("Курица", 200m)], 900m, DataQuality.Exact, [new PortionDraft(300m)]);
        MealDraft draft = new([dish], []);

        Assert.Equal(400m, Assert.Single(DraftEdits.SetWeight(draft, 0, 400m).Dishes[0].Portions).WeightInGrams);
        Assert.Contains("больше всего блюда", Assert.Throws<ArgumentException>(() => DraftEdits.SetWeight(draft, 0, 1000m)).Message);
    }

    [Fact]
    public void ARemovedLineLeavesTheOthers()
    {
        MealDraft draft = new([
            new DishDraft("А", [Ingredient("А", 100m)], 100m, DataQuality.Exact, [new PortionDraft(100m)]),
            new DishDraft("Б", [Ingredient("Б", 100m)], 100m, DataQuality.Exact, [new PortionDraft(100m)])], []);

        Assert.Equal("Б", Assert.Single(DraftEdits.Remove(draft, 0).Dishes).Name);
    }
}
