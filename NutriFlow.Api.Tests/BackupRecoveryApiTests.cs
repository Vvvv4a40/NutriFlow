using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Backups;
using NutriFlow.Infrastructure.LabelPhotos;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Api.Tests;

public sealed class BackupRecoveryApiTests
{
    private const string LegacyMigration = "20260907111329_HardenProductResolutionAndMealQuality";
    private static readonly DateOnly MealDate = new(2026, 10, 5);
    private static readonly byte[] PhotoContent = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly string[] DishMessages =
    [
        "Добавил 200 г демо-продукта A.",
        "Потом добавил 100 г демо-продукта B.",
        "Готовое блюдо весит 250 г."
    ];

    [Fact]
    public async Task CurrentBackup_RestoresDiaryGoalPhotosSavedDishCorrectionsAndDeletionAcrossRestart()
    {
        using RecoveryFixture files = new();
        CurrentState expected;
        await using (TestApiFactory source = files.CreateFactory(restored: false, seedDemoData: true))
        {
            using HttpClient client = source.CreateClient();
            MealSessionResponse dishSession = await CreateSessionAsync(client, DishMessages, MealSessionPurpose.CreateDish);
            SavedDishResponse saved = Assert.IsType<SavedDishResponse>((await ConfirmAsync(client, dishSession)).SavedDish);
            MealSessionResponse diarySession = await CreateSessionAsync(client,
                [.. DishMessages, "Съел 125 г, потом ещё две порции по 62,5 г."]);
            ConfirmMealSessionResponse diary = await ConfirmAsync(client, diarySession);
            Assert.Equal(3, diary.Entries.Count);
            using (HttpResponseMessage edit = await MutateEntryAsync(client, HttpMethod.Put, diary.Entries[0].Id, 100m))
            {
                Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
                MealEntryResponse corrected = await ReadAsync<MealEntryResponse>(edit);
                Assert.Equal(100m, corrected.WeightInGrams);
                Assert.Equal(160m, corrected.Nutrition.Calories);
                Assert.Equal(1, corrected.Revision);
            }
            using (HttpResponseMessage deletion = await MutateEntryAsync(client, HttpMethod.Delete, diary.Entries[1].Id))
            {
                Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
            }
            using (HttpResponseMessage goal = await client.PutAsJsonAsync(
                       $"/api/daily-goals/{MealDate:yyyy-MM-dd}", new SetDailyGoalRequest(500m, 40m, 35m, 60m)))
            {
                Assert.Equal(HttpStatusCode.OK, goal.StatusCode);
            }
            string photoReference;
            await using (AsyncServiceScope scope = source.Services.CreateAsyncScope())
            {
                LabelPhotoStore store = scope.ServiceProvider.GetRequiredService<LabelPhotoStore>();
                photoReference = await store.SaveAsync(LabelPhotoValidator.Validate(PhotoContent, "image/png"));
            }
            using (HttpResponseMessage product = await client.PostAsJsonAsync("/api/products/from-label",
                       new CreateLabelProductRequest(photoReference, NutritionBasis.Per100Grams,
                           "Этикетка из копии", 121m, 17m, 5m, 3m)))
            {
                Assert.Equal(HttpStatusCode.Created, product.StatusCode);
            }
            DailyProgressResponse day = await GetDayAsync(client);
            Assert.Equal(260m, day.Consumed.Calories);
            Assert.Equal(240m, day.Remaining!.Calories);
            Assert.Equal(2, day.Entries.Count);
            expected = new CurrentState(diary.Session, dishSession, saved,
                diary.Entries[1].Id, photoReference, day,
                await client.GetStringAsync($"/api/meal-sessions/{diary.Session.Id}"),
                await client.GetStringAsync($"/api/saved-dishes/{saved.Id}"),
                await client.GetStringAsync(ProductUrl("Этикетка из копии")));
        }
        Dictionary<string, string> sourceSnapshot = files.SourceSnapshot();
        await files.BackupAndRestoreAsync();
        Dictionary<string, string> backupSnapshot = files.BackupSnapshot();

        for (int restart = 0; restart < 2; restart++)
        {
            await using TestApiFactory restored = files.CreateFactory(restored: true);
            using HttpClient client = restored.CreateClient();
            await AssertCurrentStateAsync(client, expected);
            Assert.Equal(3, await ReadScalarAsync(restored, "SELECT COUNT(*) FROM MealEntries"));
            Assert.Equal(2, await ReadScalarAsync(restored, "SELECT COUNT(*) FROM MealEntryAdjustments"));
            Assert.Equal(1, await ReadScalarAsync(restored, "SELECT COUNT(*) FROM SavedDishes"));
            Assert.Equal(1, await ReadScalarAsync(restored, "SELECT COUNT(*) FROM LabelPhotos"));
            await AssertLatestSchemaAsync(restored);

            if (restart == 1)
            {
                MealSessionResponse meal = await CreateSessionAsync(client, ["Съел 100 г Демо-блюда."]);
                ProductResponse product = Assert.IsType<ProductResponse>(
                    Assert.Single(Assert.Single(meal.Dishes).Ingredients).ResolvedProduct);
                Assert.Equal("SavedDish", product.SourceKind);
                Assert.Equal($"saved-dish:{expected.SavedDish.Id:N}", product.SourceReference);
                MealEntryResponse entry = Assert.Single((await ConfirmAsync(client, meal)).Entries);
                Assert.Equal(160m, entry.Nutrition.Calories);
                Assert.Equal(420m, (await GetDayAsync(client)).Consumed.Calories);
            }
        }

        Assert.Equal(sourceSnapshot, files.SourceSnapshot());
        Assert.Equal(backupSnapshot, files.BackupSnapshot());
        await DataBackup.VerifyAsync(files.BackupPath);
        Assert.Equal(backupSnapshot, files.BackupSnapshot());
    }

    [Fact]
    public async Task LegacyBackup_ProductionStartupMigratesOwnershipAndImportsPhotosExactlyOnce()
    {
        using RecoveryFixture files = new();
        LegacyState legacy = await CreateLegacySourceAsync(files);
        Dictionary<string, string> sourceSnapshot = files.SourceSnapshot();
        await files.BackupAndRestoreAsync();
        Dictionary<string, string> backupSnapshot = files.BackupSnapshot();
        string? registeredPhotos = null;
        string latePhotoFileName = $"{Guid.NewGuid():N}.png";

        for (int restart = 0; restart < 2; restart++)
        {
            if (restart == 1)
            {
                await File.WriteAllBytesAsync(Path.Combine(files.RestoredPhotosPath, latePhotoFileName), PhotoContent);
            }
            await using TestApiFactory restored = files.CreateFactory(restored: true);
            using HttpClient client = restored.CreateClient();
            using HttpResponseMessage ready = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            ProductResponse[] products = await client.GetFromJsonAsync<ProductResponse[]>(ProductUrl("Старая этикетка"))
                ?? throw new InvalidDataException();
            ProductResponse product = Assert.Single(products);
            Assert.Equal(legacy.PhotoReference, product.SourceReference);
            Assert.Equal("LabelPhoto", product.SourceKind);
            Assert.Equal("Verified", product.DataQuality);
            Assert.Equal(121m, product.Calories);
            await AssertPhotoAsync(client, legacy.PhotoReference);
            await AssertPhotoAsync(client, legacy.UnreferencedPhotoReference);
            if (restart == 1)
            {
                using HttpResponseMessage latePhoto = await client.GetAsync($"/api/label-photos/{latePhotoFileName}");
                Assert.Equal(HttpStatusCode.NotFound, latePhoto.StatusCode);
            }
            DailyProgressResponse day = await GetDayAsync(client);
            Assert.Equal(500m, day.Goal!.Calories);
            Assert.Equal(40m, day.Goal.ProteinGrams);
            Assert.Equal(35m, day.Goal.FatGrams);
            Assert.Equal(60m, day.Goal.CarbohydratesGrams);
            Assert.Equal(500m, day.Remaining!.Calories);
            Assert.Equal(0m, day.Consumed.Calories);
            Assert.Empty(day.Entries);
            Assert.Equal(1, await ReadScalarAsync(restored, "SELECT COUNT(*) FROM Users WHERE IsLegacyLocal=1"));
            Assert.Equal(1, await ReadScalarAsync(restored,
                "SELECT LegacyLabelPhotosImported FROM Users WHERE IsLegacyLocal=1"));
            Assert.Equal(1, await ReadScalarAsync(restored,
                "SELECT COUNT(*) FROM Products p JOIN Users u ON u.Id=p.UserId WHERE u.IsLegacyLocal=1"));
            Assert.Equal(2, await ReadScalarAsync(restored, "SELECT COUNT(*) FROM LabelPhotos"));
            Assert.Equal(0, await ReadScalarAsync(restored, "SELECT COUNT(*) FROM SavedDishes"));
            Assert.Equal(0, await ReadScalarAsync(restored, "SELECT COUNT(*) FROM MealEntryAdjustments"));
            string currentPhotos = await ReadPhotoRowsAsync(restored);
            if (registeredPhotos is null)
            {
                registeredPhotos = currentPhotos;
            }
            else
            {
                Assert.Equal(registeredPhotos, currentPhotos);
            }
            await AssertLatestSchemaAsync(restored);
        }

        Assert.Equal(sourceSnapshot, files.SourceSnapshot());
        Assert.Equal(backupSnapshot, files.BackupSnapshot());
        await DataBackup.VerifyAsync(files.BackupPath);
        Assert.Equal(backupSnapshot, files.BackupSnapshot());
    }

    [Fact]
    public async Task LegacyBackup_WithoutStartupMigrationsReportsNotReadyInsteadOfHealthy()
    {
        using RecoveryFixture files = new();
        await CreateLegacySourceAsync(files);
        Dictionary<string, string> sourceSnapshot = files.SourceSnapshot();
        await files.BackupAndRestoreAsync();
        Dictionary<string, string> backupSnapshot = files.BackupSnapshot();
        string[] migrationsBefore = await ReadAppliedMigrationsAsync(files.RestoredDatabasePath);
        Assert.Equal(LegacyMigration, migrationsBefore[^1]);

        await using (TestApiFactory restored = files.CreateFactory(restored: true, applyMigrations: false))
        {
            using HttpClient client = restored.CreateClient();
            using HttpResponseMessage live = await client.GetAsync("/health/live");
            using HttpResponseMessage ready = await client.GetAsync("/health/ready");

            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal(0, await ReadScalarAsync(restored,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Users'"));
            Assert.Equal(migrationsBefore, await ReadAppliedMigrationsAsync(files.RestoredDatabasePath));
        }

        Assert.Equal(migrationsBefore, await ReadAppliedMigrationsAsync(files.RestoredDatabasePath));
        Assert.Equal(sourceSnapshot, files.SourceSnapshot());
        Assert.Equal(backupSnapshot, files.BackupSnapshot());
    }

    private static async Task AssertCurrentStateAsync(HttpClient client, CurrentState expected)
    {
        using HttpResponseMessage ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        DailyProgressResponse day = await GetDayAsync(client);
        Assert.Equal(expected.Day.Goal, day.Goal);
        Assert.Equal(expected.Day.Consumed, day.Consumed);
        Assert.Equal(expected.Day.Remaining, day.Remaining);
        Assert.Equal(expected.Day.Exceeded, day.Exceeded);
        Assert.Equal(expected.Day.Entries, day.Entries);
        foreach (MealEntryResponse entry in day.Entries)
        {
            using HttpResponseMessage response = await client.GetAsync($"/api/meal-entries/{entry.Id}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(entry, await ReadAsync<MealEntryResponse>(response));
            Assert.Equal($"\"{entry.Revision}\"", response.Headers.ETag!.Tag);
        }
        using HttpResponseMessage deleted = await client.GetAsync($"/api/meal-entries/{expected.DeletedEntryId}");
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        Assert.Equal(expected.FrozenSession, await client.GetStringAsync($"/api/meal-sessions/{expected.DiarySession.Id}"));
        ConfirmMealSessionResponse diary = await ConfirmAsync(client, expected.DiarySession);
        Assert.Equal("AlreadyConfirmed", diary.Outcome);
        Assert.Equal(expected.Day.Entries, diary.Entries);
        ConfirmMealSessionResponse dish = await ConfirmAsync(client, expected.DishSession);
        Assert.Equal("AlreadyConfirmed", dish.Outcome);
        Assert.Equal(expected.SavedDish, dish.SavedDish);
        Assert.Empty(dish.Entries);
        SavedDishResponse[] saved = await client.GetFromJsonAsync<SavedDishResponse[]>("/api/saved-dishes")
            ?? throw new InvalidDataException();
        Assert.Equal(expected.SavedDish, Assert.Single(saved));
        Assert.Equal(expected.SavedDishDetail, await client.GetStringAsync($"/api/saved-dishes/{expected.SavedDish.Id}"));
        Assert.Equal(expected.Products, await client.GetStringAsync(ProductUrl("Этикетка из копии")));
        await AssertPhotoAsync(client, expected.PhotoReference);
    }

    private static async Task<LegacyState> CreateLegacySourceAsync(RecoveryFixture files)
    {
        string referencedFileName = $"{Guid.NewGuid():N}.png";
        string unreferencedFileName = $"{Guid.NewGuid():N}.png";
        string reference = $"label-photo:{referencedFileName}";
        await File.WriteAllBytesAsync(Path.Combine(files.SourcePhotosPath, referencedFileName), PhotoContent);
        await File.WriteAllBytesAsync(Path.Combine(files.SourcePhotosPath, unreferencedFileName), PhotoContent);
        await using NutriFlowDbContext database = files.CreateSourceContext();
        await database.Database.MigrateAsync(LegacyMigration);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Products
                (Name, NormalizedName, Calories, ProteinGrams, FatGrams,
                 CarbohydratesGrams, SourceKind, SourceQuality, SourceName,
                 SourceReference, Barcode)
            VALUES ({"Старая этикетка"}, {"СТАРАЯ ЭТИКЕТКА"}, {"121"}, {"17"}, {"5"},
                    {"3"}, {(int)NutritionSourceKind.LabelPhoto}, {(int)DataQuality.Verified},
                    {"Reviewed nutrition label"}, {reference}, NULL)
            """);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO DailyGoals (Date, Calories, ProteinGrams, FatGrams, CarbohydratesGrams)
            VALUES ({MealDate}, {"500"}, {"40"}, {"35"}, {"60"})
            """);
        return new LegacyState(reference, $"label-photo:{unreferencedFileName}");
    }

    private static async Task<MealSessionResponse> CreateSessionAsync(HttpClient client,
        IReadOnlyList<string> messages, MealSessionPurpose purpose = MealSessionPurpose.Diary)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/meal-sessions",
            new CreateMealSessionRequest(messages, MealDate, purpose));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        MealSessionResponse session = await ReadAsync<MealSessionResponse>(response);
        Assert.True(session.CanConfirm);
        return session;
    }

    private static async Task<ConfirmMealSessionResponse> ConfirmAsync(HttpClient client, MealSessionResponse session)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/meal-sessions/{session.Id}/confirm",
            new ConfirmMealSessionRequest(session.PreviewToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<ConfirmMealSessionResponse>(response);
    }

    private static async Task<HttpResponseMessage> MutateEntryAsync(HttpClient client, HttpMethod method,
        int id, decimal? weight = null)
    {
        using HttpRequestMessage request = new(method, $"/api/meal-entries/{id}");
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        if (weight is not null)
        {
            request.Content = JsonContent.Create(new UpdateMealEntryRequest(weight));
        }
        return await client.SendAsync(request);
    }

    private static async Task<DailyProgressResponse> GetDayAsync(HttpClient client) =>
        await client.GetFromJsonAsync<DailyProgressResponse>($"/api/daily-progress/{MealDate:yyyy-MM-dd}")
        ?? throw new InvalidDataException();

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) where T : class =>
        await response.Content.ReadFromJsonAsync<T>() ?? throw new InvalidDataException();

    private static string ProductUrl(string name) => $"/api/products?name={Uri.EscapeDataString(name)}";

    private static async Task AssertPhotoAsync(HttpClient client, string reference)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/label-photos/{reference["label-photo:".Length..]}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(PhotoContent, await response.Content.ReadAsByteArrayAsync());
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.True(response.Headers.CacheControl.NoStore);
    }

    private static async Task AssertLatestSchemaAsync(TestApiFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        NutriFlowDbContext database = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
        Assert.Empty(await database.Database.GetPendingMigrationsAsync());
        Assert.Null(await database.Database.SqlQueryRaw<string>(
            "SELECT CAST(\"table\" AS TEXT) AS Value FROM pragma_foreign_key_check").FirstOrDefaultAsync());
    }

    private static async Task<long> ReadScalarAsync(TestApiFactory factory, string sql)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        NutriFlowDbContext database = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
        await using SqliteConnection connection = new(database.Database.GetConnectionString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadPhotoRowsAsync(TestApiFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        NutriFlowDbContext database = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
        await using SqliteConnection connection = new(database.Database.GetConnectionString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT FileName, UserId, RegisteredAtUtc FROM LabelPhotos ORDER BY FileName";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        List<string> rows = [];
        while (await reader.ReadAsync())
        {
            rows.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}");
        }
        return string.Join('\n', rows);
    }

    private static async Task<string[]> ReadAppliedMigrationsAsync(string databasePath)
    {
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        List<string> migrations = [];
        while (await reader.ReadAsync())
        {
            migrations.Add(reader.GetString(0));
        }
        return migrations.ToArray();
    }

    private sealed record CurrentState(MealSessionResponse DiarySession, MealSessionResponse DishSession,
        SavedDishResponse SavedDish, int DeletedEntryId, string PhotoReference, DailyProgressResponse Day,
        string FrozenSession, string SavedDishDetail, string Products);

    private sealed record LegacyState(string PhotoReference, string UnreferencedPhotoReference);

    private sealed class RecoveryFixture : IDisposable
    {
        private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"nutriflow-backup-recovery-{Guid.NewGuid():N}");
        private string SourcePath => Path.Combine(_rootPath, "source");
        private string RestoredPath => Path.Combine(_rootPath, "restored");
        public string SourcePhotosPath => Path.Combine(SourcePath, "label-photos");
        public string RestoredPhotosPath => Path.Combine(RestoredPath, "label-photos");
        public string RestoredDatabasePath => Path.Combine(RestoredPath, "nutriflow.db");
        public string BackupPath => Path.Combine(_rootPath, "backup");

        public RecoveryFixture()
        {
            Directory.CreateDirectory(SourcePhotosPath);
        }

        public TestApiFactory CreateFactory(bool restored, bool seedDemoData = false, bool applyMigrations = true)
        {
            string storagePath = restored ? RestoredPath : SourcePath;
            return new TestApiFactory(applyMigrations: applyMigrations, seedDemoData: seedDemoData,
                databasePath: Path.Combine(storagePath, "nutriflow.db"), environment: "Production",
                labelPhotoPath: Path.Combine(storagePath, "label-photos"));
        }

        public NutriFlowDbContext CreateSourceContext() => new(new DbContextOptionsBuilder<NutriFlowDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(SourcePath, "nutriflow.db"),
                Pooling = false
            }.ToString()).Options);

        public async Task BackupAndRestoreAsync()
        {
            TestDatabasePool.Clear(Path.Combine(SourcePath, "nutriflow.db"));
            await DataBackup.CreateAsync(Path.Combine(SourcePath, "nutriflow.db"), SourcePhotosPath, BackupPath);
            Assert.False(Directory.Exists(RestoredPath));
            await DataBackup.RestoreAsync(BackupPath, RestoredPath);
        }

        public Dictionary<string, string> SourceSnapshot() => Snapshot(SourcePath);
        public Dictionary<string, string> BackupSnapshot() => Snapshot(BackupPath);

        private static Dictionary<string, string> Snapshot(string directoryPath) =>
            Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .ToDictionary(path => Path.GetRelativePath(directoryPath, path),
                    path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

        public void Dispose()
        {
            TestDatabasePool.Clear(Path.Combine(SourcePath, "nutriflow.db"));
            TestDatabasePool.Clear(RestoredDatabasePath);
            Directory.Delete(_rootPath, recursive: true);
        }
    }
}
