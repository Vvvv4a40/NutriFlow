using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class LegacyLocalOwnershipMigrationTests
{
    [Fact]
    public async Task Migration_AssignsExistingDiaryDataToOneLocalOwnerWithoutChangingValues()
    {
        await using TestDatabase database = new TestDatabase();
        Guid sessionId = Guid.NewGuid();
        Guid creationKey = Guid.NewGuid();
        Guid messageKey = Guid.NewGuid();
        string messageHashes = $$"""{"{{messageKey}}":"{{new string('A', 64)}}"}""";
        const string draftJson = "{\"Dishes\":[],\"ClarificationQuestions\":[]}";

        await using (NutriFlowDbContext oldContext = database.CreateContext())
        {
            await oldContext.Database.MigrateAsync(
                "20261001185045_AddMealSessionMessageReceipts");

            await oldContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MealSessions"
                    ("Id", "MessagesJson", "DraftJson", "PreviewJson", "PreviewToken",
                     "Status", "MealDate", "CreatedAtUtc", "UpdatedAtUtc",
                     "IdempotencyKey", "OriginalRequestHash", "MessageRequestHashesJson")
                VALUES
                    ({sessionId}, {"[\"Готовлю рагу\"]"}, {draftJson}, {"{}"}, {new string('B', 64)},
                     {(int)MealSessionStatus.Confirmed}, {"2026-09-06"},
                     {"2026-09-06T12:00:00+00:00"}, {"2026-09-06T12:00:00+00:00"},
                     {creationKey}, {new string('C', 64)}, {messageHashes})
                """);

            await oldContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "DailyGoals"
                    ("Date", "Calories", "ProteinGrams", "FatGrams", "CarbohydratesGrams")
                VALUES ({"2026-09-06"}, {"2100.125"}, {"125.25"}, {"65.5"}, {"220.75"})
                """);

            await oldContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MealEntries"
                    ("MealSessionId", "Sequence", "MealDate", "Name", "WeightInGrams",
                     "Calories", "ProteinGrams", "FatGrams", "CarbohydratesGrams",
                     "Quality", "CreatedAtUtc")
                VALUES
                    ({sessionId}, {0}, {"2026-09-06"}, {"Порция рагу"}, {"250.125"},
                     {"413.375"}, {"31.625"}, {"12.875"}, {"42.125"},
                     {(int)DataQuality.Exact}, {"2026-09-06T12:00:00+00:00"})
                """);
        }

        await using NutriFlowDbContext upgraded = database.CreateContext();
        await upgraded.Database.MigrateAsync();
        string ownerText = await upgraded.Database.SqlQueryRaw<string>(
            "SELECT \"Id\" AS \"Value\" FROM \"Users\" WHERE \"IsLegacyLocal\" = 1")
            .SingleAsync();
        Guid ownerId = Guid.Parse(ownerText);

        Assert.NotEqual(Guid.Empty, ownerId);
        Assert.Equal(1, await upgraded.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS "Value" FROM "MealSessions"
            WHERE "Id" = {sessionId} AND "UserId" = {ownerId}
            """).SingleAsync());
        Assert.Equal(1, await upgraded.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS "Value" FROM "DailyGoals"
            WHERE "Date" = {"2026-09-06"} AND "UserId" = {ownerId}
            """).SingleAsync());
        Assert.Equal(1, await upgraded.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS "Value" FROM "MealEntries"
            WHERE "MealSessionId" = {sessionId} AND "Sequence" = {0}
            """).SingleAsync());
        Assert.Equal(messageHashes, await upgraded.Database.SqlQuery<string>($"""
            SELECT "MessageRequestHashesJson" AS "Value" FROM "MealSessions"
            WHERE "Id" = {sessionId} AND "IdempotencyKey" = {creationKey}
            """).SingleAsync());
        Assert.Equal("2100.125", await upgraded.Database.SqlQuery<string>($"""
            SELECT CAST("Calories" AS TEXT) AS "Value" FROM "DailyGoals"
            WHERE "UserId" = {ownerId} AND "Date" = {"2026-09-06"}
            """).SingleAsync());
        Assert.Equal("413.375", await upgraded.Database.SqlQuery<string>($"""
            SELECT CAST("Calories" AS TEXT) AS "Value" FROM "MealEntries"
            WHERE "MealSessionId" = {sessionId}
            """).SingleAsync());
        Assert.Equal((int)DataQuality.Exact, await upgraded.Database.SqlQuery<int>($"""
            SELECT "Quality" AS "Value" FROM "MealEntries"
            WHERE "MealSessionId" = {sessionId}
            """).SingleAsync());
    }
}
