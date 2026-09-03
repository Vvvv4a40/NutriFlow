using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class ProductTests
{
    [Fact]
    public void Constructor_WithNullName_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new Product(null!, CreateNutrition()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithBlankName_ThrowsArgumentException(string name)
    {
        Assert.Throws<ArgumentException>(
            () => new Product(name, CreateNutrition()));
    }

    [Fact]
    public void Constructor_WithNullNutrition_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new Product("Product", null!));
    }

    private static NutritionValues CreateNutrition()
    {
        return new NutritionValues(100m, 10m, 4m, 6m);
    }
}
