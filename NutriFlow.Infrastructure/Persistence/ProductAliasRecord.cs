namespace NutriFlow.Infrastructure.Persistence;

internal sealed class ProductAliasRecord
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public ProductRecord Product { get; set; } = null!;
}
