namespace NutriFlow.Infrastructure.Persistence;

internal sealed class UserRecord
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public bool IsLegacyLocal { get; set; }
}
