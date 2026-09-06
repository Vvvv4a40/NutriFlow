namespace NutriFlow.Infrastructure.Persistence;

internal sealed class DailyGoalRecord
{
    public DateOnly Date { get; set; }
    public decimal Calories { get; set; }
    public decimal ProteinGrams { get; set; }
    public decimal FatGrams { get; set; }
    public decimal CarbohydratesGrams { get; set; }
}
