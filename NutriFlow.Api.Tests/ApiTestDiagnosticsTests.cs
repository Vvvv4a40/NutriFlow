using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;
using Xunit.Sdk;

namespace NutriFlow.Api.Tests;

public sealed class ApiTestDiagnosticsTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task MatchingStatus_DoesNotReadContentOrStartHost(HttpStatusCode status)
    {
        await using TestApiFactory factory = new(aiProvider: "invalid-provider");
        using HttpResponseMessage response = new(status) { Content = new UnreadableContent() };

        await ApiTestAssertions.AssertStatusAsync(factory, response, status);
    }

    [Fact]
    public void ErrorLog_CapturesOnlyErrorsAndCriticalWithoutFormattingState()
    {
        using TestErrorLogProvider provider = new();
        ILogger logger = provider.CreateLogger("diagnostic-category");
        foreach (LogLevel level in Enum.GetValues<LogLevel>())
        {
            logger.Log(level, new EventId(41), "private state",
                new InvalidOperationException($"sentinel-{level}"),
                (_, _) => throw new InvalidOperationException("State must not be formatted."));
        }

        string description = provider.DescribeErrors();
        Assert.Contains("sentinel-Error", description, StringComparison.Ordinal);
        Assert.Contains("sentinel-Critical", description, StringComparison.Ordinal);
        Assert.Contains("diagnostic-category [41]", description, StringComparison.Ordinal);
        foreach (LogLevel level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.None })
        {
            Assert.DoesNotContain($"sentinel-{level}", description, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("private state", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorLog_BoundsEntriesAndReportsAllNestedSqliteCodes()
    {
        using TestErrorLogProvider provider = new();
        ILogger logger = provider.CreateLogger("bounded-category");
        for (int index = 0; index < 256; index++)
        {
            logger.LogError(new InvalidOperationException($"entry-{index}"), "Discarded state.");
        }

        Assert.DoesNotContain("entry-127", provider.DescribeErrors(), StringComparison.Ordinal);
        Assert.Equal(128, provider.DescribeErrors().Split("Error: bounded-category", StringSplitOptions.None).Length - 1);
        logger.LogCritical(new AggregateException(
            new SqliteException("first sqlite failure", 1, 17),
            new InvalidOperationException("outer failure", new SqliteException("second sqlite failure", 19, 2067))),
            "Discarded state.");

        string description = provider.DescribeErrors();
        Assert.Contains("SqliteErrorCode: 1; SqliteExtendedErrorCode: 17", description, StringComparison.Ordinal);
        Assert.Contains("SqliteErrorCode: 19; SqliteExtendedErrorCode: 2067", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerFailure_KeepsHttpRedactedAndIncludesExceptionInFailedAssertion()
    {
        const string detail = "synthetic private database failure";
        await using TestApiFactory factory = new(
            seedDemoData: false, environment: "Production",
            parser: new ThrowingParser(new InvalidOperationException(detail, new SqliteException("synthetic sqlite", 1, 17))));
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/meal-sessions",
            new CreateMealSessionRequest(["Проверочное сообщение"], new DateOnly(2026, 10, 6)));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(detail, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        TrueException failure = await Assert.ThrowsAsync<TrueException>(
            () => ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.Created));

        Assert.Contains("Expected HTTP 201 (Created); received 500 (InternalServerError)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("POST /api/meal-sessions", failure.Message, StringComparison.Ordinal);
        Assert.Contains("application/problem+json", failure.Message, StringComparison.Ordinal);
        Assert.Contains(detail, failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowingParser), failure.Message, StringComparison.Ordinal);
        Assert.Contains("SqliteErrorCode: 1; SqliteExtendedErrorCode: 17", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Database: ", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedAssertion_UsesActualChildDatabaseWithoutOpeningItOrStartingBase()
    {
        using TemporaryStorage storage = new();
        string configuredDatabase = Path.Combine(storage.Root, "configured.db");
        string actualDatabase = Path.Combine(storage.Root, "actual-child.db");
        await using TestApiFactory source = new(
            applyMigrations: false, seedDemoData: false,
            databasePath: configuredDatabase, labelPhotoPath: Path.Combine(storage.Root, "photos"));
        await using var child = source.WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(storage.Root);
            builder.ConfigureServices(services => services.AddDbContext<NutriFlowDbContext>(
                options => options.UseSqlite($"Data Source={actualDatabase}")));
        });
        using HttpClient client = child.CreateClient();
        using HttpResponseMessage response = new(HttpStatusCode.InternalServerError) { Content = new StringContent("diagnostic body") };

        TrueException failure = await Assert.ThrowsAsync<TrueException>(
            () => ApiTestAssertions.AssertStatusAsync(child, response, HttpStatusCode.OK));

        Assert.Contains($"Database: {actualDatabase}", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain($"Database: {configuredDatabase}", failure.Message, StringComparison.Ordinal);
        Assert.Contains("diagnostic body", failure.Message, StringComparison.Ordinal);
        Assert.Contains("No server errors were recorded.", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(configuredDatabase));
        Assert.False(File.Exists(actualDatabase));
        await using AsyncServiceScope scope = child.Services.CreateAsyncScope();
        Assert.Equal(System.Data.ConnectionState.Closed,
            scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>().Database.GetDbConnection().State);
    }

    [Fact]
    public async Task ErrorLogs_AreIsolatedBetweenConcurrentFactories()
    {
        await using TestApiFactory first = new(seedDemoData: false);
        await using TestApiFactory second = new(seedDemoData: false);
        using HttpClient firstClient = first.CreateClient();
        using HttpClient secondClient = second.CreateClient();
        ILogger firstLogger = first.Services.GetRequiredService<ILogger<ApiTestDiagnosticsTests>>();
        ILogger secondLogger = second.Services.GetRequiredService<ILogger<ApiTestDiagnosticsTests>>();

        await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
        {
            firstLogger.LogError(new InvalidOperationException($"first-factory-{index}"), "Discarded state.");
            secondLogger.LogError(new InvalidOperationException($"second-factory-{index}"), "Discarded state.");
        })));
        using HttpResponseMessage firstResponse = new(HttpStatusCode.BadGateway);
        using HttpResponseMessage secondResponse = new(HttpStatusCode.BadGateway);
        TrueException firstFailure = await Assert.ThrowsAsync<TrueException>(
            () => ApiTestAssertions.AssertStatusAsync(first, firstResponse, HttpStatusCode.OK));
        TrueException secondFailure = await Assert.ThrowsAsync<TrueException>(
            () => ApiTestAssertions.AssertStatusAsync(second, secondResponse, HttpStatusCode.OK));

        for (int index = 0; index < 32; index++)
        {
            Assert.Contains($"first-factory-{index}", firstFailure.Message, StringComparison.Ordinal);
            Assert.Contains($"second-factory-{index}", secondFailure.Message, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("second-factory-", firstFailure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("first-factory-", secondFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedAssertion_PreservesStatusFailureWhenResponseContentCannotBeRead()
    {
        await using TestApiFactory factory = new(applyMigrations: false, seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = new(HttpStatusCode.BadGateway) { Content = new UnreadableContent() };

        TrueException failure = await Assert.ThrowsAsync<TrueException>(
            () => ApiTestAssertions.AssertStatusAsync(factory, response, HttpStatusCode.OK));

        Assert.Contains("received 502 (BadGateway)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Response body unavailable", failure.Message, StringComparison.Ordinal);
    }

    private sealed class ThrowingParser(Exception exception) : IMealParser
    {
        public Task<MealDraft> ParseAsync(CaptureSession session, CancellationToken cancellationToken = default)
        {
            throw exception;
        }
    }

    private sealed class UnreadableContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            throw new InvalidOperationException("Reading this content is forbidden.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class TemporaryStorage : IDisposable
    {
        public TemporaryStorage()
        {
            Directory.CreateDirectory(Root);
        }

        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"nutriflow-diagnostic-{Guid.NewGuid():N}");

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
