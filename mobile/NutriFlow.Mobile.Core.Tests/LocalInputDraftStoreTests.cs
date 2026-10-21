using System.Text.Json;
using NutriFlow.Domain;

namespace NutriFlow.Mobile.Core.Tests;

public sealed class LocalInputDraftStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nutriflow-input-draft-tests", Guid.NewGuid().ToString("N"));
    private readonly DateOnly _date = new(2026, 10, 19);

    [Theory]
    [InlineData("Добавил масло", "Добавил масло", true)]
    [InlineData("  Добавил масло\n", "Добавил масло", true)]
    [InlineData("Добавил масло", "Добавил воду", false)]
    [InlineData(" \n", "", false)]
    public void SubmittedTextIsRecognizedOnlyAgainstTheLastMessage(string text, string lastMessage, bool expected)
    {
        LocalInputDraft draft = new(text, _date, MealSessionPurpose.Diary);
        Assert.Equal(expected, draft.MatchesLastSubmittedMessage(new[] { text, lastMessage }));
        Assert.False(draft.MatchesLastSubmittedMessage(Array.Empty<string>()));
    }

    [Theory]
    [InlineData(MealSessionPurpose.Diary)]
    [InlineData(MealSessionPurpose.CreateDish)]
    public async Task UnsubmittedTextAndItsContextSurviveRestart(MealSessionPurpose purpose)
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Первый");
        LocalInputDraft draft = new("  Добавил масло\n🍎  ", _date, purpose, Guid.NewGuid());

        await new LocalInputDraftStore(profiles).SaveAsync(profile.Id, draft);

        LocalInputDraftStore restarted = new(new LocalProfileStore(_root));
        Assert.Equal(draft, await restarted.ReadAsync(profile.Id));
        Assert.False(File.Exists(Path.Combine(ProfileDirectory(profile.Id), "nutriflow.db")));
        Assert.Single(Directory.GetFiles(ProfileDirectory(profile.Id)));
    }

    [Fact]
    public async Task DraftsAreIsolatedBetweenProfilesAndClearingDoesNotAffectOthers()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile first = await profiles.CreateAsync("Первый");
        LocalProfile second = await profiles.CreateAsync("Второй");
        LocalInputDraftStore drafts = new(profiles);
        LocalInputDraft firstDraft = new("Первый ввод", _date, MealSessionPurpose.Diary);
        LocalInputDraft secondDraft = new("Второй ввод", _date.AddDays(-1), MealSessionPurpose.CreateDish);

        Assert.Null(await drafts.ReadAsync(first.Id));
        await drafts.SaveAsync(first.Id, firstDraft);
        await drafts.SaveAsync(second.Id, secondDraft);
        await drafts.ClearAsync(first.Id);

        Assert.Null(await drafts.ReadAsync(first.Id));
        Assert.Equal(secondDraft, await drafts.ReadAsync(second.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    public async Task BlankInputClearsThePreviouslySavedDraft(string text)
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Первый");
        LocalInputDraftStore drafts = new(profiles);
        await drafts.SaveAsync(profile.Id, new LocalInputDraft("Описание", _date, MealSessionPurpose.Diary));

        await drafts.SaveAsync(profile.Id, new LocalInputDraft(text, _date, MealSessionPurpose.Diary));

        Assert.Null(await new LocalInputDraftStore(profiles).ReadAsync(profile.Id));
    }

    [Fact]
    public async Task FourThousandUtf16CharactersArePreservedWithoutTruncation()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Первый");
        LocalInputDraftStore drafts = new(profiles);
        LocalInputDraft draft = new(string.Concat(Enumerable.Repeat("🍎", 2000)), _date, MealSessionPurpose.Diary);

        await drafts.SaveAsync(profile.Id, draft);

        Assert.Equal(draft, await drafts.ReadAsync(profile.Id));
    }

    [Fact]
    public async Task InvalidInputCannotReplaceTheExistingFile()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Первый");
        LocalInputDraftStore drafts = new(profiles);
        LocalInputDraft original = new("Описание", _date, MealSessionPurpose.Diary);
        await drafts.SaveAsync(profile.Id, original);
        LocalInputDraft[] invalid =
        [
            original with { Text = new string('a', 4001) },
            original with { Text = "\ud83c" },
            original with { Text = "\udf4e" },
            original with { Text = "\ud83ca" },
            original with { Text = null! },
            original with { MealDate = default },
            original with { Purpose = (MealSessionPurpose)100 },
            original with { SessionId = Guid.Empty }
        ];

        foreach (LocalInputDraft draft in invalid)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => drafts.SaveAsync(profile.Id, draft));
            Assert.Equal(original, await drafts.ReadAsync(profile.Id));
        }
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{\"Version\":2,\"Draft\":null}")]
    [InlineData("{\"Version\":1,\"Draft\":null}")]
    [InlineData("{\"Version\":1,\"Draft\":{\"Text\":\"text\",\"MealDate\":\"2026-10-19\",\"Purpose\":99}}")]
    [InlineData("{\"Version\":1,\"Draft\":{\"Text\":\"text\",\"Purpose\":0}}")]
    public async Task InvalidFilesAreNotSilentlyErasedOrReplaced(string contents)
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Первый");
        Directory.CreateDirectory(ProfileDirectory(profile.Id));
        string path = Path.Combine(ProfileDirectory(profile.Id), "input-draft.json");
        await File.WriteAllTextAsync(path, contents);
        LocalInputDraftStore drafts = new(profiles);

        await Assert.ThrowsAsync<InvalidDataException>(() => drafts.ReadAsync(profile.Id));
        await Assert.ThrowsAsync<InvalidDataException>(() => drafts.SaveAsync(profile.Id,
            new LocalInputDraft("Новый ввод", _date, MealSessionPurpose.Diary)));
        await Assert.ThrowsAsync<InvalidDataException>(() => drafts.ClearAsync(profile.Id));

        Assert.Equal(contents, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task UnsupportedVersionPreservesOtherwiseValidDraft()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Первый");
        Directory.CreateDirectory(ProfileDirectory(profile.Id));
        string path = Path.Combine(ProfileDirectory(profile.Id), "input-draft.json");
        string contents = JsonSerializer.Serialize(new
        {
            Version = 2,
            Draft = new LocalInputDraft("Описание", _date, MealSessionPurpose.CreateDish)
        });
        await File.WriteAllTextAsync(path, contents);

        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalInputDraftStore(profiles).ReadAsync(profile.Id));

        Assert.Equal(contents, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ExplicitDiscardRemovesOnlyTheSelectedProfilesUnreadableInput()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile first = await profiles.CreateAsync("Первый");
        LocalProfile second = await profiles.CreateAsync("Второй");
        Directory.CreateDirectory(ProfileDirectory(first.Id));
        string path = Path.Combine(ProfileDirectory(first.Id), "input-draft.json");
        await File.WriteAllTextAsync(path, "not-json");
        string unrelated = Path.Combine(ProfileDirectory(first.Id), "unrelated.txt");
        await File.WriteAllTextAsync(unrelated, "keep");
        LocalInputDraftStore drafts = new(profiles);
        LocalInputDraft other = new("Другой ввод", _date, MealSessionPurpose.Diary);
        await drafts.SaveAsync(second.Id, other);

        await drafts.DiscardAsync(first.Id);

        Assert.Null(await drafts.ReadAsync(first.Id));
        Assert.Equal(other, await drafts.ReadAsync(second.Id));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
        Assert.Equal(2, (await profiles.ListAsync()).Count);
    }

    [Fact]
    public async Task OversizedFileIsRejectedWithoutReadingOrChangingItsContents()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Первый");
        Directory.CreateDirectory(ProfileDirectory(profile.Id));
        string path = Path.Combine(ProfileDirectory(profile.Id), "input-draft.json");
        string contents = new(' ', 128 * 1024 + 1);
        await File.WriteAllTextAsync(path, contents);

        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalInputDraftStore(profiles).ReadAsync(profile.Id));

        Assert.Equal(contents, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CancelledSaveAndClearLeaveTheOriginalDraftIntact()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Первый");
        LocalInputDraftStore drafts = new(profiles);
        LocalInputDraft original = new("Описание", _date, MealSessionPurpose.Diary);
        await drafts.SaveAsync(profile.Id, original);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drafts.SaveAsync(profile.Id,
            original with { Text = "Новый ввод" }, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drafts.ClearAsync(profile.Id, cancelled.Token));

        Assert.Equal(original, await drafts.ReadAsync(profile.Id));
        Assert.Single(Directory.GetFiles(ProfileDirectory(profile.Id)));
    }

    [Fact]
    public async Task OrderedSavesAndClearCannotRestoreOlderInput()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Первый");
        LocalInputDraftStore drafts = new(profiles);
        LocalInputDraft original = new("Первый ввод", _date, MealSessionPurpose.Diary);

        Task first = drafts.SaveAsync(profile.Id, original);
        Task second = drafts.SaveAsync(profile.Id, original with { Text = "Последний ввод" });
        await Task.WhenAll(first, second);
        Assert.Equal("Последний ввод", (await drafts.ReadAsync(profile.Id))!.Text);

        Task save = drafts.SaveAsync(profile.Id, original);
        Task clear = drafts.ClearAsync(profile.Id);
        await Task.WhenAll(save, clear);

        Assert.Null(await drafts.ReadAsync(profile.Id));
    }

    [Fact]
    public async Task UnknownProfilesCannotReadWriteOrCreateDirectories()
    {
        LocalProfileStore profiles = new(_root);
        await profiles.CreateAsync("Первый");
        LocalInputDraftStore drafts = new(profiles);
        Guid unknown = Guid.NewGuid();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => drafts.ReadAsync(unknown));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => drafts.SaveAsync(unknown,
            new LocalInputDraft("Описание", _date, MealSessionPurpose.Diary)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => drafts.ClearAsync(unknown));
        await Assert.ThrowsAsync<ArgumentException>(() => drafts.ReadAsync(Guid.Empty));

        Assert.False(Directory.Exists(ProfileDirectory(unknown)));
    }

    private string ProfileDirectory(Guid id) => Path.Combine(_root, "profiles", id.ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
