using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NutriFlow.Infrastructure.Audio;

namespace NutriFlow.Infrastructure.Ai;

public sealed class GroqSpeechTranscriber : ISpeechTranscriber
{
    public const int MaximumTranscriptLength = 4000;

    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly string? _language;

    public GroqSpeechTranscriber(
        HttpClient httpClient,
        string model,
        string? language = "ru")
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        if (!string.IsNullOrWhiteSpace(language) &&
            (language.Length != 2 || !language.All(char.IsAsciiLetter)))
        {
            throw new ArgumentException(
                "The speech language must be a two-letter ISO language code.",
                nameof(language));
        }

        _httpClient = httpClient;
        _model = model.Trim();
        _language = string.IsNullOrWhiteSpace(language)
            ? null
            : language.ToLowerInvariant();
    }

    public async Task<string> TranscribeAsync(
        ValidatedAudio audio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);

        using MultipartFormDataContent form = new();
        ByteArrayContent fileContent = new(audio.Content.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(audio.MediaType);
        form.Add(fileContent, "file", $"capture{audio.FileExtension}");
        form.Add(new StringContent(_model), "model");
        form.Add(new StringContent("json"), "response_format");
        form.Add(new StringContent("0"), "temperature");

        if (_language is not null)
        {
            form.Add(new StringContent(_language), "language");
        }

        using HttpRequestMessage request = new(HttpMethod.Post, "audio/transcriptions")
        {
            Content = form
        };
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        GroqTranscriptionResponse? transcription;

        try
        {
            transcription = await response.Content
                .ReadFromJsonAsync<GroqTranscriptionResponse>(
                    cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Groq returned malformed transcription JSON.",
                exception);
        }

        if (transcription?.Text is null ||
            transcription.Text.Length > MaximumTranscriptLength)
        {
            throw new InvalidDataException(
                "Groq returned an invalid or oversized transcription.");
        }

        return transcription.Text.Trim();
    }

    private sealed record GroqTranscriptionResponse(
        [property: JsonPropertyName("text")]
        string? Text);
}
