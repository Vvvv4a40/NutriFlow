namespace NutriFlow.Infrastructure.Backups;

public sealed record BackupManifest(
    int FormatVersion,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<BackupFile> Files);

public sealed record BackupFile(string Path, long Length, string Sha256);
