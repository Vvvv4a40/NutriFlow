namespace NutriFlow.Mobile.Core.Tests;

public sealed class MealInputTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void BlankExistingTextUsesTrimmedTranscript(string? currentText)
    {
        Assert.Equal("Добавил молоко", MealInputText.AppendTranscript(currentText, " \tДобавил молоко\r\n "));
    }

    [Theory]
    [InlineData("Омлет", "Омлет\nДобавил молоко")]
    [InlineData("  Омлет  ", "  Омлет  \nДобавил молоко")]
    [InlineData("Омлет\n", "Омлет\n\nДобавил молоко")]
    [InlineData("Омлет\r\n", "Омлет\r\n\nДобавил молоко")]
    public void ExistingTextIsPreservedWithOneAdditionalLineFeed(string currentText, string expected)
    {
        Assert.Equal(expected, MealInputText.AppendTranscript(currentText, " Добавил молоко "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void BlankTranscriptIsRejected(string? transcript)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => MealInputText.AppendTranscript("Омлет", transcript));
        Assert.Equal("Введите или распознайте текст описания.", error.Message);
    }

    [Theory]
    [InlineData(0, 4000)]
    [InlineData(3998, 1)]
    [InlineData(2000, 1999)]
    public void ExactlyFourThousandCharactersAreAccepted(int currentLength, int transcriptLength)
    {
        string currentText = new('a', currentLength);
        string transcript = new('b', transcriptLength);

        string result = MealInputText.AppendTranscript(currentText, transcript);

        Assert.Equal(4000, result.Length);
        Assert.Equal(currentLength == 0 ? transcript : currentText + "\n" + transcript, result);
    }

    [Theory]
    [InlineData(0, 4001, 1)]
    [InlineData(3999, 1, 1)]
    [InlineData(4000, 1, 2)]
    [InlineData(2000, 2000, 1)]
    [InlineData(4001, 1, 3)]
    public void OverflowIsRejectedIncludingTheSeparator(int currentLength, int transcriptLength, int excess)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            MealInputText.AppendTranscript(new string('a', currentLength), new string('b', transcriptLength)));

        Assert.Contains("4000", error.Message);
        Assert.Contains($"Превышение: {excess}.", error.Message);
    }

    [Fact]
    public void TranscriptIsTrimmedBeforeCheckingTheLimit()
    {
        string currentText = new('a', 3998);

        Assert.Equal(currentText + "\nb", MealInputText.AppendTranscript(currentText, " \r\n b \t "));
    }

    [Fact]
    public void WhitespaceOnlyExistingTextDoesNotConsumeTheLimit()
    {
        string transcript = new('b', 4000);

        Assert.Equal(transcript, MealInputText.AppendTranscript(new string(' ', 4000), transcript));
    }

    [Fact]
    public void UnicodeTextUsesTheSameUtf16LengthAsTheInputContract()
    {
        string currentText = string.Concat(Enumerable.Repeat("🍎", 1999));

        Assert.Equal(currentText + "\nb", MealInputText.AppendTranscript(currentText, "b"));
        Assert.Throws<ArgumentException>(() => MealInputText.AppendTranscript(currentText, "bb"));
    }

    [Fact]
    public void RejectedAppendPreservesExistingTextAndAllowsCorrection()
    {
        string original = new('a', 3998);
        string draft = original;
        string transcript = "bb";

        Assert.Throws<ArgumentException>(() => draft = MealInputText.AppendTranscript(draft, transcript));

        Assert.Equal(original, draft);
        Assert.Equal("bb", transcript);
        draft = MealInputText.AppendTranscript(draft, "b");
        Assert.Equal(original + "\nb", draft);
        Assert.Equal(4000, draft.Length);
    }
}
