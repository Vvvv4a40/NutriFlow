using System.Text.Json;
using NutriFlow.Api.Contracts;
using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Capture;
using NutriFlow.Ui.Components;
using NutriFlow.Ui.Pages;
using NutriFlow.Ui.Profiles;
using NutriFlow.Ui.Themes;

namespace NutriFlow.Ui.Tests;

public sealed class ProfileBackupsTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nutriflow-ui-" + Guid.NewGuid().ToString("N"));
    private readonly FakeJsRuntime _js = new();
    private readonly FakeAppHost _host = new();
    private readonly FakeProfileSettings _settings = new();
    private readonly LocalProfileStore _profiles;
    private readonly LocalMealClient _meals;
    private readonly ProfileState _state;
    private readonly CaptureState _capture;

    public ProfileBackupsTests()
    {
        _profiles = new LocalProfileStore(_root);
        _meals = new LocalMealClient(_profiles);
        _state = new ProfileState(_profiles, _settings, _host, new ThemeManager(_js));
        _capture = new CaptureState(_meals, new LocalInputDraftStore(_profiles), _settings, _host, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task CopyComesBackAsANewCurrentProfileWithItsDiaryAndOpenCalculation()
    {
        await _state.CreateAsync("Анна");
        Guid anna = _state.Current!.Id;
        ProductResponse oats = new("Овсянка", 350m, 12m, 6m, 60m, "ManualInput", "Exact", "Ручной ввод", null, null);
        await _meals.AddProductAsync(anna, oats);
        MealSessionResponse eaten = await _meals.CreateManualMealAsync(anna, oats, 60m, Today);
        await _meals.ConfirmAsync(anna, eaten.Id, eaten.PreviewToken);
        MealSessionResponse open = await _meals.CreateManualMealAsync(anna, oats, 40m, Today);
        await _settings.SaveSessionIdAsync(anna, open.Id);
        await _capture.LoadAsync(anna);
        _capture.SetText("ужин без веса");
        ProfileBackups backups = Create();

        byte[] archive = await backups.CreateAsync(anna);
        int saved = _host.SavedFiles;
        LocalProfile restored = await backups.RestoreAsync(new MemoryStream(archive), "Анна, копия");

        Assert.Equal("PK"u8.ToArray(), archive[..2]);
        Assert.Equal(restored, _state.Current);
        Assert.NotEqual(anna, restored.Id);
        Assert.Equal(2, (await _profiles.ListAsync()).Count);
        Assert.True(_host.SavedFiles > saved);
        // the first profile asked for persistent storage, a restore next to it does not ask again
        Assert.Equal(1, _host.PersistenceRequests);
        Assert.Equal(open.Id, _settings.Sessions[restored.Id]);
        Assert.Equal(JsonSerializer.Serialize((await _meals.GetDailyProgressAsync(anna, Today)).Entries),
            JsonSerializer.Serialize((await _meals.GetDailyProgressAsync(restored.Id, Today)).Entries));
        Assert.Equal("ужин без веса", (await new LocalInputDraftStore(_profiles).ReadAsync(restored.Id))!.Text);
    }

    [Fact]
    public async Task RestoreOnTheWelcomeScreenIsTheFirstProfile()
    {
        await _state.CreateAsync("Анна");
        byte[] archive = await Create().CreateAsync(_state.Current!.Id);
        string other = Path.Combine(_root, "other-device");
        LocalProfileStore profiles = new(other);
        FakeAppHost host = new();
        ProfileState state = new(profiles, _settings, host, new ThemeManager(_js));
        CaptureState capture = new(new LocalMealClient(profiles), new LocalInputDraftStore(profiles), _settings, host, TimeSpan.FromMilliseconds(1));

        LocalProfile restored = await new ProfileBackups(new LocalProfileBackup(profiles), state, _settings, capture)
            .RestoreAsync(new MemoryStream(archive), "Анна");

        Assert.Equal(restored, state.Current);
        Assert.Equal(1, host.PersistenceRequests);
    }

    [Fact]
    public async Task DamagedFileGivesTheTextOfThePhoneAndAddsNoProfile()
    {
        await _state.CreateAsync("Анна");
        LocalProfile current = _state.Current!;
        int changes = 0;
        _state.Changed += () => changes++;

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Create().RestoreAsync(new MemoryStream(new byte[2048]), "Повреждённая"));

        Assert.Equal(BackupText.RestoreFailed, error.Message);
        Assert.Equal([current], await _profiles.ListAsync());
        Assert.Equal(current, _state.Current);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void RestoredProfileMentionsTheOthersOnlyWhenThereAreAny()
    {
        Assert.Equal("Создан профиль «Анна». Ключ Groq в копию не входит — добавьте его в настройках, чтобы разбирать описания.",
            BackupText.Restored("Анна", hasOtherProfiles: false));
        Assert.Contains("Остальные профили не изменились.", BackupText.Restored("Анна", hasOtherProfiles: true));
    }

    [Fact]
    public void FileNameAndSizeAreReadable()
    {
        Assert.Equal("NutriFlow-20261103-091502.zip", ProfileBackups.FileName(new DateTime(2026, 11, 3, 9, 15, 2)));
        Assert.Equal("512 Б", FileField.SizeText(512));
        Assert.Equal("9,8 КБ", FileField.SizeText(10_023));
        Assert.Equal("1,5 МБ", FileField.SizeText(1_572_864));
    }

    private ProfileBackups Create() => new(new LocalProfileBackup(_profiles), _state, _settings, _capture);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
