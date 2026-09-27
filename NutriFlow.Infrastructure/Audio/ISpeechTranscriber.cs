namespace NutriFlow.Infrastructure.Audio;

public interface ISpeechTranscriber
{
    Task<string> TranscribeAsync(
        ValidatedAudio audio,
        CancellationToken cancellationToken = default);
}
