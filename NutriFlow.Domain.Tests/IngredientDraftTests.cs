using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class IngredientDraftTests
{
    [Fact]
    public void Constructor_StoresProductNameAndWeight()
    {
        IngredientDraft ingredient = new IngredientDraft("Product", 125.5m);

        Assert.Equal("Product", ingredient.ProductName);
        Assert.Equal(125.5m, ingredient.WeightInGrams);
    }

    [Fact]
    public void Constructor_WithNullProductName_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new IngredientDraft(null!, 100m));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithBlankProductName_ThrowsArgumentException(
        string productName)
    {
        Assert.Throws<ArgumentException>(
            () => new IngredientDraft(productName, 100m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithNonPositiveWeight_ThrowsArgumentOutOfRangeException(
        int weightInGrams)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IngredientDraft("Product", weightInGrams));
    }

    [Fact]
    public void Constructor_WithEstimatedWeight_StoresQuality()
    {
        IngredientDraft ingredient = new IngredientDraft(
            " Product ",
            125m,
            DataQuality.Estimated);

        Assert.Equal("Product", ingredient.ProductName);
        Assert.Equal(125m, ingredient.WeightInGrams);
        Assert.Equal(DataQuality.Estimated, ingredient.WeightQuality);
    }

    [Fact]
    public void Constructor_WithUnknownWeight_StoresUnknownValue()
    {
        IngredientDraft ingredient = new IngredientDraft(
            "Product",
            null,
            DataQuality.Unknown);

        Assert.Null(ingredient.WeightInGrams);
        Assert.Equal(DataQuality.Unknown, ingredient.WeightQuality);
    }

    [Fact]
    public void Constructor_WithUnknownWeightAndExactQuality_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new IngredientDraft(
                "Product",
                null,
                DataQuality.Exact));
    }

    [Fact]
    public void Constructor_WithKnownWeightAndUnknownQuality_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => new IngredientDraft(
                "Product",
                100m,
                DataQuality.Unknown));
    }
}
