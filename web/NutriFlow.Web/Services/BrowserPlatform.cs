using Microsoft.JSInterop;

namespace NutriFlow.Web.Services;

public sealed class BrowserPlatform(IJSRuntime js)
{
    private Task<IJSObjectReference>? _module;

    // asks the browser not to evict site data under storage pressure; installed apps usually get it without a prompt
    public async Task<bool> RequestPersistenceAsync() =>
        await (await GetModuleAsync()).InvokeAsync<bool>("requestPersistence");

    public async Task<bool> IsPersistedAsync() =>
        await (await GetModuleAsync()).InvokeAsync<bool>("isPersisted");

    private Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/platform.mjs").AsTask();
}
