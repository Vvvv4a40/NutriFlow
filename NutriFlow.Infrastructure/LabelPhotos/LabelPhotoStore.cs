namespace NutriFlow.Infrastructure.LabelPhotos;

public sealed class LabelPhotoStore
{
    private const string ReferencePrefix = "label-photo:";
    private readonly string _storagePath;

    public LabelPhotoStore(string storagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        _storagePath = Path.GetFullPath(storagePath);
        Directory.CreateDirectory(_storagePath);
    }

    public async Task<string> SaveAsync(
        ValidatedLabelPhoto photo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);

        string fileName = $"{Guid.NewGuid():N}{photo.FileExtension}";
        string filePath = Path.Combine(_storagePath, fileName);

        await using FileStream stream = new FileStream(
            filePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        await stream.WriteAsync(photo.Content, cancellationToken);

        return $"{ReferencePrefix}{fileName}";
    }

    public bool Contains(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) ||
            !reference.StartsWith(ReferencePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string fileName = reference[ReferencePrefix.Length..];

        if (fileName != Path.GetFileName(fileName))
        {
            return false;
        }

        return File.Exists(Path.Combine(_storagePath, fileName));
    }
}
