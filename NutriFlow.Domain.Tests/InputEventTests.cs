using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class InputEventTests
{
    [Fact]
    public void Constructor_StoresText()
    {
        InputEvent inputEvent = new InputEvent("Added 200 g of product A");

        Assert.Equal("Added 200 g of product A", inputEvent.Text);
    }

    [Fact]
    public void Constructor_WithNullText_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new InputEvent(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithBlankText_ThrowsArgumentException(string text)
    {
        Assert.Throws<ArgumentException>(() => new InputEvent(text));
    }
}
