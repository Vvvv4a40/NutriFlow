using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Profiles;
using NutriFlow.Ui.Themes;

namespace NutriFlow.Ui.Tests;

public sealed class ProfileStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nutriflow-ui-" + Guid.NewGuid().ToString("N"));
    private readonly FakeJsRuntime _js = new();
    private readonly FakeAppHost _host = new();
    private readonly FakeProfileSettings _settings = new();

    [Fact]
    public async Task WithoutProfilesTheWelcomeScreenKeepsTheDefaultTheme()
    {
        ProfileState state = Create();

        await state.LoadAsync();

        Assert.Null(state.Current);
        Assert.Equal(["terracotta"], _js.AppliedThemes);
    }

    [Fact]
    public async Task FirstProfileIsSavedAtOnceAndAsksToKeepTheStorage()
    {
        ProfileState state = Create();
        int changes = 0;
        state.Changed += () => changes++;

        await state.CreateAsync("Анна");
        await state.CreateAsync("Борис");

        Assert.Equal("Борис", state.Current!.Name);
        Assert.Equal(2, _host.SavedFiles);
        Assert.Equal(1, _host.PersistenceRequests);
        Assert.Equal(2, changes);
        // a fresh store reads what the first one wrote: the new profile is selected
        Assert.Equal("Борис", (await new LocalProfileStore(_root).GetSelectedAsync())!.Name);
    }

    [Fact]
    public async Task ChoosingAProfileSavesTheSelectionAndAppliesItsTheme()
    {
        ProfileState state = Create();
        await state.CreateAsync("Анна");
        Guid anna = state.Current!.Id;
        await state.SaveThemeAsync("taiga");
        await state.CreateAsync("Борис");
        int saved = _host.SavedFiles;

        await state.SelectAsync(anna);
        await state.SelectAsync(anna);

        Assert.Equal(anna, state.Current!.Id);
        Assert.Equal(saved + 1, _host.SavedFiles);
        Assert.Equal("taiga", _settings.Themes[anna]);
        Assert.Equal(["terracotta", "taiga", "terracotta", "taiga"], _js.AppliedThemes);
    }

    [Fact]
    public async Task LoadingRestoresTheSelectedProfileWithItsTheme()
    {
        ProfileState first = Create();
        await first.CreateAsync("Анна");
        await first.SaveThemeAsync("dark");
        _js.AppliedThemes.Clear();

        ProfileState second = Create();
        await second.LoadAsync();

        Assert.Equal("Анна", second.Current!.Name);
        Assert.Equal(["dark"], _js.AppliedThemes);
    }

    [Fact]
    public async Task RejectedNameLeavesNothingToSave()
    {
        ProfileState state = Create();
        await state.CreateAsync("Анна");

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() => state.CreateAsync("анна"));

        Assert.StartsWith("Профиль с таким названием уже существует.", error.Message);
        Assert.Equal(1, _host.SavedFiles);
        Assert.Equal("Анна", state.Current!.Name);
    }

    private ProfileState Create() => new(new LocalProfileStore(_root), _settings, _host, new ThemeManager(_js));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
