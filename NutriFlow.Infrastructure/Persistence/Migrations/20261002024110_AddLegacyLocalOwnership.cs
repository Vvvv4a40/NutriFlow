using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NutriFlow.Infrastructure.Persistence.Migrations
{
    public partial class AddLegacyLocalOwnership : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            Guid localOwnerId = Guid.NewGuid();

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsLegacyLocal = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Users",
                columns: new[] { "Id", "CreatedAtUtc", "IsLegacyLocal" },
                values: new object[] { localOwnerId, DateTimeOffset.UtcNow, true });

            migrationBuilder.DropIndex(
                name: "IX_MealSessions_IdempotencyKey",
                table: "MealSessions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DailyGoals",
                table: "DailyGoals");

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "MealSessions",
                type: "TEXT",
                nullable: false,
                defaultValue: localOwnerId);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "DailyGoals",
                type: "TEXT",
                nullable: false,
                defaultValue: localOwnerId);

            migrationBuilder.AddPrimaryKey(
                name: "PK_DailyGoals",
                table: "DailyGoals",
                columns: new[] { "UserId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_MealSessions_UserId_IdempotencyKey",
                table: "MealSessions",
                columns: new[] { "UserId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_IsLegacyLocal",
                table: "Users",
                column: "IsLegacyLocal",
                unique: true,
                filter: "\"IsLegacyLocal\" = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_DailyGoals_Users_UserId",
                table: "DailyGoals",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MealSessions_Users_UserId",
                table: "MealSessions",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "MealSessions",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldDefaultValue: localOwnerId);

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "DailyGoals",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldDefaultValue: localOwnerId);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Rolling back ownership could merge or lose different users' data.");
        }
    }
}
