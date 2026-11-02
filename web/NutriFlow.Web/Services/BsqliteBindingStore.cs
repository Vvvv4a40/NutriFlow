using Bit.Bsqlite;
using Microsoft.JSInterop;

namespace NutriFlow.Web.Services;

// the bsqlite default keeps bindings under bit-bsqlite.* keys; github.io shares one origin between the pages of an account
public sealed class BsqliteBindingStore(IJSRuntime js) : IBsqliteStorageBindingStore
{
    internal const string KeyPrefix = "nutriflow.bsqlite-binding.";

    public ValueTask<string?> GetProviderNameAsync(string databaseName, CancellationToken cancellationToken = default) =>
        js.InvokeAsync<string?>("localStorage.getItem", cancellationToken, KeyPrefix + databaseName);

    public ValueTask SetProviderNameAsync(string databaseName, string providerName, CancellationToken cancellationToken = default) =>
        js.InvokeVoidAsync("localStorage.setItem", cancellationToken, KeyPrefix + databaseName, providerName);

    public ValueTask RemoveAsync(string databaseName, CancellationToken cancellationToken = default) =>
        js.InvokeVoidAsync("localStorage.removeItem", cancellationToken, KeyPrefix + databaseName);
}
