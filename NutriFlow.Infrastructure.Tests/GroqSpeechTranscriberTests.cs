using System.Net;
using System.Text;
using System.Text.Json;
using NutriFlow.Infrastructure.Ai;
using NutriFlow.Infrastructure.Audio;

namespace NutriFlow.Infrastructure.Tests;

public sealed class GroqSpeechTranscriberTests
{
    [Fact]
    public async Task TranscribeAsync_SendsOriginalAudioAndReturnsTrimmedText()
    {
        RecordingHttpMessageHandler handler = new(
            HttpStatusCode.OK,
            """{"text":"  Добавил примерно 600 г говядины.  "}""");
        GroqSpeechTranscriber transcriber = CreateTranscriber(handler);
        ValidatedAudio audio = CreateAudio();

        string text = await transcriber.TranscribeAsync(audio);

        Assert.Equal("Добавил примерно 600 г говядины.", text);
        Assert.Equal(HttpMethod.Post, handler.RequestMethod);
        Assert.Equal(
            "/openai/v1/audio/transcriptions",
            handler.RequestUri!.AbsolutePath);
        Assert.Equal("whisper-test", handler.Parts["model"].Text);
        Assert.Equal("ru", handler.Parts["language"].Text);
        Assert.Equal("json", handler.Parts["response_format"].Text);
        Assert.Equal("0", handler.Parts["temperature"].Text);
        Assert.Equal(audio.Content.ToArray(), handler.Parts["file"].Bytes);
        Assert.Equal("audio/wav", handler.Parts["file"].MediaType);
        Assert.Equal("capture.wav", handler.Parts["file"].FileName);
    }

    [Fact]
    public async Task TranscribeAsync_WithAutomaticLanguage_OmitsLanguageField()
    {
        RecordingHttpMessageHandler handler = new(HttpStatusCode.OK, """{"text":"Hello"}""");
        GroqSpeechTranscriber transcriber = CreateTranscriber(handler, language: null);

        await transcriber.TranscribeAsync(CreateAudio());

        Assert.False(handler.Parts.ContainsKey("language"));
    }

    [Fact]
    public async Task TranscribeAsync_WithNoRecognizedSpeech_ReturnsEmptyText()
    {
        RecordingHttpMessageHandler handler = new(HttpStatusCode.OK, """{"text":"  "}""");
        GroqSpeechTranscriber transcriber = CreateTranscriber(handler);

        Assert.Equal(string.Empty, await transcriber.TranscribeAsync(CreateAudio()));
    }

    [Theory]
    [InlineData("{ not json }")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"text\":null}")]
    [InlineData("{\"text\":123}")]
    public async Task TranscribeAsync_WithInvalidResponse_ThrowsInvalidDataException(
        string responseBody)
    {
        RecordingHttpMessageHandler handler = new(HttpStatusCode.OK, responseBody);
        GroqSpeechTranscriber transcriber = CreateTranscriber(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => transcriber.TranscribeAsync(CreateAudio()));
    }

    [Fact]
    public async Task TranscribeAsync_WithOversizedTranscript_ThrowsInvalidDataException()
    {
        string responseBody = JsonSerializer.Serialize(new
        {
            text = new string('x', GroqSpeechTranscriber.MaximumTranscriptLength + 1)
        });
        RecordingHttpMessageHandler handler = new(HttpStatusCode.OK, responseBody);
        GroqSpeechTranscriber transcriber = CreateTranscriber(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => transcriber.TranscribeAsync(CreateAudio()));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task TranscribeAsync_WhenServiceFails_ThrowsHttpRequestException(
        HttpStatusCode statusCode)
    {
        RecordingHttpMessageHandler handler = new(statusCode, "{}");
        GroqSpeechTranscriber transcriber = CreateTranscriber(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => transcriber.TranscribeAsync(CreateAudio()));
    }

    [Fact]
    public async Task TranscribeAsync_WithCancelledRequest_PropagatesCancellation()
    {
        RecordingHttpMessageHandler handler = new(HttpStatusCode.OK, "{}");
        GroqSpeechTranscriber transcriber = CreateTranscriber(handler);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transcriber.TranscribeAsync(CreateAudio(), cancellation.Token));
    }

    [Fact]
    public async Task TranscribeAsync_WhenResponseBodyStalls_RespectsClientTimeout()
    {
        using HttpClient client = new(new DelayedBodyHttpMessageHandler())
        {
            BaseAddress = new Uri("https://api.groq.com/openai/v1/"),
            Timeout = TimeSpan.FromMilliseconds(100)
        };
        GroqSpeechTranscriber transcriber = new(client, "whisper-test");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transcriber.TranscribeAsync(CreateAudio())
                .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static GroqSpeechTranscriber CreateTranscriber(
        HttpMessageHandler handler,
        string? language = "ru")
    {
        HttpClient client = new(handler)
        {
            BaseAddress = new Uri("https://api.groq.com/openai/v1/")
        };

        return new GroqSpeechTranscriber(client, "whisper-test", language);
    }

    private static ValidatedAudio CreateAudio()
    {
        return new ValidatedAudio(new byte[] { 1, 2, 3, 4 }, "audio/wav", ".wav");
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public RecordingHttpMessageHandler(HttpStatusCode statusCode, string responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        public HttpMethod? RequestMethod { get; private set; }
        public Uri? RequestUri { get; private set; }
        public Dictionary<string, RequestPart> Parts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestMethod = request.Method;
            RequestUri = request.RequestUri;
            MultipartFormDataContent form = Assert.IsType<MultipartFormDataContent>(request.Content);

            foreach (HttpContent part in form)
            {
                byte[] bytes = await part.ReadAsByteArrayAsync(cancellationToken);
                Parts.Add(
                    part.Headers.ContentDisposition!.Name!.Trim('"'),
                    new RequestPart(
                        bytes,
                        part.Headers.ContentType?.MediaType,
                        part.Headers.ContentDisposition.FileName?.Trim('"')));
            }

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class DelayedBodyHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new DelayedBodyContent()
            });
        }
    }

    private sealed class DelayedBodyContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed record RequestPart(byte[] Bytes, string? MediaType, string? FileName)
    {
        public string Text => Encoding.UTF8.GetString(Bytes);
    }
}
