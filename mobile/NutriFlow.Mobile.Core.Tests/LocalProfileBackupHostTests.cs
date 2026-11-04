using System.Data.Common;
using System.IO.Compression;
using System.Text.Json;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure;
using NutriFlow.Infrastructure.LabelPhotos;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Mobile.Core.Tests;

// a copy made by one host restores on the other: the phone keeps the database in the profile folder,
// the browser keeps it apart and checks every file on a copy of its own (SeparateStoreDatabases mirrors that)
public sealed class LocalProfileBackupHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nutriflow-backup-host-tests", Guid.NewGuid().ToString("N"));
    private readonly DateOnly _date = new(2026, 11, 3);
    private readonly byte[] _photo = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 4, 5, 6];

    [Fact]
    public async Task CopyFromTheBrowserHostRestoresOnThePhone()
    {
        SeparateStoreDatabases browser = new(Path.Combine(_root, "browser-store"));
        LocalProfileStore browserProfiles = new(Path.Combine(_root, "browser"));
        LocalProfile original = await browserProfiles.CreateAsync("Анна");
        Profile filled = await FillAsync(Path.Combine(_root, "browser"), browserProfiles, original, new LocalMealClient(browserProfiles, null, browser.OpenAsync), browser.OpenAsync);
        string archive = Path.Combine(_root, "browser.zip");

        await new LocalProfileBackup(browserProfiles, browser).ExportAsync(original.Id, archive, filled.PendingSessionId);

        Assert.False(File.Exists(Path.Combine(_root, "browser", "profiles", original.Id.ToString("N"), "nutriflow.db")));
        Assert.True(browser.Uses > 0);
        Assert.Equal([browser.PathOf(original.Id)], browser.StoredFiles());
        LocalProfileStore phoneProfiles = new(Path.Combine(_root, "phone"));
        await using FileStream input = File.OpenRead(archive);
        LocalProfileBackupResult restored = await new LocalProfileBackup(phoneProfiles).ImportAsync(input, "Анна с ноутбука");

        Assert.Equal(filled.PendingSessionId, restored.ActiveSessionId);
        await AssertSameAsync(filled, new LocalMealClient(phoneProfiles), restored.Profile.Id, LocalProfileDatabase.OpenAsync,
            Path.Combine(_root, "phone", "profiles", restored.Profile.Id.ToString("N")));
        Assert.Equal("ужин без веса", (await new LocalInputDraftStore(phoneProfiles).ReadAsync(restored.Profile.Id))!.Text);
    }

    [Fact]
    public async Task CopyFromThePhoneRestoresInTheBrowserHost()
    {
        LocalProfileStore phoneProfiles = new(Path.Combine(_root, "phone"));
        LocalProfile original = await phoneProfiles.CreateAsync("Анна");
        Profile filled = await FillAsync(Path.Combine(_root, "phone"), phoneProfiles, original, new LocalMealClient(phoneProfiles), LocalProfileDatabase.OpenAsync);
        string archive = Path.Combine(_root, "phone.zip");
        await new LocalProfileBackup(phoneProfiles).ExportAsync(original.Id, archive, filled.PendingSessionId);

        SeparateStoreDatabases browser = new(Path.Combine(_root, "browser-store"));
        LocalProfileStore browserProfiles = new(Path.Combine(_root, "browser"));
        await using FileStream input = File.OpenRead(archive);
        LocalProfileBackupResult restored = await new LocalProfileBackup(browserProfiles, browser).ImportAsync(input, "Анна с телефона");

        string restoredDirectory = Path.Combine(_root, "browser", "profiles", restored.Profile.Id.ToString("N"));
        Assert.False(File.Exists(Path.Combine(restoredDirectory, "nutriflow.db")));
        Assert.Equal([browser.PathOf(restored.Profile.Id)], browser.StoredFiles());
        Assert.Equal(filled.PendingSessionId, restored.ActiveSessionId);
        await AssertSameAsync(filled, new LocalMealClient(browserProfiles, null, browser.OpenAsync), restored.Profile.Id,
            browser.OpenAsync, restoredDirectory);
        Assert.Equal("ужин без веса", (await new LocalInputDraftStore(browserProfiles).ReadAsync(restored.Profile.Id))!.Text);
    }

    [Fact]
    public async Task DamagedDatabaseIsRejectedByTheBrowserHostWithoutLeavingFiles()
    {
        string archive = await CreatePhoneArchiveAsync();
        byte[] damaged;
        using (MemoryStream copy = new())
        {
            await using (FileStream source = File.OpenRead(archive))
            {
                await source.CopyToAsync(copy);
            }
            using (ZipArchive zip = new(copy, ZipArchiveMode.Update, leaveOpen: true))
            {
                ZipArchiveEntry entry = zip.GetEntry("data/nutriflow.db")!;
                byte[] database;
                await using (Stream read = await entry.OpenAsync())
                using (MemoryStream bytes = new())
                {
                    await read.CopyToAsync(bytes);
                    database = bytes.ToArray();
                }
                // the header stays, the schema pages turn into noise
                Array.Fill(database, (byte)0x5A, 100, database.Length - 100);
                entry.Delete();
                await using Stream write = await zip.CreateEntry("data/nutriflow.db").OpenAsync();
                await write.WriteAsync(database);
            }
            damaged = copy.ToArray();
        }

        SeparateStoreDatabases browser = new(Path.Combine(_root, "browser-store"));
        LocalProfileStore browserProfiles = new(Path.Combine(_root, "browser"));
        LocalProfile existing = await browserProfiles.CreateAsync("Существующий");
        await using (await browser.OpenAsync("", existing.Id, default))
        {
        }

        using MemoryStream input = new(damaged);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LocalProfileBackup(browserProfiles, browser).ImportAsync(input, "Повреждённая"));

        Assert.Equal([existing], await browserProfiles.ListAsync());
        Assert.Equal([browser.PathOf(existing.Id)], browser.StoredFiles());
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "browser"), ".profile-backup-*"));
    }

    [Fact]
    public async Task RestoreThatCannotBePublishedRemovesItsDatabaseFromTheBrowserStore()
    {
        string archive = await CreatePhoneArchiveAsync();
        SeparateStoreDatabases browser = new(Path.Combine(_root, "browser-store"));
        LocalProfileStore browserProfiles = new(Path.Combine(_root, "browser"));
        LocalProfile existing = await browserProfiles.CreateAsync("Анна");

        await using FileStream input = File.OpenRead(archive);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new LocalProfileBackup(browserProfiles, browser).ImportAsync(input, "анна"));

        Assert.Equal([existing], await browserProfiles.ListAsync());
        Assert.Single(browser.Installed);
        Assert.Empty(browser.StoredFiles());
    }

    private async Task<string> CreatePhoneArchiveAsync()
    {
        LocalProfileStore phoneProfiles = new(Path.Combine(_root, "phone"));
        LocalProfile original = await phoneProfiles.CreateAsync("Анна");
        await FillAsync(Path.Combine(_root, "phone"), phoneProfiles, original, new LocalMealClient(phoneProfiles), LocalProfileDatabase.OpenAsync);
        string archive = Path.Combine(_root, "phone.zip");
        await new LocalProfileBackup(phoneProfiles).ExportAsync(original.Id, archive);
        return archive;
    }

    private async Task<Profile> FillAsync(string root, LocalProfileStore profiles, LocalProfile profile, LocalMealClient client,
        Func<string, Guid, CancellationToken, Task<NutriFlowDbContext>> openDatabase)
    {
        string directory = Path.Combine(root, "profiles", profile.Id.ToString("N"));
        string photoReference;
        await using (NutriFlowDbContext context = await openDatabase(Path.Combine(directory, "nutriflow.db"), profile.Id, default))
        {
            photoReference = await new LabelPhotoStore(context, Path.Combine(directory, "label-photos"), profile.Id)
                .SaveAsync(LabelPhotoValidator.Validate(_photo, "image/png"));
        }
        ProductResponse label = new("Йогурт с этикетки", 80m, 4m, 2.5m, 10m,
            "LabelPhoto", "Estimated", "Фото этикетки", photoReference, null);
        ProductResponse oats = new("Овсянка", 350m, 12m, 6m, 60m, "ManualInput", "Exact", "Ручной ввод", null, null);
        await client.AddProductAsync(profile.Id, label);
        await client.AddProductAsync(profile.Id, oats);
        await client.SetDailyGoalAsync(profile.Id, _date, new NutritionResponse(1800m, 120m, 60m, 200m));
        MealSessionResponse dishDraft = await client.CreateManualDishAsync(profile.Id, "Каша с йогуртом",
            new ManualIngredient[] { new(oats, 60m), new(label, 150m) }, 300m, _date);
        SavedDishResponse dish = (await client.ConfirmAsync(profile.Id, dishDraft.Id, dishDraft.PreviewToken))!.SavedDish!;
        MealSessionResponse portion = await client.CreateSavedDishMealAsync(profile.Id, dish.Id, 250m, _date);
        await client.ConfirmAsync(profile.Id, portion.Id, portion.PreviewToken);
        MealSessionResponse manual = await client.CreateManualMealAsync(profile.Id, oats, 40m, _date);
        await client.ConfirmAsync(profile.Id, manual.Id, manual.PreviewToken);
        MealSessionResponse pending = await client.CreateManualMealAsync(profile.Id, label, 120m, _date);
        await new LocalInputDraftStore(profiles).SaveAsync(profile.Id, new LocalInputDraft("ужин без веса", _date, MealSessionPurpose.Diary));
        return new Profile(
            JsonSerializer.Serialize(await client.GetDailyProgressAsync(profile.Id, _date)),
            JsonSerializer.Serialize(await client.FindSavedDishAsync(profile.Id, dish.Id)),
            JsonSerializer.Serialize(await client.SearchProductsAsync(profile.Id)),
            dish.Id,
            pending.Id,
            photoReference);
    }

    private async Task AssertSameAsync(Profile expected, LocalMealClient client, Guid profileId,
        Func<string, Guid, CancellationToken, Task<NutriFlowDbContext>> openDatabase, string profileDirectory)
    {
        Assert.Equal(expected.Progress, JsonSerializer.Serialize(await client.GetDailyProgressAsync(profileId, _date)));
        Assert.Equal(expected.Dish, JsonSerializer.Serialize(await client.FindSavedDishAsync(profileId, expected.DishId)));
        Assert.Equal(expected.Products, JsonSerializer.Serialize(await client.SearchProductsAsync(profileId)));
        Assert.True((await client.FindSessionAsync(profileId, expected.PendingSessionId))!.CanConfirm);
        await using NutriFlowDbContext context = await openDatabase(Path.Combine(profileDirectory, "nutriflow.db"), profileId, default);
        StoredLabelPhoto photo = (await new LabelPhotoStore(context, Path.Combine(profileDirectory, "label-photos"), profileId)
            .FindAsync(expected.PhotoReference))!;
        Assert.Equal(_photo, await File.ReadAllBytesAsync(photo.FilePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed record Profile(string Progress, string Dish, string Products, Guid DishId, Guid PendingSessionId, string PhotoReference);

    // like web/NutriFlow.Web/Services/WebBackupDatabases.cs: profile databases sit in a store of their own,
    // and a database file is only ever touched through a temporary copy that is written back after writable work
    private sealed class SeparateStoreDatabases(string store) : LocalProfileBackupDatabases
    {
        public int Uses { get; private set; }

        public List<Guid> Installed { get; } = [];

        public string PathOf(Guid profileId) => Path.Combine(store, $"profile-{profileId:N}.db");

        // the open profile database keeps its -wal and -shm files next to it
        public string[] StoredFiles() => Directory.Exists(store) ? Directory.GetFiles(store, "*.db").Order().ToArray() : [];

        public Task<NutriFlowDbContext> OpenAsync(string profileDatabasePath, Guid ownerId, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(store);
            return LocalProfileDatabase.OpenAsync(PathOf(ownerId), ownerId, cancellationToken);
        }

        public override async Task SnapshotProfileAsync(string profileDatabasePath, Guid profileId, string destinationPath,
            CancellationToken cancellationToken)
        {
            await using (await OpenAsync(profileDatabasePath, profileId, cancellationToken))
            {
            }
            await base.SnapshotProfileAsync(PathOf(profileId), profileId, destinationPath, cancellationToken);
        }

        public override async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
        {
            string temporary = TemporaryCopy(sourcePath);
            try
            {
                await base.CopyAsync(temporary, destinationPath, cancellationToken);
            }
            finally
            {
                DeleteWithJournal(temporary);
            }
        }

        public override async Task<T> UseAsync<T>(string path, bool writable,
            Func<DbConnection, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        {
            Uses++;
            string temporary = TemporaryCopy(path);
            try
            {
                T result = await base.UseAsync(temporary, writable, work, cancellationToken);
                if (writable)
                {
                    File.Copy(temporary, path, overwrite: true);
                }
                return result;
            }
            finally
            {
                DeleteWithJournal(temporary);
            }
        }

        public override Task InstallRestoredAsync(Guid profileId, string preparedDirectory, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(store);
            File.Move(Path.Combine(preparedDirectory, "nutriflow.db"), PathOf(profileId));
            Installed.Add(profileId);
            return Task.CompletedTask;
        }

        public override Task DiscardRestoredAsync(Guid profileId, CancellationToken cancellationToken)
        {
            File.Delete(PathOf(profileId));
            return Task.CompletedTask;
        }

        private string TemporaryCopy(string path)
        {
            Directory.CreateDirectory(store);
            string temporary = Path.Combine(store, $"backup-{Guid.NewGuid():N}.db");
            File.Copy(path, temporary);
            return temporary;
        }

        private static void DeleteWithJournal(string path)
        {
            foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
            {
                File.Delete(path + suffix);
            }
        }
    }
}
