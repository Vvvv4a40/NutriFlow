using Microsoft.JSInterop;
using NutriFlow.Ui.Hosting;

namespace NutriFlow.Web.Tests;

// localStorage and the storage.mjs module of the browser, kept in memory
internal sealed class FakeJsRuntime : IJSRuntime
{
    public Dictionary<string, string> LocalStorage { get; } = new(StringComparer.Ordinal);

    public FakeStorageModule Storage { get; } = new();

    public FakePlatformModule Platform { get; } = new();

    public FakeMediaModule Media { get; } = new();

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        object?[] values = args ?? [];
        object? result = identifier switch
        {
            "localStorage.getItem" => LocalStorage.GetValueOrDefault((string)values[0]!),
            "localStorage.setItem" => Set((string)values[0]!, (string)values[1]!),
            "localStorage.removeItem" => Remove((string)values[0]!),
            "import" when (string?)values[0] == "./js/storage.mjs" => Storage,
            "import" when (string?)values[0] == "./js/platform.mjs" => Platform,
            "import" when (string?)values[0] == "./js/media.mjs" => Media,
            _ => throw new InvalidOperationException($"Unexpected call: {identifier}")
        };
        return ValueTask.FromResult((TValue)result!);
    }

    private object? Set(string key, string value)
    {
        LocalStorage[key] = value;
        return null;
    }

    private object? Remove(string key)
    {
        LocalStorage.Remove(key);
        return null;
    }
}

internal sealed class FakeStorageModule : IJSObjectReference
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public List<string> Writes { get; } = [];

    public List<string> Removals { get; } = [];

    public bool FailWrites { get; set; }

    // database files for bsqlite, written without the header of the mirrored files
    public Dictionary<string, byte[]> Databases { get; } = new(StringComparer.Ordinal);

    public List<string> DatabaseRemovals { get; } = [];

    // removals that fail as if a closing bsqlite worker still held the file
    public int LockedDatabaseRemovals { get; set; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        object?[] values = args ?? [];
        string path = (string)values[0]!;
        object? result;
        switch (identifier)
        {
            case "listFiles":
                result = Files.Keys.Concat(Databases.Keys).Where(key => key.StartsWith(path + "/", StringComparison.Ordinal))
                    .Select(key => key[(path.Length + 1)..]).Order(StringComparer.Ordinal).ToArray();
                break;
            case "readFile":
                result = Files.TryGetValue(path, out byte[]? content) ? content.ToArray() : null;
                break;
            case "writeFile":
                if (FailWrites)
                {
                    throw new JSException("QuotaExceededError: The quota has been exceeded.");
                }

                Files[path] = ((byte[])values[1]!).ToArray();
                Writes.Add(path);
                result = null;
                break;
            case "removeFile":
                Files.Remove(path);
                Removals.Add(path);
                result = null;
                break;
            case "writeDatabase":
                Databases[path] = ((byte[])values[1]!).ToArray();
                result = null;
                break;
            case "removeDatabase":
                if (LockedDatabaseRemovals > 0)
                {
                    LockedDatabaseRemovals--;
                    throw new JSException("NoModificationAllowedError: An attempt was made to modify an object where modifications are not allowed.");
                }

                Databases.Remove(path);
                DatabaseRemovals.Add(path);
                result = null;
                break;
            default:
                throw new InvalidOperationException($"Unexpected call: {identifier}");
        }

        return ValueTask.FromResult((TValue)result!);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// media.mjs: the photo chosen in the input and the recorder over speech-capture.mjs
internal sealed class FakeMediaModule : IJSObjectReference
{
    public PickedPhoto? Photo { get; set; }

    public PickedBarcode? Barcode { get; set; }

    public bool PhotoFails { get; set; }

    public FakeVoiceCaptureObject Capture { get; } = new();

    public int? MaximumMilliseconds { get; private set; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        object? result;
        switch (identifier)
        {
            case "readPhoto":
                if (PhotoFails)
                {
                    throw new JSException("EncodingError: The source image cannot be decoded.");
                }

                result = Photo;
                break;
            case "readBarcode":
                if (PhotoFails)
                {
                    throw new JSException("EncodingError: The source image cannot be decoded.");
                }

                result = Barcode;
                break;
            case "createVoiceCapture":
                MaximumMilliseconds = (int)args![1]!;
                result = Capture;
                break;
            default:
                throw new InvalidOperationException($"Unexpected call: {identifier}");
        }

        return ValueTask.FromResult((TValue)result!);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeVoiceCaptureObject : IJSObjectReference
{
    public List<string> Calls { get; } = [];

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        Calls.Add(identifier);
        return ValueTask.FromResult(default(TValue)!);
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        InvokeAsync<TValue>(identifier, args);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// platform.mjs: installation as chrome offers it and the persistent storage flag
internal sealed class FakePlatformModule : IJSObjectReference
{
    public bool InstallAvailable { get; set; }

    public string InstallOutcome { get; set; } = "accepted";

    public bool Persisted { get; set; }

    public int PersistenceRequests { get; private set; }

    public int PersistenceRenewals { get; private set; }

    public string SaveOutcome { get; set; } = "saved";

    public List<(string Name, string ContentType, byte[] Content)> SavedFiles { get; } = [];

    public object? Watcher { get; private set; }

    public object? VisibilityWatcher { get; private set; }

    public bool UpdateWaiting { get; set; }

    public object? UpdateWatcher { get; private set; }

    public object? InstalledAppWatcher { get; private set; }

    public int AppliedUpdates { get; private set; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        object? result;
        switch (identifier)
        {
            case "watchVisibility":
                VisibilityWatcher = args![0];
                result = null;
                break;
            case "watchUpdates":
                UpdateWatcher = args![0];
                result = UpdateWaiting;
                break;
            case "watchInstalledApp":
                InstalledAppWatcher = args![0];
                result = null;
                break;
            case "applyUpdate":
                AppliedUpdates++;
                result = null;
                break;
            case "watchInstall":
                Watcher = args![0];
                result = InstallAvailable;
                break;
            case "promptInstall":
                result = InstallAvailable ? InstallOutcome : "unavailable";
                break;
            case "isPersisted":
                result = Persisted;
                break;
            case "requestPersistence":
                PersistenceRequests++;
                result = Persisted;
                break;
            case "renewPersistence":
                PersistenceRenewals++;
                result = Persisted;
                break;
            case "saveFile":
                SavedFiles.Add(((string)args![0]!, (string)args[1]!, (byte[])args[2]!));
                result = SaveOutcome;
                break;
            default:
                throw new InvalidOperationException($"Unexpected call: {identifier}");
        }

        return ValueTask.FromResult((TValue)result!);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
