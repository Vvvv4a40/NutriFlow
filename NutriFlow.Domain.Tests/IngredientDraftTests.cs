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
}
