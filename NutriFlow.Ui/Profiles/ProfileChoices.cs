using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Dialogs;

namespace NutriFlow.Ui.Profiles;

// the profile sheet: a profile answers with its id, so one called «Создать профиль» cannot pass for that button
public sealed class ProfileChoices
{
    public const string Create = "Создать профиль";
    public const string Restore = "Восстановить из копии";
    private readonly IReadOnlyList<LocalProfile> _profiles;

    public ProfileChoices(IReadOnlyList<LocalProfile> profiles, Guid? currentId)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = profiles;
        Buttons =
        [
            .. profiles.Select(profile => new DialogButton(profile.Name, DialogButtonRole.Default, Key(profile), profile.Id == currentId)),
            new DialogButton(Create, DialogButtonRole.Primary),
            new DialogButton(Restore, DialogButtonRole.Primary)
        ];
    }

    public IReadOnlyList<DialogButton> Buttons { get; }

    public LocalProfile? Find(string? choice) => _profiles.FirstOrDefault(profile => Key(profile) == choice);

    private static string Key(LocalProfile profile) => profile.Id.ToString("N");
}
