using System.Security.Cryptography;

namespace NutriFlow.Web.Services;

// .NET in the browser keeps files in memory; this mirrors the profile folder (profiles.json, input drafts, label photos)
// to the origin private file system. Restore runs once before anything reads the folder, flush after every change.
public sealed class PersistentDirectory
{
    public const string MemoryRoot = "/nutriflow/profiles";
    public const string StorageRoot = "nutriflow/files";
    // small files are compared by content: two writes within one millisecond keep the same time and maybe the same length
    private const long ContentComparisonLimit = 256 * 1024;
    // LocalProfileStore.CreateBackupWorkingDirectory
    private const string BackupWorkingPrefix = ".profile-backup-";
    private readonly string _root;
    private readonly string _storageRoot;
    private readonly OpfsFiles _files;
    private readonly Dictionary<string, FileState> _stored = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _restored;

    public PersistentDirectory(OpfsFiles files, string root = MemoryRoot, string storageRoot = StorageRoot)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        _files = files;
        _root = Path.GetFullPath(root);
        _storageRoot = storageRoot.Trim('/');
    }

    public string Root => _root;

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_restored)
            {
                throw new InvalidOperationException("The persistent directory is already restored.");
            }

            Directory.CreateDirectory(_root);
            foreach (string relativePath in await _files.ListAsync(_storageRoot, cancellationToken))
            {
                string path = ToMemoryPath(relativePath);
                byte[]? content = await _files.ReadAsync(StoragePath(relativePath), cancellationToken);
                if (content is null)
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, content, cancellationToken);
                _stored[relativePath] = FileState.Capture(new FileInfo(path), content);
            }

            _restored = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_restored)
            {
                // writing before the restore would replace stored files with an empty folder
                throw new InvalidOperationException("The persistent directory has to be restored before it is flushed.");
            }

            HashSet<string> present = new(StringComparer.Ordinal);
            foreach (FileInfo file in new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(_root, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
                // a backup works in its own folder next to the profiles and removes it at the end
                if (relativePath.StartsWith(BackupWorkingPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                present.Add(relativePath);
                _stored.TryGetValue(relativePath, out FileState? previous);
                if (previous is not null && file.Length > ContentComparisonLimit && previous.Matches(FileState.Capture(file, null)))
                {
                    continue;
                }

                byte[] content = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
                FileState current = FileState.Capture(file, content);
                if (previous is not null && previous.Matches(current))
                {
                    continue;
                }

                await _files.WriteAsync(StoragePath(relativePath), content, cancellationToken);
                _stored[relativePath] = current;
            }

            foreach (string relativePath in _stored.Keys.Where(path => !present.Contains(path)).ToArray())
            {
                await _files.RemoveAsync(StoragePath(relativePath), cancellationToken);
                _stored.Remove(relativePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string StoragePath(string relativePath) => $"{_storageRoot}/{relativePath}";

    private string ToMemoryPath(string relativePath)
    {
        string[] parts = relativePath.Split('/');
        string path = Path.GetFullPath(Path.Combine([_root, .. parts]));
        if (parts.Any(part => part is "" or "." or ".." || part.Contains('\\')) ||
            !path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unexpected stored file path: {relativePath}");
        }

        return path;
    }

    private sealed record FileState(long Length, DateTime LastWriteTimeUtc, byte[]? Hash)
    {
        public static FileState Capture(FileInfo file, byte[]? content)
        {
            file.Refresh();
            byte[]? hash = content is not null && content.LongLength <= ContentComparisonLimit ? SHA256.HashData(content) : null;
            return new FileState(content?.LongLength ?? file.Length, file.LastWriteTimeUtc, hash);
        }

        public bool Matches(FileState other) =>
            Length == other.Length &&
            (Hash is not null && other.Hash is not null
                ? Hash.AsSpan().SequenceEqual(other.Hash)
                : LastWriteTimeUtc == other.LastWriteTimeUtc);
    }
}
