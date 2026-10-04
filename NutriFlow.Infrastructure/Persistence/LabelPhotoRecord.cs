namespace NutriFlow.Infrastructure.Persistence;

internal sealed class LabelPhotoRecord
{
    public required string FileName { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset RegisteredAtUtc { get; set; }
}
