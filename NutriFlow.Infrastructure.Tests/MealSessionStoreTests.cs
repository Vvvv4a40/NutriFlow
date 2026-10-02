using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class MealSessionStoreTests
{
    private const string InitialToken =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string UpdatedToken =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OriginalRequestHash =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public async Task Migration_PreservesSessionsCreatedBeforeIdempotencyFields()
    {
        await using TestDatabase database = new TestDatabase();
        Guid legacyId = Guid.NewGuid();
        const string legacyDraftJson = """
            {
              "Dishes": [
                {
                  "Name": "Рагу",
                  "Ingredients": [
                    {
                      "ProductName": "Говядина",
                      "WeightInGrams": 600,
                      "WeightQuality": 3,
                      "RemovedWeightInGrams": 0,
                      "RemovedWeightQuality": 1,
                      "RemovalSpecified": true
                    }
                  ],
                  "FinalWeightInGrams": 500,
                  "FinalWeightQuality": 1,
                  "Portions": []
                }
              ],
              "ClarificationQuestions": []
            }
            """;

        await using (NutriFlowDbContext oldContext = database.CreateContext())
        {
            await oldContext.Database.MigrateAsync(
                "20260907111329_HardenProductResolutionAndMealQuality");

            await oldContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MealSessions"
                    ("Id", "MessagesJson", "DraftJson", "PreviewJson", "PreviewToken",
                     "Status", "MealDate", "CreatedAtUtc", "UpdatedAtUtc")
                VALUES
                    ({legacyId}, {"[\"Готовлю рагу\"]"}, {legacyDraftJson}, {"{}"}, {InitialToken},
                     {(int)MealSessionStatus.ReadyForConfirmation}, {"2026-09-06"},
                     {"2026-09-06T12:00:00+00:00"}, {"2026-09-06T12:00:00+00:00"})
                """);
        }

        await using NutriFlowDbContext upgradedContext = database.CreateContext();
        await upgradedContext.Database.MigrateAsync();
        StoredMealSession restored = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(upgradedContext).FindAsync(legacyId));

        Assert.Equal(["Готовлю рагу"], restored.Messages);
        Assert.Equal("Рагу", Assert.Single(restored.Draft.Dishes).Name);
        Assert.Null(restored.IdempotencyKey);
        Assert.Null(restored.OriginalRequestHash);
        Assert.Empty(restored.MessageRequestHashes);

        Assert.Equal(
            1,
            await upgradedContext.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS Value FROM "MealSessions"
                WHERE "Id" = {legacyId}
                    AND "IdempotencyKey" IS NULL
                    AND "OriginalRequestHash" IS NULL
                    AND "MessageRequestHashesJson" = {"{}"}
                """).SingleAsync());
    }

    [Fact]
    public async Task CreateAsync_AllowsMultipleLegacySessionsWithoutIdempotencyKeys()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();

        await using NutriFlowDbContext context = database.CreateContext();
        MealSessionStore store = new MealSessionStore(context);
        StoredMealSession first = await store.CreateAsync(
            ["Первое блюдо"],
            CreateReadyDraft(),
            "{}",
            InitialToken,
            MealSessionStatus.ReadyForConfirmation,
            new DateOnly(2026, 9, 6));
        StoredMealSession second = await store.CreateAsync(
            ["Второе блюдо"],
            CreateReadyDraft(),
            "{}",
            InitialToken,
            MealSessionStatus.ReadyForConfirmation,
            new DateOnly(2026, 9, 6));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(first.IdempotencyKey);
        Assert.Null(first.OriginalRequestHash);
        Assert.Null(second.IdempotencyKey);
        Assert.Null(second.OriginalRequestHash);
        Assert.Equal(
            2,
            await context.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM \"MealSessions\"").SingleAsync());
    }

    [Fact]
    public async Task CreateWithIdempotencyKeyAsync_PersistsKeyAndReturnsExistingSessionOnRetry()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid key = Guid.NewGuid();
        StoredMealSession created;

        await using (NutriFlowDbContext createContext = database.CreateContext())
        {
            MealSessionStore store = new MealSessionStore(createContext);
            (created, bool wasCreated) = await store.CreateWithIdempotencyKeyAsync(
                ["Первоначальное сообщение"],
                CreateReadyDraft(),
                "{}",
                InitialToken,
                MealSessionStatus.ReadyForConfirmation,
                new DateOnly(2026, 9, 6),
                key,
                OriginalRequestHash);

            Assert.True(wasCreated);
            Assert.Equal(key, created.IdempotencyKey);
            Assert.Equal(OriginalRequestHash, created.OriginalRequestHash);
        }

        await using (NutriFlowDbContext retryContext = database.CreateContext())
        {
            MealSessionStore store = new MealSessionStore(retryContext);
            StoredMealSession found = Assert.IsType<StoredMealSession>(
                await store.FindByIdempotencyKeyAsync(key));
            Assert.Equal(created.Id, found.Id);
            Assert.Equal(OriginalRequestHash, found.OriginalRequestHash);

            (StoredMealSession existing, bool wasCreated) =
                await store.CreateWithIdempotencyKeyAsync(
                    ["Повтор с другим сообщением"],
                    CreateReadyDraft(),
                    "{}",
                    UpdatedToken,
                    MealSessionStatus.NeedsProducts,
                    new DateOnly(2026, 9, 7),
                    key,
                    new string('d', 64));

            Assert.False(wasCreated);
            Assert.Equal(created.Id, existing.Id);
            Assert.Equal(["Первоначальное сообщение"], existing.Messages);
            Assert.Equal(OriginalRequestHash, existing.OriginalRequestHash);
            Assert.Equal(
                1,
                await retryContext.Database.SqlQueryRaw<int>(
                    "SELECT COUNT(*) AS Value FROM \"MealSessions\"").SingleAsync());
        }
    }

    [Fact]
    public async Task Owners_CanReuseIdempotencyKeyWithoutReadingOrEditingEachOthersSessions()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid otherOwnerId = Guid.NewGuid();
        Guid requestKey = Guid.NewGuid();
        await using NutriFlowDbContext context = database.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Users" ("Id", "CreatedAtUtc", "IsLegacyLocal")
            VALUES ({otherOwnerId}, {DateTimeOffset.UtcNow}, {false})
            """);

        MealSessionStore localStore = new(context);
        MealSessionStore otherStore = new(context, otherOwnerId);
        (StoredMealSession localSession, bool localCreated) =
            await localStore.CreateWithIdempotencyKeyAsync(
                ["Обед первого владельца"],
                CreateReadyDraft(),
                "{}",
                InitialToken,
                MealSessionStatus.ReadyForConfirmation,
                new DateOnly(2026, 9, 6),
                requestKey,
                OriginalRequestHash);

        Assert.True(localCreated);
        Assert.Null(await otherStore.FindAsync(localSession.Id));
        Assert.Null(await otherStore.FindByIdempotencyKeyAsync(requestKey));

        (StoredMealSession otherSession, bool otherCreated) =
            await otherStore.CreateWithIdempotencyKeyAsync(
                ["Обед второго владельца"],
                CreateReadyDraft(),
                "{}",
                InitialToken,
                MealSessionStatus.ReadyForConfirmation,
                new DateOnly(2026, 9, 6),
                requestKey,
                OriginalRequestHash);

        Assert.True(otherCreated);
        Assert.NotEqual(localSession.Id, otherSession.Id);
        Assert.Equal(localSession.Id,
            (await localStore.FindByIdempotencyKeyAsync(requestKey))?.Id);
        Assert.Equal(otherSession.Id,
            (await otherStore.FindByIdempotencyKeyAsync(requestKey))?.Id);
        Assert.Null(await localStore.FindAsync(otherSession.Id));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            otherStore.ReplaceDraftAsync(
                localSession.Id,
                InitialToken,
                ["Обед первого владельца", "Чужое изменение"],
                CreateReadyDraft(),
                "{}",
                UpdatedToken,
                MealSessionStatus.ReadyForConfirmation));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            localStore.UpdatePreviewAsync(
                otherSession.Id,
                InitialToken,
                "{}",
                UpdatedToken,
                MealSessionStatus.ReadyForConfirmation));

        Assert.Equal(["Обед первого владельца"],
            (await localStore.FindAsync(localSession.Id))?.Messages);
        Assert.Equal(["Обед второго владельца"],
            (await otherStore.FindAsync(otherSession.Id))?.Messages);
    }

    [Fact]
    public async Task CreateAsync_ThenFindAsync_RestoresCompleteDraftAndPreview()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        DateOnly mealDate = new DateOnly(2026, 9, 6);
        MealDraft draft = CreateIncompleteDraft();
        string previewJson = """{"version":1,"canConfirm":false}""";
        StoredMealSession created;

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            MealSessionStore store = new MealSessionStore(context);
            created = await store.CreateAsync(
                ["Добавил примерно 600 г говядины", "Вес масла не помню"],
                draft,
                previewJson,
                InitialToken,
                MealSessionStatus.NeedsClarification,
                mealDate);
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        MealSessionStore readStore = new MealSessionStore(readContext);
        StoredMealSession restored = Assert.IsType<StoredMealSession>(
            await readStore.FindAsync(created.Id));

        Assert.Equal(mealDate, restored.MealDate);
        Assert.Equal(MealSessionStatus.NeedsClarification, restored.Status);
        Assert.Equal(previewJson, restored.PreviewJson);
        Assert.Equal(InitialToken, restored.PreviewToken);
        Assert.Null(restored.ConfirmedAtUtc);
        Assert.Equal(created.CreatedAtUtc, restored.CreatedAtUtc);
        Assert.Equal(created.UpdatedAtUtc, restored.UpdatedAtUtc);
        Assert.Equal(
            ["Добавил примерно 600 г говядины", "Вес масла не помню"],
            restored.Messages);
        Assert.Equal(
            ["Сколько масла вошло в блюдо?"],
            restored.Draft.ClarificationQuestions);

        DishDraft dish = Assert.Single(restored.Draft.Dishes);
        Assert.Equal("Рагу", dish.Name);
        Assert.Null(dish.FinalWeightInGrams);
        Assert.Equal(DataQuality.Unknown, dish.FinalWeightQuality);
        Assert.Collection(
            dish.Ingredients,
            ingredient =>
            {
                Assert.Equal("Говядина", ingredient.ProductName);
                Assert.Equal(600m, ingredient.WeightInGrams);
                Assert.Equal(DataQuality.Estimated, ingredient.WeightQuality);
            },
            ingredient =>
            {
                Assert.Equal("Масло", ingredient.ProductName);
                Assert.Null(ingredient.WeightInGrams);
                Assert.Equal(DataQuality.Unknown, ingredient.WeightQuality);
            });
        PortionDraft portion = Assert.Single(dish.Portions);
        Assert.Equal(350m, portion.WeightInGrams);
        Assert.Equal(DataQuality.Estimated, portion.WeightQuality);
    }

    [Fact]
    public async Task ReplaceDraftAsync_UpdatesMessagesDraftPreviewAndStatus()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        StoredMealSession initial;

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            MealSessionStore store = new MealSessionStore(context);
            initial = await store.CreateAsync(
                ["Начал готовить"],
                CreateIncompleteDraft(),
                """{"version":1}""",
                InitialToken,
                MealSessionStatus.NeedsClarification,
                new DateOnly(2026, 9, 6));
        }

        MealDraft replacement = CreateReadyDraft();

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            MealSessionStore store = new MealSessionStore(context);
            await store.ReplaceDraftAsync(
                initial.Id,
                InitialToken,
                ["Начал готовить", "Готовое блюдо весит 800 г"],
                replacement,
                """{"version":2,"canConfirm":true}""",
                UpdatedToken,
                MealSessionStatus.ReadyForConfirmation);
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        StoredMealSession restored = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(readContext).FindAsync(initial.Id));

        Assert.Equal(
            ["Начал готовить", "Готовое блюдо весит 800 г"],
            restored.Messages);
        Assert.Equal(MealSessionStatus.ReadyForConfirmation, restored.Status);
        Assert.Equal("""{"version":2,"canConfirm":true}""", restored.PreviewJson);
        Assert.Equal(UpdatedToken, restored.PreviewToken);
        Assert.Empty(restored.Draft.ClarificationQuestions);
        Assert.Equal(800m, Assert.Single(restored.Draft.Dishes).FinalWeightInGrams);
        Assert.True(restored.UpdatedAtUtc >= initial.UpdatedAtUtc);
    }

    [Fact]
    public async Task ReplaceDraftAsync_PersistsMessageRequestHashWithAppendedMessage()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid requestKey = Guid.NewGuid();
        string requestHash = new('A', 64);
        Guid sessionId;

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            StoredMealSession session = await new MealSessionStore(context).CreateAsync(
                ["Готовлю рагу"],
                CreateReadyDraft(),
                "{}",
                InitialToken,
                MealSessionStatus.ReadyForConfirmation,
                new DateOnly(2026, 9, 6));
            sessionId = session.Id;
        }

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            await new MealSessionStore(context).ReplaceDraftAsync(
                sessionId,
                InitialToken,
                ["Готовлю рагу", "Добавил масло"],
                CreateReadyDraft(),
                "{}",
                UpdatedToken,
                MealSessionStatus.ReadyForConfirmation,
                messageRequestHashes: new Dictionary<Guid, string>
                {
                    [requestKey] = requestHash
                });
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        StoredMealSession restored = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(readContext).FindAsync(sessionId));

        Assert.Equal(["Готовлю рагу", "Добавил масло"], restored.Messages);
        Assert.Equal(requestHash, restored.MessageRequestHashes[requestKey]);
        Assert.Single(restored.MessageRequestHashes);
    }

    [Fact]
    public async Task ReplaceDraftAsync_WithStaleToken_DoesNotPersistMessageRequestHash()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid acceptedKey = Guid.NewGuid();
        Guid staleKey = Guid.NewGuid();
        StoredMealSession session;

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            session = await new MealSessionStore(context).CreateAsync(
                ["Готовлю рагу"],
                CreateReadyDraft(),
                "{}",
                InitialToken,
                MealSessionStatus.ReadyForConfirmation,
                new DateOnly(2026, 9, 6));
        }

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            await new MealSessionStore(context).ReplaceDraftAsync(
                session.Id,
                InitialToken,
                ["Готовлю рагу", "Добавил масло"],
                CreateReadyDraft(),
                "{}",
                UpdatedToken,
                MealSessionStatus.ReadyForConfirmation,
                messageRequestHashes: new Dictionary<Guid, string>
                {
                    [acceptedKey] = new string('A', 64)
                });
        }

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            await Assert.ThrowsAsync<MealSessionConflictException>(() =>
                new MealSessionStore(context).ReplaceDraftAsync(
                    session.Id,
                    InitialToken,
                    ["Готовлю рагу", "Изменённое сообщение"],
                    CreateReadyDraft(),
                    "{}",
                    new string('d', 64),
                    MealSessionStatus.ReadyForConfirmation,
                    messageRequestHashes: new Dictionary<Guid, string>
                    {
                        [staleKey] = new string('B', 64)
                    }));
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        StoredMealSession restored = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(readContext).FindAsync(session.Id));

        Assert.Equal(["Готовлю рагу", "Добавил масло"], restored.Messages);
        Assert.Equal(UpdatedToken, restored.PreviewToken);
        Assert.Equal(new string('A', 64), restored.MessageRequestHashes[acceptedKey]);
        Assert.False(restored.MessageRequestHashes.ContainsKey(staleKey));
    }

    [Fact]
    public async Task ReplaceDraftAsync_WithoutMessageRequestHashes_PreservesExistingReceipt()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        Guid requestKey = Guid.NewGuid();
        StoredMealSession session;

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            session = await new MealSessionStore(context).CreateAsync(
                ["Готовлю рагу"],
                CreateReadyDraft(),
                "{}",
                InitialToken,
                MealSessionStatus.ReadyForConfirmation,
                new DateOnly(2026, 9, 6));
        }

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            await new MealSessionStore(context).ReplaceDraftAsync(
                session.Id,
                InitialToken,
                ["Готовлю рагу", "Добавил масло"],
                CreateReadyDraft(),
                "{}",
                UpdatedToken,
                MealSessionStatus.ReadyForConfirmation,
                messageRequestHashes: new Dictionary<Guid, string>
                {
                    [requestKey] = new string('A', 64)
                });
        }

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            await new MealSessionStore(context).ReplaceDraftAsync(
                session.Id,
                UpdatedToken,
                ["Готовлю рагу", "Добавил масло", "И немного соли"],
                CreateReadyDraft(),
                "{}",
                new string('d', 64),
                MealSessionStatus.ReadyForConfirmation);
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        StoredMealSession restored = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(readContext).FindAsync(session.Id));

        Assert.Equal(3, restored.Messages.Count);
        Assert.Equal(new string('A', 64), restored.MessageRequestHashes[requestKey]);
        Assert.Single(restored.MessageRequestHashes);
    }

    [Fact]
    public async Task ReplaceDraftAsync_RejectsInvalidOrUnboundedMessageRequestHashes()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        MealSessionStore store = new MealSessionStore(context);
        StoredMealSession session = await store.CreateAsync(
            ["Готовлю рагу"],
            CreateReadyDraft(),
            "{}",
            InitialToken,
            MealSessionStatus.ReadyForConfirmation,
            new DateOnly(2026, 9, 6));

        IReadOnlyDictionary<Guid, string>[] invalidReceipts =
        [
            new Dictionary<Guid, string> { [Guid.Empty] = new string('A', 64) },
            new Dictionary<Guid, string> { [Guid.NewGuid()] = new string('a', 64) },
            Enumerable.Range(0, 51).ToDictionary(
                _ => Guid.NewGuid(),
                _ => new string('A', 64))
        ];

        foreach (IReadOnlyDictionary<Guid, string> receipts in invalidReceipts)
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.ReplaceDraftAsync(
                    session.Id,
                    InitialToken,
                    ["Готовлю рагу", "Добавил масло"],
                    CreateReadyDraft(),
                    "{}",
                    UpdatedToken,
                    MealSessionStatus.ReadyForConfirmation,
                    messageRequestHashes: receipts));
        }

        StoredMealSession unchanged = Assert.IsType<StoredMealSession>(
            await store.FindAsync(session.Id));
        Assert.Equal(["Готовлю рагу"], unchanged.Messages);
        Assert.Empty(unchanged.MessageRequestHashes);
        Assert.Equal(InitialToken, unchanged.PreviewToken);
    }

    [Fact]
    public async Task FindAsync_RejectsCorruptMessageRequestHashes()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        await using NutriFlowDbContext context = database.CreateContext();
        MealSessionStore store = new MealSessionStore(context);
        StoredMealSession session = await store.CreateAsync(
            ["Готовлю рагу"],
            CreateReadyDraft(),
            "{}",
            InitialToken,
            MealSessionStatus.ReadyForConfirmation,
            new DateOnly(2026, 9, 6));

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "MealSessions"
            SET "MessageRequestHashesJson" = {"{\"not-a-guid\":\"ABC\"}"}
            WHERE "Id" = {session.Id}
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.FindAsync(session.Id));
    }

    [Fact]
    public async Task UpdatePreviewAsync_LeavesMessagesAndDraftUnchanged()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        StoredMealSession initial;

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            MealSessionStore store = new MealSessionStore(context);
            initial = await store.CreateAsync(
                ["Готовлю рагу"],
                CreateReadyDraft(),
                """{"products":"missing"}""",
                InitialToken,
                MealSessionStatus.NeedsProducts,
                new DateOnly(2026, 9, 6));
        }

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            MealSessionStore store = new MealSessionStore(context);
            await store.UpdatePreviewAsync(
                initial.Id,
                InitialToken,
                """{"products":"resolved"}""",
                UpdatedToken,
                MealSessionStatus.ReadyForConfirmation);
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        StoredMealSession restored = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(readContext).FindAsync(initial.Id));

        Assert.Equal(initial.Messages, restored.Messages);
        Assert.Equal(
            initial.Draft.Dishes[0].FinalWeightInGrams,
            restored.Draft.Dishes[0].FinalWeightInGrams);
        Assert.Equal("""{"products":"resolved"}""", restored.PreviewJson);
        Assert.Equal(UpdatedToken, restored.PreviewToken);
        Assert.Equal(MealSessionStatus.ReadyForConfirmation, restored.Status);
    }

    [Fact]
    public async Task ReplaceDraftAndUpdatePreview_WhenConfirmed_RejectChanges()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        StoredMealSession session;

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            MealSessionStore store = new MealSessionStore(context);
            session = await store.CreateAsync(
                ["Готовое рагу 800 г, съел 200 г"],
                CreateReadyDraft(),
                """{"canConfirm":true}""",
                InitialToken,
                MealSessionStatus.ReadyForConfirmation,
                new DateOnly(2026, 9, 6));
        }

        await using (NutriFlowDbContext context = database.CreateContext())
        {
            DailyDiaryStore diary = new DailyDiaryStore(context);
            MealSessionConfirmationResult result = await diary.ConfirmSessionAsync(
                session.Id,
                InitialToken,
                [CreateEntry("Рагу", 200m, 250m)]);

            Assert.Equal(MealSessionConfirmationResult.Confirmed, result);
        }

        await using NutriFlowDbContext editContext = database.CreateContext();
        MealSessionStore editStore = new MealSessionStore(editContext);

        await Assert.ThrowsAsync<MealSessionConflictException>(() =>
            editStore.ReplaceDraftAsync(
                session.Id,
                InitialToken,
                ["Изменённое сообщение"],
                CreateReadyDraft(),
                """{"changed":true}""",
                UpdatedToken,
                MealSessionStatus.ReadyForConfirmation));
        await Assert.ThrowsAsync<MealSessionConflictException>(() =>
            editStore.UpdatePreviewAsync(
                session.Id,
                InitialToken,
                """{"changed":true}""",
                UpdatedToken,
                MealSessionStatus.ReadyForConfirmation));
    }

    [Fact]
    public async Task UpdatePreviewAsync_WithStaleToken_DoesNotOverwriteNewerPreview()
    {
        await using TestDatabase database = new TestDatabase();
        await database.MigrateAsync();
        StoredMealSession initial;

        await using (NutriFlowDbContext createContext = database.CreateContext())
        {
            initial = await new MealSessionStore(createContext).CreateAsync(
                ["Готовлю рагу"],
                CreateReadyDraft(),
                """{"version":1}""",
                InitialToken,
                MealSessionStatus.NeedsProducts,
                new DateOnly(2026, 9, 6));
        }

        await using (NutriFlowDbContext firstContext = database.CreateContext())
        {
            await new MealSessionStore(firstContext).UpdatePreviewAsync(
                initial.Id,
                InitialToken,
                """{"version":2}""",
                UpdatedToken,
                MealSessionStatus.ReadyForConfirmation);
        }

        await using (NutriFlowDbContext staleContext = database.CreateContext())
        {
            MealSessionStore staleStore = new MealSessionStore(staleContext);

            await Assert.ThrowsAsync<MealSessionConflictException>(() =>
                staleStore.UpdatePreviewAsync(
                    initial.Id,
                    InitialToken,
                    """{"version":"stale"}""",
                    new string('c', 64),
                    MealSessionStatus.NeedsClarification));
        }

        await using NutriFlowDbContext readContext = database.CreateContext();
        StoredMealSession actual = Assert.IsType<StoredMealSession>(
            await new MealSessionStore(readContext).FindAsync(initial.Id));

        Assert.Equal("""{"version":2}""", actual.PreviewJson);
        Assert.Equal(UpdatedToken, actual.PreviewToken);
        Assert.Equal(MealSessionStatus.ReadyForConfirmation, actual.Status);
    }

    private static MealDraft CreateIncompleteDraft()
    {
        return new MealDraft(
            [
                new DishDraft(
                    "Рагу",
                    [
                        new IngredientDraft(
                            "Говядина",
                            600m,
                            DataQuality.Estimated),
                        new IngredientDraft(
                            "Масло",
                            null,
                            DataQuality.Unknown)
                    ],
                    null,
                    DataQuality.Unknown,
                    [new PortionDraft(350m, DataQuality.Estimated)])
            ],
            ["Сколько масла вошло в блюдо?"]);
    }

    internal static MealDraft CreateReadyDraft()
    {
        return new MealDraft(
            [
                new DishDraft(
                    "Рагу",
                    [
                        new IngredientDraft(
                            "Говядина",
                            600m,
                            DataQuality.Estimated),
                        new IngredientDraft("Масло", 20m, DataQuality.Exact)
                    ],
                    800m,
                    DataQuality.Exact,
                    [new PortionDraft(200m, DataQuality.Exact)])
            ],
            []);
    }

    internal static MealEntry CreateEntry(
        string name,
        decimal weight,
        decimal calories)
    {
        return new MealEntry(
            name,
            weight,
            new NutritionValues(calories, 20.25m, 13.5m, 7.75m));
    }
}
