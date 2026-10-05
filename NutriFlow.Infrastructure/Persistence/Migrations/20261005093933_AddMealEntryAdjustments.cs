using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NutriFlow.Infrastructure.Persistence.Migrations
{
    public partial class AddMealEntryAdjustments : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MealEntryAdjustments",
                columns: table => new
                {
                    MealEntryId = table.Column<int>(type: "INTEGER", nullable: false),
                    WeightInGrams = table.Column<decimal>(type: "TEXT", nullable: false),
                    WeightQuality = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MealEntryAdjustments", x => x.MealEntryId);
                    table.ForeignKey(
                        name: "FK_MealEntryAdjustments_MealEntries_MealEntryId",
                        column: x => x.MealEntryId,
                        principalTable: "MealEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Rolling back meal entry adjustments would restore deleted portions and discard corrections.");
        }
    }
}
