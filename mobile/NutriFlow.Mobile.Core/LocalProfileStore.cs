using System.Text.Json;

namespace NutriFlow.Mobile.Core;

public sealed class LocalProfileStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _rootDirectory;
    private readonly string _catalogPath;

    public LocalProfileStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _catalogPath = Path.Combine(_rootDirectory, "profiles.json");
    }

    public async Task<IReadOnlyList<LocalProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadAsync(cancellationToken)).Profiles.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalProfile?> GetSelectedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ProfileCatalog catalog = await ReadAsync(cancellationToken);
            return catalog.Profiles.SingleOrDefault(profile => profile.Id == catalog.SelectedId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalProfile> RequireAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Profile ID cannot be empty.", nameof(id));
        }
        return (await ListAsync(cancellationToken)).SingleOrDefault(profile => profile.Id == id)
            ?? throw new KeyNotFoundException("Локальный профиль не найден.");
    }

    public async Task<LocalProfile> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim().Normalize();
        if (name.Length > 80 || name.Any(char.IsControl))
        {
            throw new ArgumentException("Название профиля должно содержать от 1 до 80 обычных символов.", nameof(name));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ProfileCatalog catalog = await ReadAsync(cancellationToken);
            if (catalog.Profiles.Any(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("Профиль с таким названием уже существует.", nameof(name));
            }

            if (catalog.Profiles.Count >= 30)
            {
                throw new InvalidOperationException("На этом устройстве уже создано 30 профилей.");
            }

            LocalProfile profile = new(Guid.NewGuid(), name);
            await SaveAsync(new ProfileCatalog(1, profile.Id, [.. catalog.Profiles, profile]), cancellationToken);
            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocalProfile> SelectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ProfileCatalog catalog = await ReadAsync(cancellationToken);
            LocalProfile profile = catalog.Profiles.SingleOrDefault(item => item.Id == id)
                ?? throw new KeyNotFoundException("Локальный профиль не найден.");
            await SaveAsync(catalog with { SelectedId = id }, cancellationToken);
            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal string GetDatabasePath(Guid id) => Path.Combine(GetProfileDirectory(id), "nutriflow.db");

    internal string GetPhotoDirectory(Guid id) => Path.Combine(GetProfileDirectory(id), "label-photos");

    private string GetProfileDirectory(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Profile ID cannot be empty.", nameof(id));
        }

        return Path.Combine(_rootDirectory, "profiles", id.ToString("N"));
    }

    private async Task<ProfileCatalog> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_catalogPath))
        {
            return new ProfileCatalog(1, null, []);
        }

        try
        {
            await using FileStream stream = File.OpenRead(_catalogPath);
            ProfileCatalog catalog = await JsonSerializer.DeserializeAsync<ProfileCatalog>(stream, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Файл профилей пуст.");
            if (catalog.Version != 1 || catalog.Profiles is null || catalog.Profiles.Count > 30 ||
                catalog.Profiles.Any(profile => profile is null || profile.Id == Guid.Empty ||
                    string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80 || profile.Name.Any(char.IsControl)) ||
                catalog.Profiles.Select(profile => profile.Id).Distinct().Count() != catalog.Profiles.Count ||
                catalog.Profiles.Select(profile => profile.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Profiles.Count ||
                (catalog.SelectedId is not null && !catalog.Profiles.Any(profile => profile.Id == catalog.SelectedId)))
            {
                throw new InvalidDataException("Файл профилей повреждён. Его содержимое сохранено без изменений.");
            }

            return catalog;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Не удалось прочитать профили. Исходный файл не изменён.", exception);
        }
    }

    private async Task SaveAsync(ProfileCatalog catalog, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_rootDirectory);
        string temporaryPath = Path.Combine(_rootDirectory, $"profiles.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, catalog, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _catalogPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record ProfileCatalog(int Version, Guid? SelectedId, List<LocalProfile> Profiles);
}
