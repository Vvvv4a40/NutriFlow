using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Api.Tests;

public sealed class MealSessionErrorBoundaryTests
{
    private const string SensitiveDetail = "sensitive stored or upstream detail";
    private static readonly CreateMealSessionRequest InitialRequest = new(
        ["Добавил 100 г продукта и съел 50 г."],
        new DateOnly(2026, 10, 5));

    [Theory]
    [InlineData("create")]
    [InlineData("message")]
    public async Task InvalidParserResponse_ReturnsRedactedBadGatewayAndWritesNothing(string operation)
    {
        RecordingParser parser = new();
        await using TestApiFactory factory = new(
            seedDemoData: false,
            parser: parser,
            environment: "Production");
        using HttpClient client = factory.CreateClient();
        MealSessionResponse? session = operation == "message"
            ? await CreateSessionAsync(client, Guid.NewGuid())
            : null;
        DatabaseSnapshot before = await ReadSnapshotAsync(factory);
        int callsBefore = parser.CallCount;
        parser.Failure = new InvalidDataException(SensitiveDetail);

        using HttpResponseMessage response = session is null
            ? await client.PostAsJsonAsync("/api/meal-sessions", InitialRequest)
            : await client.PostAsJsonAsync(
                $"/api/meal-sessions/{session.Id}/messages",
                new AddMealSessionMessageRequest("Уточнение."));

        await AssertProblemAsync(response, HttpStatusCode.BadGateway);
        Assert.Equal(callsBefore + 1, parser.CallCount);
        Assert.Equal(before, await ReadSnapshotAsync(factory));
    }

    [Theory]
    [InlineData("create", "draft")]
    [InlineData("create", "messages")]
    [InlineData("create", "receipts")]
    [InlineData("message", "draft")]
    [InlineData("message", "messages")]
    [InlineData("message", "receipts")]
    [InlineData("get", "draft")]
    [InlineData("get", "messages")]
    [InlineData("get", "receipts")]
    [InlineData("confirm", "draft")]
    [InlineData("confirm", "messages")]
    [InlineData("confirm", "receipts")]
    public async Task CorruptStoredSession_ReturnsRedactedInternalErrorWithoutParsingOrWriting(
        string operation,
        string corruptedField)
    {
        RecordingParser parser = new();
        await using TestApiFactory factory = new(
            seedDemoData: false,
            parser: parser,
            environment: "Production");
        using HttpClient client = factory.CreateClient();
        Guid idempotencyKey = Guid.NewGuid();
        MealSessionResponse session = await CreateSessionAsync(client, idempotencyKey);
        await CorruptSessionAsync(factory, session.Id, corruptedField);
        DatabaseSnapshot before = await ReadSnapshotAsync(factory);
        int callsBefore = parser.CallCount;

        using HttpResponseMessage response = operation switch
        {
            "create" => await PostWithKeyAsync(client, idempotencyKey),
            "message" => await client.PostAsJsonAsync(
                $"/api/meal-sessions/{session.Id}/messages",
                new AddMealSessionMessageRequest("Уточнение.")),
            "confirm" => await client.PostAsJsonAsync(
                $"/api/meal-sessions/{session.Id}/confirm",
                new ConfirmMealSessionRequest(session.PreviewToken)),
            _ => await client.GetAsync($"/api/meal-sessions/{session.Id}")
        };

        await AssertProblemAsync(response, HttpStatusCode.InternalServerError);
        Assert.Equal(callsBefore, parser.CallCount);
        Assert.Equal(before, await ReadSnapshotAsync(factory));
    }

    private static async Task<MealSessionResponse> CreateSessionAsync(
        HttpClient client,
        Guid idempotencyKey)
    {
        using HttpResponseMessage response = await PostWithKeyAsync(client, idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<MealSessionResponse>()
            ?? throw new InvalidDataException();
    }

    private static async Task<HttpResponseMessage> PostWithKeyAsync(
        HttpClient client,
        Guid idempotencyKey)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/meal-sessions")
        {
            Content = JsonContent.Create(InitialRequest)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey.ToString("D"));
        return await client.SendAsync(request);
    }

    private static async Task CorruptSessionAsync(
        TestApiFactory factory,
        Guid sessionId,
        string field)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        NutriFlowDbContext dbContext = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
        int changedRows = field switch
        {
            "draft" => await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"MealSessions\" SET \"DraftJson\" = {SensitiveDetail} WHERE \"Id\" = {sessionId}"),
            "messages" => await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"MealSessions\" SET \"MessagesJson\" = {SensitiveDetail} WHERE \"Id\" = {sessionId}"),
            _ => await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"MealSessions\" SET \"MessageRequestHashesJson\" = {SensitiveDetail} WHERE \"Id\" = {sessionId}")
        };
        Assert.Equal(1, changedRows);
    }

    private static async Task<DatabaseSnapshot> ReadSnapshotAsync(TestApiFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        NutriFlowDbContext dbContext = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
        DbConnection connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        return new DatabaseSnapshot(
            await ReadRowsAsync(connection, "SELECT * FROM \"MealSessions\" ORDER BY \"Id\""),
            await ReadRowsAsync(connection, "SELECT * FROM \"MealEntries\" ORDER BY \"Id\""));
    }

    private static async Task<string> ReadRowsAsync(DbConnection connection, string query)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = query;
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        List<Dictionary<string, object?>> rows = [];
        while (await reader.ReadAsync())
        {
            Dictionary<string, object?> row = [];
            for (int index = 0; index < reader.FieldCount; index++)
            {
                row.Add(reader.GetName(index), reader.IsDBNull(index) ? null : reader.GetValue(index));
            }

            rows.Add(row);
        }

        return JsonSerializer.Serialize(rows);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SensitiveDetail, body, StringComparison.Ordinal);
        using JsonDocument problem = JsonDocument.Parse(body);
        Assert.Equal((int)expectedStatus, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(problem.RootElement.TryGetProperty("exception", out _));
        if (expectedStatus == HttpStatusCode.InternalServerError)
        {
            Assert.DoesNotContain("AI provider", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Stored", body, StringComparison.Ordinal);
        }
    }

    private sealed record DatabaseSnapshot(string SessionsJson, string EntriesJson);

    private sealed class RecordingParser : IMealParser
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public Exception? Failure { get; set; }

        public Task<MealDraft> ParseAsync(
            CaptureSession session,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            if (Failure is not null)
            {
                return Task.FromException<MealDraft>(Failure);
            }

            return Task.FromResult(new MealDraft(
                [new DishDraft("Блюдо", [new IngredientDraft("Продукт", 100m)], 100m, [50m])],
                []));
        }
    }
}
