namespace NutriFlow.Mobile.Core.Tests;

public sealed class NutritionArcLayoutTests
{
    private const decimal AngleTolerance = 0.000000000000000000000001m;

    [Fact]
    public void UnequalGramAmountsProduceUnequalSectorsInMacroOrder()
    {
        IReadOnlyList<NutritionArcSegment> segments = NutritionArcLayout.Create(75m, 25m, 50m);

        Assert.Equal(3, segments.Count);
        AssertSegment(segments[0], NutritionMacro.Protein, 180m, 90m);
        AssertSegment(segments[1], NutritionMacro.Fat, 90m, 30m);
        AssertSegment(segments[2], NutritionMacro.Carbohydrates, 60m, 60m);
        AssertSemicircle(segments);
    }

    [Fact]
    public void GoalAndConsumedRingsUseTheirOwnGramComposition()
    {
        IReadOnlyList<NutritionArcSegment> goal = NutritionArcLayout.Create(100m, 50m, 150m);
        IReadOnlyList<NutritionArcSegment> consumed = NutritionArcLayout.Create(75m, 25m, 50m);

        AssertAngle(60m, goal[0].SweepAngle);
        AssertAngle(90m, consumed[0].SweepAngle);
        AssertAngle(90m, goal[2].SweepAngle);
        AssertAngle(60m, consumed[2].SweepAngle);
        AssertSemicircle(goal);
        AssertSemicircle(consumed);
    }

    [Fact]
    public void ConsumptionAboveGoalIsNotClippedOrUsedAsGoalProgress()
    {
        IReadOnlyList<NutritionArcSegment> goal = NutritionArcLayout.Create(10m, 10m, 10m);
        IReadOnlyList<NutritionArcSegment> consumed = NutritionArcLayout.Create(80m, 10m, 10m);

        AssertAngle(60m, goal[0].SweepAngle);
        AssertAngle(144m, consumed[0].SweepAngle);
        AssertSemicircle(consumed);
    }

    [Fact]
    public void IncreasingAllGramAmountsKeepsTheCompositionUnchanged()
    {
        IReadOnlyList<NutritionArcSegment> first = NutritionArcLayout.Create(75m, 25m, 50m);
        IReadOnlyList<NutritionArcSegment> doubled = NutritionArcLayout.Create(150m, 50m, 100m);

        Assert.Equal(first, doubled);
    }

    [Fact]
    public void ZeroGramMacrosAreOmittedWithoutLeavingAngularSpace()
    {
        IReadOnlyList<NutritionArcSegment> segments = NutritionArcLayout.Create(75m, 0m, 25m);

        Assert.Equal(2, segments.Count);
        AssertSegment(segments[0], NutritionMacro.Protein, 180m, 135m);
        AssertSegment(segments[1], NutritionMacro.Carbohydrates, 45m, 45m);
        AssertSemicircle(segments);
    }

    [Fact]
    public void AllZeroGramsHaveNoColoredSegments()
    {
        Assert.Empty(NutritionArcLayout.Create(0m, 0m, 0m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(28)]
    public void SignedZeroGramsAreTreatedAsMathematicalZero(int scale)
    {
        decimal signedZero = new(0, 0, 0, true, (byte)scale);

        Assert.True(decimal.IsNegative(signedZero));
        Assert.Empty(NutritionArcLayout.Create(signedZero, signedZero, signedZero));
        NutritionArcSegment segment = Assert.Single(NutritionArcLayout.Create(signedZero, 25m, signedZero));
        AssertSegment(segment, NutritionMacro.Fat, 180m, 180m);
    }

    [Theory]
    [InlineData(1, 0, 0, NutritionMacro.Protein)]
    [InlineData(0, 1, 0, NutritionMacro.Fat)]
    [InlineData(0, 0, 1, NutritionMacro.Carbohydrates)]
    public void SingleNonzeroMacroOccupiesTheWholeSemicircle(
        int protein, int fat, int carbohydrates, NutritionMacro macro)
    {
        NutritionArcSegment segment = Assert.Single(NutritionArcLayout.Create(protein, fat, carbohydrates));

        AssertSegment(segment, macro, 180m, 180m);
    }

    [Fact]
    public void SmallestDecimalGramAmountsRetainTheirProportions()
    {
        decimal smallest = 0.0000000000000000000000000001m;

        IReadOnlyList<NutritionArcSegment> segments = NutritionArcLayout.Create(smallest, smallest * 2, smallest * 3);

        AssertSegment(segments[0], NutritionMacro.Protein, 180m, 30m);
        AssertSegment(segments[1], NutritionMacro.Fat, 150m, 60m);
        AssertSegment(segments[2], NutritionMacro.Carbohydrates, 90m, 90m);
        AssertSemicircle(segments);
    }

    [Fact]
    public void HugeValuesDoNotOverflowWhenTheirSumExceedsDecimalRange()
    {
        IReadOnlyList<NutritionArcSegment> segments = NutritionArcLayout.Create(decimal.MaxValue, decimal.MaxValue, decimal.MaxValue);

        Assert.Equal(3, segments.Count);
        foreach (NutritionArcSegment segment in segments)
        {
            AssertAngle(60m, segment.SweepAngle);
        }

        AssertSemicircle(segments);
    }

    [Fact]
    public void HugeUnequalValuesPreserveTheirRatiosWithinDecimalAnglePrecision()
    {
        decimal largest = decimal.MaxValue;

        IReadOnlyList<NutritionArcSegment> segments = NutritionArcLayout.Create(largest, largest / 2, largest / 3);

        AssertAngle(180m * (6m / 11m), segments[0].SweepAngle);
        AssertAngle(180m * (3m / 11m), segments[1].SweepAngle);
        AssertAngle(180m * (2m / 11m), segments[2].SweepAngle);
        AssertSemicircle(segments);
    }

    [Fact]
    public void UnrepresentableRatiosAreNotInflatedIntoVisibleSectors()
    {
        IReadOnlyList<NutritionArcSegment> segments = NutritionArcLayout.Create(
            decimal.MaxValue, 0.0000000000000000000000000001m, 0m);

        NutritionArcSegment segment = Assert.Single(segments);
        AssertSegment(segment, NutritionMacro.Protein, 180m, 180m);
    }

    [Theory]
    [InlineData(-1, 0, 0, "proteinGrams")]
    [InlineData(0, -1, 0, "fatGrams")]
    [InlineData(0, 0, -1, "carbohydratesGrams")]
    public void NegativeGramsAreRejected(int protein, int fat, int carbohydrates, string parameter)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            NutritionArcLayout.Create(protein, fat, carbohydrates));

        Assert.Equal(parameter, error.ParamName);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 7, 13)]
    [InlineData(119, 67, 233)]
    [InlineData(0, 2, 3)]
    public void RecurringRatiosRemainContiguousAndCompleteWithinDecimalAnglePrecision(
        int protein, int fat, int carbohydrates)
    {
        AssertSemicircle(NutritionArcLayout.Create(protein, fat, carbohydrates));
    }

    private static void AssertSegment(NutritionArcSegment segment, NutritionMacro macro, decimal start, decimal sweep)
    {
        Assert.Equal(macro, segment.Macro);
        AssertAngle(start, segment.StartAngle);
        AssertAngle(sweep, segment.SweepAngle);
    }

    private static void AssertSemicircle(IReadOnlyList<NutritionArcSegment> segments)
    {
        decimal nextStart = 180m;
        foreach (NutritionArcSegment segment in segments)
        {
            Assert.InRange(segment.SweepAngle, 0.0000000000000000000000000001m, 180m);
            AssertAngle(nextStart, segment.StartAngle);
            nextStart -= segment.SweepAngle;
        }

        AssertAngle(0m, nextStart);
        AssertAngle(180m, segments.Sum(segment => segment.SweepAngle));
    }

    private static void AssertAngle(decimal expected, decimal actual)
    {
        Assert.InRange(Math.Abs(expected - actual), 0m, AngleTolerance);
    }
}
