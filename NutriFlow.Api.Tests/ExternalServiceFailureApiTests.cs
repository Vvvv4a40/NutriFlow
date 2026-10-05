using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using NutriFlow.Api.Contracts;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Api.Tests;

public sealed class ExternalServiceFailureApiTests
{
    private const string SensitiveDetail = "private upstream response and credential detail";

    public static TheoryData<string, string> FailureCases => CreateCases(
        "rate-limit", "unavailable", "headers-timeout", "body-timeout");

    public static TheoryData<string, string> CancellationCases => CreateCases(
        "headers-timeout", "body-timeout");

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task UpstreamFailure_ReturnsSafeProblemWithoutRetryOrDataChanges(
        string operation,
        string failure)
    {
        await using ExternalApiFixture fixture = new();
        Guid? sessionId = await PrepareAsync(fixture, operation);
        string before = await ReadSnapshotAsync(fixture);
        int callsBefore = fixture.Transport.CallCount;
        fixture.Transport.Failure = failure;
        using HttpRequestMessage request = CreateRequest(operation, sessionId);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request)
            .WaitAsync(TimeSpan.FromSeconds(10));
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(body);
        HttpStatusCode expectedStatus = failure.EndsWith("timeout", StringComparison.Ordinal)
            ? HttpStatusCode.GatewayTimeout
            : HttpStatusCode.BadGateway;

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal((int)expectedStatus, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(SensitiveDetail, body, StringComparison.Ordinal);
        Assert.DoesNotContain("test-key", body, StringComparison.Ordinal);
        Assert.DoesNotContain("groq.test", body, StringComparison.Ordinal);
        Assert.Null(response.Headers.RetryAfter);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(callsBefore + 1, fixture.Transport.CallCount);
        Assert.Equal(before, await ReadSnapshotAsync(fixture));
        Assert.Empty(Directory.EnumerateFiles(fixture.PhotoPath, "*", SearchOption.AllDirectories));

        if (failure.EndsWith("timeout", StringComparison.Ordinal))
        {
            await fixture.Transport.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [MemberData(nameof(CancellationCases))]
    public async Task ClientCancellation_StopsExternalRequestWithoutRetryOrDataChanges(
        string operation,
        string failure)
    {
        await using ExternalApiFixture fixture = new(infiniteTimeout: true);
        Guid? sessionId = await PrepareAsync(fixture, operation);
        string before = await ReadSnapshotAsync(fixture);
        int callsBefore = fixture.Transport.CallCount;
        fixture.Transport.Failure = failure;
        using HttpRequestMessage request = CreateRequest(operation, sessionId);
        using CancellationTokenSource cancellation = new();

        Task<HttpResponseMessage> pending = fixture.Client.SendAsync(request, cancellation.Token);
        await fixture.Transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await fixture.Transport.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (int serverStatus, bool requestAborted) = await fixture.Transport.RequestCompleted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(requestAborted);
        Assert.Equal(StatusCodes.Status499ClientClosedRequest, serverStatus);
        Assert.Equal(callsBefore + 1, fixture.Transport.CallCount);
        Assert.Equal(before, await ReadSnapshotAsync(fixture));
        Assert.Empty(Directory.EnumerateFiles(fixture.PhotoPath, "*", SearchOption.AllDirectories));
    }

    private static TheoryData<string, string> CreateCases(params string[] failures)
    {
        TheoryData<string, string> cases = new();
        foreach (string operation in new[] { "create", "message", "parse", "label", "speech", "barcode" })
        {
            foreach (string failure in failures)
            {
                cases.Add(operation, failure);
            }
        }

        return cases;
    }

    private static async Task<Guid?> PrepareAsync(ExternalApiFixture fixture, string operation)
    {
        await fixture.AssertStoragePathsAsync();
        if (operation != "message")
        {
            return null;
        }

        using HttpRequestMessage request = CreateRequest("create", null);
        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        MealSessionResponse session = Assert.IsType<MealSessionResponse>(
            await response.Content.ReadFromJsonAsync<MealSessionResponse>());
        return session.Id;
    }

    private static HttpRequestMessage CreateRequest(string operation, Guid? sessionId)
    {
        if (operation == "barcode")
        {
            return new HttpRequestMessage(HttpMethod.Get, "/api/products/barcode/3017620422003");
        }

        string path = operation switch
        {
            "create" => "/api/meal-sessions",
            "message" => $"/api/meal-sessions/{sessionId}/messages",
            "parse" => "/api/meal-drafts/parse",
            "label" => "/api/labels/analyze",
            "speech" => "/api/audio/transcribe",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        HttpRequestMessage request = new(HttpMethod.Post, path);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        if (operation is "label" or "speech")
        {
            byte[] bytes = operation == "label"
                ? [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]
                : [0x52, 0x49, 0x46, 0x46, 0x01, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45, 0x00];
            ByteArrayContent file = new(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(operation == "label" ? "image/png" : "audio/wav");
            MultipartFormDataContent form = new();
            form.Add(file, operation == "label" ? "photo" : "audio", operation == "label" ? "label.png" : "speech.wav");
            request.Content = form;
        }
        else
        {
            request.Content = operation == "message"
                ? JsonContent.Create(new AddMealSessionMessageRequest("Уточнение массы."))
                : JsonContent.Create(new CreateMealSessionRequest(
                    ["Добавил 100 г говядины, готовое блюдо 100 г, съел 50 г."], new DateOnly(2026, 10, 5)));
        }

        return request;
    }

    private static async Task<string> ReadSnapshotAsync(ExternalApiFixture fixture)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        NutriFlowDbContext db = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
        DbConnection connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        Dictionary<string, List<Dictionary<string, object?>>> snapshot = [];

        foreach (string table in new[] { "Users", "MealSessions", "MealEntries", "Products", "ProductAliases", "DailyGoals", "LabelPhotos", "SavedDishes", "MealEntryAdjustments" })
        {
            using DbCommand command = connection.CreateCommand();
            string ordering = table == "DailyGoals" ? "\"UserId\", \"Date\"" : "1";
            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY {ordering}";
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            List<Dictionary<string, object?>> rows = [];
            while (await reader.ReadAsync())
            {
                Dictionary<string, object?> row = [];
                for (int column = 0; column < reader.FieldCount; column++)
                {
                    row.Add(reader.GetName(column), reader.IsDBNull(column) ? null : reader.GetValue(column));
                }

                rows.Add(row);
            }

            snapshot.Add(table, rows);
        }

        return JsonSerializer.Serialize(snapshot);
    }

    private sealed class ExternalApiFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"nutriflow-external-api-{Guid.NewGuid():N}");
        private readonly TestApiFactory _sourceFactory;

        public ExternalApiFixture(bool infiniteTimeout = false)
        {
            Directory.CreateDirectory(PhotoPath);
            _sourceFactory = new TestApiFactory(
                seedDemoData: false, aiProvider: "Groq", databasePath: DatabasePath,
                labelPhotoPath: PhotoPath, environment: "Production");
            Factory = _sourceFactory.WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(_root);
                builder.UseSetting(WebHostDefaults.ApplicationKey, typeof(ExternalServiceFailureApiTests).Assembly.GetName().Name);
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new TransportFilter(Transport));
                    services.AddSingleton<IStartupFilter>(new CompletionFilter(Transport));
                    services.ConfigureAll<HttpClientFactoryOptions>(options =>
                        options.HttpClientActions.Add(client => client.Timeout = infiniteTimeout
                            ? Timeout.InfiniteTimeSpan
                            : TimeSpan.FromMilliseconds(200)));
                });
            });
            Client = Factory.CreateClient();
        }

        public RecordingTransport Transport { get; } = new();
        public WebApplicationFactory<Program> Factory { get; }
        public HttpClient Client { get; }
        public string DatabasePath => Path.Combine(_root, "nutriflow.db");
        public string PhotoPath => Path.Combine(_root, "label-photos");

        public async Task AssertStoragePathsAsync()
        {
            await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
            NutriFlowDbContext db = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
            Assert.Equal(DatabasePath, db.Database.GetDbConnection().DataSource);
            Assert.Equal(PhotoPath, scope.ServiceProvider.GetRequiredService<IConfiguration>()["Storage:LabelPhotosPath"]);
            Assert.Equal(_root, scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>().ContentRootPath);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
            await _sourceFactory.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TransportFilter(RecordingTransport transport) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
        {
            return builder =>
            {
                next(builder);
                builder.PrimaryHandler = new RecordingHandler(transport);
            };
        }
    }

    private sealed class RecordingTransport
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public string? Failure { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<(int StatusCode, bool RequestAborted)> RequestCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RecordRequest()
        {
            Interlocked.Increment(ref _callCount);
        }

        public async Task StallAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class CompletionFilter(RecordingTransport transport) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, continuation) =>
                {
                    bool observeRequest = transport.Failure is not null;
                    try
                    {
                        await continuation(context);
                    }
                    finally
                    {
                        if (observeRequest)
                        {
                            transport.RequestCompleted.TrySetResult((
                                context.Response.StatusCode, context.RequestAborted.IsCancellationRequested));
                        }
                    }
                });
                next(app);
            };
        }
    }

    private sealed class RecordingHandler(RecordingTransport transport) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            transport.RecordRequest();
            if (transport.Failure == "headers-timeout")
            {
                await transport.StallAsync(cancellationToken);
            }

            if (transport.Failure == "body-timeout")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent(transport) };
            }

            if (transport.Failure is not null)
            {
                HttpResponseMessage error = new(transport.Failure == "rate-limit"
                    ? HttpStatusCode.TooManyRequests : HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(SensitiveDetail)
                };
                error.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
                return error;
            }

            Assert.Equal("/openai/v1/chat/completions", request.RequestUri?.AbsolutePath);
            const string draft = """
                {"dishes":[{"name":"Говядина","ingredients":[{"productName":"Говядина","weightInGrams":100,
                "weightQuality":"exact","removedWeightInGrams":0,"removedWeightQuality":"exact"}],
                "finalWeightInGrams":100,"finalWeightQuality":"exact","portions":[{"weightInGrams":50,
                "fractionOfDish":null,"weightQuality":"exact"}]}],"clarificationQuestions":[]}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    choices = new[] { new { message = new { content = draft }, finish_reason = "stop" } }
                })
            };
        }
    }

    private sealed class StalledContent(RecordingTransport transport) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            return transport.StallAsync(cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
