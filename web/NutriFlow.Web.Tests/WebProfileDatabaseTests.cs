using Microsoft.JSInterop;
using NutriFlow.Web.Services;

namespace NutriFlow.Web.Tests;

public sealed class WebProfileDatabaseTests
{
    [Fact]
    public async Task OpenIsRetriedWhileAnotherPageStillHoldsTheFile()
    {
        int attempts = 0;
        List<TimeSpan> delays = [];

        string result = await WebProfileDatabase.RetryWhileFileLockedAsync(_ =>
        {
            attempts++;
            return attempts < 3
                ? Task.FromException<string>(new JSException("NoModificationAllowedError: the file is locked"))
                : Task.FromResult("session");
        }, Delay(delays), CancellationToken.None);

        Assert.Equal("session", result);
        Assert.Equal(3, attempts);
        Assert.Equal([WebProfileDatabase.LockedRetryDelay, WebProfileDatabase.LockedRetryDelay], delays);
    }

    [Fact]
    public async Task OpenGivesUpAfterTheRetryLimit()
    {
        List<TimeSpan> delays = [];

        await Assert.ThrowsAsync<InvalidOperationException>(() => WebProfileDatabase.RetryWhileFileLockedAsync<string>(
            _ => throw new InvalidOperationException("open failed", new JSException("NoModificationAllowedError")),
            Delay(delays), CancellationToken.None));

        Assert.Equal(WebProfileDatabase.LockedRetryLimit, delays.Aggregate(TimeSpan.Zero, (total, delay) => total + delay));
    }

    [Fact]
    public async Task OtherFailuresAreNotRetried()
    {
        int attempts = 0;
        List<TimeSpan> delays = [];

        await Assert.ThrowsAsync<JSException>(() => WebProfileDatabase.RetryWhileFileLockedAsync<string>(_ =>
        {
            attempts++;
            throw new JSException("QuotaExceededError");
        }, Delay(delays), CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Empty(delays);
    }

    [Fact]
    public void ProfileDatabaseNamesMatchTheTestStand()
    {
        Assert.Equal("nutriflow/profile-1c7dd555ece843a1a87153faad6ebec5.db",
            WebProfileDatabase.DatabaseName(Guid.Parse("1c7dd555-ece8-43a1-a871-53faad6ebec5")));
    }

    private static Func<TimeSpan, CancellationToken, Task> Delay(List<TimeSpan> delays) => (delay, _) =>
    {
        delays.Add(delay);
        return Task.CompletedTask;
    };
}
