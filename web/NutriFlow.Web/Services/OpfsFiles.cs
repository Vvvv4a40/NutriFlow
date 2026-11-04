using Microsoft.JSInterop;

namespace NutriFlow.Web.Services;

// files in the origin private file system through js/storage.mjs; paths use "/" and never start with it
public sealed class OpfsFiles(IJSRuntime js)
{
    private Task<IJSObjectReference>? _module;

    public async Task<IReadOnlyList<string>> ListAsync(string directory, CancellationToken cancellationToken = default) =>
        await (await GetModuleAsync()).InvokeAsync<string[]>("listFiles", cancellationToken, directory);

    public async Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        await (await GetModuleAsync()).InvokeAsync<byte[]?>("readFile", cancellationToken, path);

    public async Task WriteAsync(string path, byte[] content, CancellationToken cancellationToken = default) =>
        await (await GetModuleAsync()).InvokeVoidAsync("writeFile", cancellationToken, path, content);

    public async Task RemoveAsync(string path, CancellationToken cancellationToken = default) =>
        await (await GetModuleAsync()).InvokeVoidAsync("removeFile", cancellationToken, path);

    // a database file for bsqlite, without the header of the mirrored files
    public async Task WriteDatabaseAsync(string path, byte[] content, CancellationToken cancellationToken = default) =>
        await (await GetModuleAsync()).InvokeVoidAsync("writeDatabase", cancellationToken, path, content);

    public async Task RemoveDatabaseAsync(string path, CancellationToken cancellationToken = default) =>
        await (await GetModuleAsync()).InvokeVoidAsync("removeDatabase", cancellationToken, path);

    private Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/storage.mjs").AsTask();
}
