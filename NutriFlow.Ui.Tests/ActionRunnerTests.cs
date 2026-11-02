using NutriFlow.Ui.Dialogs;

namespace NutriFlow.Ui.Tests;

public sealed class ActionRunnerTests
{
    private readonly DialogService _dialogs = new();
    private readonly List<TaskCompletionSource> _delays = [];

    [Fact]
    public async Task SecondActionIsIgnoredWhileTheFirstRuns()
    {
        ActionRunner runner = Create();
        TaskCompletionSource release = new();
        int runs = 0;

        Task first = runner.RunAsync(async () => { runs++; await release.Task; });
        await runner.RunAsync(() => { runs++; return Task.CompletedTask; });
        Assert.True(runner.IsBusy);

        release.SetResult();
        await first;
        Assert.Equal(1, runs);
        Assert.False(runner.IsBusy);
    }

    [Fact]
    public async Task SpinnerAppearsOnlyAfterTheDelayAndNotAfterTheAction()
    {
        ActionRunner runner = Create();
        TaskCompletionSource release = new();
        Task action = runner.RunAsync(() => release.Task);
        Assert.False(runner.ShowsActivity);

        _delays[0].SetResult();
        await WaitUntilAsync(() => runner.ShowsActivity);

        release.SetResult();
        await action;
        Assert.False(runner.ShowsActivity);
    }

    [Fact]
    public async Task FastActionNeverShowsTheSpinner()
    {
        ActionRunner runner = Create();
        await runner.RunAsync(() => Task.CompletedTask);

        _delays[0].SetResult();
        await Task.Delay(50);

        Assert.False(runner.ShowsActivity);
    }

    [Fact]
    public async Task ProgressTextBelongsToItsButtonUntilTheActionEnds()
    {
        ActionRunner runner = Create();
        TaskCompletionSource release = new();
        Task action = runner.RunAsync(async () =>
        {
            runner.ShowProgress("parse", "Разбираю описание…");
            await release.Task;
        });
        Assert.Equal(("parse", "Разбираю описание…"), (runner.ProgressKey, runner.ProgressText));

        release.SetResult();
        await action;
        Assert.Null(runner.ProgressKey);
        Assert.Null(runner.ProgressText);
    }

    [Fact]
    public async Task FailureIsShownAsAnAlertWithTheRussianMessage()
    {
        ActionRunner runner = Create();
        Task action = runner.RunAsync(() => throw new InvalidOperationException("Укажите вес продукта."));

        DialogRequest alert = Assert.IsType<DialogRequest>(_dialogs.Current);
        Assert.Equal("Не удалось выполнить действие", alert.Title);
        Assert.Equal("Укажите вес продукта.", alert.Message);
        Assert.Equal("Понятно", alert.Cancel);
        Assert.False(runner.IsBusy);

        _dialogs.Resolve(alert, null);
        await action;
    }

    [Fact]
    public async Task SpinnerHidesBehindADialogAndReturnsAfterIt()
    {
        ActionRunner runner = Create();
        TaskCompletionSource release = new();
        Task action = runner.RunAsync(async () =>
        {
            await runner.ConfirmAsync("Удалить запись?", "Запись исчезнет из дневника.", "Удалить", "Оставить");
            await release.Task;
        });
        await WaitUntilAsync(() => _dialogs.Current is not null);
        _delays[0].SetResult();
        await Task.Delay(50);
        Assert.False(runner.ShowsActivity);

        _dialogs.Resolve(_dialogs.Current!, "Удалить");
        await WaitUntilAsync(() => _delays.Count == 2);
        _delays[1].SetResult();
        await WaitUntilAsync(() => runner.ShowsActivity);

        release.SetResult();
        await action;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    // the fake timer ignores cancellation, like a timer that already fired: the runner itself must drop late spinners
    private ActionRunner Create() => new(_dialogs, _ =>
    {
        TaskCompletionSource delay = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _delays.Add(delay);
        return delay.Task;
    });
}
