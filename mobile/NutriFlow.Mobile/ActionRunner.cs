namespace NutriFlow.Mobile;

// runs one user action at a time: a transparent layer blocks input instead of disabling controls, the spinner appears
// only for slow work and hides behind dialogs, and long network steps report progress on the button that started them
internal sealed class ActionRunner(Page page, ActivityIndicator activity, BoxView blocker)
{
    private readonly List<(Button Button, string Text)> _progress = [];
    private CancellationTokenSource? _delay;
    private int _dialogs;

    public bool IsBusy { get; private set; }

    public async Task RunAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        blocker.IsVisible = true;
        StartDelay();
        Exception? failure = null;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            StopActivity();
            foreach ((Button button, string text) in _progress)
            {
                button.Text = text;
            }

            _progress.Clear();
            blocker.IsVisible = false;
            IsBusy = false;
        }

        if (failure is not null)
        {
            await AlertAsync("Не удалось выполнить действие", FormPage.ErrorText(failure), "Понятно");
        }
    }

    public void ShowProgress(Button button, string text)
    {
        _progress.Add((button, button.Text));
        button.Text = text;
        SemanticScreenReader.Announce(text);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
    {
        Pause();
        try
        {
            return await page.DisplayAlertAsync(title, message, accept, cancel);
        }
        finally
        {
            Resume();
        }
    }

    public async Task AlertAsync(string title, string message, string cancel)
    {
        Pause();
        try
        {
            await page.DisplayAlertAsync(title, message, cancel);
        }
        finally
        {
            Resume();
        }
    }

    public async Task<string?> ChooseAsync(string title, string cancel, string? destruction, params string[] buttons)
    {
        Pause();
        try
        {
            return await page.DisplayActionSheetAsync(title, cancel, destruction, buttons);
        }
        finally
        {
            Resume();
        }
    }

    private void StartDelay()
    {
        _delay?.Cancel();
        CancellationTokenSource delay = new();
        _delay = delay;
        _ = ShowActivityAsync(delay.Token);
    }

    private async Task ShowActivityAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // the delay can finish just before the action completes, so the late continuation must not resurrect the spinner
        if (cancellationToken.IsCancellationRequested || !IsBusy || _dialogs > 0)
        {
            return;
        }

        activity.IsVisible = true;
        activity.IsRunning = true;
    }

    private void StopActivity()
    {
        _delay?.Cancel();
        _delay = null;
        activity.IsRunning = false;
        activity.IsVisible = false;
    }

    private void Pause()
    {
        _dialogs++;
        StopActivity();
    }

    private void Resume()
    {
        _dialogs--;
        if (IsBusy && _dialogs == 0)
        {
            StartDelay();
        }
    }
}
