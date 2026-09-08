using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Persistence;

internal sealed class ProductRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public decimal Calories { get; set; }
    public decimal ProteinGrams { get; set; }
    public decimal FatGrams { get; set; }
    public decimal CarbohydratesGrams { get; set; }
    public NutritionSourceKind SourceKind { get; set; }
    public DataQuality SourceQuality { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string? SourceReference { get; set; }
    public List<ProductAliasRecord> Aliases { get; set; } = new();
}
