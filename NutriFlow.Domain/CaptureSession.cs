namespace NutriFlow.Domain;

public sealed class CaptureSession
{
    private readonly List<InputEvent> _inputEvents;

    public CaptureSession()
    {
        _inputEvents = new List<InputEvent>();
        InputEvents = _inputEvents.AsReadOnly();
        State = CaptureSessionState.Collecting;
    }

    public CaptureSessionState State { get; private set; }
    public IReadOnlyList<InputEvent> InputEvents { get; }

    public void AddEvent(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        EnsureState(CaptureSessionState.Collecting, "add an input event");

        _inputEvents.Add(inputEvent);
    }

    public void FinishCollecting()
    {
        EnsureState(CaptureSessionState.Collecting, "finish collecting input");

        if (_inputEvents.Count == 0)
        {
            throw new InvalidOperationException(
                "A capture session must contain at least one input event.");
        }

        State = CaptureSessionState.ReadyForReview;
    }

    public void ReopenForEditing()
    {
        EnsureState(CaptureSessionState.ReadyForReview, "reopen the session");

        State = CaptureSessionState.Collecting;
    }

    public void Confirm()
    {
        EnsureState(CaptureSessionState.ReadyForReview, "confirm the session");

        State = CaptureSessionState.Confirmed;
    }

    private void EnsureState(
        CaptureSessionState requiredState,
        string operation)
    {
        if (State != requiredState)
        {
            throw new InvalidOperationException(
                $"Cannot {operation} while the session is {State}.");
        }
    }
}
