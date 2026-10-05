using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Persistence;

internal sealed class MealEntryAdjustmentRecord
{
    public int MealEntryId { get; set; }
    public MealEntryRecord MealEntry { get; set; } = null!;
    public decimal WeightInGrams { get; set; }
    public DataQuality WeightQuality { get; set; }
    public int Revision { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
