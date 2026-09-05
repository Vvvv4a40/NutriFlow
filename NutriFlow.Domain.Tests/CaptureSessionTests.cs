using NutriFlow.Domain;

namespace NutriFlow.Domain.Tests;

public sealed class CaptureSessionTests
{
    [Fact]
    public void Constructor_CreatesEmptyCollectingSession()
    {
        CaptureSession session = new CaptureSession();

        Assert.Equal(CaptureSessionState.Collecting, session.State);
        Assert.Empty(session.InputEvents);
    }

    [Fact]
    public void AddEvent_PreservesInsertionOrder()
    {
        CaptureSession session = new CaptureSession();
        InputEvent firstEvent = new InputEvent("First message");
        InputEvent secondEvent = new InputEvent("Second message");
        InputEvent correction = new InputEvent("Correction");

        session.AddEvent(firstEvent);
        session.AddEvent(secondEvent);
        session.AddEvent(correction);

        Assert.Equal(3, session.InputEvents.Count);
        Assert.Same(firstEvent, session.InputEvents[0]);
        Assert.Same(secondEvent, session.InputEvents[1]);
        Assert.Same(correction, session.InputEvents[2]);
    }

    [Fact]
    public void AddEvent_WithNull_ThrowsArgumentNullException()
    {
        CaptureSession session = new CaptureSession();

        Assert.Throws<ArgumentNullException>(() => session.AddEvent(null!));
    }

    [Fact]
    public void InputEvents_CannotBeChangedThroughExposedCollection()
    {
        CaptureSession session = CreateSessionWithEvent();
        ICollection<InputEvent> exposedEvents = Assert.IsAssignableFrom<ICollection<InputEvent>>(
            session.InputEvents);

        Assert.True(exposedEvents.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => exposedEvents.Clear());
    }

    [Fact]
    public void FinishCollecting_WithNoEvents_ThrowsInvalidOperationException()
    {
        CaptureSession session = new CaptureSession();

        Assert.Throws<InvalidOperationException>(() => session.FinishCollecting());
    }

    [Fact]
    public void FinishCollecting_ChangesStateToReadyForReview()
    {
        CaptureSession session = CreateSessionWithEvent();

        session.FinishCollecting();

        Assert.Equal(CaptureSessionState.ReadyForReview, session.State);
    }

    [Fact]
    public void AddEvent_WhileReadyForReview_ThrowsInvalidOperationException()
    {
        CaptureSession session = CreateReadyForReviewSession();

        Assert.Throws<InvalidOperationException>(
            () => session.AddEvent(new InputEvent("Late message")));
    }

    [Fact]
    public void Confirm_WhileCollecting_ThrowsInvalidOperationException()
    {
        CaptureSession session = CreateSessionWithEvent();

        Assert.Throws<InvalidOperationException>(() => session.Confirm());
    }

    [Fact]
    public void Confirm_WhileReadyForReview_ChangesStateToConfirmed()
    {
        CaptureSession session = CreateReadyForReviewSession();

        session.Confirm();

        Assert.Equal(CaptureSessionState.Confirmed, session.State);
    }

    [Fact]
    public void ReopenForEditing_PreservesEventsAndAllowsCorrection()
    {
        CaptureSession session = CreateReadyForReviewSession();

        session.ReopenForEditing();
        session.AddEvent(new InputEvent("Not 200 g, but 250 g"));

        Assert.Equal(CaptureSessionState.Collecting, session.State);
        Assert.Equal(2, session.InputEvents.Count);
        Assert.Equal("Added 200 g of product A", session.InputEvents[0].Text);
        Assert.Equal("Not 200 g, but 250 g", session.InputEvents[1].Text);
    }

    [Fact]
    public void ReopenForEditing_WhileCollecting_ThrowsInvalidOperationException()
    {
        CaptureSession session = CreateSessionWithEvent();

        Assert.Throws<InvalidOperationException>(() => session.ReopenForEditing());
    }

    [Fact]
    public void ReopenForEditing_AfterConfirmation_ThrowsInvalidOperationException()
    {
        CaptureSession session = CreateConfirmedSession();

        Assert.Throws<InvalidOperationException>(() => session.ReopenForEditing());
    }

    [Fact]
    public void AddEvent_AfterConfirmation_ThrowsInvalidOperationException()
    {
        CaptureSession session = CreateConfirmedSession();

        Assert.Throws<InvalidOperationException>(
            () => session.AddEvent(new InputEvent("Late message")));
    }

    [Fact]
    public void FinishCollecting_AfterConfirmation_ThrowsInvalidOperationException()
    {
        CaptureSession session = CreateConfirmedSession();

        Assert.Throws<InvalidOperationException>(() => session.FinishCollecting());
    }

    [Fact]
    public void Confirm_AfterConfirmation_ThrowsInvalidOperationException()
    {
        CaptureSession session = CreateConfirmedSession();

        Assert.Throws<InvalidOperationException>(() => session.Confirm());
    }

    private static CaptureSession CreateSessionWithEvent()
    {
        CaptureSession session = new CaptureSession();
        session.AddEvent(new InputEvent("Added 200 g of product A"));

        return session;
    }

    private static CaptureSession CreateReadyForReviewSession()
    {
        CaptureSession session = CreateSessionWithEvent();
        session.FinishCollecting();

        return session;
    }

    private static CaptureSession CreateConfirmedSession()
    {
        CaptureSession session = CreateReadyForReviewSession();
        session.Confirm();

        return session;
    }
}
