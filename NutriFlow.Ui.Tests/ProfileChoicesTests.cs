using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Dialogs;
using NutriFlow.Ui.Profiles;

namespace NutriFlow.Ui.Tests;

public sealed class ProfileChoicesTests
{
    private static readonly LocalProfile Anna = new(Guid.NewGuid(), "Анна");
    private static readonly LocalProfile Odd = new(Guid.NewGuid(), "Создать профиль");

    [Fact]
    public void ProfilesKeepTheirNamesAndTheCurrentOneIsMarked()
    {
        ProfileChoices choices = new([Anna, Odd], Anna.Id);

        Assert.Equal(["Анна", "Создать профиль", "Создать профиль", "Восстановить из копии"], choices.Buttons.Select(button => button.Text));
        Assert.Equal([true, false, false, false], choices.Buttons.Select(button => button.Current));
    }

    [Fact]
    public void ChoiceFindsItsProfileAndNotTheCreateButton()
    {
        ProfileChoices choices = new([Anna, Odd], Anna.Id);
        DialogButton oddButton = choices.Buttons[1];

        Assert.Same(Odd, choices.Find(oddButton.Key));
        Assert.Same(Anna, choices.Find(choices.Buttons[0].Key));
        Assert.Null(choices.Find(ProfileChoices.Create));
        Assert.Null(choices.Find(ProfileChoices.Restore));
        Assert.Null(choices.Find(null));
    }
}
