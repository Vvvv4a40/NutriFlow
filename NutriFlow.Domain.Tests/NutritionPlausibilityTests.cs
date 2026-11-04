namespace NutriFlow.Domain.Tests;

public sealed class NutritionPlausibilityTests
{
    [Theory]
    [InlineData(110, 4, 1, 21, true)]
    [InlineData(0, 0, 0, 0, true)]
    // beer: the energy of alcohol is not among the macros
    [InlineData(43, 0.5, 0, 3.6, true)]
    [InlineData(899, 0, 99.9, 0, true)]
    [InlineData(10, 40, 30, 20, false)]
    [InlineData(950, 0, 100, 0, false)]
    [InlineData(300, 60, 30, 20, false)]
    [InlineData(-1, 0, 0, 0, false)]
    public void ImpossibleNumbersAreRecognised(double calories, double protein, double fat, double carbohydrates, bool possible)
    {
        Assert.Equal(possible, NutritionPlausibility.IsPossible((decimal)calories, (decimal)protein, (decimal)fat, (decimal)carbohydrates));
    }

    [Theory]
    [InlineData(378, 12.5, 3.3, 72, true)]
    [InlineData(62, 4.1, 1.5, 6.8, true)]
    // a lost decimal comma: 12,5 read as 125
    [InlineData(378, 125, 3.3, 72, false)]
    [InlineData(1582, 12.5, 3.3, 72, false)]
    public void PrintedEnergyIsComparedWithPrintedMacros(double calories, double protein, double fat, double carbohydrates, bool match)
    {
        Assert.Equal(match, NutritionPlausibility.MacrosMatchCalories((decimal)calories, (decimal)protein, (decimal)fat, (decimal)carbohydrates));
    }
}
