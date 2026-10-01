using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;

namespace NutriFlow.Api.Tests;

public sealed class MealSessionMessageIdempotencyApiTests
{
    private static readonly DateOnly MealDate = new(2026, 10, 2);

    [Fact]
    public async Task OpenApi_DescribesOptionalIdempotencyHeaderForMessages()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using JsonDocument document = JsonDocument.Parse(
            await client.GetStringAsync("/openapi/v1.json"));
        JsonElement parameters = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/meal-sessions/{id}/messages")
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
    public async Task RetryWithSameKey_ReturnsCurrentSessionWithoutParsingAgain()
    {
        CountingParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        MealSessionResponse initial = await CreateSessionAsync(client);
        string key = Guid.NewGuid().ToString("D");

        using HttpResponseMessage first = await PostMessageAsync(
            client, initial.Id, "Добавил овощи.", key);
        using HttpResponseMessage repeated = await PostMessageAsync(
            client, initial.Id, "Добавил овощи.", key);
        MealSessionResponse firstSession = await ReadSessionAsync(first);
        MealSessionResponse replayed = await ReadSessionAsync(repeated);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(firstSession.Id, replayed.Id);
        Assert.Equal(firstSession.Messages, replayed.Messages);
        Assert.Equal(firstSession.PreviewToken, replayed.PreviewToken);
        Assert.Equal(2, replayed.Messages.Count);
        Assert.Equal(2, parser.CallCount);
    }

    [Fact]
    public async Task ReusedKeyWithDifferentMessage_ReturnsConflictBeforeParsing()
    {
        CountingParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        MealSessionResponse initial = await CreateSessionAsync(client);
        string key = Guid.NewGuid().ToString("D");

        using HttpResponseMessage first = await PostMessageAsync(
            client, initial.Id, "Добавил овощи.", key);
        using HttpResponseMessage changed = await PostMessageAsync(
            client, initial.Id, "Добавил масло.", key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal("application/problem+json", changed.Content.Headers.ContentType?.MediaType);
        Assert.Equal(2, parser.CallCount);
        MealSessionResponse saved = await ReadSessionAsync(client, initial.Id);
        Assert.Equal(["Готовлю обед.", "Добавил овощи."], saved.Messages);
    }

    [Fact]
    public async Task ReplayAfterLaterAppendAndConfirmation_ReturnsCurrentSessionWithoutParsing()
    {
        CountingParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        MealSessionResponse initial = await CreateSessionAsync(client);
        string key = Guid.NewGuid().ToString("D");

        using HttpResponseMessage first = await PostMessageAsync(
            client, initial.Id, "Добавил овощи.", key);
        MealSessionResponse afterFirst = await ReadSessionAsync(first);
        using HttpResponseMessage later = await PostMessageAsync(
            client, initial.Id, "Добавил специи.", null);
        MealSessionResponse afterLater = await ReadSessionAsync(later);
        using HttpResponseMessage confirmed = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{initial.Id}/confirm",
            new ConfirmMealSessionRequest(afterLater.PreviewToken));
        using HttpResponseMessage replay = await PostMessageAsync(
            client, initial.Id, "Добавил овощи.", key);
        MealSessionResponse current = await ReadSessionAsync(replay);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(afterFirst.Id, current.Id);
        Assert.Equal("Confirmed", current.Status);
        Assert.Equal(
            ["Готовлю обед.", "Добавил овощи.", "Добавил специи."],
            current.Messages);
        Assert.Equal(3, parser.CallCount);
    }

    [Fact]
    public async Task RetryAfterServerRestart_DoesNotAppendOrParseAgain()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"nutriflow-message-idempotency-{Guid.NewGuid():N}.db");
        CountingParser parser = new();
        string key = Guid.NewGuid().ToString("D");
        Guid sessionId;

        try
        {
            await using (TestApiFactory firstFactory = new(
                databasePath: databasePath, parser: parser))
            {
                using HttpClient client = firstFactory.CreateClient();
                MealSessionResponse initial = await CreateSessionAsync(client);
                sessionId = initial.Id;
                using HttpResponseMessage first = await PostMessageAsync(
                    client, sessionId, "Добавил овощи.", key);
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            }

            await using (TestApiFactory secondFactory = new(
                databasePath: databasePath, parser: parser))
            {
                using HttpClient client = secondFactory.CreateClient();
                using HttpResponseMessage repeated = await PostMessageAsync(
                    client, sessionId, "Добавил овощи.", key);
                MealSessionResponse replayed = await ReadSessionAsync(repeated);

                Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
                Assert.Equal(sessionId, replayed.Id);
                Assert.Equal(["Готовлю обед.", "Добавил овощи."], replayed.Messages);
            }

            Assert.Equal(2, parser.CallCount);
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-shm");
            File.Delete($"{databasePath}-wal");
        }
    }

    [Fact]
    public async Task ConcurrentRetries_AppendOnlyOneMessage()
    {
        BarrierParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        MealSessionResponse initial = await CreateSessionAsync(client);
        string key = Guid.NewGuid().ToString("D");

        Task<HttpResponseMessage>[] requests =
        [
            PostMessageAsync(client, initial.Id, "Добавил овощи.", key),
            PostMessageAsync(client, initial.Id, "Добавил овощи.", key)
        ];
        HttpResponseMessage[] responses = await Task.WhenAll(requests)
            .WaitAsync(TimeSpan.FromSeconds(20));

        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            Assert.Equal(2, parser.SynchronizedRequestCount);
            foreach (HttpResponseMessage response in responses)
            {
                MealSessionResponse session = await ReadSessionAsync(response);
                Assert.Equal(["Готовлю обед.", "Добавил овощи."], session.Messages);
            }

            MealSessionResponse saved = await ReadSessionAsync(client, initial.Id);
            Assert.Equal(["Готовлю обед.", "Добавил овощи."], saved.Messages);
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
    [InlineData(" ")]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidKey_IsRejectedBeforeParsing(string key)
    {
        CountingParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        MealSessionResponse initial = await CreateSessionAsync(client);

        using HttpResponseMessage response = await PostMessageAsync(
            client, initial.Id, "Добавил овощи.", key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, parser.CallCount);
        Assert.Single((await ReadSessionAsync(client, initial.Id)).Messages);
    }

    [Fact]
    public async Task MultipleKeys_AreRejectedBeforeParsing()
    {
        CountingParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        MealSessionResponse initial = await CreateSessionAsync(client);
        using HttpRequestMessage request = new(
            HttpMethod.Post, $"/api/meal-sessions/{initial.Id}/messages")
        {
            Content = JsonContent.Create(new AddMealSessionMessageRequest("Добавил овощи."))
        };
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            [Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")]);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, parser.CallCount);
    }

    [Fact]
    public async Task MissingKey_KeepsPreviousAppendBehavior()
    {
        CountingParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        MealSessionResponse initial = await CreateSessionAsync(client);

        using HttpResponseMessage first = await PostMessageAsync(
            client, initial.Id, "Добавил овощи.", null);
        using HttpResponseMessage second = await PostMessageAsync(
            client, initial.Id, "Добавил овощи.", null);
        MealSessionResponse result = await ReadSessionAsync(second);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            ["Готовлю обед.", "Добавил овощи.", "Добавил овощи."],
            result.Messages);
        Assert.Equal(3, parser.CallCount);
    }

    [Fact]
    public async Task SameKeyInAnotherSession_AppendsToEachSession()
    {
        CountingParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        MealSessionResponse firstSession = await CreateSessionAsync(client);
        MealSessionResponse secondSession = await CreateSessionAsync(client);
        string key = Guid.NewGuid().ToString("D");

        using HttpResponseMessage first = await PostMessageAsync(
            client, firstSession.Id, "Добавил овощи.", key);
        using HttpResponseMessage second = await PostMessageAsync(
            client, secondSession.Id, "Добавил масло.", key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(["Готовлю обед.", "Добавил овощи."],
            (await ReadSessionAsync(first)).Messages);
        Assert.Equal(["Готовлю обед.", "Добавил масло."],
            (await ReadSessionAsync(second)).Messages);
        Assert.Equal(4, parser.CallCount);
    }

    [Fact]
    public async Task DifferentKeysWithSameText_AppendTwice()
    {
        CountingParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        MealSessionResponse initial = await CreateSessionAsync(client);

        using HttpResponseMessage first = await PostMessageAsync(
            client, initial.Id, "Добавил овощи.", Guid.NewGuid().ToString("D"));
        using HttpResponseMessage second = await PostMessageAsync(
            client, initial.Id, "Добавил овощи.", Guid.NewGuid().ToString("D"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            ["Готовлю обед.", "Добавил овощи.", "Добавил овощи."],
            (await ReadSessionAsync(second)).Messages);
        Assert.Equal(3, parser.CallCount);
    }

    private static async Task<MealSessionResponse> CreateSessionAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/meal-sessions",
            new CreateMealSessionRequest(["Готовлю обед."], MealDate));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadSessionAsync(response);
    }

    private static async Task<HttpResponseMessage> PostMessageAsync(
        HttpClient client,
        Guid sessionId,
        string message,
        string? key)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Post, $"/api/meal-sessions/{sessionId}/messages")
        {
            Content = JsonContent.Create(new AddMealSessionMessageRequest(message))
        };

        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return await client.SendAsync(request);
    }

    private static async Task<MealSessionResponse> ReadSessionAsync(
        HttpClient client,
        Guid sessionId) =>
        await client.GetFromJsonAsync<MealSessionResponse>(
            $"/api/meal-sessions/{sessionId}") ?? throw new InvalidDataException();

    private static async Task<MealSessionResponse> ReadSessionAsync(
        HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<MealSessionResponse>() ??
        throw new InvalidDataException();

    private sealed class CountingParser : IMealParser
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<MealDraft> ParseAsync(
            CaptureSession session,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(CreateDraft());
        }
    }

    private sealed class BarrierParser : IMealParser
    {
        private readonly TaskCompletionSource _bothRequestsArrived = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _synchronizedRequestCount;

        public int SynchronizedRequestCount => Volatile.Read(ref _synchronizedRequestCount);

        public async Task<MealDraft> ParseAsync(
            CaptureSession session,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (session.InputEvents.Count > 1)
            {
                if (Interlocked.Increment(ref _synchronizedRequestCount) == 2)
                {
                    _bothRequestsArrived.TrySetResult();
                }

                await _bothRequestsArrived.Task.WaitAsync(
                    TimeSpan.FromSeconds(10), cancellationToken);
            }

            return CreateDraft();
        }
    }

    private static MealDraft CreateDraft() => new(
        [new DishDraft(
            "Блюдо",
            [new IngredientDraft("Демо-продукт A", 100m)],
            100m,
            [50m])],
        []);
}
