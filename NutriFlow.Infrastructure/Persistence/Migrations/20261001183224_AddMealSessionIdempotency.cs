using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NutriFlow.Infrastructure.Persistence.Migrations
{
    public partial class AddMealSessionIdempotency : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "IdempotencyKey",
                table: "MealSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalRequestHash",
                table: "MealSessions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MealSessions_IdempotencyKey",
                table: "MealSessions",
                column: "IdempotencyKey",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MealSessions_IdempotencyKey",
                table: "MealSessions");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "MealSessions");

            migrationBuilder.DropColumn(
                name: "OriginalRequestHash",
                table: "MealSessions");
        }
    }
}
