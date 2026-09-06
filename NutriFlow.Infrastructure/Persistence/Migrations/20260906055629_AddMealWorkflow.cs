using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NutriFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMealWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyGoals",
                columns: table => new
                {
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Calories = table.Column<decimal>(type: "TEXT", nullable: false),
                    ProteinGrams = table.Column<decimal>(type: "TEXT", nullable: false),
                    FatGrams = table.Column<decimal>(type: "TEXT", nullable: false),
                    CarbohydratesGrams = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyGoals", x => x.Date);
                });

            migrationBuilder.CreateTable(
                name: "MealSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessagesJson = table.Column<string>(type: "TEXT", nullable: false),
                    DraftJson = table.Column<string>(type: "TEXT", nullable: false),
                    PreviewJson = table.Column<string>(type: "TEXT", nullable: false),
                    PreviewToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    MealDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConfirmedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MealSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MealEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MealSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    MealDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    WeightInGrams = table.Column<decimal>(type: "TEXT", nullable: false),
                    Calories = table.Column<decimal>(type: "TEXT", nullable: false),
                    ProteinGrams = table.Column<decimal>(type: "TEXT", nullable: false),
                    FatGrams = table.Column<decimal>(type: "TEXT", nullable: false),
                    CarbohydratesGrams = table.Column<decimal>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MealEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MealEntries_MealSessions_MealSessionId",
                        column: x => x.MealSessionId,
                        principalTable: "MealSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MealEntries_MealDate",
                table: "MealEntries",
                column: "MealDate");

            migrationBuilder.CreateIndex(
                name: "IX_MealEntries_MealSessionId_Sequence",
                table: "MealEntries",
                columns: new[] { "MealSessionId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyGoals");

            migrationBuilder.DropTable(
                name: "MealEntries");

            migrationBuilder.DropTable(
                name: "MealSessions");
        }
    }
}
