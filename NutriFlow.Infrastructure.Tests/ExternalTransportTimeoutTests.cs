using System.Net;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Ai;
using NutriFlow.Infrastructure.Audio;
using NutriFlow.Infrastructure.ExternalProducts;

namespace NutriFlow.Infrastructure.Tests;

public sealed class ExternalTransportTimeoutTests
{
    public static IEnumerable<object[]> Providers()
    {
        yield return new object[] { nameof(GroqMealParser) };
        yield return new object[] { nameof(GroqNutritionLabelReader) };
        yield return new object[] { nameof(GroqSpeechTranscriber) };
        yield return new object[] { nameof(OpenAiMealParser) };
        yield return new object[] { nameof(OpenAiNutritionLabelReader) };
        yield return new object[] { nameof(OpenFoodFactsClient) };
    }

    public static IEnumerable<object[]> StalledResponses()
    {
        foreach (object[] provider in Providers())
        {
            yield return new object[] { provider[0], false };
            yield return new object[] { provider[0], true };
        }
    }

    [Theory]
    [MemberData(nameof(StalledResponses))]
    public async Task WhenResponseStalls_RespectsClientTimeout(
        string provider,
        bool stallBody)
    {
        DelayedResponseHttpMessageHandler handler = new(stallBody);
        using HttpClient client = CreateClient(handler);
        client.Timeout = TimeSpan.FromMilliseconds(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SendRequestAsync(provider, client)
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, handler.RequestCount);
        Assert.True(handler.RequestToken.IsCancellationRequested);

        if (stallBody)
        {
            Assert.True(handler.Content!.ReadToken.IsCancellationRequested);
            Assert.True(handler.Content.IsDisposed);
        }
    }

    [Theory]
    [MemberData(nameof(StalledResponses))]
    public async Task WhenResponseStalls_PropagatesCallerCancellation(
        string provider,
        bool stallBody)
    {
        DelayedResponseHttpMessageHandler handler = new(stallBody);
        using HttpClient client = CreateClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        using CancellationTokenSource cancellation = new();

        Task request = SendRequestAsync(provider, client, cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => request.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, handler.RequestCount);
        Assert.True(handler.RequestToken.IsCancellationRequested);

        if (stallBody)
        {
            Assert.True(handler.Content!.ReadToken.IsCancellationRequested);
            Assert.True(handler.Content.IsDisposed);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WhenAlreadyCancelled_ForwardsCancellationWithoutStartingTransportWork(
        string provider)
    {
        using TrackingContent content = new();
        RecordingHttpMessageHandler handler = new(HttpStatusCode.OK, content);
        using HttpClient client = CreateClient(handler);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        OperationCanceledException exception =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => SendRequestAsync(provider, client, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(handler.RequestToken.IsCancellationRequested);
        Assert.Equal(1, handler.InvocationCount);
        Assert.Equal(0, handler.AcceptedRequestCount);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WhenRateLimited_PreservesStatusWithoutRetryAndDisposesResponse(
        string provider)
    {
        using TrackingContent content = new();
        RecordingHttpMessageHandler handler = new(HttpStatusCode.TooManyRequests, content);
        using HttpClient client = CreateClient(handler);

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => SendRequestAsync(provider, client));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(1, handler.InvocationCount);
        Assert.Equal(1, handler.AcceptedRequestCount);
        Assert.True(content.IsDisposed);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler)
        {
            BaseAddress = new Uri("https://provider.test/")
        };
    }

    private static Task SendRequestAsync(
        string provider,
        HttpClient client,
        CancellationToken cancellationToken = default)
    {
        CaptureSession session = new();
        session.AddEvent(new InputEvent("Message"));
        session.FinishCollecting();

        return provider switch
        {
            nameof(GroqMealParser) => new GroqMealParser(client, "test-model")
                .ParseAsync(session, cancellationToken),
            nameof(GroqNutritionLabelReader) => new GroqNutritionLabelReader(client, "test-model")
                .ReadAsync(new byte[] { 1 }, "image/jpeg", cancellationToken),
            nameof(GroqSpeechTranscriber) => new GroqSpeechTranscriber(client, "test-model")
                .TranscribeAsync(
                    new ValidatedAudio(new byte[] { 1 }, "audio/wav", ".wav"),
                    cancellationToken),
            nameof(OpenAiMealParser) => new OpenAiMealParser(client, "test-model")
                .ParseAsync(session, cancellationToken),
            nameof(OpenAiNutritionLabelReader) => new OpenAiNutritionLabelReader(client, "test-model")
                .ReadAsync(new byte[] { 1 }, "image/jpeg", cancellationToken),
            nameof(OpenFoodFactsClient) => new OpenFoodFactsClient(client)
                .FindByBarcodeAsync("3017620422003", cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
    }

    private sealed class DelayedResponseHttpMessageHandler : HttpMessageHandler
    {
        private readonly bool _stallBody;

        public DelayedResponseHttpMessageHandler(bool stallBody)
        {
            _stallBody = stallBody;
        }

        public int RequestCount { get; private set; }
        public CancellationToken RequestToken { get; private set; }
        public DelayedBodyContent? Content { get; private set; }
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestToken = cancellationToken;

            if (!_stallBody)
            {
                Started.SetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            Content = new DelayedBodyContent(Started);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Content
            };
        }
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly HttpContent _content;

        public RecordingHttpMessageHandler(HttpStatusCode statusCode, HttpContent content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        public int InvocationCount { get; private set; }
        public int AcceptedRequestCount { get; private set; }
        public CancellationToken RequestToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            RequestToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            AcceptedRequestCount++;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = _content
            });
        }
    }

    private sealed class DelayedBodyContent : HttpContent
    {
        private readonly TaskCompletionSource _started;

        public DelayedBodyContent(TaskCompletionSource started)
        {
            _started = started;
        }

        public CancellationToken ReadToken { get; private set; }
        public bool IsDisposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            ReadToken = cancellationToken;
            _started.SetResult();
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingContent : StringContent
    {
        public TrackingContent() : base("{}")
        {
        }

        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
