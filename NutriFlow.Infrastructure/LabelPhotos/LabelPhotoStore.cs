using Microsoft.EntityFrameworkCore;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.LabelPhotos;

public sealed class LabelPhotoStore
{
    private const string ReferencePrefix = "label-photo:";
    private readonly NutriFlowDbContext _dbContext;
    private readonly string _storagePath;
    private readonly Guid _ownerId;

    public LabelPhotoStore(NutriFlowDbContext dbContext, string storagePath)
        : this(dbContext, storagePath, GetLocalOwnerId(dbContext))
    {
    }

    public LabelPhotoStore(NutriFlowDbContext dbContext, string storagePath, Guid ownerId)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An owner ID cannot be empty.", nameof(ownerId));
        }

        _dbContext = dbContext;
        _ownerId = ownerId;
        _storagePath = Path.GetFullPath(storagePath);
        Directory.CreateDirectory(_storagePath);
    }

    public async Task<string> SaveAsync(
        ValidatedLabelPhoto photo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        cancellationToken.ThrowIfCancellationRequested();

        string fileName = $"{Guid.NewGuid():N}{photo.FileExtension}";
        string? mediaType = GetMediaType(fileName);

        if (mediaType is null || mediaType != photo.MediaType)
        {
            throw new ArgumentException("The photo format is invalid.", nameof(photo));
        }

        string filePath = Path.Combine(_storagePath, fileName);
        bool fileCreated = false;
        LabelPhotoRecord record = new()
        {
            FileName = fileName,
            UserId = _ownerId,
            RegisteredAtUtc = DateTimeOffset.UtcNow
        };

        try
        {
            await using (FileStream stream = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                fileCreated = true;
                await stream.WriteAsync(photo.Content, cancellationToken);
            }

            _dbContext.LabelPhotos.Add(record);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            _dbContext.Entry(record).State = EntityState.Detached;

            if (fileCreated)
            {
                File.Delete(filePath);
            }

            throw;
        }

        return $"{ReferencePrefix}{fileName}";
    }

    public async Task<bool> ContainsAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        return await FindAsync(reference, cancellationToken) is not null;
    }

    public async Task<StoredLabelPhoto?> FindAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reference) ||
            !reference.StartsWith(ReferencePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string fileName = reference[ReferencePrefix.Length..];

        if (GetMediaType(fileName) is null ||
            !await _dbContext.LabelPhotos.AsNoTracking().AnyAsync(
                photo => photo.FileName == fileName && photo.UserId == _ownerId,
                cancellationToken))
        {
            return null;
        }

        return FindFile(_storagePath, fileName);
    }

    internal static StoredLabelPhoto? FindFile(string storagePath, string fileName)
    {
        string? mediaType = GetMediaType(fileName);

        if (mediaType is null)
        {
            return null;
        }

        string filePath = Path.Combine(storagePath, fileName);
        FileInfo file = new(filePath);

        return file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0
            ? new StoredLabelPhoto(filePath, mediaType)
            : null;
    }

    private static string? GetMediaType(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);

        if (fileName != Path.GetFileName(fileName) ||
            !Guid.TryParseExact(stem, "N", out Guid id) ||
            stem != id.ToString("N"))
        {
            return null;
        }

        return Path.GetExtension(fileName) switch
        {
            ".jpg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => null
        };
    }

    private static Guid GetLocalOwnerId(NutriFlowDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        return dbContext.Users.AsNoTracking()
            .Where(user => user.IsLegacyLocal)
            .Select(user => user.Id)
            .Single();
    }
}

public sealed record StoredLabelPhoto(string FilePath, string MediaType);
