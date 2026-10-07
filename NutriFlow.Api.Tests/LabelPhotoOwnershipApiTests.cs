using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.LabelPhotos;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Api.Tests;

public sealed class LabelPhotoOwnershipApiTests
{
    [Fact]
    public async Task AnalyzeLabel_RegistersUploadedPhotoForLocalOwnerBeforeProductReview()
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        await using var uploadFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<INutritionLabelReader>();
                services.AddSingleton<INutritionLabelReader, StaticLabelReader>();
            }));
        using HttpClient client = uploadFactory.CreateClient();
        byte[] content = CreatePhotoContent("image/png");
        using MultipartFormDataContent form = new();
        using ByteArrayContent file = new(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "photo", "label.png");

        using HttpResponseMessage response = await client.PostAsync("/api/labels/analyze", form);
        NutritionLabelDraftResponse draft = Assert.IsType<NutritionLabelDraftResponse>(
            await response.Content.ReadFromJsonAsync<NutritionLabelDraftResponse>());
        using HttpResponseMessage photo = await client.GetAsync(GetPhotoUrl(draft.PhotoReference));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(draft.CanCreateProduct);
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        Assert.Equal(content, await photo.Content.ReadAsByteArrayAsync());
        AssertPrivateCache(photo);
        Assert.Equal(1, await ReadScalarAsync(factory, """
            SELECT COUNT(*) FROM LabelPhotos p
            JOIN Users u ON u.Id = p.UserId WHERE u.IsLegacyLocal = 1
            """));
        Assert.Equal(0, await ReadScalarAsync(factory, "SELECT COUNT(*) FROM Products"));
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    public async Task GetPhoto_ForLocalOwner_ReturnsBytesMediaTypeAndPrivateRangeResponse(
        string mediaType)
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        byte[] content = CreatePhotoContent(mediaType);
        string reference = await SavePhotoAsync(factory, content, mediaType);
        string url = GetPhotoUrl(reference);

        using HttpResponseMessage response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
        AssertPrivateCache(response);

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(1, 2);
        using HttpResponseMessage range = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(mediaType, range.Content.Headers.ContentType?.MediaType);
        Assert.Equal(content[1..3], await range.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, range.Content.Headers.ContentRange?.From);
        Assert.Equal(2, range.Content.Headers.ContentRange?.To);
        Assert.Equal(content.Length, range.Content.Headers.ContentRange?.Length);
        AssertPrivateCache(range);
    }

    [Fact]
    public async Task GetPhoto_ForAnotherOwner_IsIndistinguishableFromUnknownPhoto()
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        Guid foreignOwner = await AddForeignOwnerAsync(factory);
        string foreignReference = await SavePhotoAsync(
            factory, CreatePhotoContent("image/png"), "image/png", foreignOwner);
        string unknownReference = $"label-photo:{Guid.NewGuid():N}.png";

        using HttpResponseMessage foreign = await client.GetAsync(GetPhotoUrl(foreignReference));
        using HttpResponseMessage unknown = await client.GetAsync(GetPhotoUrl(unknownReference));
        string foreignBody = await foreign.Content.ReadAsStringAsync();
        string unknownBody = await unknown.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/problem+json", foreign.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ReadProblem(foreignBody), ReadProblem(unknownBody));
        Assert.DoesNotContain(foreignReference, foreignBody, StringComparison.Ordinal);
        Assert.DoesNotContain(foreignOwner.ToString(), foreignBody, StringComparison.OrdinalIgnoreCase);
        AssertPrivateCache(foreign);
        AssertPrivateCache(unknown);
    }

    [Fact]
    public async Task CreateProductFromAnotherOwnersPhoto_RejectsWithoutChangingCatalog()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid foreignOwner = await AddForeignOwnerAsync(factory);
        string foreignReference = await SavePhotoAsync(
            factory, CreatePhotoContent("image/png"), "image/png", foreignOwner);
        string before = await ReadRowsSnapshotAsync(factory, "SELECT * FROM Products ORDER BY Id");

        using HttpResponseMessage foreign = await client.PostAsJsonAsync(
            "/api/products/from-label", CreateProductRequest(foreignReference));
        using HttpResponseMessage unknown = await client.PostAsJsonAsync(
            "/api/products/from-label",
            CreateProductRequest($"label-photo:{Guid.NewGuid():N}.png"));

        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using JsonDocument foreignProblem = JsonDocument.Parse(
            await foreign.Content.ReadAsStringAsync());
        using JsonDocument unknownProblem = JsonDocument.Parse(
            await unknown.Content.ReadAsStringAsync());
        Assert.Equal(
            foreignProblem.RootElement.GetProperty("errors").GetRawText(),
            unknownProblem.RootElement.GetProperty("errors").GetRawText());
        Assert.Equal(before, await ReadRowsSnapshotAsync(factory, "SELECT * FROM Products ORDER BY Id"));
        Assert.Equal(0, await ReadScalarAsync(factory, "SELECT COUNT(*) FROM ProductAliases"));
    }

    [Fact]
    public async Task GetPhoto_WithServerSelectedOwner_OnlyReturnsThatOwnersFiles()
    {
        await using TestApiFactory factory = new(seedDemoData: false);
        using HttpClient localClient = factory.CreateClient();
        Guid foreignOwner = await AddForeignOwnerAsync(factory);
        byte[] content = CreatePhotoContent("image/png");
        string localReference = await SavePhotoAsync(factory, content, "image/png");
        string foreignReference = await SavePhotoAsync(
            factory, content, "image/png", foreignOwner);
        string storagePath = await GetStoragePathAsync(factory, localReference);

        await using var selectedOwnerFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<LabelPhotoStore>();
                services.AddScoped(provider => new LabelPhotoStore(
                    provider.GetRequiredService<NutriFlowDbContext>(),
                    storagePath,
                    foreignOwner));
            }));
        using HttpClient selectedOwnerClient = selectedOwnerFactory.CreateClient();
        using HttpResponseMessage own = await selectedOwnerClient.GetAsync(
            GetPhotoUrl(foreignReference));
        using HttpResponseMessage other = await selectedOwnerClient.GetAsync(
            GetPhotoUrl(localReference));

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(content, await own.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        AssertPrivateCache(own);
        AssertPrivateCache(other);
    }

    [Fact]
    public async Task StartupMigration_RegistersLegacyPhotoOnceAndPreservesProductReference()
    {
        await using TemporaryPhotoDatabase files = new();
        byte[] content = CreatePhotoContent("image/png");
        string legacyFileName = $"{Guid.NewGuid():N}.png";
        string legacyReference = $"label-photo:{legacyFileName}";
        await File.WriteAllBytesAsync(Path.Combine(files.PhotoPath, legacyFileName), content);

        await using (NutriFlowDbContext dbContext = files.CreateContext())
        {
            await dbContext.Database.MigrateAsync("20261004175726_AddProductOwnership");
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Products
                    (Name, NormalizedName, Calories, ProteinGrams, FatGrams,
                     CarbohydratesGrams, SourceKind, SourceQuality, SourceName,
                     SourceReference, Barcode, UserId)
                SELECT {"Старая этикетка"}, {"СТАРАЯ ЭТИКЕТКА"}, {"121"}, {"17"}, {"5"},
                    {"3"}, {(int)NutritionSourceKind.LabelPhoto}, {(int)DataQuality.Verified},
                    {"Reviewed nutrition label"}, {legacyReference}, NULL, Id
                FROM Users WHERE IsLegacyLocal = 1
                """);
        }

        await using (TestApiFactory factory = new(
                         seedDemoData: false,
                         databasePath: files.DatabasePath,
                         labelPhotoPath: files.PhotoPath))
        {
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage photo = await client.GetAsync(GetPhotoUrl(legacyReference));
            ProductResponse[]? products = await client.GetFromJsonAsync<ProductResponse[]>(
                $"/api/products?name={Uri.EscapeDataString("Старая этикетка")}");

            Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
            Assert.Equal(content, await photo.Content.ReadAsByteArrayAsync());
            ProductResponse product = Assert.Single(Assert.IsType<ProductResponse[]>(products));
            Assert.Equal(legacyReference, product.SourceReference);
            Assert.Equal("LabelPhoto", product.SourceKind);
            Assert.Equal("Verified", product.DataQuality);
            Assert.Equal(121m, product.Calories);
            Assert.Equal(1, await ReadScalarAsync(factory, "SELECT COUNT(*) FROM LabelPhotos"));
            Assert.Equal(1, await ReadScalarAsync(factory,
                "SELECT LegacyLabelPhotosImported FROM Users WHERE IsLegacyLocal = 1"));
        }

        string orphanFileName = $"{Guid.NewGuid():N}.png";
        await File.WriteAllBytesAsync(Path.Combine(files.PhotoPath, orphanFileName), content);

        await using (TestApiFactory restarted = new(
                         seedDemoData: false,
                         databasePath: files.DatabasePath,
                         labelPhotoPath: files.PhotoPath))
        {
            using HttpClient client = restarted.CreateClient();
            using HttpResponseMessage legacy = await client.GetAsync(GetPhotoUrl(legacyReference));
            using HttpResponseMessage orphan = await client.GetAsync(
                GetPhotoUrl($"label-photo:{orphanFileName}"));

            Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
            Assert.Equal(content, await legacy.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound, orphan.StatusCode);
            Assert.Equal(1, await ReadScalarAsync(restarted, "SELECT COUNT(*) FROM LabelPhotos"));
        }

        Assert.Equal(content, await File.ReadAllBytesAsync(
            Path.Combine(files.PhotoPath, legacyFileName)));
        Assert.Equal(content, await File.ReadAllBytesAsync(
            Path.Combine(files.PhotoPath, orphanFileName)));
    }

    [Fact]
    public async Task StartupWithoutMigrations_DoesNotImportUnregisteredFiles()
    {
        await using TemporaryPhotoDatabase files = new();
        string fileName = $"{Guid.NewGuid():N}.png";
        await File.WriteAllBytesAsync(
            Path.Combine(files.PhotoPath, fileName), CreatePhotoContent("image/png"));
        await using (NutriFlowDbContext dbContext = files.CreateContext())
        {
            await dbContext.Database.MigrateAsync();
        }

        await using TestApiFactory factory = new(
            applyMigrations: false,
            seedDemoData: false,
            databasePath: files.DatabasePath,
            labelPhotoPath: files.PhotoPath);
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(
            GetPhotoUrl($"label-photo:{fileName}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await ReadScalarAsync(factory, "SELECT COUNT(*) FROM LabelPhotos"));
        Assert.Equal(0, await ReadScalarAsync(factory,
            "SELECT LegacyLabelPhotosImported FROM Users WHERE IsLegacyLocal = 1"));
        Assert.True(File.Exists(Path.Combine(files.PhotoPath, fileName)));
    }

    private static async Task<string> SavePhotoAsync(
        TestApiFactory factory,
        byte[] content,
        string mediaType,
        Guid? ownerId = null)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        LabelPhotoStore localStore = scope.ServiceProvider.GetRequiredService<LabelPhotoStore>();
        LabelPhotoStore store = localStore;

        if (ownerId is not null)
        {
            store = new LabelPhotoStore(
                scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>(),
                scope.ServiceProvider.GetRequiredService<IConfiguration>()["Storage:LabelPhotosPath"]!,
                ownerId.Value);
        }

        return await store.SaveAsync(LabelPhotoValidator.Validate(content, mediaType));
    }

    private static async Task<Guid> AddForeignOwnerAsync(TestApiFactory factory)
    {
        Guid ownerId = Guid.NewGuid();
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        NutriFlowDbContext dbContext = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO Users (Id, CreatedAtUtc, IsLegacyLocal, LegacyLabelPhotosImported)
            VALUES ({ownerId}, {DateTimeOffset.UtcNow}, 0, 0)
            """);
        return ownerId;
    }

    private static async Task<string> GetStoragePathAsync(TestApiFactory factory, string reference)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        LabelPhotoStore store = scope.ServiceProvider.GetRequiredService<LabelPhotoStore>();
        StoredLabelPhoto photo = Assert.IsType<StoredLabelPhoto>(await store.FindAsync(reference));
        return Path.GetDirectoryName(photo.FilePath)!;
    }

    private static async Task<long> ReadScalarAsync(TestApiFactory factory, string sql)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        NutriFlowDbContext dbContext = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
        await using SqliteConnection connection = new(dbContext.Database.GetConnectionString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadRowsSnapshotAsync(TestApiFactory factory, string sql)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        NutriFlowDbContext dbContext = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
        await using SqliteConnection connection = new(dbContext.Database.GetConnectionString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        List<object[]> rows = new();

        while (await reader.ReadAsync())
        {
            object[] values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
        }

        return JsonSerializer.Serialize(rows);
    }

    private static CreateLabelProductRequest CreateProductRequest(string reference)
    {
        return new CreateLabelProductRequest(
            reference, NutritionBasis.Per100Grams, "Чужая этикетка", 121m, 17m, 5m, 3m);
    }

    private static string GetPhotoUrl(string reference)
    {
        return $"/api/label-photos/{reference["label-photo:".Length..]}";
    }

    private static (string? Title, string? Detail, int Status) ReadProblem(string json)
    {
        using JsonDocument problem = JsonDocument.Parse(json);
        JsonElement root = problem.RootElement;
        return (root.GetProperty("title").GetString(), root.GetProperty("detail").GetString(),
            root.GetProperty("status").GetInt32());
    }

    private static void AssertPrivateCache(HttpResponseMessage response)
    {
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl.Private);
        Assert.True(response.Headers.CacheControl.NoStore);
    }

    private static byte[] CreatePhotoContent(string mediaType)
    {
        return mediaType switch
        {
            "image/png" => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            "image/jpeg" => [0xFF, 0xD8, 0xFF, 0x01],
            "image/webp" => [0x52, 0x49, 0x46, 0x46, 0x04, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50],
            _ => throw new ArgumentOutOfRangeException(nameof(mediaType))
        };
    }

    private sealed class TemporaryPhotoDatabase : IAsyncDisposable
    {
        private readonly string _directoryPath = Path.Combine(
            Path.GetTempPath(), $"nutriflow-label-photo-api-{Guid.NewGuid():N}");

        public TemporaryPhotoDatabase()
        {
            Directory.CreateDirectory(PhotoPath);
        }

        public string DatabasePath => Path.Combine(_directoryPath, "nutriflow.db");
        public string PhotoPath => Path.Combine(_directoryPath, "label-photos");

        public NutriFlowDbContext CreateContext()
        {
            return new NutriFlowDbContext(new DbContextOptionsBuilder<NutriFlowDbContext>()
                .UseSqlite($"Data Source={DatabasePath}")
                .Options);
        }

        public ValueTask DisposeAsync()
        {
            TestDatabasePool.Clear(DatabasePath);
            Directory.Delete(_directoryPath, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StaticLabelReader : INutritionLabelReader
    {
        public Task<NutritionLabelDraft> ReadAsync(
            ReadOnlyMemory<byte> image,
            string mediaType,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new NutritionLabelDraft(
                "Творог", NutritionBasis.Per100Grams, 121m, 17m, 5m, 3m, []));
        }
    }
}
