using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class DishDraftTests
{
    [Fact]
    public void Constructor_StoresValuesAndCopiesCollections()
    {
        List<IngredientDraft> ingredients = new List<IngredientDraft>
        {
            new IngredientDraft("Product", 100m)
        };
        List<decimal> portionWeights = new List<decimal> { 50m };

        DishDraft dish = new DishDraft(
            "Dish",
            ingredients,
            150m,
            portionWeights);
        ingredients.Clear();
        portionWeights.Clear();

        Assert.Equal("Dish", dish.Name);
        Assert.Equal(150m, dish.FinalWeightInGrams);
        Assert.Single(dish.Ingredients);
        Assert.Equal("Product", dish.Ingredients[0].ProductName);
        Assert.Equal(new List<decimal> { 50m }, dish.PortionWeightsInGrams);
    }

    [Fact]
    public void Constructor_AllowsNoConsumedPortions()
    {
        DishDraft dish = new DishDraft(
            "Dish",
            CreateIngredients(),
            150m,
            new List<decimal>());

        Assert.Empty(dish.PortionWeightsInGrams);
    }

    [Fact]
    public void Constructor_WithNullName_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DishDraft(
                null!,
                CreateIngredients(),
                150m,
                new List<decimal>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithBlankName_ThrowsArgumentException(string name)
    {
        Assert.Throws<ArgumentException>(
            () => new DishDraft(
                name,
                CreateIngredients(),
                150m,
                new List<decimal>()));
    }

    [Fact]
    public void Constructor_WithNullIngredients_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DishDraft(
                "Dish",
                null!,
                150m,
                new List<decimal>()));
    }

    [Fact]
    public void Constructor_WithEmptyIngredients_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new DishDraft(
                "Dish",
                new List<IngredientDraft>(),
                150m,
                new List<decimal>()));
    }

    [Fact]
    public void Constructor_WithNullIngredient_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new DishDraft(
                "Dish",
                new List<IngredientDraft> { null! },
                150m,
                new List<decimal>()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithNonPositiveFinalWeight_ThrowsArgumentOutOfRangeException(
        int finalWeightInGrams)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DishDraft(
                "Dish",
                CreateIngredients(),
                finalWeightInGrams,
                new List<decimal>()));
    }

    [Fact]
    public void Constructor_WithNullPortionWeights_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DishDraft(
                "Dish",
                CreateIngredients(),
                150m,
                null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(151)]
    public void Constructor_WithInvalidPortionWeight_ThrowsArgumentOutOfRangeException(
        int portionWeightInGrams)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DishDraft(
                "Dish",
                CreateIngredients(),
                150m,
                new List<decimal> { portionWeightInGrams }));
    }

    [Fact]
    public void Constructor_WithTotalPortionWeightAboveDishWeight_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new DishDraft(
                "Dish",
                CreateIngredients(),
                150m,
                new List<decimal> { 100m, 51m }));
    }

    [Fact]
    public void Constructor_WithUnknownFinalWeight_StoresIncompleteDraft()
    {
        DishDraft dish = new DishDraft(
            "Dish",
            CreateIngredients(),
            null,
            DataQuality.Unknown,
            new List<PortionDraft>());

        Assert.Null(dish.FinalWeightInGrams);
        Assert.Equal(DataQuality.Unknown, dish.FinalWeightQuality);
    }

    [Fact]
    public void Constructor_WithEstimatedValues_StoresTheirQuality()
    {
        DishDraft dish = new DishDraft(
            "Dish",
            new List<IngredientDraft>
            {
                new IngredientDraft("Product", 100m, DataQuality.Estimated)
            },
            150m,
            DataQuality.Estimated,
            new List<PortionDraft>
            {
                new PortionDraft(50m, DataQuality.Estimated)
            });

        Assert.Equal(DataQuality.Estimated, dish.FinalWeightQuality);
        Assert.Equal(DataQuality.Estimated, dish.Portions[0].WeightQuality);
    }

    [Fact]
    public void Constructor_WithNullPortion_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new DishDraft(
                "Dish",
                CreateIngredients(),
                150m,
                DataQuality.Exact,
                new List<PortionDraft> { null! }));
    }

    private static IReadOnlyList<IngredientDraft> CreateIngredients()
    {
        return new List<IngredientDraft>
        {
            new IngredientDraft("Product", 100m)
        };
    }
}
