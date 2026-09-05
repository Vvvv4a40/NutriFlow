using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class NutritionSourceTests
{
    [Fact]
    public void Constructor_StoresSourceData()
    {
        NutritionSource source = new NutritionSource(
            NutritionSourceKind.ExternalService,
            DataQuality.Verified,
            " Example service ",
            " https://example.com/products/42 ");

        Assert.Equal(NutritionSourceKind.ExternalService, source.Kind);
        Assert.Equal(DataQuality.Verified, source.Quality);
        Assert.Equal("Example service", source.Name);
        Assert.Equal("https://example.com/products/42", source.Reference);
    }

    [Fact]
    public void Constructor_WithoutReference_AllowsNullReference()
    {
        NutritionSource source = new NutritionSource(
            NutritionSourceKind.ManualInput,
            DataQuality.Exact,
            "Manual input");

        Assert.Null(source.Reference);
    }

    [Fact]
    public void Constructor_WithUnknownValues_AllowsExplicitUnknownSource()
    {
        NutritionSource source = new NutritionSource(
            NutritionSourceKind.Unknown,
            DataQuality.Unknown,
            "Unknown source");

        Assert.Equal(NutritionSourceKind.Unknown, source.Kind);
        Assert.Equal(DataQuality.Unknown, source.Quality);
    }

    [Fact]
    public void Constructor_WithUndefinedKind_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NutritionSource(
                (NutritionSourceKind)int.MaxValue,
                DataQuality.Exact,
                "Source"));
    }

    [Fact]
    public void Constructor_WithUndefinedQuality_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NutritionSource(
                NutritionSourceKind.ManualInput,
                (DataQuality)int.MaxValue,
                "Source"));
    }

    [Fact]
    public void Constructor_WithNullName_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => new NutritionSource(
                NutritionSourceKind.ManualInput,
                DataQuality.Exact,
                null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithBlankName_ThrowsArgumentException(string name)
    {
        Assert.Throws<ArgumentException>(
            () => new NutritionSource(
                NutritionSourceKind.ManualInput,
                DataQuality.Exact,
                name));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithBlankReference_ThrowsArgumentException(
        string reference)
    {
        Assert.Throws<ArgumentException>(
            () => new NutritionSource(
                NutritionSourceKind.ExternalService,
                DataQuality.Verified,
                "Source",
                reference));
    }

    [Theory]
    [InlineData(NutritionSourceKind.LabelPhoto)]
    [InlineData(NutritionSourceKind.DishPhoto)]
    public void Constructor_WithPhotoSourceWithoutReference_ThrowsArgumentException(
        NutritionSourceKind kind)
    {
        Assert.Throws<ArgumentException>(
            () => new NutritionSource(
                kind,
                DataQuality.Estimated,
                "Photo"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("products/42")]
    [InlineData("ftp://example.com/products/42")]
    public void Constructor_WithWebPageWithoutHttpUrl_ThrowsArgumentException(
        string? reference)
    {
        Assert.Throws<ArgumentException>(
            () => new NutritionSource(
                NutritionSourceKind.WebPage,
                DataQuality.Verified,
                "Web page",
                reference));
    }

    [Theory]
    [InlineData("http://example.com/products/42")]
    [InlineData("https://example.com/products/42")]
    public void Constructor_WithHttpWebPageReference_StoresReference(
        string reference)
    {
        NutritionSource source = new NutritionSource(
            NutritionSourceKind.WebPage,
            DataQuality.Verified,
            "Web page",
            reference);

        Assert.Equal(reference, source.Reference);
    }
}
