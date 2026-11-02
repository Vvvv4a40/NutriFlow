using NutriFlow.Ui.Navigation;

namespace NutriFlow.Ui.Tests;

public sealed class BackStackTests
{
    private int _entries;
    private int _backSteps;
    private bool _busy;

    [Fact]
    public async Task SystemBackClosesTheTopLayerOnly()
    {
        BackStack stack = Create();
        List<string> closed = [];
        await stack.PushAsync(() => Close(closed, "section"));
        await stack.PushAsync(() => Close(closed, "form"));

        await stack.OnPopStateAsync();

        Assert.Equal(["form"], closed);
        Assert.Equal(1, stack.Depth);
        Assert.Equal(2, _entries);
    }

    [Fact]
    public async Task BackControlGoesThroughTheBrowserOnceEvenWhenPressedTwice()
    {
        BackStack stack = Create();
        List<string> closed = [];
        await stack.PushAsync(() => Close(closed, "section"));
        await stack.PushAsync(() => Close(closed, "form"));

        await stack.BackAsync();
        await stack.BackAsync();
        Assert.Equal(1, _backSteps);
        Assert.Empty(closed);

        await stack.OnPopStateAsync();
        Assert.Equal(["form"], closed);
    }

    [Fact]
    public async Task LayerClosedByTheAppGivesBackItsEntryWithoutItsBackAction()
    {
        BackStack stack = Create();
        List<string> closed = [];
        await stack.PushAsync(() => Close(closed, "section"));
        BackLayer form = await stack.PushAsync(() => Close(closed, "form"));

        await form.RemoveAsync();
        await form.RemoveAsync();
        Assert.Equal(1, _backSteps);
        await stack.OnPopStateAsync();

        Assert.Empty(closed);
        Assert.Equal(1, stack.Depth);
        await stack.OnPopStateAsync();
        Assert.Equal(["section"], closed);
    }

    [Fact]
    public async Task NewLayerWaitsUntilTheBrowserFinishedTheStepOfAClosedOne()
    {
        BackStack stack = Create();
        List<string> closed = [];
        await stack.PushAsync(() => Close(closed, "section"));
        BackLayer sheet = await stack.PushAsync(() => Close(closed, "sheet"), closesWhileBusy: true);

        await sheet.RemoveAsync();
        Task<BackLayer> confirm = stack.PushAsync(() => Close(closed, "confirm"), closesWhileBusy: true);
        await Task.Delay(50);
        Assert.Equal(2, _entries);
        Assert.False(confirm.IsCompleted);

        await stack.OnPopStateAsync();
        await confirm;
        Assert.Equal(3, _entries);
        Assert.Empty(closed);

        await stack.OnPopStateAsync();
        await stack.OnPopStateAsync();
        Assert.Equal(["confirm", "section"], closed);
    }

    [Fact]
    public async Task RunningActionKeepsItsFormAndTheEntryComesBack()
    {
        BackStack stack = Create();
        List<string> closed = [];
        await stack.PushAsync(() => Close(closed, "form"));
        _busy = true;

        await stack.OnPopStateAsync();

        Assert.Empty(closed);
        Assert.Equal(1, stack.Depth);
        Assert.Equal(2, _entries);
    }

    [Fact]
    public async Task DialogOfARunningActionStillClosesWithBack()
    {
        BackStack stack = Create();
        List<string> closed = [];
        await stack.PushAsync(() => Close(closed, "form"));
        await stack.PushAsync(() => Close(closed, "dialog"), closesWhileBusy: true);
        _busy = true;

        await stack.OnPopStateAsync();

        Assert.Equal(["dialog"], closed);
    }

    [Fact]
    public async Task BackWithoutLayersLeavesTheAppToTheBrowser()
    {
        BackStack stack = Create();

        await stack.BackAsync();
        await stack.OnPopStateAsync();

        Assert.Equal(0, _backSteps);
        Assert.Equal(0, stack.Depth);
    }

    private BackStack Create() => new(
        () => { _entries++; return Task.CompletedTask; },
        () => { _backSteps++; return Task.CompletedTask; },
        () => _busy);

    private static Task Close(List<string> closed, string name)
    {
        closed.Add(name);
        return Task.CompletedTask;
    }
}
