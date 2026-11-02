using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Hosting;
using NutriFlow.Ui.Themes;

namespace NutriFlow.Ui.Profiles;

// the profile the screens work with; creating or choosing one rewrites profiles.json, so the host saves the files at once
public sealed class ProfileState(LocalProfileStore profiles, IProfileSettings settings, IAppHost host, ThemeManager themes)
{
    public LocalProfile? Current { get; private set; }

    public event Action? Changed;

    public async Task LoadAsync()
    {
        Current = await profiles.GetSelectedAsync();
        await ApplyThemeAsync();
        Changed?.Invoke();
    }

    public Task<IReadOnlyList<LocalProfile>> ListAsync() => profiles.ListAsync();

    public async Task CreateAsync(string name)
    {
        bool first = (await profiles.ListAsync()).Count == 0;
        LocalProfile created = await profiles.CreateAsync(name);
        await profiles.SelectAsync(created.Id);
        await host.SaveProfileFilesAsync();
        Current = created;
        await ApplyThemeAsync();
        // asked once: firefox shows a prompt for it, chrome and safari decide silently
        if (first)
        {
            await host.RequestPersistentStorageAsync();
        }

        Changed?.Invoke();
    }

    public async Task SelectAsync(Guid profileId)
    {
        if (Current?.Id == profileId)
        {
            return;
        }

        LocalProfile selected = await profiles.SelectAsync(profileId);
        await host.SaveProfileFilesAsync();
        Current = selected;
        await ApplyThemeAsync();
        Changed?.Invoke();
    }

    public async Task SaveThemeAsync(string themeId)
    {
        LocalProfile profile = Current ?? throw new InvalidOperationException("Сначала создайте или выберите профиль.");
        await settings.SaveThemeAsync(profile.Id, themeId);
        await themes.ApplyAsync(themeId);
    }

    // the welcome screen keeps the default theme, every profile has its own
    private async Task ApplyThemeAsync() =>
        await themes.ApplyAsync(Current is null ? ThemePalette.DefaultId : await settings.GetThemeAsync(Current.Id));
}
