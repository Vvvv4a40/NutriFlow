using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class MealDraftTests
{
    [Fact]
    public void Constructor_StoresValuesAndCopiesCollections()
    {
        List<DishDraft> dishes = new List<DishDraft>
        {
            CreateDish("First dish"),
            CreateDish("Second dish")
        };
        List<string> questions = new List<string> { "Which product was used?" };

        MealDraft draft = new MealDraft(dishes, questions);
        dishes.Clear();
        questions.Clear();

        Assert.Equal(2, draft.Dishes.Count);
        Assert.Equal("First dish", draft.Dishes[0].Name);
        Assert.Equal("Second dish", draft.Dishes[1].Name);
        Assert.Equal(
            new List<string> { "Which product was used?" },
            draft.ClarificationQuestions);
        Assert.True(draft.RequiresClarification);
    }

    [Fact]
    public void RequiresClarification_WithNoQuestions_ReturnsFalse()
    {
        MealDraft draft = new MealDraft(
            new List<DishDraft> { CreateDish("Dish") },
            new List<string>());

        Assert.False(draft.RequiresClarification);
    }

    [Fact]
    public void Constructor_WithNullDishes_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new MealDraft(null!, new List<string>()));
    }

    [Fact]
    public void Constructor_WithNoDishes_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new MealDraft(
                new List<DishDraft>(),
                new List<string>()));
    }

    [Fact]
    public void Constructor_WithNullDish_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new MealDraft(
                new List<DishDraft> { null! },
                new List<string>()));
    }

    [Fact]
    public void Constructor_WithNullQuestions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new MealDraft(
                new List<DishDraft> { CreateDish("Dish") },
                null!));
    }

    [Fact]
    public void Constructor_WithNullQuestion_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new MealDraft(
                new List<DishDraft> { CreateDish("Dish") },
                new List<string> { null! }));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithBlankQuestion_ThrowsArgumentException(string question)
    {
        Assert.Throws<ArgumentException>(
            () => new MealDraft(
                new List<DishDraft> { CreateDish("Dish") },
                new List<string> { question }));
    }

    private static DishDraft CreateDish(string name)
    {
        return new DishDraft(
            name,
            new List<IngredientDraft>
            {
                new IngredientDraft("Product", 100m)
            },
            150m,
            new List<decimal> { 50m });
    }
}
