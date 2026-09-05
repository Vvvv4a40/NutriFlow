using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class ProductTests
{
    [Fact]
    public void Constructor_StoresProductData()
    {
        NutritionValues nutrition = CreateNutrition();
        NutritionSource source = CreateSource();

        Product product = new Product("Product", nutrition, source);

        Assert.Equal("Product", product.Name);
        Assert.Same(nutrition, product.NutritionPer100Grams);
        Assert.Same(source, product.Source);
    }

    [Fact]
    public void Constructor_WithOuterSpaces_TrimsName()
    {
        Product product = new Product(
            " Product ",
            CreateNutrition(),
            CreateSource());

        Assert.Equal("Product", product.Name);
    }

    [Fact]
    public void Constructor_WithNullName_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new Product(null!, CreateNutrition(), CreateSource()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithBlankName_ThrowsArgumentException(string name)
    {
        Assert.Throws<ArgumentException>(
            () => new Product(name, CreateNutrition(), CreateSource()));
    }

    [Fact]
    public void Constructor_WithNullNutrition_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new Product("Product", null!, CreateSource()));
    }

    [Fact]
    public void Constructor_WithNullSource_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new Product("Product", CreateNutrition(), null!));
    }

    private static NutritionValues CreateNutrition()
    {
        return new NutritionValues(100m, 10m, 4m, 6m);
    }

    private static NutritionSource CreateSource()
    {
        return new NutritionSource(
            NutritionSourceKind.ManualInput,
            DataQuality.Exact,
            "Test data");
    }
}
