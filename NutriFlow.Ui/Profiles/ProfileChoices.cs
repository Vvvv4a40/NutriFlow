using NutriFlow.Mobile.Core;

namespace NutriFlow.Ui.Profiles;

// the profile sheet: numbered names keep every button distinct, even from a profile called «Создать профиль»
// or «Восстановить из копии»
public sealed class ProfileChoices
{
    public const string Create = "Создать профиль";
    public const string Restore = "Восстановить из копии";
    private readonly IReadOnlyList<LocalProfile> _profiles;
    private readonly string[] _names;

    public ProfileChoices(IReadOnlyList<LocalProfile> profiles, Guid? currentId)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = profiles;
        _names = profiles.Select((profile, index) => profile.Id == currentId
            ? $"{index + 1}. {profile.Name} ✓"
            : $"{index + 1}. {profile.Name}").ToArray();
        Buttons = [.. _names, Create, Restore];
    }

    public string[] Buttons { get; }

    public LocalProfile? Find(string? choice)
    {
        int index = Array.IndexOf(_names, choice);
        return index < 0 ? null : _profiles[index];
    }
}
