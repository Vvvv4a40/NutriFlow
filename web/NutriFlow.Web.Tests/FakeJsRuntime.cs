using Microsoft.JSInterop;

namespace NutriFlow.Web.Tests;

// localStorage and the storage.mjs module of the browser, kept in memory
internal sealed class FakeJsRuntime : IJSRuntime
{
    public Dictionary<string, string> LocalStorage { get; } = new(StringComparer.Ordinal);

    public FakeStorageModule Storage { get; } = new();

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
                result = Files.Keys.Where(key => key.StartsWith(path + "/", StringComparison.Ordinal))
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
            default:
                throw new InvalidOperationException($"Unexpected call: {identifier}");
        }

        return ValueTask.FromResult((TValue)result!);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
