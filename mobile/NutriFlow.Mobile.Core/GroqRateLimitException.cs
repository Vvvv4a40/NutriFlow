using System.Net;

namespace NutriFlow.Mobile.Core;

// the key has used up its limit for longer than the app waits on its own
public sealed class GroqRateLimitException(TimeSpan? wait, bool isDailyLimit)
    : HttpRequestException("Groq limited the requests of this key.", null, HttpStatusCode.TooManyRequests)
{
    public TimeSpan? Wait { get; } = wait;

    public bool IsDailyLimit { get; } = isDailyLimit;
}
