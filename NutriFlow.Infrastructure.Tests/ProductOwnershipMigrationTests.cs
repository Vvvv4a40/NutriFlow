using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class ProductOwnershipMigrationTests
{
    [Fact]
    public async Task Migration_WithoutLegacyOwner_DoesNotPublishOldProducts()
    {
        await using TestDatabase database = new TestDatabase();
        await using NutriFlowDbContext context = database.CreateContext();
        await context.Database.MigrateAsync("20261002024110_AddLegacyLocalOwnership");
        await context.Database.ExecuteSqlRawAsync("DELETE FROM \"Users\"");
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Products"
                ("Name", "NormalizedName", "Calories", "ProteinGrams", "FatGrams",
                 "CarbohydratesGrams", "SourceKind", "SourceQuality", "SourceName")
            VALUES ({"Личный продукт"}, {"ЛИЧНЫЙ ПРОДУКТ"}, {"60"}, {"3"}, {"3"}, {"4"},
                    {(int)NutritionSourceKind.ManualInput}, {(int)DataQuality.Exact}, {"Manual input"})
            """);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            context.Database.MigrateAsync());
        Assert.DoesNotContain("20261004175726_AddProductOwnership",
            await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(1, await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS \"Value\" FROM \"Products\"").SingleAsync());
    }

    [Fact]
    public async Task Migration_PreservesOldProductsAndAliasesAsPersonalData()
    {
        await using TestDatabase database = new TestDatabase();
        Guid localOwner;

        await using (NutriFlowDbContext oldContext = database.CreateContext())
        {
            await oldContext.Database.MigrateAsync("20261002024110_AddLegacyLocalOwnership");
            localOwner = Guid.Parse(await oldContext.Database.SqlQueryRaw<string>(
                "SELECT \"Id\" AS \"Value\" FROM \"Users\" WHERE \"IsLegacyLocal\" = 1")
                .SingleAsync());

            await oldContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Products"
                    ("Id", "Name", "NormalizedName", "Barcode", "Calories",
                     "ProteinGrams", "FatGrams", "CarbohydratesGrams", "SourceKind",
                     "SourceQuality", "SourceName", "SourceReference")
                VALUES
                    ({41}, {"Молоко"}, {"МОЛОКО"}, {"12345678"},
                     {"61.123456789012345678901234567"}, {"3.125"}, {"3.25"}, {"4.75"},
                     {(int)NutritionSourceKind.LabelPhoto}, {(int)DataQuality.Verified},
                     {"Reviewed label"}, {"label-photo:old.webp"}),
                    ({42}, {"Внешнее молоко"}, {"ВНЕШНЕЕ МОЛОКО"}, {"87654321"},
                     {"62.5"}, {"3.25"}, {"3.5"}, {"4.5"},
                     {(int)NutritionSourceKind.ExternalService}, {(int)DataQuality.Unknown},
                     {"Public catalogue"}, {"https://example.com/product/87654321"}),
                    ({43}, {"Рис"}, {"РИС"}, NULL, {"340.125"}, {"7.25"}, {"1.5"}, {"76.75"},
                     {(int)NutritionSourceKind.ManualInput}, {(int)DataQuality.Exact},
                     {"Manual input"}, NULL)
                """);
            await oldContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ProductAliases" ("Id", "ProductId", "Name", "NormalizedName")
                VALUES ({51}, {41}, {"Для каши"}, {"ДЛЯ КАШИ"}),
                       ({52}, {42}, {"Мой напиток"}, {"МОЙ НАПИТОК"})
                """);
        }

        await using NutriFlowDbContext upgraded = database.CreateContext();
        await upgraded.Database.MigrateAsync();
        await upgraded.Database.MigrateAsync();

        Assert.Equal(3, await upgraded.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS "Value" FROM "Products" WHERE "UserId" = {localOwner}
            """).SingleAsync());
        Assert.Equal(2, await upgraded.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS "Value" FROM "ProductAliases" WHERE "UserId" = {localOwner}
            """).SingleAsync());
        Assert.Equal(0, await upgraded.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS \"Value\" FROM \"Products\" WHERE \"UserId\" IS NULL")
            .SingleAsync());
        Assert.Equal(1, await upgraded.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS \"Value\" FROM \"Users\" WHERE \"IsLegacyLocal\" = 1")
            .SingleAsync());
        Assert.Equal(new[] { 41, 42, 43 }, await upgraded.Database.SqlQueryRaw<int>(
            "SELECT \"Id\" AS \"Value\" FROM \"Products\" ORDER BY \"Id\"").ToArrayAsync());
        Assert.Equal(new[] { 51, 52 }, await upgraded.Database.SqlQueryRaw<int>(
            "SELECT \"Id\" AS \"Value\" FROM \"ProductAliases\" ORDER BY \"Id\"").ToArrayAsync());

        LocalProductCatalog localCatalog = new LocalProductCatalog(upgraded);
        Product labelProduct = Assert.Single(await localCatalog.FindByNameAsync("Для каши"));
        Assert.Equal("Молоко", labelProduct.Name);
        Assert.Equal("12345678", labelProduct.Barcode);
        Assert.Equal(61.123456789012345678901234567m, labelProduct.NutritionPer100Grams.Calories);
        Assert.Equal(3.125m, labelProduct.NutritionPer100Grams.ProteinGrams);
        Assert.Equal(3.25m, labelProduct.NutritionPer100Grams.FatGrams);
        Assert.Equal(4.75m, labelProduct.NutritionPer100Grams.CarbohydratesGrams);
        Assert.Equal(NutritionSourceKind.LabelPhoto, labelProduct.Source.Kind);
        Assert.Equal(DataQuality.Verified, labelProduct.Source.Quality);
        Assert.Equal("Reviewed label", labelProduct.Source.Name);
        Assert.Equal("label-photo:old.webp", labelProduct.Source.Reference);
        Product externalProduct = Assert.Single(await localCatalog.FindByNameAsync("Мой напиток"));
        Assert.Equal("https://example.com/product/87654321", externalProduct.Source.Reference);
        Assert.Equal(DataQuality.Unknown, externalProduct.Source.Quality);
        Assert.Equal(340.125m, Assert.Single(await localCatalog.FindByNameAsync("Рис"))
            .NutritionPer100Grams.Calories);

        Guid otherOwner = Guid.NewGuid();
        await upgraded.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Users" ("Id", "CreatedAtUtc", "IsLegacyLocal")
            VALUES ({otherOwner}, {DateTimeOffset.UtcNow}, {false})
            """);
        LocalProductCatalog otherCatalog = new LocalProductCatalog(upgraded, otherOwner);
        Assert.Null(await otherCatalog.FindByBarcodeAsync("12345678"));
        Assert.Null(await otherCatalog.FindByBarcodeAsync("87654321"));
        Assert.Empty(await otherCatalog.FindByNameAsync("Молоко"));
        Assert.Empty(await otherCatalog.FindByNameAsync("Для каши"));
        Assert.Empty(await otherCatalog.FindByNameAsync("Мой напиток"));
        Assert.Empty(await otherCatalog.FindByNameAsync("Рис"));
        Assert.Equal("none", await upgraded.Database.SqlQueryRaw<string>("""
            SELECT COALESCE(dflt_value, 'none') AS "Value"
            FROM pragma_table_info('ProductAliases') WHERE name = 'UserId'
            """).SingleAsync());
        Assert.Empty(await upgraded.Database.SqlQueryRaw<string>(
            "SELECT \"table\" AS \"Value\" FROM pragma_foreign_key_check").ToArrayAsync());
        Assert.Equal("ok", await upgraded.Database.SqlQueryRaw<string>(
            "SELECT integrity_check AS \"Value\" FROM pragma_integrity_check").SingleAsync());
    }
}
