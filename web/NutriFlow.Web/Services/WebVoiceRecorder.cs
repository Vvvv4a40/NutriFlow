using Microsoft.JSInterop;
using NutriFlow.Ui;
using NutriFlow.Ui.Hosting;

namespace NutriFlow.Web.Services;

// speech-capture.mjs does the recording, its limits and the microphone errors; the audio comes here for the transcription
public sealed class WebVoiceRecorder(Func<Task<IJSObjectReference>> getModule) : IVoiceRecorder
{
    // the limit of the android app
    public const int MaximumMilliseconds = 110_000;

    private readonly CancellationTokenSource _closing = new();
    private DotNetObjectReference<WebVoiceRecorder>? _reference;
    private IJSObjectReference? _capture;
    private Func<RecordedAudio, CancellationToken, Task<string>>? _transcribe;
    private string? _transcriptionError;

    public event Action<VoicePhase>? PhaseChanged;

    public event Action<string>? Transcribed;

    public event Action<string>? Failed;

    public async Task StartAsync(Func<RecordedAudio, CancellationToken, Task<string>> transcribe)
    {
        ArgumentNullException.ThrowIfNull(transcribe);
        _transcribe = transcribe;
        _reference ??= DotNetObjectReference.Create(this);
        _capture ??= await (await getModule()).InvokeAsync<IJSObjectReference>("createVoiceCapture", _reference, MaximumMilliseconds);
        await _capture.InvokeVoidAsync("start");
    }

    public async Task StopAsync()
    {
        if (_capture is not null)
        {
            await _capture.InvokeVoidAsync("stop");
        }
    }

    [JSInvokable]
    public void OnPhaseChanged(string phase) => PhaseChanged?.Invoke(Enum.Parse<VoicePhase>(phase, ignoreCase: true));

    [JSInvokable]
    public void OnTranscribed(string text) => Transcribed?.Invoke(text);

    [JSInvokable]
    public void OnFailed(string message)
    {
        string text = _transcriptionError ?? message;
        _transcriptionError = null;
        Failed?.Invoke(text);
    }

    // an exception would reach the script with its stack trace as the message; an empty text ends the operation with
    // a failure there, and OnFailed puts the real reason back
    [JSInvokable]
    public async Task<string> OnRecorded(byte[] audio, string mediaType)
    {
        try
        {
            return await _transcribe!(new RecordedAudio(audio, mediaType), _closing.Token);
        }
        catch (Exception exception)
        {
            UiLog.Error(exception);
            _transcriptionError = exception.Message;
            return "";
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync();
        if (_capture is not null)
        {
            try
            {
                await _capture.InvokeVoidAsync("dispose");
                await _capture.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // the page is gone and the microphone with it
            }
        }

        _reference?.Dispose();
        _closing.Dispose();
    }
}
