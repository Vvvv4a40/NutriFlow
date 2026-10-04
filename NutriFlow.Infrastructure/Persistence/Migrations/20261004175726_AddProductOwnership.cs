using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NutriFlow.Infrastructure.Persistence.Migrations
{
    public partial class AddProductOwnership : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_Barcode",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_NormalizedName",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_ProductAliases_NormalizedName",
                table: "ProductAliases");

            migrationBuilder.DropIndex(
                name: "IX_ProductAliases_ProductId_NormalizedName",
                table: "ProductAliases");

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "Products",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "ProductAliases",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // старый каталог не публикуем, даже если у продукта внешний источник
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "__ProductOwnershipGuard" ("UserId" TEXT NOT NULL);
                INSERT INTO "__ProductOwnershipGuard" ("UserId")
                VALUES ((SELECT "Id" FROM "Users" WHERE "IsLegacyLocal" = 1));
                DROP TABLE "__ProductOwnershipGuard";
                UPDATE "Products"
                SET "UserId" = (SELECT "Id" FROM "Users" WHERE "IsLegacyLocal" = 1);
                UPDATE "ProductAliases"
                SET "UserId" = (SELECT "Id" FROM "Users" WHERE "IsLegacyLocal" = 1);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Products_Barcode",
                table: "Products",
                column: "Barcode",
                unique: true,
                filter: "\"UserId\" IS NULL AND \"Barcode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Products_UserId_Barcode",
                table: "Products",
                columns: new[] { "UserId", "Barcode" },
                unique: true,
                filter: "\"UserId\" IS NOT NULL AND \"Barcode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Products_UserId_NormalizedName",
                table: "Products",
                columns: new[] { "UserId", "NormalizedName" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductAliases_ProductId",
                table: "ProductAliases",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductAliases_UserId_NormalizedName",
                table: "ProductAliases",
                columns: new[] { "UserId", "NormalizedName" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductAliases_UserId_ProductId_NormalizedName",
                table: "ProductAliases",
                columns: new[] { "UserId", "ProductId", "NormalizedName" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ProductAliases_Users_UserId",
                table: "ProductAliases",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Products_Users_UserId",
                table: "Products",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "ProductAliases",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldDefaultValue: Guid.Empty);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Rolling back catalogue ownership could expose or merge users' personal products.");
        }
    }
}
