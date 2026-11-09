using NutriFlow.Infrastructure.Ai;

namespace NutriFlow.Api.Configuration;

// two clients over one transport, as on the phone: the main text model gives up on its limit at once because the
// reserve answers instead, everything else may wait a little for the limit to pass
internal sealed class GroqClients : IDisposable
{
    public const string TransportName = "Groq";
    private static readonly TimeSpan RateLimitWait = TimeSpan.FromSeconds(25);

    public GroqClients(IHttpClientFactory httpClientFactory, Uri baseAddress, GroqRetryDelay? retryDelay)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        Func<TimeSpan, CancellationToken, Task> delay = retryDelay is null ? Task.Delay : retryDelay.Invoke;
        Immediate = Create(httpClientFactory, baseAddress, TimeSpan.Zero, delay);
        Waiting = Create(httpClientFactory, baseAddress, RateLimitWait, delay);
    }

    public HttpClient Immediate { get; }

    public HttpClient Waiting { get; }

    public void Dispose()
    {
        Immediate.Dispose();
        Waiting.Dispose();
    }

    private static HttpClient Create(
        IHttpClientFactory httpClientFactory, Uri baseAddress, TimeSpan maximumRateLimitWait,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        return new HttpClient(new GroqRetryHandler(
            httpClientFactory.CreateClient(TransportName), maximumRateLimitWait, delay))
        {
            BaseAddress = baseAddress,
            // every attempt has the timeout of the transport, this one would cut the retries short
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}
