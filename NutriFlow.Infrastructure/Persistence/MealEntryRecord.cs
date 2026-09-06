namespace NutriFlow.Infrastructure.Persistence;

internal sealed class MealEntryRecord
{
    public int Id { get; set; }
    public Guid MealSessionId { get; set; }
    public MealSessionRecord MealSession { get; set; } = null!;
    public int Sequence { get; set; }
    public DateOnly MealDate { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal WeightInGrams { get; set; }
    public decimal Calories { get; set; }
    public decimal ProteinGrams { get; set; }
    public decimal FatGrams { get; set; }
    public decimal CarbohydratesGrams { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
