using NutriFlow.Api.Contracts;
using NutriFlow.Ui.Formatting;

namespace NutriFlow.Ui.Tests;

public sealed class NutritionTextTests
{
    [Fact]
    public void NumbersUseRussianSeparatorsAndRounding()
    {
        Assert.Equal("1 234", NutritionText.Kilocalories(1234.4m));
        Assert.Equal("12,35", NutritionText.Amount(12.345m));
        Assert.Equal("12", NutritionText.Amount(12m));
        Assert.Equal("13", NutritionText.MacroAmount(12.5m));
    }

    [Theory]
    [InlineData("12,5", false, true, 12.5)]
    [InlineData(" 150.25 ", true, true, 150.25)]
    [InlineData("0", false, true, 0)]
    [InlineData("0", true, false, 0)]
    [InlineData("-3", false, false, 0)]
    [InlineData("", false, false, 0)]
    [InlineData("1 200", false, false, 0)]
    [InlineData("сто", true, false, 0)]
    public void FieldNumbersTakeACommaOrADot(string text, bool positive, bool valid, double expected)
    {
        Assert.Equal(valid, NutritionText.TryParseNumber(text, positive, out decimal number));
        if (valid)
        {
            Assert.Equal((decimal)expected, number);
        }
    }

    [Fact]
    public void ShortAndSpokenNutritionMatchTheAndroidTexts()
    {
        NutritionResponse value = new(612.5m, 41.2m, 18.6m, 70.4m);

        Assert.Equal("612,5 ккал · Б 41 · Ж 19 · У 70 г", NutritionText.Nutrition(value));
        Assert.Equal("612,5 килокалорий, белки 41, жиры 19, углеводы 70 граммов", NutritionText.SpokenNutrition(value));
        Assert.Equal("белки 41, жиры 19, углеводы 70 граммов", NutritionText.SpokenMacros(41.2m, 18.6m, 70.4m));
    }

    [Theory]
    [InlineData("Exact", "точные данные")]
    [InlineData("Verified", "проверенные данные")]
    [InlineData("Estimated", "оценочные данные")]
    [InlineData(null, "качество не установлено")]
    public void QualityIsNamedInWords(string? quality, string expected) =>
        Assert.Equal(expected, NutritionText.Quality(quality));

    [Theory]
    [InlineData("150", 150)]
    [InlineData(" 12,5 ", 12.5)]
    [InlineData("0.25", 0.25)]
    public void NumbersAcceptCommaAndDot(string text, double expected) =>
        Assert.Equal((decimal)expected, NutritionText.ParseNumber(text, "Вес"));

    [Theory]
    [InlineData(null, false, "Вес: введите неотрицательное число.")]
    [InlineData("-1", false, "Вес: введите неотрицательное число.")]
    [InlineData("абв", false, "Вес: введите неотрицательное число.")]
    [InlineData("0", true, "Вес: введите положительное число.")]
    public void InvalidNumbersExplainWhatToEnter(string? text, bool positive, string message)
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(() => NutritionText.ParseNumber(text, "Вес", positive));
        Assert.Equal(message, failure.Message);
    }

    [Fact]
    public void RequiredNameIsTrimmedAndBlankIsRejected()
    {
        Assert.Equal("Гречка", NutritionText.RequireName("  Гречка ", "Название"));
        Assert.Equal("Заполните поле «Название».",
            Assert.Throws<ArgumentException>(() => NutritionText.RequireName("  ", "Название")).Message);
    }
}
