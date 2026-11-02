using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace NutriFlow.Ui.Navigation;

// layers the system back closes - forms, dialogs, sections other than the diary - each own one browser history entry.
// back from the phone, the browser or a back control in the app all become popstate, and the top layer closes there
public sealed class BackStack : IAsyncDisposable
{
    private readonly Func<Task> _pushEntry;
    private readonly Func<Task> _goBack;
    private readonly Func<bool> _isBusy;
    private readonly List<BackLayer> _layers = [];
    private readonly IJSRuntime? _js;
    private IJSObjectReference? _module;
    private DotNetObjectReference<BackStack>? _reference;
    // a browser step back finishes later with popstate; a push before that would cancel it and shift every entry
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(1);
    private int _ignoredSteps;
    private bool _stepPending;
    private TaskCompletionSource? _step;

    public BackStack(IJSRuntime js, ActionRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _js = js;
        _pushEntry = async () => await (await GetModuleAsync()).InvokeVoidAsync("push");
        _goBack = async () => await (await GetModuleAsync()).InvokeVoidAsync("back");
        _isBusy = () => runner.IsBusy;
    }

    internal BackStack(Func<Task> pushEntry, Func<Task> goBack, Func<bool> isBusy)
    {
        _pushEntry = pushEntry;
        _goBack = goBack;
        _isBusy = isBusy;
    }

    public int Depth => _layers.Count;

    public async Task StartAsync()
    {
        if (_js is null || _reference is not null)
        {
            return;
        }

        _reference = DotNetObjectReference.Create(this);
        await (await GetModuleAsync()).InvokeVoidAsync("start", _reference);
    }

    // closesWhileBusy: a dialog shown by a running action can be dismissed, a form under a running action cannot
    public async Task<BackLayer> PushAsync(Func<Task> onBack, bool closesWhileBusy = false)
    {
        ArgumentNullException.ThrowIfNull(onBack);
        BackLayer layer = new(this, onBack, closesWhileBusy);
        _layers.Add(layer);
        await WaitForStepAsync();
        await _pushEntry();
        return layer;
    }

    // a back control inside the app: the browser takes the step and popstate closes the layer
    public async Task BackAsync()
    {
        if (_layers.Count == 0 || _stepPending)
        {
            return;
        }

        _stepPending = true;
        await StepBackAsync();
    }

    // a full-screen layer hides what lies under it from tab and screen readers and takes the focus;
    // leaving it gives the focus back to the control that opened it
    public async Task EnterLayerAsync(string key, ElementReference layer, ElementReference focus)
    {
        if (_js is not null)
        {
            IJSObjectReference module = await GetModuleAsync();
            await module.InvokeVoidAsync("enterLayer", key, layer);
            await module.InvokeVoidAsync("focus", focus);
        }
    }

    // keyboard and screen reader activation pass the transparent blocker, so they are dropped while an action runs
    public async Task SetInputBlockedAsync(bool blocked)
    {
        if (_js is not null)
        {
            await (await GetModuleAsync()).InvokeVoidAsync("setInputBlocked", blocked);
        }
    }

    public async Task LeaveLayerAsync(string key)
    {
        if (_js is not null)
        {
            try
            {
                await (await GetModuleAsync()).InvokeVoidAsync("leaveLayer", key);
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    [JSInvokable]
    public async Task OnPopStateAsync()
    {
        _stepPending = false;
        _step?.TrySetResult();
        if (_ignoredSteps > 0)
        {
            _ignoredSteps--;
            return;
        }

        if (_layers.Count == 0)
        {
            return;
        }

        BackLayer top = _layers[^1];
        if (_isBusy() && !top.ClosesWhileBusy)
        {
            // the running action keeps its screen, so the entry the browser removed comes back
            await _pushEntry();
            return;
        }

        _layers.RemoveAt(_layers.Count - 1);
        await top.OnBack();
    }

    // a layer the app closed itself (a saved form, a chosen dialog button) gives back its entry without its back action
    internal async Task RemoveAsync(BackLayer layer)
    {
        if (!_layers.Remove(layer))
        {
            return;
        }

        _ignoredSteps++;
        await StepBackAsync();
    }

    private async Task StepBackAsync()
    {
        _step = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _goBack();
    }

    // the timeout keeps the app usable if a browser never reports the step
    private async Task WaitForStepAsync()
    {
        if (_step is { Task.IsCompleted: false } step)
        {
            await Task.WhenAny(step.Task, Task.Delay(StepTimeout));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _reference?.Dispose();
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    private async Task<IJSObjectReference> GetModuleAsync() =>
        _module ??= await _js!.InvokeAsync<IJSObjectReference>("import", "./_content/NutriFlow.Ui/navigation.mjs");
}

public sealed class BackLayer
{
    private readonly BackStack _stack;

    internal BackLayer(BackStack stack, Func<Task> onBack, bool closesWhileBusy)
    {
        _stack = stack;
        OnBack = onBack;
        ClosesWhileBusy = closesWhileBusy;
    }

    internal Func<Task> OnBack { get; }

    internal bool ClosesWhileBusy { get; }

    public Task RemoveAsync() => _stack.RemoveAsync(this);
}
