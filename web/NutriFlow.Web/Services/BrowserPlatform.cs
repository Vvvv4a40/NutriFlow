using Microsoft.JSInterop;

namespace NutriFlow.Web.Services;

public sealed class BrowserPlatform(IJSRuntime js) : IDisposable
{
    private Task<IJSObjectReference>? _module;
    private DotNetObjectReference<BrowserPlatform>? _reference;

    public bool CanInstall { get; private set; }

    public event Action? InstallChanged;

    // boot.mjs keeps the install prompt from the first moments of the page; from here on it reports changes
    public async Task StartAsync()
    {
        _reference ??= DotNetObjectReference.Create(this);
        CanInstall = await (await GetModuleAsync()).InvokeAsync<bool>("watchInstall", _reference);
    }

    [JSInvokable]
    public void OnInstallAvailabilityChanged(bool available)
    {
        if (CanInstall != available)
        {
            CanInstall = available;
            InstallChanged?.Invoke();
        }
    }

    public async Task<bool> InstallAsync() =>
        await (await GetModuleAsync()).InvokeAsync<string>("promptInstall") == "accepted";

    // asks the browser not to evict site data under storage pressure; installed apps usually get it without a prompt
    public async Task<bool> RequestPersistenceAsync() =>
        await (await GetModuleAsync()).InvokeAsync<bool>("requestPersistence");

    public async Task<bool> IsPersistedAsync() =>
        await (await GetModuleAsync()).InvokeAsync<bool>("isPersisted");

    private Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/platform.mjs").AsTask();

    public void Dispose() => _reference?.Dispose();
}
