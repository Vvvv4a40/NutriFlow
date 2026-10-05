using System.Security.Cryptography;

namespace NutriFlow.Infrastructure.Backups;

internal static class BackupFiles
{
    private static readonly string[] DatabaseSuffixes = ["", "-wal", "-shm", "-journal"];

    public static string FullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        EnsureNoLinks(fullPath);
        return fullPath;
    }

    public static void EnsureNoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("Symbolic links and junctions are not supported for backups.");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    public static bool IsWithin(string path, string directory)
    {
        string prefix = Path.EndsInDirectorySeparator(directory)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return path.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsDatabaseDestination(string destinationPath, string databasePath) =>
        DatabaseSuffixes.Any(suffix => IsWithin(destinationPath, databasePath + suffix));

    public static void ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(['/', '\\', ':']) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("A backup file name is invalid.");
        }
    }

    public static string[] ReadPhotoNames(string directory)
    {
        EnsureNoLinks(directory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The label-photo directory does not exist.");
        }

        string[] entries = Directory.GetFileSystemEntries(directory);
        List<string> names = new(entries.Length);
        foreach (string entry in entries)
        {
            EnsureNoLinks(entry);
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
            {
                throw new InvalidDataException("The label-photo directory must not contain nested directories.");
            }
            string name = Path.GetFileName(entry);
            ValidateFileName(name);
            names.Add(name);
        }
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
        {
            throw new InvalidDataException("Photo names must be unique regardless of letter case.");
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    public static string[] ReadDatabaseSuffixes(string databasePath)
    {
        List<string> suffixes = new();
        foreach (string suffix in DatabaseSuffixes)
        {
            string path = databasePath + suffix;
            EnsureNoLinks(path);
            if (Directory.Exists(path))
            {
                throw new InvalidDataException("A database file cannot be a directory.");
            }
            if (File.Exists(path))
            {
                suffixes.Add(suffix);
            }
        }
        if (!suffixes.Contains(""))
        {
            throw new FileNotFoundException("The source database does not exist.");
        }
        return suffixes.ToArray();
    }

    public static async Task<BackupFile> CopyAsync(
        string sourcePath, string destinationPath, string relativePath, CancellationToken cancellationToken)
    {
        EnsureNoLinks(sourcePath);
        await using FileStream source = OpenRead(sourcePath);
        await using FileStream destination = new(destinationPath, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        source.Position = 0;
        byte[] hash = await SHA256.HashDataAsync(source, cancellationToken);
        return new BackupFile(relativePath, destination.Length, Convert.ToHexString(hash));
    }

    public static async Task<BackupFile> DescribeAsync(
        string path, string relativePath, CancellationToken cancellationToken)
    {
        EnsureNoLinks(path);
        await using FileStream stream = OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return new BackupFile(relativePath, stream.Length, Convert.ToHexString(hash));
    }

    public static FileStream OpenRead(string path) => new(path, FileMode.Open,
        FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    public static void DeleteOwnedDirectory(string path)
    {
        path = Path.GetFullPath(path);
        string name = Path.GetFileName(path);
        bool isStaging = IsStagingName(name);
        bool isSourceCopy = name == "source" && IsStagingName(Path.GetFileName(Path.GetDirectoryName(path)));
        const string checkPrefix = "nutriflow-backup-check-";
        bool isCheckCopy = name.StartsWith(checkPrefix, StringComparison.Ordinal) &&
                           Guid.TryParseExact(name[checkPrefix.Length..], "N", out _);
        if (!isStaging && !isSourceCopy && !isCheckCopy)
        {
            throw new InvalidOperationException("Only a private backup working directory may be removed.");
        }
        EnsureNoLinks(path);
        Directory.Delete(path, recursive: true);
    }

    private static bool IsStagingName(string? name)
    {
        const string prefix = ".nutriflow-backup-";
        const string suffix = ".partial";
        return name is not null && name.StartsWith(prefix, StringComparison.Ordinal) &&
               name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > prefix.Length + suffix.Length &&
               Guid.TryParseExact(name[prefix.Length..^suffix.Length], "N", out _);
    }
}
