using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace NutriFlow.Mobile.Core;

// groq limits a key by tokens per minute and its edge sometimes drops a request; both pass within seconds, so the user
// should not have to repeat the action. Browsers do not expose retry-after here, the wait is read from the error text too
internal sealed partial class GroqRetryHandler(
    HttpClient inner,
    TimeSpan maximumRateLimitWait,
    Func<TimeSpan, CancellationToken, Task> delay) : HttpMessageHandler
{
    private const int MaximumRateLimitRetries = 2;
    private static readonly TimeSpan[] FailureDelays = [TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4)];
    private static readonly TimeSpan UnknownWait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WaitMargin = TimeSpan.FromMilliseconds(300);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int failures = 0;
        int rateLimits = 0;
        while (true)
        {
            HttpResponseMessage response;
            try
            {
                response = await inner.SendAsync(Copy(request), HttpCompletionOption.ResponseContentRead, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException &&
                !cancellationToken.IsCancellationRequested && failures < FailureDelays.Length)
            {
                await delay(FailureDelays[failures++], cancellationToken);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                string text = await response.Content.ReadAsStringAsync(cancellationToken);
                TimeSpan? wait = ReadWait(response, text);
                response.Dispose();
                if ((wait ?? UnknownWait) <= maximumRateLimitWait && rateLimits < MaximumRateLimitRetries)
                {
                    rateLimits++;
                    await delay((wait ?? UnknownWait) + WaitMargin, cancellationToken);
                    continue;
                }

                throw new GroqRateLimitException(wait, DailyLimit().IsMatch(text));
            }

            if (response.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
                    HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout &&
                failures < FailureDelays.Length)
            {
                response.Dispose();
                await delay(FailureDelays[failures++], cancellationToken);
                continue;
            }

            return response;
        }
    }

    internal static TimeSpan? ReadWait(HttpResponseMessage response, string text)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        Match match = TryAgain().Match(text);
        if (!match.Success)
        {
            return null;
        }

        double Part(string name) => match.Groups[name].Success
            ? double.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture)
            : 0;
        return TimeSpan.FromHours(Part("h")) + TimeSpan.FromMinutes(Part("m")) +
            TimeSpan.FromSeconds(Part("s")) + TimeSpan.FromMilliseconds(Part("ms"));
    }

    // a message cannot be sent twice, every attempt needs its own. The content is shared: it can be written again,
    // and the original message disposes it, so these copies are deliberately not disposed
    private static HttpRequestMessage Copy(HttpRequestMessage request)
    {
        HttpRequestMessage copy = new(request.Method, request.RequestUri) { Content = request.Content };
        foreach ((string name, IEnumerable<string> values) in request.Headers)
        {
            copy.Headers.TryAddWithoutValidation(name, values);
        }

        return copy;
    }

    [GeneratedRegex(@"try again in (?:(?<h>\d+)h)?(?:(?<m>\d+)m(?!s))?(?:(?<s>\d+(?:\.\d+)?)s)?(?:(?<ms>\d+(?:\.\d+)?)ms)?", RegexOptions.IgnoreCase)]
    private static partial Regex TryAgain();

    [GeneratedRegex(@"per day|\((?:TPD|RPD|ASD)\)", RegexOptions.IgnoreCase)]
    private static partial Regex DailyLimit();
}
