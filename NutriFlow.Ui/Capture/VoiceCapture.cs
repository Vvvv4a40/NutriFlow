using System.Diagnostics;
using NutriFlow.Infrastructure.Audio;
using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Hosting;
using NutriFlow.Ui.Pages;

namespace NutriFlow.Ui.Capture;

// the voice form without its markup (AudioPageAsync on android): the state of a recording, the text that can be edited at
// any moment and the last audio, so a failed request to Groq is repeated instead of the user speaking again
public sealed class VoiceCapture : IAsyncDisposable
{
    private readonly IVoiceRecorder _recorder;
    private readonly LocalMealClient _meals;
    private readonly IProfileSettings _settings;
    private readonly Guid _profileId;
    private readonly CancellationTokenSource _closing = new();
    private readonly Stopwatch _recording = new();
    private CancellationTokenSource? _ticking;
    private RecordedAudio? _audio;
    private bool _stopRequested;

    public VoiceCapture(IVoiceRecorder recorder, LocalMealClient meals, IProfileSettings settings, Guid profileId)
    {
        _recorder = recorder;
        _meals = meals;
        _settings = settings;
        _profileId = profileId;
        _recorder.PhaseChanged += OnPhaseChanged;
        _recorder.Transcribed += OnTranscribed;
        _recorder.Failed += OnFailed;
    }

    public VoicePhase Phase { get; private set; }

    public int ElapsedSeconds { get; private set; }

    public string Text { get; private set; } = "";

    public string? Error { get; private set; }

    // the last audio can go to Groq again
    public bool CanRetry { get; private set; }

    public bool Recognised { get; private set; }

    // the limit or the browser ended the recording, not the user
    public bool StoppedByItself { get; private set; }

    public event Action? Changed;

    public void SetText(string? text) => Text = text ?? "";

    public async Task StartAsync()
    {
        if (Phase != VoicePhase.Idle)
        {
            return;
        }

        Error = null;
        CanRetry = false;
        Recognised = false;
        StoppedByItself = false;
        _stopRequested = false;
        _audio = null;
        ElapsedSeconds = 0;
        try
        {
            // without a key the recording would be wasted, so this comes before the question about the microphone
            await _settings.GetGroqAsync(_profileId);
            await _recorder.StartAsync(TranscribeAsync);
        }
        catch (Exception exception)
        {
            UiLog.Error(exception);
            // no key is the expected case and has its own text; anything else comes from the browser
            Error = exception is InvalidOperationException ? UiErrors.Describe(exception) : VoiceText.StartFailed;
            Changed?.Invoke();
        }
    }

    public async Task StopAsync()
    {
        if (Phase == VoicePhase.Recording)
        {
            _stopRequested = true;
            await _recorder.StopAsync();
        }
    }

    public async Task RetryAsync()
    {
        if (_audio is not { } audio || Phase != VoicePhase.Idle)
        {
            return;
        }

        Error = null;
        CanRetry = false;
        Phase = VoicePhase.Transcribing;
        Changed?.Invoke();
        string? failure = null;
        string text = "";
        try
        {
            text = await TranscribeAsync(audio, _closing.Token);
            if (text.Length == 0)
            {
                failure = VoiceText.NotRecognised;
            }
        }
        catch (Exception exception)
        {
            UiLog.Error(exception);
            failure = exception.Message;
        }

        Phase = VoicePhase.Idle;
        if (failure is null)
        {
            OnTranscribed(text);
        }
        else
        {
            OnFailed(failure);
        }
    }

    private async Task<string> TranscribeAsync(RecordedAudio audio, CancellationToken cancellationToken)
    {
        _audio = audio;
        try
        {
            AudioUploadValidator.Validate(audio.Content, audio.MediaType);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidOperationException(VoiceText.WrongFormat, exception);
        }

        try
        {
            GroqSettings settings = await _settings.GetGroqAsync(_profileId);
            return await _meals.TranscribeAsync(_profileId, audio.Content, audio.MediaType, settings, cancellationToken);
        }
        catch (Exception exception)
        {
            CanRetry = true;
            throw new InvalidOperationException(VoiceText.Error(exception), exception);
        }
    }

    private void OnPhaseChanged(VoicePhase phase)
    {
        if (phase == VoicePhase.Recording)
        {
            StartTicking();
        }
        else if (Phase == VoicePhase.Recording)
        {
            StopTicking();
            StoppedByItself = phase == VoicePhase.Transcribing && !_stopRequested;
        }

        Phase = phase;
        Changed?.Invoke();
    }

    // new words join the text already in the field: it may have been edited or typed by hand
    private void OnTranscribed(string text)
    {
        try
        {
            Text = string.IsNullOrWhiteSpace(Text) ? text : MealInputText.AppendTranscript(Text, text);
            Recognised = true;
        }
        catch (ArgumentException exception)
        {
            Error = exception.Message;
            CanRetry = true;
        }

        Changed?.Invoke();
    }

    private void OnFailed(string message)
    {
        Error = message;
        Changed?.Invoke();
    }

    private void StartTicking()
    {
        _recording.Restart();
        ElapsedSeconds = 0;
        _ticking = new CancellationTokenSource();
        _ = TickAsync(_ticking.Token);
    }

    private void StopTicking()
    {
        ElapsedSeconds = Math.Min((int)_recording.Elapsed.TotalSeconds, VoiceText.MaximumSeconds);
        _recording.Stop();
        _ticking?.Cancel();
        _ticking?.Dispose();
        _ticking = null;
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(1000, cancellationToken);
                ElapsedSeconds = Math.Min((int)_recording.Elapsed.TotalSeconds, VoiceText.MaximumSeconds);
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            // the recording ended
        }
    }

    public async ValueTask DisposeAsync()
    {
        _recorder.PhaseChanged -= OnPhaseChanged;
        _recorder.Transcribed -= OnTranscribed;
        _recorder.Failed -= OnFailed;
        StopTicking();
        await _closing.CancelAsync();
        _closing.Dispose();
        await _recorder.DisposeAsync();
    }
}
