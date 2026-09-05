using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class PortionDraftTests
{
    [Theory]
    [InlineData(DataQuality.Exact)]
    [InlineData(DataQuality.Estimated)]
    public void Constructor_WithSupportedQuality_StoresValues(DataQuality quality)
    {
        PortionDraft portion = new PortionDraft(125.5m, quality);

        Assert.Equal(125.5m, portion.WeightInGrams);
        Assert.Equal(quality, portion.WeightQuality);
    }

    [Theory]
    [InlineData(DataQuality.Unknown)]
    [InlineData(DataQuality.Verified)]
    public void Constructor_WithUnsupportedQuality_ThrowsArgumentException(
        DataQuality quality)
    {
        Assert.Throws<ArgumentException>(
            () => new PortionDraft(125m, quality));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WithNonPositiveWeight_ThrowsArgumentOutOfRangeException(
        int weightInGrams)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PortionDraft(weightInGrams));
    }
}
