using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NutriFlow.Infrastructure.LabelPhotos;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class LabelPhotoOwnershipTests
{
    private static readonly byte[] PhotoContent = { 0xFF, 0xD8, 0xFF, 0x00 };

    [Theory]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".png", "image/png")]
    [InlineData(".webp", "image/webp")]
    public async Task SaveAsync_PersistsOwnerAndSurvivesRestart(string extension, string mediaType)
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        using PhotoDirectory directory = new();
        string reference;
        Guid ownerId;

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            ownerId = await GetLocalOwnerAsync(context);
            LabelPhotoStore store = new(context, directory.Path);
            reference = await store.SaveAsync(new ValidatedLabelPhoto(PhotoContent, mediaType, extension));
            string fileName = reference["label-photo:".Length..];
            Assert.True(Guid.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(fileName), "N", out _));
            Assert.Equal(ownerId.ToString().ToUpperInvariant(), await context.Database.SqlQuery<string>($"""
                SELECT "UserId" AS "Value" FROM "LabelPhotos" WHERE "FileName" = {fileName}
                """).SingleAsync());
            Assert.NotEqual(default, DateTimeOffset.Parse(await context.Database.SqlQuery<string>($"""
                SELECT "RegisteredAtUtc" AS "Value" FROM "LabelPhotos" WHERE "FileName" = {fileName}
                """).SingleAsync()));
        }

        await using NutriFlowDbContext restarted = database.CreateContext();
        StoredLabelPhoto photo = Assert.IsType<StoredLabelPhoto>(
            await new LabelPhotoStore(restarted, directory.Path, ownerId).FindAsync(reference));
        Assert.Equal(mediaType, photo.MediaType);
        Assert.Equal(PhotoContent, await File.ReadAllBytesAsync(photo.FilePath));
    }

    [Fact]
    public async Task FindAsync_TwoOwners_CannotReadEachOthersPhotos()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        using PhotoDirectory directory = new();
        await using NutriFlowDbContext firstContext = database.CreateContext();
        await using NutriFlowDbContext secondContext = database.CreateContext();
        Guid secondOwner = await AddOwnerAsync(secondContext);
        LabelPhotoStore firstStore = new(firstContext, directory.Path);
        LabelPhotoStore secondStore = new(secondContext, directory.Path, secondOwner);
        string firstReference = await firstStore.SaveAsync(CreatePhoto());
        string secondReference = await secondStore.SaveAsync(CreatePhoto());

        Assert.True(await firstStore.ContainsAsync(firstReference));
        Assert.True(await secondStore.ContainsAsync(secondReference));
        Assert.Null(await firstStore.FindAsync(secondReference));
        Assert.Null(await secondStore.FindAsync(firstReference));
        Assert.False(await firstStore.ContainsAsync(secondReference));
        Assert.False(await secondStore.ContainsAsync(firstReference));
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
    }

    [Fact]
    public async Task FindAsync_UnregisteredOrMissingFile_ReturnsNull()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        using PhotoDirectory directory = new();
        await using NutriFlowDbContext context = database.CreateContext();
        LabelPhotoStore store = new(context, directory.Path);
        string unregisteredName = $"{Guid.NewGuid():N}.jpg";
        await File.WriteAllBytesAsync(System.IO.Path.Combine(directory.Path, unregisteredName), PhotoContent);

        Assert.Null(await store.FindAsync($"label-photo:{unregisteredName}"));
        Assert.False(await store.ContainsAsync($"label-photo:{unregisteredName}"));
        string reference = await store.SaveAsync(CreatePhoto());
        File.Delete(Assert.IsType<StoredLabelPhoto>(await store.FindAsync(reference)).FilePath);
        Assert.Null(await store.FindAsync(reference));
        Assert.False(await store.ContainsAsync(reference));
        Assert.Equal(1, await PhotoCountAsync(context));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("LABEL-PHOTO:0123456789abcdef0123456789abcdef.jpg")]
    [InlineData("label-photo:../0123456789abcdef0123456789abcdef.jpg")]
    [InlineData("label-photo:..\\0123456789abcdef0123456789abcdef.jpg")]
    [InlineData("label-photo:nested/0123456789abcdef0123456789abcdef.jpg")]
    [InlineData("label-photo:nested\\0123456789abcdef0123456789abcdef.jpg")]
    [InlineData("label-photo:01234567-89ab-cdef-0123-456789abcdef.jpg")]
    [InlineData("label-photo:0123456789ABCDEF0123456789ABCDEF.jpg")]
    [InlineData("label-photo:0123456789abcdef0123456789abcdef.JPG")]
    [InlineData("label-photo:0123456789abcdef0123456789abcdef.txt")]
    [InlineData("label-photo:existing.jpg")]
    [InlineData("label-photo:0123456789abcdef0123456789abcdef.jpg:other")]
    public async Task FindAsync_InvalidReference_ReturnsNull(string? reference)
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        using PhotoDirectory directory = new();
        await using NutriFlowDbContext context = database.CreateContext();
        LabelPhotoStore store = new(context, directory.Path);

        Assert.Null(await store.FindAsync(reference!));
        Assert.False(await store.ContainsAsync(reference!));
    }

    [Theory]
    [InlineData("existing.jpg")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef.jpg")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF.jpg")]
    [InlineData("0123456789abcdef0123456789abcdef.JPG")]
    [InlineData("0123456789abcdef0123456789abcdef.txt")]
    [InlineData("nested/0123456789abcdef0123456789abcdef.jpg")]
    public async Task FindAsync_InvalidFileNameWithMetadataAndFile_StillReturnsNull(string fileName)
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        using PhotoDirectory directory = new();
        string filePath = System.IO.Path.Combine(directory.Path, fileName);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, PhotoContent);
        await using NutriFlowDbContext context = database.CreateContext();
        await InsertMetadataAsync(context, fileName, await GetLocalOwnerAsync(context));
        LabelPhotoStore store = new(context, directory.Path);

        Assert.Null(await store.FindAsync($"label-photo:{fileName}"));
        Assert.False(await store.ContainsAsync($"label-photo:{fileName}"));
        Assert.Equal(1, await PhotoCountAsync(context));
        Assert.Equal(PhotoContent, await File.ReadAllBytesAsync(filePath));
    }

    [Fact]
    public async Task SaveAsync_UnknownOwner_DoesNotLeaveFileOrMetadata()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        using PhotoDirectory directory = new();
        await using NutriFlowDbContext context = database.CreateContext();
        LabelPhotoStore store = new(context, directory.Path, Guid.NewGuid());

        await Assert.ThrowsAsync<DbUpdateException>(() => store.SaveAsync(CreatePhoto()));

        Assert.Empty(Directory.GetFiles(directory.Path));
        Assert.Equal(0, await PhotoCountAsync(context));
    }

    [Theory]
    [InlineData(".exe", null)]
    [InlineData(".exe", "image/jpeg")]
    [InlineData(".jpg", "image/png")]
    [InlineData("/../secret.jpg", "image/jpeg")]
    public async Task SaveAsync_InvalidPublicPhotoFormat_DoesNotCreateFileOrMetadata(
        string extension,
        string? mediaType)
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        using PhotoDirectory directory = new();
        await using NutriFlowDbContext context = database.CreateContext();
        LabelPhotoStore store = new(context, directory.Path);
        ValidatedLabelPhoto photo = new(PhotoContent, mediaType!, extension);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(photo));

        Assert.Empty(Directory.GetFiles(directory.Path));
        Assert.Equal(0, await PhotoCountAsync(context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_MetadataFails_RemovesOnlyOwnFileAndAllowsRetry(bool cancelSave)
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        using PhotoDirectory directory = new();
        string existingReference;
        string existingPath;
        await using (NutriFlowDbContext context = database.CreateContext())
        {
            LabelPhotoStore store = new(context, directory.Path);
            existingReference = await store.SaveAsync(CreatePhoto());
            existingPath = Assert.IsType<StoredLabelPhoto>(await store.FindAsync(existingReference)).FilePath;
        }

        using CancellationTokenSource cancellation = new();
        PhotoSaveFailureInterceptor interceptor = new(directory.Path, cancelSave ? cancellation : null);
        await using NutriFlowDbContext failedContext = CreateInterceptedContext(database, interceptor);
        LabelPhotoStore failedStore = new(failedContext, directory.Path);

        if (cancelSave)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => failedStore.SaveAsync(CreatePhoto(), cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => failedStore.SaveAsync(CreatePhoto()));
        }

        Assert.True(interceptor.FileWasClosed);
        Assert.Equal(existingPath, Assert.Single(Directory.GetFiles(directory.Path)));
        Assert.Equal(PhotoContent, await File.ReadAllBytesAsync(existingPath));
        Assert.Equal(1, await PhotoCountAsync(failedContext));
        Assert.True(await failedStore.ContainsAsync(existingReference));
        Assert.Null(failedContext.Database.CurrentTransaction);
        string retriedReference = await failedStore.SaveAsync(CreatePhoto());
        Assert.True(await failedStore.ContainsAsync(retriedReference));
        Assert.Equal(2, await PhotoCountAsync(failedContext));
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
    }

    [Fact]
    public async Task ImportAsync_ImportsUnlinkedLegacyFilesWithoutChangingReferencesOrContent()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        using PhotoDirectory directory = new();
        string[] names = { $"{Guid.NewGuid():N}.jpg", $"{Guid.NewGuid():N}.png", $"{Guid.NewGuid():N}.webp" };
        foreach (string name in names)
        {
            await File.WriteAllBytesAsync(System.IO.Path.Combine(directory.Path, name), PhotoContent);
        }

        await using NutriFlowDbContext context = database.CreateContext();
        Guid otherOwner = await AddOwnerAsync(context);
        LabelPhotoStore legacyStore = new(context, directory.Path);
        Assert.Null(await legacyStore.FindAsync($"label-photo:{names[0]}"));

        await LegacyLabelPhotoImporter.ImportAsync(context, directory.Path);

        Assert.Equal(3, await PhotoCountAsync(context));
        Assert.Equal(1, await ImportedMarkerAsync(context));
        Assert.Equal(0, await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS \"Value\" FROM \"Products\"").SingleAsync());
        foreach (string name in names)
        {
            StoredLabelPhoto stored = Assert.IsType<StoredLabelPhoto>(await legacyStore.FindAsync($"label-photo:{name}"));
            Assert.Equal(System.IO.Path.Combine(directory.Path, name), stored.FilePath);
            Assert.Equal(PhotoContent, await File.ReadAllBytesAsync(stored.FilePath));
            Assert.Null(await new LabelPhotoStore(context, directory.Path, otherOwner).FindAsync($"label-photo:{name}"));
        }

        string[] firstSnapshot = await GetRowsAsync(context);
        await using NutriFlowDbContext restarted = database.CreateContext();
        await LegacyLabelPhotoImporter.ImportAsync(restarted, directory.Path);
        Assert.Equal(firstSnapshot, await GetRowsAsync(restarted));
        Assert.Equal(names.Order(), Directory.GetFiles(directory.Path).Select(System.IO.Path.GetFileName).Order());
    }

    [Fact]
    public async Task ImportAsync_WhenCompleted_DoesNotClaimLaterFilesOrForeignMetadata()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        using PhotoDirectory directory = new();
        await using NutriFlowDbContext context = database.CreateContext();
        Guid otherOwner = await AddOwnerAsync(context);
        LabelPhotoStore foreignStore = new(context, directory.Path, otherOwner);
        string foreignReference = await foreignStore.SaveAsync(CreatePhoto());
        string firstName = $"{Guid.NewGuid():N}.jpg";
        await File.WriteAllBytesAsync(System.IO.Path.Combine(directory.Path, firstName), PhotoContent);
        string[] foreignSnapshot = await GetRowsAsync(context);

        await LegacyLabelPhotoImporter.ImportAsync(context, directory.Path);
        string[] importedSnapshot = await GetRowsAsync(context);
        Assert.Contains(Assert.Single(foreignSnapshot), importedSnapshot);
        string laterName = $"{Guid.NewGuid():N}.jpg";
        await File.WriteAllBytesAsync(System.IO.Path.Combine(directory.Path, laterName), PhotoContent);
        await using NutriFlowDbContext restarted = database.CreateContext();
        await LegacyLabelPhotoImporter.ImportAsync(restarted, directory.Path);

        Assert.Equal(importedSnapshot, await GetRowsAsync(restarted));
        LabelPhotoStore localStore = new(restarted, directory.Path);
        Assert.Null(await localStore.FindAsync($"label-photo:{laterName}"));
        Assert.Null(await localStore.FindAsync(foreignReference));
        Assert.True(await new LabelPhotoStore(restarted, directory.Path, otherOwner).ContainsAsync(foreignReference));
        Assert.True(await localStore.ContainsAsync($"label-photo:{firstName}"));
        Assert.Equal(3, Directory.GetFiles(directory.Path).Length);
    }

    [Fact]
    public async Task ImportAsync_InitiallyEmptyDirectory_DoesNotClaimFutureUnregisteredFiles()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        using PhotoDirectory directory = new();
        await using NutriFlowDbContext context = database.CreateContext();
        await LegacyLabelPhotoImporter.ImportAsync(context, directory.Path);
        Assert.Equal(1, await ImportedMarkerAsync(context));
        string name = $"{Guid.NewGuid():N}.jpg";
        string filePath = System.IO.Path.Combine(directory.Path, name);
        await File.WriteAllBytesAsync(filePath, PhotoContent);
        await using NutriFlowDbContext restarted = database.CreateContext();

        await LegacyLabelPhotoImporter.ImportAsync(restarted, directory.Path);

        Assert.Equal(0, await PhotoCountAsync(restarted));
        Assert.Null(await new LabelPhotoStore(restarted, directory.Path).FindAsync($"label-photo:{name}"));
        Assert.Equal(PhotoContent, await File.ReadAllBytesAsync(filePath));
    }

    [Fact]
    public async Task ImportAsync_IgnoresInvalidNamesAndNestedFiles()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        using PhotoDirectory directory = new();
        string validName = $"{Guid.NewGuid():N}.jpg";
        string[] ignoredNames =
        {
            "existing.jpg", $"{Guid.NewGuid():D}.jpg", $"{Guid.NewGuid():N}.JPG",
            $"{Guid.NewGuid():N}".ToUpperInvariant() + ".png", $"{Guid.NewGuid():N}.txt"
        };
        await File.WriteAllBytesAsync(System.IO.Path.Combine(directory.Path, validName), PhotoContent);
        foreach (string name in ignoredNames)
        {
            await File.WriteAllBytesAsync(System.IO.Path.Combine(directory.Path, name), PhotoContent);
        }

        string nestedPath = System.IO.Path.Combine(directory.Path, "nested");
        Directory.CreateDirectory(nestedPath);
        string nestedFilePath = System.IO.Path.Combine(nestedPath, $"{Guid.NewGuid():N}.webp");
        await File.WriteAllBytesAsync(nestedFilePath, PhotoContent);
        await using NutriFlowDbContext context = database.CreateContext();

        await LegacyLabelPhotoImporter.ImportAsync(context, directory.Path);

        Assert.Equal(1, await PhotoCountAsync(context));
        Assert.True(await new LabelPhotoStore(context, directory.Path).ContainsAsync($"label-photo:{validName}"));
        Assert.Equal(ignoredNames.Length + 1, Directory.GetFiles(directory.Path).Length);
        Assert.Equal(PhotoContent, await File.ReadAllBytesAsync(nestedFilePath));
        foreach (string name in ignoredNames)
        {
            Assert.Equal(PhotoContent, await File.ReadAllBytesAsync(System.IO.Path.Combine(directory.Path, name)));
        }
    }

    [Fact]
    public async Task ImportAsync_WithCancelledRequest_DoesNotImportOrSetMarker()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        using PhotoDirectory directory = new();
        string filePath = System.IO.Path.Combine(directory.Path, $"{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(filePath, PhotoContent);
        await using NutriFlowDbContext context = database.CreateContext();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LegacyLabelPhotoImporter.ImportAsync(context, directory.Path, cancellation.Token));

        Assert.Equal(0, await PhotoCountAsync(context));
        Assert.Equal(0, await ImportedMarkerAsync(context));
        Assert.Equal(PhotoContent, await File.ReadAllBytesAsync(filePath));
    }

    [Fact]
    public async Task ImportAsync_StoragePathIsAFile_DoesNotSetMarker()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        using PhotoDirectory directory = new();
        string filePath = System.IO.Path.Combine(directory.Path, $"{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(filePath, PhotoContent);
        await using NutriFlowDbContext context = database.CreateContext();

        await Assert.ThrowsAsync<IOException>(
            () => LegacyLabelPhotoImporter.ImportAsync(context, filePath));

        Assert.Equal(0, await PhotoCountAsync(context));
        Assert.Equal(0, await ImportedMarkerAsync(context));
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(PhotoContent, await File.ReadAllBytesAsync(filePath));
    }

    [Fact]
    public async Task ImportAsync_WithoutLocalOwner_DoesNotClaimFiles()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        using PhotoDirectory directory = new();
        string filePath = System.IO.Path.Combine(directory.Path, $"{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(filePath, PhotoContent);
        await using NutriFlowDbContext context = database.CreateContext();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM \"Users\"");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => LegacyLabelPhotoImporter.ImportAsync(context, directory.Path));

        Assert.Equal(0, await PhotoCountAsync(context));
        Assert.Equal(0, await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS \"Value\" FROM \"Users\"").SingleAsync());
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(PhotoContent, await File.ReadAllBytesAsync(filePath));
    }

    [Fact]
    public async Task ImportAsync_CancelledAfterInsert_RollsBackRowsAndMarkerAndAllowsRetry()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        using PhotoDirectory directory = new();
        string[] names = { $"{Guid.NewGuid():N}.jpg", $"{Guid.NewGuid():N}.png" };
        foreach (string name in names)
        {
            await File.WriteAllBytesAsync(System.IO.Path.Combine(directory.Path, name), PhotoContent);
        }

        using CancellationTokenSource cancellation = new();
        ImportCancellationInterceptor interceptor = new(cancellation);
        await using NutriFlowDbContext context = CreateInterceptedContext(database, interceptor);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LegacyLabelPhotoImporter.ImportAsync(context, directory.Path, cancellation.Token));

        Assert.True(interceptor.InsertWasExecuted);
        Assert.Null(context.Database.CurrentTransaction);
        await using NutriFlowDbContext verification = database.CreateContext();
        Assert.Equal(0, await PhotoCountAsync(verification));
        Assert.Equal(0, await ImportedMarkerAsync(verification));
        await LegacyLabelPhotoImporter.ImportAsync(verification, directory.Path);
        Assert.Equal(2, await PhotoCountAsync(verification));
        Assert.Equal(1, await ImportedMarkerAsync(verification));
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
        foreach (string name in names)
        {
            Assert.Equal(PhotoContent, await File.ReadAllBytesAsync(System.IO.Path.Combine(directory.Path, name)));
        }
    }

    [Fact]
    public async Task Migration_PreservesUsersAndEnforcesMetadataConstraints()
    {
        await using TestDatabase database = new();
        await using NutriFlowDbContext context = database.CreateContext();
        await context.Database.MigrateAsync("20261004175726_AddProductOwnership");
        Guid localOwner = await GetLocalOwnerAsync(context);
        Guid otherOwner = await AddOwnerAsync(context);
        string[] before = await context.Database.SqlQueryRaw<string>("""
            SELECT "Id" || '|' || "CreatedAtUtc" || '|' || "IsLegacyLocal" AS "Value"
            FROM "Users" ORDER BY "Id"
            """).ToArrayAsync();

        await context.Database.MigrateAsync();

        Assert.Equal(before, await context.Database.SqlQueryRaw<string>("""
            SELECT "Id" || '|' || "CreatedAtUtc" || '|' || "IsLegacyLocal" AS "Value"
            FROM "Users" ORDER BY "Id"
            """).ToArrayAsync());
        Assert.Equal(0, await context.Database.SqlQueryRaw<int>(
            "SELECT SUM(\"LegacyLabelPhotosImported\") AS \"Value\" FROM \"Users\"").SingleAsync());
        string fileName = $"{Guid.NewGuid():N}.jpg";
        await InsertMetadataAsync(context, fileName, localOwner);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => InsertMetadataAsync(context, fileName, otherOwner));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => InsertMetadataAsync(context, $"{Guid.NewGuid():N}.jpg", Guid.NewGuid()));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Users\" WHERE \"Id\" = {localOwner}"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "LabelPhotos" ("FileName", "UserId", "RegisteredAtUtc")
                VALUES ({$"{Guid.NewGuid():N}.jpg"}, NULL, {DateTimeOffset.UtcNow})
                """));
        Assert.Equal(1, await PhotoCountAsync(context));
        Assert.Equal(2, await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS \"Value\" FROM \"Users\"").SingleAsync());
        Assert.Empty(await context.Database.SqlQueryRaw<string>(
            "SELECT \"table\" AS \"Value\" FROM pragma_foreign_key_check").ToArrayAsync());
        Assert.Equal("ok", await context.Database.SqlQueryRaw<string>(
            "SELECT integrity_check AS \"Value\" FROM pragma_integrity_check").SingleAsync());
    }

    private static ValidatedLabelPhoto CreatePhoto() => new(PhotoContent, "image/jpeg", ".jpg");

    private static async Task<Guid> GetLocalOwnerAsync(NutriFlowDbContext context)
    {
        return Guid.Parse(await context.Database.SqlQueryRaw<string>(
            "SELECT \"Id\" AS \"Value\" FROM \"Users\" WHERE \"IsLegacyLocal\" = 1").SingleAsync());
    }

    private static async Task<Guid> AddOwnerAsync(NutriFlowDbContext context)
    {
        Guid ownerId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Users" ("Id", "CreatedAtUtc", "IsLegacyLocal")
            VALUES ({ownerId}, {DateTimeOffset.UtcNow}, {false})
            """);
        return ownerId;
    }

    private static Task<int> InsertMetadataAsync(NutriFlowDbContext context, string fileName, Guid ownerId)
    {
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "LabelPhotos" ("FileName", "UserId", "RegisteredAtUtc")
            VALUES ({fileName}, {ownerId}, {DateTimeOffset.UtcNow})
            """);
    }

    private static Task<int> PhotoCountAsync(NutriFlowDbContext context) =>
        context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM \"LabelPhotos\"").SingleAsync();

    private static Task<int> ImportedMarkerAsync(NutriFlowDbContext context) =>
        context.Database.SqlQueryRaw<int>("""
            SELECT "LegacyLabelPhotosImported" AS "Value" FROM "Users" WHERE "IsLegacyLocal" = 1
            """).SingleAsync();

    private static Task<string[]> GetRowsAsync(NutriFlowDbContext context) =>
        context.Database.SqlQueryRaw<string>("""
            SELECT "FileName" || '|' || "UserId" || '|' || "RegisteredAtUtc" AS "Value"
            FROM "LabelPhotos" ORDER BY "FileName"
            """).ToArrayAsync();

    private static NutriFlowDbContext CreateInterceptedContext(TestDatabase database, IInterceptor interceptor)
    {
        using NutriFlowDbContext source = database.CreateContext();
        DbContextOptions<NutriFlowDbContext> options = new DbContextOptionsBuilder<NutriFlowDbContext>()
            .UseSqlite(source.Database.GetDbConnection().ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        return new NutriFlowDbContext(options);
    }

    private sealed class PhotoSaveFailureInterceptor : SaveChangesInterceptor
    {
        private readonly string _directoryPath;
        private readonly CancellationTokenSource? _cancellation;
        private bool _failed;

        public PhotoSaveFailureInterceptor(string directoryPath, CancellationTokenSource? cancellation)
        {
            _directoryPath = directoryPath;
            _cancellation = cancellation;
        }

        public bool FileWasClosed { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_failed && eventData.Context!.ChangeTracker.Entries().Any(entry =>
                    entry.State == EntityState.Added && entry.Metadata.GetTableName() == "LabelPhotos"))
            {
                _failed = true;
                foreach (string filePath in Directory.GetFiles(_directoryPath))
                {
                    using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
                    Assert.Equal(PhotoContent.Length, stream.Length);
                }

                FileWasClosed = true;
                _cancellation?.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                throw new DbUpdateException("Simulated photo metadata save failure.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class ImportCancellationInterceptor : DbCommandInterceptor
    {
        private readonly CancellationTokenSource _cancellation;

        public ImportCancellationInterceptor(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public bool InsertWasExecuted { get; private set; }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            CancelAfterInsert(command, cancellationToken);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            CancelAfterInsert(command, cancellationToken);
            return ValueTask.FromResult(result);
        }

        private void CancelAfterInsert(DbCommand command, CancellationToken cancellationToken)
        {
            if (!InsertWasExecuted && command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("\"LabelPhotos\"", StringComparison.Ordinal))
            {
                InsertWasExecuted = true;
                _cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private sealed class PhotoDirectory : IDisposable
    {
        public PhotoDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nutriflow-photo-owner-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
