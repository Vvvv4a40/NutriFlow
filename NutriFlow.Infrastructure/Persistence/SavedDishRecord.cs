using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Persistence;

internal sealed class SavedDishRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid SourceSessionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public decimal FinalWeightInGrams { get; set; }
    public decimal Calories { get; set; }
    public decimal ProteinGrams { get; set; }
    public decimal FatGrams { get; set; }
    public decimal CarbohydratesGrams { get; set; }
    public DataQuality Quality { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
