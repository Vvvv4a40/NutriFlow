using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Api.Tests;

public sealed class MealSessionCreationIdempotencyApiTests
{
    private static readonly DateOnly MealDate = new(2026, 9, 28);
    private static readonly string[] DemoMessages =
    [
        "Добавил 200 г демо-продукта A.",
        "Потом добавил 100 г демо-продукта B.",
        "Готовое блюдо весит 250 г.",
        "Съел 125 г, потом ещё две порции по 62,5 г."
    ];

    [Fact]
    public async Task OpenApi_DescribesOptionalIdempotencyHeader()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using JsonDocument document = JsonDocument.Parse(
            await client.GetStringAsync("/openapi/v1.json"));
        JsonElement parameters = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/meal-sessions")
            .GetProperty("post")
            .GetProperty("parameters");

        bool hasOptionalHeader = parameters.EnumerateArray().Any(parameter =>
            parameter.TryGetProperty("name", out JsonElement name) &&
            name.GetString() == "Idempotency-Key" &&
            parameter.TryGetProperty("in", out JsonElement location) &&
            location.GetString() == "header" &&
            (!parameter.TryGetProperty("required", out JsonElement required) ||
             !required.GetBoolean()));

        Assert.True(hasOptionalHeader, parameters.GetRawText());
    }

    [Fact]
    public async Task RetryWithSameKey_ReturnsSameSessionWithoutParsingAgain()
    {
        CountingParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        string key = Guid.NewGuid().ToString("D");
        CreateMealSessionRequest request = new(DemoMessages, MealDate);

        using HttpResponseMessage first = await PostSessionAsync(client, request, key);
        using HttpResponseMessage repeated = await PostSessionAsync(client, request, key);
        MealSessionResponse created = await ReadSessionAsync(first);
        MealSessionResponse replayed = await ReadSessionAsync(repeated);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(created.Id, replayed.Id);
        Assert.Equal(created.PreviewToken, replayed.PreviewToken);
        Assert.Equal(first.Headers.Location, repeated.Headers.Location);
        Assert.Equal(1, parser.CallCount);
    }

    [Fact]
    public async Task ReusedKeyWithDifferentMessagesOrDate_ReturnsConflictBeforeParsing()
    {
        CountingParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        string key = Guid.NewGuid().ToString("D");

        using HttpResponseMessage first = await PostSessionAsync(
            client, new CreateMealSessionRequest(DemoMessages, MealDate), key);
        MealSessionResponse created = await ReadSessionAsync(first);
        using HttpResponseMessage changedMessages = await PostSessionAsync(
            client, new CreateMealSessionRequest(["Другая история."], MealDate), key);
        using HttpResponseMessage changedDate = await PostSessionAsync(
            client, new CreateMealSessionRequest(DemoMessages, MealDate.AddDays(1)), key);

        Assert.Equal(HttpStatusCode.Conflict, changedMessages.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changedDate.StatusCode);
        Assert.Equal("application/problem+json", changedMessages.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, parser.CallCount);

        MealSessionResponse saved = await client.GetFromJsonAsync<MealSessionResponse>(
            $"/api/meal-sessions/{created.Id}") ?? throw new InvalidDataException();
        Assert.Equal(DemoMessages, saved.Messages);
        Assert.Equal(MealDate, saved.MealDate);
    }

    [Fact]
    public async Task RetryAfterAddingMessage_UsesOriginalRequestInsteadOfCurrentHistory()
    {
        CountingParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        string key = Guid.NewGuid().ToString("D");
        CreateMealSessionRequest request = new(
            ["Добавил 500 г макарон.", "Готовое блюдо весит 900 г.", "Съел 300 г."],
            MealDate);

        using HttpResponseMessage first = await PostSessionAsync(client, request, key);
        MealSessionResponse created = await ReadSessionAsync(first);
        using HttpResponseMessage addition = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{created.Id}/messages",
            new AddMealSessionMessageRequest("В сухом виде."));
        Assert.Equal(HttpStatusCode.OK, addition.StatusCode);

        using HttpResponseMessage repeated = await PostSessionAsync(client, request, key);
        MealSessionResponse replayed = await ReadSessionAsync(repeated);

        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(created.Id, replayed.Id);
        Assert.Equal(4, replayed.Messages.Count);
        Assert.Equal("В сухом виде.", replayed.Messages[^1]);
        Assert.Equal(2, parser.CallCount);
    }

    [Fact]
    public async Task RetryAfterServerRestart_FindsExistingSession()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"nutriflow-idempotency-{Guid.NewGuid():N}.db");
        CountingParser parser = new();
        string key = Guid.NewGuid().ToString("D");
        CreateMealSessionRequest request = new(DemoMessages, MealDate);
        Guid sessionId;

        try
        {
            await using (TestApiFactory firstFactory = new(
                databasePath: databasePath, parser: parser))
            {
                using HttpClient client = firstFactory.CreateClient();
                using HttpResponseMessage first = await PostSessionAsync(client, request, key);
                Assert.Equal(HttpStatusCode.Created, first.StatusCode);
                sessionId = (await ReadSessionAsync(first)).Id;
            }

            await using (TestApiFactory secondFactory = new(
                databasePath: databasePath, parser: parser))
            {
                using HttpClient client = secondFactory.CreateClient();
                using HttpResponseMessage repeated = await PostSessionAsync(client, request, key);
                Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
                Assert.Equal(sessionId, (await ReadSessionAsync(repeated)).Id);
            }

            Assert.Equal(1, parser.CallCount);
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-shm");
            File.Delete($"{databasePath}-wal");
        }
    }

    [Fact]
    public async Task ConcurrentRetries_CreateOnlyOneSession()
    {
        BarrierParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        string key = Guid.NewGuid().ToString("D");
        CreateMealSessionRequest request = new(DemoMessages, MealDate);

        Task<HttpResponseMessage>[] requests =
        [
            PostSessionAsync(client, request, key),
            PostSessionAsync(client, request, key)
        ];
        HttpResponseMessage[] responses = await Task.WhenAll(requests)
            .WaitAsync(TimeSpan.FromSeconds(20));

        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Equal(
                (await ReadSessionAsync(responses[0])).Id,
                (await ReadSessionAsync(responses[1])).Id);
            Assert.Equal(2, parser.CallCount);

            using IServiceScope scope = factory.Services.CreateScope();
            NutriFlowDbContext database = scope.ServiceProvider
                .GetRequiredService<NutriFlowDbContext>();
            int count = await database.Database
                .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM MealSessions")
                .SingleAsync();
            Assert.Equal(1, count);
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidKey_IsRejectedBeforeParsing(string key)
    {
        CountingParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostSessionAsync(
            client, new CreateMealSessionRequest(DemoMessages, MealDate), key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, parser.CallCount);
    }

    [Fact]
    public async Task MultipleKeys_AreRejectedBeforeParsing()
    {
        CountingParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/meal-sessions")
        {
            Content = JsonContent.Create(new CreateMealSessionRequest(DemoMessages, MealDate))
        };
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            [Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")]);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, parser.CallCount);
    }

    [Fact]
    public async Task KeyWithoutExplicitDate_IsRejectedBeforeParsing()
    {
        CountingParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await PostSessionAsync(
            client,
            new CreateMealSessionRequest(DemoMessages),
            Guid.NewGuid().ToString("D"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, parser.CallCount);
    }

    [Fact]
    public async Task MissingKey_KeepsPreviousCreateBehavior()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        CreateMealSessionRequest request = new(DemoMessages, MealDate);

        using HttpResponseMessage first = await PostSessionAsync(client, request, null);
        using HttpResponseMessage second = await PostSessionAsync(client, request, null);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.NotEqual(
            (await ReadSessionAsync(first)).Id,
            (await ReadSessionAsync(second)).Id);
    }

    private static async Task<HttpResponseMessage> PostSessionAsync(
        HttpClient client,
        CreateMealSessionRequest request,
        string? key)
    {
        using HttpRequestMessage message = new(HttpMethod.Post, "/api/meal-sessions")
        {
            Content = JsonContent.Create(request)
        };

        if (key is not null)
        {
            message.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(message);
    }

    private static async Task<MealSessionResponse> ReadSessionAsync(HttpResponseMessage response)
    {
        return await response.Content.ReadFromJsonAsync<MealSessionResponse>() ??
            throw new InvalidDataException();
    }

    private sealed class CountingParser : IMealParser
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<MealDraft> ParseAsync(
            CaptureSession session,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return new FakeMealParser().ParseAsync(session, cancellationToken);
        }
    }

    private sealed class BarrierParser : IMealParser
    {
        private readonly TaskCompletionSource _bothRequestsArrived = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<MealDraft> ParseAsync(
            CaptureSession session,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _callCount) == 2)
            {
                _bothRequestsArrived.TrySetResult();
            }

            await _bothRequestsArrived.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken);

            return await new FakeMealParser().ParseAsync(session, cancellationToken);
        }
    }
}
