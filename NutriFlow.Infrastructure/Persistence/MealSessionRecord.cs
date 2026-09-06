using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Persistence;

internal sealed class MealSessionRecord
{
    public Guid Id { get; set; }
    public string MessagesJson { get; set; } = string.Empty;
    public string DraftJson { get; set; } = string.Empty;
    public string PreviewJson { get; set; } = string.Empty;
    public string PreviewToken { get; set; } = string.Empty;
    public MealSessionStatus Status { get; set; }
    public DateOnly MealDate { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? ConfirmedAtUtc { get; set; }
    public List<MealEntryRecord> MealEntries { get; set; } = new();
}
