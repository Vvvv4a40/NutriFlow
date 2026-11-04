namespace NutriFlow.Ui.Dialogs;

// alerts, confirmations and action sheets in place of the system dialogs of the android app; DialogHost shows them one at a time
public sealed class DialogService
{
    private readonly Queue<DialogRequest> _queue = new();

    public DialogRequest? Current => _queue.Count > 0 ? _queue.Peek() : null;

    public event Action? Changed;

    // destructive: the accept button takes the danger colour, as «Удалить» does in the action sheet before it
    public async Task<bool> ConfirmAsync(string title, string message, string accept, string cancel, bool destructive = false) =>
        await ShowAsync(new DialogRequest(title, message,
            [new DialogButton(accept, destructive ? DialogButtonRole.Destructive : DialogButtonRole.Primary)], cancel, false)) == accept;

    public Task AlertAsync(string title, string message, string cancel) =>
        ShowAsync(new DialogRequest(title, message, [], cancel, false));

    // the answer is the text of the chosen button, or null for cancel
    public Task<string?> ChooseAsync(string title, string cancel, string? destruction, params string[] buttons)
    {
        List<DialogButton> choices = [.. buttons.Select(text => new DialogButton(text, DialogButtonRole.Default))];
        if (destruction is not null)
        {
            choices.Add(new DialogButton(destruction, DialogButtonRole.Destructive));
        }

        return ShowAsync(new DialogRequest(title, null, choices, cancel, true));
    }

    // for choices named by the user: the answer is the Key of the button, so a name cannot pass for another button
    public Task<string?> ChooseAsync(string title, string cancel, IReadOnlyList<DialogButton> choices) =>
        ShowAsync(new DialogRequest(title, null, choices, cancel, true));

    internal void Resolve(DialogRequest request, string? answer)
    {
        if (Current != request)
        {
            return;
        }

        _queue.Dequeue();
        request.Completion.TrySetResult(answer);
        Changed?.Invoke();
    }

    private Task<string?> ShowAsync(DialogRequest request)
    {
        _queue.Enqueue(request);
        Changed?.Invoke();
        return request.Completion.Task;
    }
}

public sealed class DialogRequest(string title, string? message, IReadOnlyList<DialogButton> buttons, string cancel, bool sheet)
{
    public string Title { get; } = title;

    public string? Message { get; } = message;

    public IReadOnlyList<DialogButton> Buttons { get; } = buttons;

    public string Cancel { get; } = cancel;

    // an action sheet slides up from the bottom; an alert sits in the middle
    public bool Sheet { get; } = sheet;

    internal TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed record DialogButton(string Text, DialogButtonRole Role, string? Key = null, bool Current = false);

public enum DialogButtonRole
{
    Default,
    Primary,
    Destructive
}
