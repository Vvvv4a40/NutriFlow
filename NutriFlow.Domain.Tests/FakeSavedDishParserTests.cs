using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class FakeSavedDishParserTests
{
    [Fact]
    public async Task ParseAsync_WithCreateDishPurpose_ReturnsCompositionWithoutPortions()
    {
        CaptureSession session = new CaptureSession(MealSessionPurpose.CreateDish);
        session.AddEvent(new InputEvent("Добавил 200 г демо-продукта A."));
        session.AddEvent(new InputEvent("Потом добавил 100 г демо-продукта B."));
        session.AddEvent(new InputEvent("Готовое блюдо весит 250 г."));
        session.FinishCollecting();

        MealDraft draft = await new FakeMealParser().ParseAsync(session);

        DishDraft dish = Assert.Single(draft.Dishes);
        Assert.Equal("Демо-блюдо", dish.Name);
        Assert.Equal(250m, dish.FinalWeightInGrams);
        Assert.Equal(2, dish.Ingredients.Count);
        Assert.Equal(200m, dish.Ingredients[0].WeightInGrams);
        Assert.Equal(100m, dish.Ingredients[1].WeightInGrams);
        Assert.Empty(dish.Portions);
        Assert.Empty(draft.ClarificationQuestions);
    }

    [Fact]
    public async Task ParseAsync_WithKnownSavedDish_ReturnsCanonicalReferenceAndEatenMass()
    {
        CaptureSession session = new CaptureSession(savedDishNames: ["Демо-блюдо"]);
        session.AddEvent(new InputEvent("Съел 100 г Демо-блюда."));
        session.FinishCollecting();

        MealDraft draft = await new FakeMealParser().ParseAsync(session);

        DishDraft dish = Assert.Single(draft.Dishes);
        IngredientDraft ingredient = Assert.Single(dish.Ingredients);
        Assert.Equal("Демо-блюдо", ingredient.ProductName);
        Assert.Equal(100m, ingredient.WeightInGrams);
        Assert.Equal(100m, dish.FinalWeightInGrams);
        Assert.Equal(100m, Assert.Single(dish.Portions).WeightInGrams);
        Assert.Empty(draft.ClarificationQuestions);
    }

    [Fact]
    public async Task ParseAsync_WithoutKnownSavedDish_DoesNotInventDish()
    {
        CaptureSession session = new CaptureSession();
        session.AddEvent(new InputEvent("Съел 100 г Демо-блюда."));
        session.FinishCollecting();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new FakeMealParser().ParseAsync(session));
    }

    [Fact]
    public async Task ParseAsync_WithCreateDishPurpose_DoesNotAcceptDiaryScenario()
    {
        CaptureSession session = new CaptureSession(
            MealSessionPurpose.CreateDish,
            ["Демо-блюдо"]);
        session.AddEvent(new InputEvent("Съел 100 г Демо-блюда."));
        session.FinishCollecting();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new FakeMealParser().ParseAsync(session));
    }
}
