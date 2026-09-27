using NutriFlow.Infrastructure.Audio;

namespace NutriFlow.Infrastructure.Ai;

public sealed class UnavailableSpeechTranscriber : ISpeechTranscriber
{
    public Task<string> TranscribeAsync(
        ValidatedAudio audio,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "Speech transcription requires the Groq provider to be configured.");
    }
}
