using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure;
using NutriFlow.Infrastructure.LabelPhotos;

namespace NutriFlow.Mobile.Core.Tests;

public sealed class LocalProfileBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nutriflow-profile-backup-tests", Guid.NewGuid().ToString("N"));
    private readonly DateOnly _date = new(2026, 10, 19);

    [Fact]
    public async Task RoundTripCreatesSeparateProfileAndPreservesPhotosDishesGoalsAdjustmentsAndActiveSession()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile original = await profiles.CreateAsync("Original");
        LocalMealClient client = new(profiles);
        string photoReference;
        byte[] photo = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        string originalDirectory = ProfileDirectory(original.Id);
        await using (var context = await LocalProfileDatabase.OpenAsync(Path.Combine(originalDirectory, "nutriflow.db"), original.Id))
        {
            photoReference = await new LabelPhotoStore(context, Path.Combine(originalDirectory, "label-photos"), original.Id)
                .SaveAsync(LabelPhotoValidator.Validate(photo, "image/png"));
        }
        ProductResponse product = new("Synthetic label product", 100m, 0m, 0m, 25m,
            "LabelPhoto", "Estimated", "Synthetic test label", photoReference, null);
        await client.AddProductAsync(original.Id, product);
        await client.SetDailyGoalAsync(original.Id, _date, new NutritionResponse(1700m, 128m, 57m, 170m));
        MealSessionResponse dishDraft = await client.CreateManualDishAsync(original.Id, "Synthetic saved dish",
            new ManualIngredient[] { new(product, 300m, RemovedWeightInGrams: 100m) }, 400m, _date);
        SavedDishResponse saved = (await client.ConfirmAsync(original.Id, dishDraft.Id, dishDraft.PreviewToken))!.SavedDish!;
        MealSessionResponse portion = await client.CreateSavedDishMealAsync(original.Id, saved.Id, 200m, _date);
        MealEntryResponse entry = Assert.Single((await client.ConfirmAsync(original.Id, portion.Id, portion.PreviewToken))!.Entries);
        await client.UpdateEntryAsync(original.Id, entry.Id, entry.Revision, 100m, DataQuality.Estimated);
        MealSessionResponse pending = await client.CreateManualMealAsync(original.Id, product, 75m, _date);
        DailyProgressResponse originalProgress = await client.GetDailyProgressAsync(original.Id, _date);
        SavedDishDetailResponse originalDish = (await client.FindSavedDishAsync(original.Id, saved.Id))!;
        await File.WriteAllTextAsync(Path.Combine(originalDirectory, "groq-key.txt"), "not-a-real-key");
        await File.WriteAllTextAsync(Path.Combine(_root, "preferences.json"), "not-exported");
        string archive = Path.Combine(_root, "profile.zip");
        await new LocalProfileBackup(profiles).ExportAsync(original.Id, archive, pending.Id);

        Dictionary<string, byte[]> files = ReadArchive(archive);
        Assert.Equal(4, files.Count);
        Assert.DoesNotContain(files.Keys, name => name.Contains("key", StringComparison.OrdinalIgnoreCase) || name.Contains("preferences", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(photo, files["data/label-photos/" + photoReference["label-photo:".Length..]]);

        await using FileStream input = File.OpenRead(archive);
        LocalProfileBackupResult restored = await new LocalProfileBackup(profiles).ImportAsync(input, "Restored");
        Assert.NotEqual(original.Id, restored.Profile.Id);
        Assert.Equal(original, await profiles.GetSelectedAsync());
        Assert.Equal(pending.Id, restored.ActiveSessionId);
        Assert.Equal(2, (await profiles.ListAsync()).Count);
        LocalMealClient restarted = new(new LocalProfileStore(_root));
        Assert.Equal(JsonSerializer.Serialize(originalProgress), JsonSerializer.Serialize(await restarted.GetDailyProgressAsync(restored.Profile.Id, _date)));
        Assert.Equal(JsonSerializer.Serialize(originalDish), JsonSerializer.Serialize(await restarted.FindSavedDishAsync(restored.Profile.Id, saved.Id)));
        Assert.Equal(product, Assert.Single(await restarted.SearchProductsAsync(restored.Profile.Id)));
        MealSessionResponse restoredPending = (await restarted.FindSessionAsync(restored.Profile.Id, pending.Id))!;
        Assert.Equal(pending.PreviewToken, restoredPending.PreviewToken);
        Assert.True(restoredPending.CanConfirm);
        Assert.Equal("Confirmed", (await restarted.ConfirmAsync(restored.Profile.Id, pending.Id, pending.PreviewToken))!.Outcome);
        Assert.Equal(2, (await restarted.GetDailyProgressAsync(restored.Profile.Id, _date)).Entries.Count);
        Assert.Equal(JsonSerializer.Serialize(originalProgress), JsonSerializer.Serialize(await restarted.GetDailyProgressAsync(original.Id, _date)));
        Assert.True((await restarted.FindSessionAsync(original.Id, pending.Id))!.CanConfirm);

        await using (var context = await LocalProfileDatabase.OpenAsync(Path.Combine(ProfileDirectory(restored.Profile.Id), "nutriflow.db"), restored.Profile.Id))
        {
            StoredLabelPhoto stored = (await new LabelPhotoStore(context,
                Path.Combine(ProfileDirectory(restored.Profile.Id), "label-photos"), restored.Profile.Id).FindAsync(photoReference))!;
            Assert.Equal(photo, await File.ReadAllBytesAsync(stored.FilePath));
        }
        await AssertAllOwnersAsync(restored.Profile.Id);
        Assert.Empty(Directory.GetDirectories(_root, ".profile-backup-*"));
    }

    [Fact]
    public async Task EmptyProfileCanBeExportedAndRestoredWithoutChangingSelection()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile original = await profiles.CreateAsync("Empty");
        string archive = Path.Combine(_root, "empty.zip");
        LocalProfileBackup backup = new(profiles);
        await backup.ExportAsync(original.Id, archive);
        await using FileStream input = File.OpenRead(archive);
        LocalProfileBackupResult result = await backup.ImportAsync(input, "Empty copy");
        Assert.Null(result.ActiveSessionId);
        Assert.Equal(original, await profiles.GetSelectedAsync());
        Assert.Empty((await new LocalMealClient(profiles).GetDailyProgressAsync(result.Profile.Id, _date)).Entries);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("data/../../outside.txt")]
    [InlineData("data\\nutriflow.db")]
    [InlineData("/absolute.txt")]
    [InlineData("data/label-photos/not-an-id.png")]
    [InlineData("secrets.json")]
    public async Task UnexpectedArchivePathsAreRejectedWithoutPublishingProfile(string unexpectedPath)
    {
        (LocalProfileStore profiles, LocalProfile original, string archive) = await CreateEmptyArchiveAsync();
        Dictionary<string, byte[]> entries = ReadArchive(archive);
        entries.Add(unexpectedPath, [1]);
        using MemoryStream malicious = MakeArchive(entries);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalProfileBackup(profiles).ImportAsync(malicious, "Rejected"));
        await AssertOriginalOnlyAsync(profiles, original);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("symlink")]
    [InlineData("future")]
    [InlineData("owner")]
    [InlineData("hash")]
    public async Task InvalidArchiveMetadataIsRejectedWithoutPublishingProfile(string kind)
    {
        (LocalProfileStore profiles, LocalProfile original, string archive) = await CreateEmptyArchiveAsync();
        Dictionary<string, byte[]> entries = ReadArchive(archive);
        if (kind is "future" or "owner")
        {
            JsonObject manifest = JsonNode.Parse(entries["profile.json"])!.AsObject();
            if (kind == "future")
            {
                manifest["FormatVersion"] = 2;
            }
            else
            {
                manifest["ProfileId"] = Guid.NewGuid();
            }
            entries["profile.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        }
        if (kind == "hash")
        {
            entries["data/nutriflow.db"][100] ^= 0xFF;
        }
        using MemoryStream malicious = MakeArchive(entries, kind);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalProfileBackup(profiles).ImportAsync(malicious, "Rejected"));
        await AssertOriginalOnlyAsync(profiles, original);
    }

    [Theory]
    [InlineData("migration")]
    [InlineData("trigger")]
    [InlineData("trigger-prefix")]
    [InlineData("foreign-owner")]
    public async Task UntrustedDatabaseStructureIsRejectedEvenWithCorrectFileHash(string change)
    {
        (LocalProfileStore profiles, LocalProfile original, string archive) = await CreateEmptyArchiveAsync();
        Dictionary<string, byte[]> entries = ReadArchive(archive);
        string databasePath = Path.Combine(_root, "modified.db");
        await File.WriteAllBytesAsync(databasePath, entries["data/nutriflow.db"]);
        await using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = change switch
            {
                "migration" => "INSERT INTO __EFMigrationsHistory VALUES ('20990101000000_FutureMigration', '99.0.0');",
                "trigger" => "CREATE TRIGGER unexpected_trigger AFTER INSERT ON Products BEGIN SELECT 1; END;",
                "trigger-prefix" => "CREATE TRIGGER sqliteXunexpected AFTER INSERT ON Users BEGIN SELECT RAISE(ABORT, 'unexpected trigger ran'); END;",
                _ => "INSERT INTO Users (Id, CreatedAtUtc, IsLegacyLocal, LegacyLabelPhotosImported) VALUES ('00000000-0000-0000-0000-000000000001', '2026-10-19', 0, 0);"
            };
            await command.ExecuteNonQueryAsync();
        }
        entries["data/nutriflow.db"] = await File.ReadAllBytesAsync(databasePath);
        JsonObject manifest = JsonNode.Parse(entries["data/manifest.json"])!.AsObject();
        JsonNode file = manifest["files"]!.AsArray().Single(item => item!["path"]!.GetValue<string>() == "nutriflow.db")!;
        file["length"] = entries["data/nutriflow.db"].Length;
        file["sha256"] = Convert.ToHexString(SHA256.HashData(entries["data/nutriflow.db"]));
        entries["data/manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        using MemoryStream malicious = MakeArchive(entries);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalProfileBackup(profiles).ImportAsync(malicious, "Rejected"));
        await AssertOriginalOnlyAsync(profiles, original);
    }

    [Fact]
    public async Task DuplicateProfileNameDoesNotOverwriteOriginalOrLeaveNewDirectory()
    {
        (LocalProfileStore profiles, LocalProfile original, string archive) = await CreateEmptyArchiveAsync();
        await using FileStream input = File.OpenRead(archive);
        await Assert.ThrowsAsync<ArgumentException>(() => new LocalProfileBackup(profiles).ImportAsync(input, " original "));
        await AssertOriginalOnlyAsync(profiles, original);
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "profiles")));
    }

    [Fact]
    public async Task CancellationAndExistingExportDestinationLeaveExistingDataUntouched()
    {
        (LocalProfileStore profiles, LocalProfile original, string archive) = await CreateEmptyArchiveAsync();
        byte[] before = await File.ReadAllBytesAsync(archive);
        await Assert.ThrowsAsync<IOException>(() => new LocalProfileBackup(profiles).ExportAsync(original.Id, archive));
        Assert.Equal(before, await File.ReadAllBytesAsync(archive));
        using CancellationTokenSource cancellation = new();
        using CancellingStream input = new(before, cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalProfileBackup(profiles).ImportAsync(input, "Cancelled", cancellation.Token));
        await AssertOriginalOnlyAsync(profiles, original);
    }

    [Fact]
    public async Task ExcessiveEntryCountIsRejectedBeforeExtraction()
    {
        (LocalProfileStore profiles, LocalProfile original, _) = await CreateEmptyArchiveAsync();
        using MemoryStream input = new();
        using (ZipArchive archive = new(input, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int index = 0; index < 4097; index++)
            {
                archive.CreateEntry($"data/label-photos/{Guid.NewGuid():N}.png");
            }
        }
        input.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalProfileBackup(profiles).ImportAsync(input, "Rejected"));
        await AssertOriginalOnlyAsync(profiles, original);
    }

    [Fact]
    public async Task OversizedPhotoIsRejectedBeforePublication()
    {
        (LocalProfileStore profiles, LocalProfile original, string archive) = await CreateEmptyArchiveAsync();
        Dictionary<string, byte[]> entries = ReadArchive(archive);
        entries[$"data/label-photos/{Guid.NewGuid():N}.png"] = new byte[LabelPhotoValidator.MaximumFileSizeInBytes + 1];
        using MemoryStream input = MakeArchive(entries);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalProfileBackup(profiles).ImportAsync(input, "Rejected"));
        await AssertOriginalOnlyAsync(profiles, original);
    }

    [Fact]
    public async Task FalseUncompressedEntryLengthIsRejectedWithoutPublishingProfile()
    {
        (LocalProfileStore profiles, LocalProfile original, string archive) = await CreateEmptyArchiveAsync();
        Dictionary<string, byte[]> entries = ReadArchive(archive);
        string photoName = $"data/label-photos/{Guid.NewGuid():N}.png";
        entries[photoName] = new byte[8192];
        using MemoryStream validArchive = MakeArchive(entries);
        byte[] bytes = validArchive.ToArray();
        bool patched = false;
        for (int index = 0; index <= bytes.Length - 46; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) != 0x02014B50)
            {
                continue;
            }
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(index + 28, 2));
            if (index + 46 + nameLength <= bytes.Length && Encoding.UTF8.GetString(bytes, index + 46, nameLength) == photoName)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index + 24, 4), 1);
                patched = true;
                break;
            }
        }
        Assert.True(patched);
        using MemoryStream malicious = new(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalProfileBackup(profiles).ImportAsync(malicious, "Rejected"));
        await AssertOriginalOnlyAsync(profiles, original);
    }

    [Fact]
    public async Task MissingReferencedPhotoIsRejectedWithoutChangingOriginal()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile original = await profiles.CreateAsync("Original");
        string reference;
        await using (var context = await LocalProfileDatabase.OpenAsync(
            Path.Combine(ProfileDirectory(original.Id), "nutriflow.db"), original.Id))
        {
            reference = await new LabelPhotoStore(context, Path.Combine(ProfileDirectory(original.Id), "label-photos"), original.Id)
                .SaveAsync(LabelPhotoValidator.Validate(new byte[] { 0xFF, 0xD8, 0xFF, 1 }, "image/jpeg"));
        }
        string archive = Path.Combine(_root, "photo.zip");
        await new LocalProfileBackup(profiles).ExportAsync(original.Id, archive);
        Dictionary<string, byte[]> entries = ReadArchive(archive);
        entries.Remove("data/label-photos/" + reference["label-photo:".Length..]);
        using MemoryStream input = MakeArchive(entries);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalProfileBackup(profiles).ImportAsync(input, "Rejected"));
        await AssertOriginalOnlyAsync(profiles, original);
        Assert.True(File.Exists(Path.Combine(ProfileDirectory(original.Id), "label-photos", reference["label-photo:".Length..])));
    }

    [Fact]
    public async Task ProfileLimitIsRespectedWithoutPublishingDirectory()
    {
        (LocalProfileStore profiles, _, string archive) = await CreateEmptyArchiveAsync();
        for (int index = 1; index < 30; index++)
        {
            await profiles.CreateAsync($"Profile {index}");
        }
        LocalProfile? selected = await profiles.GetSelectedAsync();
        await using FileStream input = File.OpenRead(archive);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LocalProfileBackup(profiles).ImportAsync(input, "Overflow"));
        Assert.Equal(30, (await profiles.ListAsync()).Count);
        Assert.Equal(selected, await profiles.GetSelectedAsync());
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "profiles")));
        Assert.Empty(Directory.GetDirectories(_root, ".profile-backup-*"));
    }

    [Fact]
    public async Task ExportRejectsForeignActiveSessionAndKeepsExistingDatabaseUnchanged()
    {
        (LocalProfileStore profiles, LocalProfile original, _) = await CreateEmptyArchiveAsync();
        string database = Path.Combine(ProfileDirectory(original.Id), "nutriflow.db");
        byte[] before = await File.ReadAllBytesAsync(database);
        string destination = Path.Combine(_root, "rejected.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalProfileBackup(profiles)
            .ExportAsync(original.Id, destination, Guid.NewGuid()));
        Assert.False(File.Exists(destination));
        Assert.Equal(before, await File.ReadAllBytesAsync(database));
        await AssertOriginalOnlyAsync(profiles, original);
    }

    [Fact]
    public async Task ConcurrentProfileStoresCannotLoseRestoredProfileDuringCatalogUpdate()
    {
        (LocalProfileStore profiles, LocalProfile original, string archive) = await CreateEmptyArchiveAsync();
        LocalProfileStore other = new(_root + Path.DirectorySeparatorChar);
        await using FileStream input = File.OpenRead(archive);
        Task<LocalProfileBackupResult> restore = new LocalProfileBackup(other).ImportAsync(input, "Restored");
        Task<LocalProfile> create = profiles.CreateAsync("Concurrent");
        await Task.WhenAll(restore, create);
        LocalProfileBackupResult restored = await restore;
        LocalProfile created = await create;
        IReadOnlyList<LocalProfile> list = await profiles.ListAsync();
        Assert.Equal(3, list.Count);
        Assert.Contains(original, list);
        Assert.Contains(restored.Profile, list);
        Assert.Contains(created, list);
        Assert.Equal(created, await profiles.GetSelectedAsync());
    }

    private async Task<(LocalProfileStore Profiles, LocalProfile Original, string Archive)> CreateEmptyArchiveAsync()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile original = await profiles.CreateAsync("Original");
        string archive = Path.Combine(_root, "profile.zip");
        await new LocalProfileBackup(profiles).ExportAsync(original.Id, archive);
        return (profiles, original, archive);
    }

    private async Task AssertOriginalOnlyAsync(LocalProfileStore profiles, LocalProfile original)
    {
        Assert.Equal(original, Assert.Single(await profiles.ListAsync()));
        Assert.Equal(original, await profiles.GetSelectedAsync());
        Assert.Empty(Directory.GetDirectories(_root, ".profile-backup-*"));
        Assert.Empty((await new LocalMealClient(profiles).GetDailyProgressAsync(original.Id, _date)).Entries);
    }

    private async Task AssertAllOwnersAsync(Guid profileId)
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(ProfileDirectory(profileId), "nutriflow.db"),
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        foreach (string table in new[] { "Products", "ProductAliases", "MealSessions", "DailyGoals", "SavedDishes", "LabelPhotos" })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT DISTINCT UserId FROM \"{table}\" WHERE UserId IS NOT NULL;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(profileId, reader.GetGuid(0));
            Assert.False(await reader.ReadAsync());
        }
    }

    private static Dictionary<string, byte[]> ReadArchive(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        return archive.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using Stream stream = entry.Open();
            using MemoryStream contents = new();
            stream.CopyTo(contents);
            return contents.ToArray();
        });
    }

    private static MemoryStream MakeArchive(Dictionary<string, byte[]> entries, string? invalidMetadata = null)
    {
        MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] bytes) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                if (invalidMetadata == "symlink" && name == "profile.json")
                {
                    entry.ExternalAttributes = 0xA1FF << 16;
                }
                using Stream contents = entry.Open();
                contents.Write(bytes);
            }
            if (invalidMetadata == "duplicate")
            {
                using Stream contents = archive.CreateEntry("PROFILE.JSON").Open();
                contents.Write(entries["profile.json"]);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private string ProfileDirectory(Guid profileId) => Path.Combine(_root, "profiles", profileId.ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CancellingStream(byte[] buffer, CancellationTokenSource cancellation) : MemoryStream(buffer)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled<int>(cancellation.Token);
        }
    }
}
