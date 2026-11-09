using NutriFlow.Api.Services;
using NutriFlow.Domain;

namespace NutriFlow.Api.Tests;

public sealed class DraftEditsTests
{
    private static IngredientDraft Ingredient(string name, decimal? grams, DataQuality quality = DataQuality.Exact) =>
        new(name, grams, grams is null ? DataQuality.Unknown : quality);

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
