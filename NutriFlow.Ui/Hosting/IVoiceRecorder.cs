namespace NutriFlow.Ui.Hosting;

public enum VoicePhase
{
    Idle,
    Requesting,
    Recording,
    Transcribing
}

public sealed record RecordedAudio(byte[] Content, string MediaType);

// one recording at a time, up to the limit of the platform. The audio goes to the delegate given to StartAsync: its text
// comes back as Transcribed, its exception as Failed with the message of the exception
public interface IVoiceRecorder : IAsyncDisposable
{
    event Action<VoicePhase>? PhaseChanged;

    event Action<string>? Transcribed;

    event Action<string>? Failed;

    Task StartAsync(Func<RecordedAudio, CancellationToken, Task<string>> transcribe);

    Task StopAsync();
}
