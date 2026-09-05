using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class FakeMealParserTests
{
    [Fact]
    public void Parse_WithSupportedInput_ReturnsStructuredDraft()
    {
        CaptureSession session = CreateSupportedSession();
        FakeMealParser parser = new FakeMealParser();

        MealDraft result = parser.Parse(session);

        Assert.False(result.RequiresClarification);
        Assert.Empty(result.ClarificationQuestions);
        DishDraft dish = Assert.Single(result.Dishes);
        Assert.Equal("Демо-блюдо", dish.Name);
        Assert.Equal(250m, dish.FinalWeightInGrams);
        Assert.Equal(2, dish.Ingredients.Count);
        Assert.Equal("Демо-продукт A", dish.Ingredients[0].ProductName);
        Assert.Equal(200m, dish.Ingredients[0].WeightInGrams);
        Assert.Equal("Демо-продукт B", dish.Ingredients[1].ProductName);
        Assert.Equal(100m, dish.Ingredients[1].WeightInGrams);
        Assert.Equal(
            new List<decimal> { 125m, 62.5m, 62.5m },
            dish.PortionWeightsInGrams);
        Assert.Equal(CaptureSessionState.ReadyForReview, session.State);
    }

    [Fact]
    public void Parse_WithNullSession_ThrowsArgumentNullException()
    {
        FakeMealParser parser = new FakeMealParser();

        Assert.Throws<ArgumentNullException>(() => parser.Parse(null!));
    }

    [Fact]
    public void Parse_WhileSessionIsCollecting_ThrowsInvalidOperationException()
    {
        CaptureSession session = new CaptureSession();
        session.AddEvent(new InputEvent("Message"));
        FakeMealParser parser = new FakeMealParser();

        Assert.Throws<InvalidOperationException>(() => parser.Parse(session));
    }

    [Fact]
    public void Parse_AfterSessionIsConfirmed_ThrowsInvalidOperationException()
    {
        CaptureSession session = CreateSupportedSession();
        session.Confirm();
        FakeMealParser parser = new FakeMealParser();

        Assert.Throws<InvalidOperationException>(() => parser.Parse(session));
    }

    [Fact]
    public void Parse_WithUnsupportedInput_ThrowsNotSupportedException()
    {
        CaptureSession session = new CaptureSession();
        session.AddEvent(new InputEvent("Unknown scenario"));
        session.FinishCollecting();
        FakeMealParser parser = new FakeMealParser();

        Assert.Throws<NotSupportedException>(() => parser.Parse(session));
    }

    [Fact]
    public void Parse_WithChangedEventInSupportedSequence_ThrowsNotSupportedException()
    {
        CaptureSession session = new CaptureSession();
        session.AddEvent(new InputEvent("Добавил 200 г демо-продукта A."));
        session.AddEvent(new InputEvent("Потом добавил 100 г другого продукта."));
        session.AddEvent(new InputEvent("Готовое блюдо весит 250 г."));
        session.AddEvent(
            new InputEvent("Съел 125 г, потом ещё две порции по 62,5 г."));
        session.FinishCollecting();
        FakeMealParser parser = new FakeMealParser();

        Assert.Throws<NotSupportedException>(() => parser.Parse(session));
    }

    [Fact]
    public void Parse_WithAmbiguousInput_ReturnsQuestionAndKeepsSessionReadyForReview()
    {
        CaptureSession session = new CaptureSession();
        session.AddEvent(new InputEvent("Добавил 500 г макарон."));
        session.AddEvent(new InputEvent("Готовое блюдо весит 900 г."));
        session.AddEvent(new InputEvent("Съел 300 г."));
        session.FinishCollecting();
        FakeMealParser parser = new FakeMealParser();

        MealDraft result = parser.Parse(session);

        Assert.True(result.RequiresClarification);
        Assert.Equal(
            new List<string>
            {
                "500 г макарон указаны в сухом или уже сваренном виде?"
            },
            result.ClarificationQuestions);
        DishDraft dish = Assert.Single(result.Dishes);
        Assert.Equal("Макароны", dish.Name);
        Assert.Equal(CaptureSessionState.ReadyForReview, session.State);
    }

    [Fact]
    public void Parse_AfterClarification_ReturnsResolvedDraft()
    {
        CaptureSession session = new CaptureSession();
        session.AddEvent(new InputEvent("Добавил 500 г макарон."));
        session.AddEvent(new InputEvent("Готовое блюдо весит 900 г."));
        session.AddEvent(new InputEvent("Съел 300 г."));
        session.FinishCollecting();
        FakeMealParser parser = new FakeMealParser();

        MealDraft ambiguousDraft = parser.Parse(session);
        session.ReopenForEditing();
        session.AddEvent(new InputEvent("В сухом виде."));
        session.FinishCollecting();
        MealDraft resolvedDraft = parser.Parse(session);

        Assert.True(ambiguousDraft.RequiresClarification);
        Assert.False(resolvedDraft.RequiresClarification);
        DishDraft dish = Assert.Single(resolvedDraft.Dishes);
        IngredientDraft ingredient = Assert.Single(dish.Ingredients);
        Assert.Equal("Макароны сухие", ingredient.ProductName);
        Assert.Equal(500m, ingredient.WeightInGrams);
        Assert.Equal(900m, dish.FinalWeightInGrams);
        Assert.Equal(new List<decimal> { 300m }, dish.PortionWeightsInGrams);
        Assert.Equal(CaptureSessionState.ReadyForReview, session.State);
    }

    private static CaptureSession CreateSupportedSession()
    {
        CaptureSession session = new CaptureSession();
        session.AddEvent(new InputEvent("Добавил 200 г демо-продукта A."));
        session.AddEvent(new InputEvent("Потом добавил 100 г демо-продукта B."));
        session.AddEvent(new InputEvent("Готовое блюдо весит 250 г."));
        session.AddEvent(
            new InputEvent("Съел 125 г, потом ещё две порции по 62,5 г."));
        session.FinishCollecting();

        return session;
    }
}
