using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class NutritionLabelDraftTests
{
    [Fact]
    public void CompletePer100GramDraft_CreatesNutritionValues()
    {
        NutritionLabelDraft draft = new NutritionLabelDraft(
            " Product ",
            NutritionBasis.Per100Grams,
            200m,
            10m,
            8m,
            24m,
            Array.Empty<string>());

        NutritionValues nutrition = draft.CreateNutritionValues();

        Assert.Equal("Product", draft.ProductName);
        Assert.True(draft.CanCreateProduct);
        Assert.Equal(200m, nutrition.Calories);
        Assert.Equal(10m, nutrition.ProteinGrams);
        Assert.Equal(8m, nutrition.FatGrams);
        Assert.Equal(24m, nutrition.CarbohydratesGrams);
    }

    [Fact]
    public void IncompleteDraft_CannotCreateNutritionValues()
    {
        NutritionLabelDraft draft = new NutritionLabelDraft(
            null,
            NutritionBasis.Unknown,
            null,
            null,
            null,
            null,
            new[] { "Покажите таблицу КБЖУ крупнее." });

        Assert.True(draft.RequiresClarification);
        Assert.False(draft.CanCreateProduct);
        Assert.Throws<InvalidOperationException>(draft.CreateNutritionValues);
    }

    [Theory]
    [InlineData(NutritionBasis.Per100Milliliters)]
    [InlineData(NutritionBasis.PerServing)]
    public void NonGramBasis_CannotCreateProduct(NutritionBasis basis)
    {
        NutritionLabelDraft draft = new NutritionLabelDraft(
            "Product",
            basis,
            100m,
            5m,
            2m,
            10m,
            Array.Empty<string>());

        Assert.False(draft.CanCreateProduct);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(1001, 0, 0, 0)]
    [InlineData(0, 101, 0, 0)]
    [InlineData(0, 0, 101, 0)]
    [InlineData(0, 0, 0, 101)]
    public void Constructor_WithImplausibleValue_ThrowsArgumentOutOfRangeException(
        int calories,
        int proteinGrams,
        int fatGrams,
        int carbohydratesGrams)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new NutritionLabelDraft(
                "Product",
                NutritionBasis.Per100Grams,
                calories,
                proteinGrams,
                fatGrams,
                carbohydratesGrams,
                Array.Empty<string>()));
    }
}
