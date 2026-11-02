using System.Text;
using Microsoft.JSInterop;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;
using NutriFlow.Web.Services;

namespace NutriFlow.Web.Tests;

public sealed class PersistentDirectoryTests : IDisposable
{
    private const string Storage = "nutriflow/files";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nutriflow-web-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeJsRuntime _js = new();

    private FakeStorageModule Module => _js.Storage;

    [Fact]
    public async Task RestoreBringsBackNestedFilesWithoutWritingThemAgain()
    {
        Module.Files[$"{Storage}/profiles.json"] = Encoding.UTF8.GetBytes("{\"Version\":1}");
        Module.Files[$"{Storage}/profiles/a1/input-draft.json"] = Encoding.UTF8.GetBytes("draft");
        Module.Files[$"{Storage}/profiles/a1/label-photos/p.jpg"] = new byte[300 * 1024];
        PersistentDirectory directory = Create();

        await directory.RestoreAsync();
        await directory.FlushAsync();

        Assert.Equal("{\"Version\":1}", await File.ReadAllTextAsync(Path.Combine(_root, "profiles.json")));
        Assert.Equal("draft", await File.ReadAllTextAsync(Path.Combine(_root, "profiles", "a1", "input-draft.json")));
        Assert.Equal(300 * 1024, new FileInfo(Path.Combine(_root, "profiles", "a1", "label-photos", "p.jpg")).Length);
        Assert.Empty(Module.Writes);
        Assert.Empty(Module.Removals);
    }

    [Fact]
    public async Task FlushWritesNewAndChangedFilesAndRemovesDeletedOnes()
    {
        Module.Files[$"{Storage}/old.json"] = [1];
        Module.Files[$"{Storage}/same.json"] = [2];
        PersistentDirectory directory = Create();
        await directory.RestoreAsync();

        File.Delete(Path.Combine(_root, "old.json"));
        Directory.CreateDirectory(Path.Combine(_root, "profiles", "a1"));
        await File.WriteAllTextAsync(Path.Combine(_root, "profiles", "a1", "input-draft.json"), "new");
        await directory.FlushAsync();

        Assert.Equal([$"{Storage}/profiles/a1/input-draft.json"], Module.Writes);
        Assert.Equal([$"{Storage}/old.json"], Module.Removals);
        Assert.Equal("new", Encoding.UTF8.GetString(Module.Files[$"{Storage}/profiles/a1/input-draft.json"]));
        Assert.False(Module.Files.ContainsKey($"{Storage}/old.json"));
        Assert.Equal([2], Module.Files[$"{Storage}/same.json"]);
    }

    [Fact]
    public async Task SmallFileWithSameLengthAndTimeIsComparedByContent()
    {
        PersistentDirectory directory = Create();
        await directory.RestoreAsync();
        string path = Path.Combine(_root, "profiles.json");
        await File.WriteAllTextAsync(path, "aaaa");
        DateTime time = new(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, time);
        await directory.FlushAsync();

        await File.WriteAllTextAsync(path, "bbbb");
        File.SetLastWriteTimeUtc(path, time);
        await directory.FlushAsync();

        Assert.Equal(2, Module.Writes.Count);
        Assert.Equal("bbbb", Encoding.UTF8.GetString(Module.Files[$"{Storage}/profiles.json"]));
    }

    [Fact]
    public async Task LargeFileIsWrittenAgainOnlyWhenItsTimeOrLengthChanges()
    {
        PersistentDirectory directory = Create();
        await directory.RestoreAsync();
        string path = Path.Combine(_root, "photo.jpg");
        await File.WriteAllBytesAsync(path, new byte[300 * 1024]);
        await directory.FlushAsync();
        await directory.FlushAsync();
        Assert.Single(Module.Writes);

        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));
        await directory.FlushAsync();

        Assert.Equal(2, Module.Writes.Count);
    }

    [Fact]
    public async Task FailedWriteIsRetriedByTheNextFlush()
    {
        PersistentDirectory directory = Create();
        await directory.RestoreAsync();
        await File.WriteAllTextAsync(Path.Combine(_root, "profiles.json"), "profiles");
        Module.FailWrites = true;

        JSException failure = await Assert.ThrowsAsync<JSException>(() => directory.FlushAsync());
        Assert.StartsWith("QuotaExceededError", failure.Message, StringComparison.Ordinal);

        Module.FailWrites = false;
        await directory.FlushAsync();
        Assert.Equal("profiles", Encoding.UTF8.GetString(Module.Files[$"{Storage}/profiles.json"]));
    }

    [Fact]
    public async Task FlushBeforeRestoreAndSecondRestoreAreRejected()
    {
        Module.Files[$"{Storage}/profiles.json"] = [1];
        PersistentDirectory directory = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => directory.FlushAsync());
        Assert.Empty(Module.Removals);
        await directory.RestoreAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => directory.RestoreAsync());
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("profiles/../../outside.json")]
    [InlineData("profiles//a.json")]
    [InlineData("profiles\\..\\outside.json")]
    public async Task RestoreRejectsStoredPathsThatLeaveTheFolder(string relativePath)
    {
        Module.Files[$"{Storage}/{relativePath}"] = [1];

        await Assert.ThrowsAsync<InvalidDataException>(() => Create().RestoreAsync());
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "outside.json")));
    }

    [Fact]
    public async Task ProfileStoreWorksOnTheRestoredFolder()
    {
        PersistentDirectory first = Create();
        await first.RestoreAsync();
        LocalProfile created = await new LocalProfileStore(_root).CreateAsync("Веб");
        await new LocalInputDraftStore(new LocalProfileStore(_root)).SaveAsync(created.Id,
            new LocalInputDraft("200 г творога", new DateOnly(2026, 11, 2), MealSessionPurpose.Diary));
        await first.FlushAsync();
        Directory.Delete(_root, recursive: true);

        await Create().RestoreAsync();

        LocalProfileStore restored = new(_root);
        Assert.Equal(created, Assert.Single(await restored.ListAsync()));
        Assert.Equal("200 г творога", (await new LocalInputDraftStore(restored).ReadAsync(created.Id))?.Text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private PersistentDirectory Create() => new(new OpfsFiles(_js), _root);
}
