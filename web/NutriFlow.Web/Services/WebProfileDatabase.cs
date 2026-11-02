using Bit.Bsqlite.Interop;
using Microsoft.EntityFrameworkCore;
using NutriFlow.Infrastructure;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Web.Services;

// opens profile databases in the origin private file system; one bsqlite session per profile lives until the page closes
public sealed class WebProfileDatabase(IBsqliteSessionFactory sessions)
{
    internal static readonly TimeSpan LockedRetryDelay = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan LockedRetryLimit = TimeSpan.FromSeconds(3);
    private readonly Dictionary<Guid, Task<BsqliteSession>> _sessions = [];
    private readonly HashSet<Guid> _prepared = [];

    internal static string DatabaseName(Guid ownerId) => $"nutriflow/profile-{ownerId:N}.db";

    // signature of the openDatabase parameter of LocalMealClient; the file path of the profile is not used in the browser
    public async Task<NutriFlowDbContext> OpenAsync(string profileDatabasePath, Guid ownerId, CancellationToken cancellationToken)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An owner ID cannot be empty.", nameof(ownerId));
        }

        BsqliteSession session = await GetSessionAsync(ownerId, cancellationToken);
        DbContextOptions<NutriFlowDbContext> options = new DbContextOptionsBuilder<NutriFlowDbContext>()
            .UseBitBsqlite(session)
            .Options;
        if (_prepared.Contains(ownerId))
        {
            return new NutriFlowDbContext(options);
        }

        // migrations and the owner row are checked once per launch: every check costs hundreds of milliseconds here
        NutriFlowDbContext context = await LocalProfileDatabase.OpenAsync(options, ownerId, cancellationToken);
        _prepared.Add(ownerId);
        return context;
    }

    internal static async Task<T> RetryWhileFileLockedAsync<T>(
        Func<CancellationToken, Task<T>> open,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        TimeSpan waited = TimeSpan.Zero;
        while (true)
        {
            try
            {
                return await open(cancellationToken);
            }
            catch (Exception exception) when (IsFileLocked(exception) && waited < LockedRetryLimit)
            {
                // a page that is just going away (reload, back-forward cache) still holds the file for a moment
                await delay(LockedRetryDelay, cancellationToken);
                waited += LockedRetryDelay;
            }
        }
    }

    private Task<BsqliteSession> GetSessionAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(ownerId, out Task<BsqliteSession>? session) || session.IsFaulted || session.IsCanceled)
        {
            session = RetryWhileFileLockedAsync(
                token => sessions.OpenAsync(DatabaseName(ownerId), token),
                Task.Delay,
                cancellationToken);
            _sessions[ownerId] = session;
        }

        return session;
    }

    private static bool IsFileLocked(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("NoModificationAllowedError", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
