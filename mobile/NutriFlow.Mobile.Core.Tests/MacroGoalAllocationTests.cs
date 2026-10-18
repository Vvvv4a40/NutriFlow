using NutriFlow.Domain;

namespace NutriFlow.Mobile.Core.Tests;

public sealed class MacroGoalAllocationTests
{
    private const decimal Tolerance = 0.000000000000000000000001m;

    [Fact]
    public void GramsBecomeEnergySharesUsingFourNineFourFactors()
    {
        MacroGoalAllocation allocation = MacroGoalAllocation.FromGrams(75m, 20m, 130m);

        AssertShares(allocation, 30m, 18m, 52m);
    }

    [Fact]
    public void CalorieTargetDeterminesAllThreeGramAmounts()
    {
        MacroGoalAllocation allocation = MacroGoalAllocation.FromGrams(75m, 20m, 130m);

        NutritionValues nutrition = allocation.ToNutrition(1700m);

        Assert.Equal(1700m, nutrition.Calories);
        Assert.Equal(127.5m, nutrition.ProteinGrams);
        Assert.Equal(34m, nutrition.FatGrams);
        Assert.Equal(221m, nutrition.CarbohydratesGrams);
        AssertEnergyMatchesCalories(nutrition);
    }

    [Fact]
    public void ZeroGramsUseTechnicalInitialShares()
    {
        AssertShares(MacroGoalAllocation.FromGrams(0m, 0m, 0m), 30m, 30m, 40m);
    }

    [Fact]
    public void MovingOneShareRedistributesTheOtherTwoProportionally()
    {
        MacroGoalAllocation original = MacroGoalAllocation.FromGrams(100m, 20m, 55m);

        MacroGoalAllocation adjusted = original.WithShare(NutritionMacro.Protein, 20m);

        AssertShares(adjusted, 20m, 36m, 44m);
        AssertShares(original, 50m, 22.5m, 27.5m);
    }

    [Theory]
    [InlineData(NutritionMacro.Protein)]
    [InlineData(NutritionMacro.Fat)]
    [InlineData(NutritionMacro.Carbohydrates)]
    public void AnyMacroCanBeSelectedWithoutChangingTheOtherMacrosRatio(NutritionMacro macro)
    {
        MacroGoalAllocation original = MacroGoalAllocation.FromGrams(75m, 20m, 130m);

        MacroGoalAllocation adjusted = original.WithShare(macro, 37.123456789m);

        Assert.Equal(37.123456789m, Share(adjusted, macro));
        NutritionMacro[] others = Enum.GetValues<NutritionMacro>().Where(value => value != macro).ToArray();
        AssertNear(
            Share(original, others[0]) / Share(original, others[1]),
            Share(adjusted, others[0]) / Share(adjusted, others[1]));
        AssertComplete(adjusted);
    }

    [Theory]
    [InlineData(NutritionMacro.Protein)]
    [InlineData(NutritionMacro.Fat)]
    [InlineData(NutritionMacro.Carbohydrates)]
    public void AFullShareLeavesZeroForTheOtherTwoMacros(NutritionMacro macro)
    {
        MacroGoalAllocation adjusted = MacroGoalAllocation.FromGrams(75m, 20m, 130m).WithShare(macro, 100m);

        Assert.Equal(100m, Share(adjusted, macro));
        foreach (NutritionMacro other in Enum.GetValues<NutritionMacro>().Where(value => value != macro))
        {
            AssertPositiveZero(Share(adjusted, other));
        }

        AssertComplete(adjusted);
        AssertEnergyMatchesCalories(adjusted.ToNutrition(1700m));
    }

    [Theory]
    [InlineData(NutritionMacro.Protein)]
    [InlineData(NutritionMacro.Fat)]
    [InlineData(NutritionMacro.Carbohydrates)]
    public void ReducingASingleFullShareSplitsTheRemainderEvenly(NutritionMacro macro)
    {
        MacroGoalAllocation original = MacroGoalAllocation.FromGrams(0m, 0m, 0m).WithShare(macro, 100m);

        MacroGoalAllocation adjusted = original.WithShare(macro, 20m);

        Assert.Equal(20m, Share(adjusted, macro));
        foreach (NutritionMacro other in Enum.GetValues<NutritionMacro>().Where(value => value != macro))
        {
            Assert.Equal(40m, Share(adjusted, other));
        }

        AssertComplete(adjusted);
    }

    [Theory]
    [InlineData(NutritionMacro.Protein)]
    [InlineData(NutritionMacro.Fat)]
    [InlineData(NutritionMacro.Carbohydrates)]
    public void AZeroShareRedistributesTheEntireTarget(NutritionMacro macro)
    {
        MacroGoalAllocation adjusted = MacroGoalAllocation.FromGrams(75m, 20m, 130m).WithShare(macro, 0m);

        AssertPositiveZero(Share(adjusted, macro));
        AssertComplete(adjusted);
        AssertEnergyMatchesCalories(adjusted.ToNutrition(1700m));
    }

    [Fact]
    public void RepeatedAdjustmentsKeepTheSelectedShareAndExactTotal()
    {
        MacroGoalAllocation allocation = MacroGoalAllocation.FromGrams(119m, 67m, 233m);
        decimal[] percentages =
        [
            0m, 100m, 33.333333333333333333333333333m, 1m, 99m,
            0.0000000000000000000000000001m, 99.99999999999999999999999999m,
            73.5m, 17.125m, 48.765432109876543210987654321m
        ];

        for (int iteration = 0; iteration < 20; iteration++)
        {
            foreach (NutritionMacro macro in Enum.GetValues<NutritionMacro>())
            {
                foreach (decimal percentage in percentages)
                {
                    allocation = allocation.WithShare(macro, percentage);

                    Assert.Equal(percentage, Share(allocation, macro));
                    AssertComplete(allocation);
                    AssertEnergyMatchesCalories(allocation.ToNutrition(1700m));
                }
            }
        }
    }

    [Fact]
    public void VariedDecimalScalesKeepTheSelectedShareAndExactTotal()
    {
        MacroGoalAllocation allocation = MacroGoalAllocation.FromGrams(119m, 67m, 233m);
        Random random = new(1700);

        for (int iteration = 0; iteration < 10000; iteration++)
        {
            NutritionMacro macro = (NutritionMacro)(iteration % 3);
            decimal percentage = new decimal(random.Next(), random.Next(), random.Next(), false, (byte)random.Next(0, 29)) % 100m;

            allocation = allocation.WithShare(macro, percentage);

            Assert.Equal(percentage, Share(allocation, macro));
            AssertComplete(allocation);
            AssertEnergyMatchesCalories(allocation.ToNutrition(1700m));
        }
    }

    [Fact]
    public void ExistingIncoherentGramsAreNormalizedAgainstTheChosenCalorieTarget()
    {
        NutritionValues existing = new(1700m, 100m, 50m, 150m);
        MacroGoalAllocation allocation = MacroGoalAllocation.FromGrams(
            existing.ProteinGrams, existing.FatGrams, existing.CarbohydratesGrams);

        NutritionValues normalized = allocation.ToNutrition(existing.Calories);

        AssertNear(117.24137931034482758620689655m, normalized.ProteinGrams);
        AssertNear(58.620689655172413793103448276m, normalized.FatGrams);
        AssertNear(175.86206896551724137931034483m, normalized.CarbohydratesGrams);
        AssertEnergyMatchesCalories(normalized);
        Assert.Equal(100m, existing.ProteinGrams);
        Assert.Equal(50m, existing.FatGrams);
        Assert.Equal(150m, existing.CarbohydratesGrams);
    }

    [Fact]
    public void RecurringFatDivisionPreservesDecimalPrecision()
    {
        NutritionValues nutrition = MacroGoalAllocation.FromGrams(0m, 0m, 0m).ToNutrition(1700m);

        Assert.Equal(127.5m, nutrition.ProteinGrams);
        Assert.Equal(56.666666666666666666666666667m, nutrition.FatGrams);
        Assert.Equal(170m, nutrition.CarbohydratesGrams);
        AssertEnergyMatchesCalories(nutrition);
    }

    [Fact]
    public void AZeroCalorieTargetProducesPositiveZeroForEveryValue()
    {
        NutritionValues nutrition = MacroGoalAllocation.FromGrams(75m, 20m, 130m).ToNutrition(0m);

        AssertPositiveZero(nutrition.Calories);
        AssertPositiveZero(nutrition.ProteinGrams);
        AssertPositiveZero(nutrition.FatGrams);
        AssertPositiveZero(nutrition.CarbohydratesGrams);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(28)]
    public void SignedZerosAreAcceptedAndNormalized(int scale)
    {
        decimal signedZero = new(0, 0, 0, true, (byte)scale);
        MacroGoalAllocation allocation = MacroGoalAllocation.FromGrams(signedZero, signedZero, signedZero);

        AssertShares(allocation, 30m, 30m, 40m);
        foreach (NutritionMacro macro in Enum.GetValues<NutritionMacro>())
        {
            AssertPositiveZero(Share(allocation.WithShare(macro, signedZero), macro));
        }

        NutritionValues nutrition = allocation.ToNutrition(signedZero);
        AssertPositiveZero(nutrition.Calories);
        AssertPositiveZero(nutrition.ProteinGrams);
        AssertPositiveZero(nutrition.FatGrams);
        AssertPositiveZero(nutrition.CarbohydratesGrams);
    }

    [Theory]
    [InlineData(-1, 0, 0, "proteinGrams")]
    [InlineData(0, -1, 0, "fatGrams")]
    [InlineData(0, 0, -1, "carbohydratesGrams")]
    public void NegativeGramsAreRejected(int protein, int fat, int carbohydrates, string parameter)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            MacroGoalAllocation.FromGrams(protein, fat, carbohydrates));

        Assert.Equal(parameter, error.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void SharesOutsideZeroToOneHundredAreRejected(int percentage)
    {
        MacroGoalAllocation allocation = MacroGoalAllocation.FromGrams(75m, 20m, 130m);

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            allocation.WithShare(NutritionMacro.Protein, percentage));

        Assert.Equal("percent", error.ParamName);
        AssertShares(allocation, 30m, 18m, 52m);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void UnknownMacroIsRejected(int macro)
    {
        MacroGoalAllocation allocation = MacroGoalAllocation.FromGrams(75m, 20m, 130m);

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            allocation.WithShare((NutritionMacro)macro, 50m));

        Assert.Equal("macro", error.ParamName);
    }

    [Fact]
    public void NegativeCalorieTargetsAreRejected()
    {
        MacroGoalAllocation allocation = MacroGoalAllocation.FromGrams(75m, 20m, 130m);

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => allocation.ToNutrition(-1m));

        Assert.Equal("calories", error.ParamName);
    }

    [Fact]
    public void HugeGramInputsKeepTheirEnergyRatiosWithoutOverflow()
    {
        MacroGoalAllocation allocation = MacroGoalAllocation.FromGrams(decimal.MaxValue, decimal.MaxValue, decimal.MaxValue);

        AssertNear(23.529411764705882352941176471m, allocation.ProteinPercent);
        AssertNear(52.941176470588235294117647059m, allocation.FatPercent);
        AssertNear(23.529411764705882352941176471m, allocation.CarbohydratesPercent);
        AssertComplete(allocation);
    }

    [Fact]
    public void SmallestGramInputsRetainTheirEnergyRatios()
    {
        decimal smallest = 0.0000000000000000000000000001m;

        MacroGoalAllocation allocation = MacroGoalAllocation.FromGrams(smallest, smallest, smallest);

        AssertNear(23.529411764705882352941176471m, allocation.ProteinPercent);
        AssertNear(52.941176470588235294117647059m, allocation.FatPercent);
        AssertNear(23.529411764705882352941176471m, allocation.CarbohydratesPercent);
        AssertComplete(allocation);
    }

    [Fact]
    public void HugeCalorieTargetDoesNotOverflowDuringConversion()
    {
        NutritionValues nutrition = MacroGoalAllocation.FromGrams(1m, 0m, 0m).ToNutrition(decimal.MaxValue);

        Assert.Equal(decimal.MaxValue, nutrition.Calories);
        Assert.Equal(decimal.MaxValue / 4m, nutrition.ProteinGrams);
        AssertPositiveZero(nutrition.FatGrams);
        AssertPositiveZero(nutrition.CarbohydratesGrams);
    }

    private static decimal Share(MacroGoalAllocation allocation, NutritionMacro macro) => macro switch
    {
        NutritionMacro.Protein => allocation.ProteinPercent,
        NutritionMacro.Fat => allocation.FatPercent,
        NutritionMacro.Carbohydrates => allocation.CarbohydratesPercent,
        _ => throw new ArgumentOutOfRangeException(nameof(macro))
    };

    private static void AssertShares(MacroGoalAllocation allocation, decimal protein, decimal fat, decimal carbohydrates)
    {
        Assert.Equal(protein, allocation.ProteinPercent);
        Assert.Equal(fat, allocation.FatPercent);
        Assert.Equal(carbohydrates, allocation.CarbohydratesPercent);
        AssertComplete(allocation);
    }

    private static void AssertComplete(MacroGoalAllocation allocation)
    {
        Assert.InRange(allocation.ProteinPercent, 0m, 100m);
        Assert.InRange(allocation.FatPercent, 0m, 100m);
        Assert.InRange(allocation.CarbohydratesPercent, 0m, 100m);
        Assert.Equal(100m, allocation.ProteinPercent + allocation.FatPercent + allocation.CarbohydratesPercent);
    }

    private static void AssertEnergyMatchesCalories(NutritionValues nutrition) => AssertNear(
        nutrition.Calories,
        nutrition.ProteinGrams * 4m + nutrition.FatGrams * 9m + nutrition.CarbohydratesGrams * 4m);

    private static void AssertNear(decimal expected, decimal actual) =>
        Assert.InRange(Math.Abs(expected - actual), 0m, Tolerance);

    private static void AssertPositiveZero(decimal value)
    {
        Assert.Equal(0m, value);
        Assert.False(decimal.IsNegative(value));
    }
}
