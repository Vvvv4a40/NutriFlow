using NutriFlow.Ui.Dialogs;

namespace NutriFlow.Ui;

// runs one user action at a time: a transparent layer blocks input instead of disabling controls, the spinner appears
// only for slow work and hides behind dialogs, and long network steps report progress on the button that started them
public sealed class ActionRunner
{
    private readonly DialogService _dialogs;
    private readonly Func<CancellationToken, Task> _activityDelay;
    private CancellationTokenSource? _delay;
    private int _dialogsOpen;

    public ActionRunner(DialogService dialogs)
        : this(dialogs, token => Task.Delay(250, token))
    {
    }

    internal ActionRunner(DialogService dialogs, Func<CancellationToken, Task> activityDelay)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        _dialogs = dialogs;
        _activityDelay = activityDelay;
    }

    public bool IsBusy { get; private set; }

    public bool ShowsActivity { get; private set; }

    public string? ProgressKey { get; private set; }

    public string? ProgressText { get; private set; }

    // the live region of UiRoot reads it; every call is a new message, even with the same text
    public string? Announcement { get; private set; }

    public int AnnouncementNumber { get; private set; }

    public event Action? Changed;

    public async Task RunAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Changed?.Invoke();
        StartDelay();
        Exception? failure = null;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            // the dialog shows a next step only; the cause stays for the browser console or the debugger
            UiLog.Error(exception);
            failure = exception;
        }
        finally
        {
            StopActivity();
            ProgressKey = null;
            ProgressText = null;
            IsBusy = false;
            Changed?.Invoke();
        }

        if (failure is not null)
        {
            await AlertAsync("Не удалось выполнить действие", UiErrors.Describe(failure), "Понятно");
        }
    }

    // the button whose ProgressKey matches shows the text, and screen readers hear it through the live region of UiRoot
    public void ShowProgress(string key, string text)
    {
        ProgressKey = key;
        ProgressText = text;
        Changed?.Invoke();
    }

    // for a change screen readers would miss, like an error on the field that already has the focus
    public void Announce(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        Announcement = text;
        AnnouncementNumber++;
        Changed?.Invoke();
    }

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel, bool destructive = false) =>
        WithDialogAsync(() => _dialogs.ConfirmAsync(title, message, accept, cancel, destructive));

    public Task AlertAsync(string title, string message, string cancel) =>
        WithDialogAsync(async () =>
        {
            await _dialogs.AlertAsync(title, message, cancel);
            return true;
        });

    public Task<string?> ChooseAsync(string title, string cancel, string? destruction, params string[] buttons) =>
        WithDialogAsync(() => _dialogs.ChooseAsync(title, cancel, destruction, buttons));

    private async Task<T> WithDialogAsync<T>(Func<Task<T>> show)
    {
        _dialogsOpen++;
        StopActivity();
        try
        {
            return await show();
        }
        finally
        {
            _dialogsOpen--;
            if (IsBusy && _dialogsOpen == 0)
            {
                StartDelay();
            }
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
            await _activityDelay(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // the delay can finish just before the action completes, so the late continuation must not resurrect the spinner
        if (cancellationToken.IsCancellationRequested || !IsBusy || _dialogsOpen > 0)
        {
            return;
        }

        ShowsActivity = true;
        Changed?.Invoke();
    }

    private void StopActivity()
    {
        _delay?.Cancel();
        _delay = null;
        if (ShowsActivity)
        {
            ShowsActivity = false;
            Changed?.Invoke();
        }
    }
}
