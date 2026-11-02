using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Profiles;

namespace NutriFlow.Ui.Tests;

public sealed class ProfileChoicesTests
{
    private static readonly LocalProfile Anna = new(Guid.NewGuid(), "Анна");
    private static readonly LocalProfile Odd = new(Guid.NewGuid(), "Создать профиль");

    [Fact]
    public void ButtonsAreNumberedWithTheCurrentProfileMarked()
    {
        ProfileChoices choices = new([Anna, Odd], Anna.Id);

        Assert.Equal(["1. Анна ✓", "2. Создать профиль", "Создать профиль"], choices.Buttons);
    }

    [Fact]
    public void ChoiceFindsItsProfileAndNotTheCreateButton()
    {
        ProfileChoices choices = new([Anna, Odd], Anna.Id);

        Assert.Same(Odd, choices.Find("2. Создать профиль"));
        Assert.Same(Anna, choices.Find("1. Анна ✓"));
        Assert.Null(choices.Find(ProfileChoices.Create));
        Assert.Null(choices.Find(null));
    }
}
