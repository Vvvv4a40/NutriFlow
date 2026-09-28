using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;

namespace NutriFlow.Api.Tests;

public sealed class MobileApiContractTests
{
    private static readonly DateOnly MealDate = new(2026, 9, 28);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] DemoMessages =
    [
        "Добавил 200 г демо-продукта A.",
        "Потом добавил 100 г демо-продукта B.",
        "Готовое блюдо весит 250 г.",
        "Съел 125 г, потом ещё две порции по 62,5 г."
    ];

    [Fact]
    public async Task SessionResponse_UsesCamelCaseNumbersAndExplicitNulls()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/meal-sessions", new CreateMealSessionRequest(DemoMessages, MealDate));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement session = document.RootElement;
        Assert.True(Guid.TryParse(session.GetProperty("id").GetString(), out _));
        Assert.Equal("2026-09-28", session.GetProperty("mealDate").GetString());
        Assert.Equal("ReadyForConfirmation", session.GetProperty("status").GetString());
        Assert.False(session.TryGetProperty("MealDate", out _));
        JsonElement dish = session.GetProperty("dishes")[0];
        Assert.Equal(JsonValueKind.Number, dish.GetProperty("totalNutrition").GetProperty("calories").ValueKind);
        Assert.Equal(400m, dish.GetProperty("totalNutrition").GetProperty("calories").GetDecimal());
        Assert.Equal(62.5m, dish.GetProperty("portions")[1].GetProperty("weightInGrams").GetDecimal());
        Assert.Equal(JsonValueKind.Null, dish.GetProperty("portions")[1].GetProperty("fractionOfDish").ValueKind);
        JsonElement product = dish.GetProperty("ingredients")[0].GetProperty("resolvedProduct");
        Assert.Equal("Unknown", product.GetProperty("dataQuality").GetString());
        Assert.False(string.IsNullOrWhiteSpace(product.GetProperty("sourceName").GetString()));
    }

    [Fact]
    public async Task UnsupportedCreate_ReturnsUnprocessableEntityWithoutDiaryEntries()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/meal-sessions", new CreateMealSessionRequest(["Неизвестный Fake-сценарий."], MealDate));

        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        Assert.Empty((await ReadProgressAsync(client)).Entries);
    }

    [Fact]
    public async Task UnsupportedClarification_PreservesPreviousSession()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        MealSessionResponse original = await CreateSessionAsync(client);
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{original.Id}/messages", new AddMealSessionMessageRequest("Неизвестное Fake-уточнение."));

        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity);
        MealSessionResponse restored = await ReadSessionAsync(client, original.Id);
        Assert.Equal(original.Messages, restored.Messages);
        Assert.Equal(original.PreviewToken, restored.PreviewToken);
        Assert.Empty((await ReadProgressAsync(client)).Entries);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"messages\":null}")]
    [InlineData("{\"messages\":[]}")]
    [InlineData("{\"messages\":[null]}")]
    [InlineData("{\"messages\":[\" \" ]}")]
    public async Task CreateSession_WithInvalidMessages_ReturnsValidationProblemBeforeParsing(string body)
    {
        CountingParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await PostJsonAsync(client, "/api/meal-sessions", body);

        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("Messages", out _));
        Assert.Equal(0, parser.CallCount);
    }

    [Theory]
    [InlineData("single-message")]
    [InlineData("message-count")]
    [InlineData("total-length")]
    public async Task CreateSession_AboveMessageLimits_RejectsBeforeParsing(string limit)
    {
        string[] messages = limit switch
        {
            "single-message" => [new string('x', 4001)],
            "message-count" => Enumerable.Repeat("message", 51).ToArray(),
            _ => Enumerable.Repeat(new string('x', 4000), 6).ToArray()
        };
        CountingParser parser = new();
        await using TestApiFactory factory = new(parser: parser);
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/meal-sessions", new CreateMealSessionRequest(messages, MealDate));

        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("Messages", out _));
        Assert.Equal(0, parser.CallCount);
    }

    [Theory]
    [InlineData("/api/meal-sessions", "{\"messages\":")]
    [InlineData("/api/meal-sessions", "{\"messages\":\"not-an-array\"}")]
    [InlineData("/api/meal-sessions", "{\"messages\":[\"message\"],\"mealDate\":\"2026-02-30\"}")]
    [InlineData("/api/daily-goals/2026-09-28", "{\"calories\":\"not-a-number\"}")]
    public async Task InvalidJsonContract_ReturnsMachineReadableBadRequest(string path, string body)
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(
            path.StartsWith("/api/daily-goals", StringComparison.Ordinal) ? HttpMethod.Put : HttpMethod.Post,
            path);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.SendAsync(request);

        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.False(problem.RootElement.TryGetProperty("exception", out _));
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026-02-30")]
    public async Task InvalidDateRoute_ReturnsMachineReadableBadRequest(string date)
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync($"/api/daily-progress/{date}");

        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("POST", "/api/meal-sessions", "{\"messages\":", 400)]
    [InlineData("POST", "/api/meal-sessions", "{}", 415)]
    [InlineData("GET", "/api/daily-progress/not-a-date", null, 400)]
    [InlineData("GET", "/api/meal-sessions/not-a-guid", null, 404)]
    public async Task ProductionBindingErrors_ReturnProblemDetails(string method, string path, string? body, int status)
    {
        await using TestApiFactory factory = new(environment: "Production");
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, status == 415 ? "text/plain" : "application/json");
        }
        using HttpResponseMessage response = await client.SendAsync(request);

        using JsonDocument problem = await AssertProblemAsync(response, (HttpStatusCode)status);
        Assert.False(problem.RootElement.TryGetProperty("exception", out _));
    }

    [Theory]
    [InlineData("get")]
    [InlineData("message")]
    [InlineData("confirm")]
    public async Task UnknownSession_ReturnsNotFound(string operation)
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        string path = $"/api/meal-sessions/{Guid.NewGuid()}";
        using HttpResponseMessage response = operation switch
        {
            "message" => await client.PostAsJsonAsync(path + "/messages", new AddMealSessionMessageRequest("Уточнение.")),
            "confirm" => await client.PostAsJsonAsync(path + "/confirm", new ConfirmMealSessionRequest(new string('A', 64))),
            _ => await client.GetAsync(path)
        };

        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"message\":null}")]
    [InlineData("{\"message\":\" \"}")]
    public async Task InvalidClarification_DoesNotChangeSession(string body)
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        MealSessionResponse original = await CreateSessionAsync(client);
        using HttpResponseMessage response = await PostJsonAsync(client, $"/api/meal-sessions/{original.Id}/messages", body);

        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        MealSessionResponse restored = await ReadSessionAsync(client, original.Id);
        Assert.Equal(original.Messages, restored.Messages);
        Assert.Equal(original.PreviewToken, restored.PreviewToken);
        Assert.Equal(original.Status, restored.Status);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"previewToken\":\" \"}")]
    public async Task MissingConfirmationToken_DoesNotWriteDiary(string body)
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        MealSessionResponse original = await CreateSessionAsync(client);
        using HttpResponseMessage response = await PostJsonAsync(client, $"/api/meal-sessions/{original.Id}/confirm", body);

        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("previewToken", out _));
        Assert.Empty((await ReadProgressAsync(client)).Entries);
        Assert.Equal("ReadyForConfirmation", (await ReadSessionAsync(client, original.Id)).Status);
    }

    [Fact]
    public async Task ConfirmedSession_RejectsFurtherMessagesAndKeepsDiaryUnchanged()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        MealSessionResponse original = await CreateSessionAsync(client);
        using HttpResponseMessage confirmation = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{original.Id}/confirm", new ConfirmMealSessionRequest(original.PreviewToken));
        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{original.Id}/messages", new AddMealSessionMessageRequest("Съел ещё 10 г."));

        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.Conflict);
        MealSessionResponse restored = await ReadSessionAsync(client, original.Id);
        Assert.Equal("Confirmed", restored.Status);
        Assert.Equal(original.Messages, restored.Messages);
        DailyProgressResponse progress = await ReadProgressAsync(client);
        Assert.Equal(3, progress.Entries.Count);
        Assert.Equal(400m, progress.Consumed.Calories);
    }

    [Fact]
    public async Task DailyGoal_RepeatedPutIsIdempotentAndDatesStaySeparate()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        DailyProgressResponse empty = await ReadProgressAsync(client);
        Assert.Null(empty.Goal);
        Assert.Null(empty.Remaining);
        Assert.Null(empty.Exceeded);
        Assert.Empty(empty.Entries);
        Assert.Equal(0m, empty.Consumed.Calories);
        SetDailyGoalRequest goal = new(2000m, 100m, 70m, 250m);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            using HttpResponseMessage response = await client.PutAsJsonAsync($"/api/daily-goals/{MealDate:yyyy-MM-dd}", goal);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        DailyProgressResponse progress = await ReadProgressAsync(client);
        Assert.Equal(2000m, progress.Goal?.Calories);
        Assert.Equal(2000m, progress.Remaining?.Calories);
        Assert.Empty(progress.Entries);
        DailyProgressResponse nextDay = await client.GetFromJsonAsync<DailyProgressResponse>(
            $"/api/daily-progress/{MealDate.AddDays(1):yyyy-MM-dd}") ?? throw new InvalidDataException();
        Assert.Null(nextDay.Goal);
        Assert.Empty(nextDay.Entries);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"calories\":-1,\"proteinGrams\":100,\"fatGrams\":70,\"carbohydratesGrams\":250}")]
    public async Task InvalidDailyGoal_PreservesPreviouslySavedGoal(string body)
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage saved = await client.PutAsJsonAsync(
            $"/api/daily-goals/{MealDate:yyyy-MM-dd}", new SetDailyGoalRequest(2000m, 100m, 70m, 250m));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using StringContent content = new(body, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PutAsync($"/api/daily-goals/{MealDate:yyyy-MM-dd}", content);

        using JsonDocument problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(2000m, (await ReadProgressAsync(client)).Goal?.Calories);
    }

    [Fact]
    public async Task SessionAndDiary_SurviveServerRestartAndConfirmationRetry()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("nutriflow-api-restart-");
        string databasePath = Path.Combine(directory.FullName, "nutriflow.db");
        Guid sessionId;
        string previewToken;

        try
        {
            await using (TestApiFactory first = new(databasePath: databasePath))
            {
                using HttpClient client = first.CreateClient();
                using HttpResponseMessage goal = await client.PutAsJsonAsync(
                    $"/api/daily-goals/{MealDate:yyyy-MM-dd}", new SetDailyGoalRequest(2000m, 100m, 70m, 250m));
                Assert.Equal(HttpStatusCode.OK, goal.StatusCode);
                MealSessionResponse session = await CreateSessionAsync(client);
                sessionId = session.Id;
                previewToken = session.PreviewToken;
                Assert.Empty((await ReadProgressAsync(client)).Entries);
            }

            await using (TestApiFactory second = new(databasePath: databasePath))
            {
                using HttpClient client = second.CreateClient();
                MealSessionResponse restored = await ReadSessionAsync(client, sessionId);
                Assert.Equal(previewToken, restored.PreviewToken);
                Assert.Equal(DemoMessages, restored.Messages);
                Assert.Equal(MealDate, restored.MealDate);
                using HttpResponseMessage confirmed = await client.PostAsJsonAsync(
                    $"/api/meal-sessions/{sessionId}/confirm", new ConfirmMealSessionRequest(previewToken));
                Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
            }

            await using (TestApiFactory third = new(databasePath: databasePath))
            {
                using HttpClient client = third.CreateClient();
                using HttpResponseMessage repeated = await client.PostAsJsonAsync(
                    $"/api/meal-sessions/{sessionId}/confirm", new ConfirmMealSessionRequest(previewToken));
                ConfirmMealSessionResponse confirmation = await repeated.Content.ReadFromJsonAsync<ConfirmMealSessionResponse>() ??
                    throw new InvalidDataException();
                Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
                Assert.Equal("AlreadyConfirmed", confirmation.Outcome);
                Assert.Equal(3, confirmation.Entries.Count);
                Assert.Equal("Confirmed", (await ReadSessionAsync(client, sessionId)).Status);
                DailyProgressResponse progress = await ReadProgressAsync(client);
                Assert.Equal(2000m, progress.Goal?.Calories);
                Assert.Equal(3, progress.Entries.Count);
                Assert.Equal(400m, progress.Consumed.Calories);
                Assert.Equal(1600m, progress.Remaining?.Calories);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, string body)
    {
        using StringContent content = new(body, Encoding.UTF8, "application/json");
        return await client.PostAsync(path, content);
    }

    private static async Task<JsonDocument> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)expected, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("title").GetString()));
        return problem;
    }

    private static async Task<MealSessionResponse> CreateSessionAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/meal-sessions", new CreateMealSessionRequest(DemoMessages, MealDate));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(response.Headers.Location);
        MealSessionResponse session = await response.Content.ReadFromJsonAsync<MealSessionResponse>() ??
            throw new InvalidDataException();
        Assert.Equal($"/api/meal-sessions/{session.Id}", response.Headers.Location.ToString());
        Assert.Equal(MealDate, session.MealDate);
        return session;
    }

    private static async Task<MealSessionResponse> ReadSessionAsync(HttpClient client, Guid id)
    {
        return await client.GetFromJsonAsync<MealSessionResponse>($"/api/meal-sessions/{id}", JsonOptions) ??
            throw new InvalidDataException();
    }

    private static async Task<DailyProgressResponse> ReadProgressAsync(HttpClient client)
    {
        return await client.GetFromJsonAsync<DailyProgressResponse>($"/api/daily-progress/{MealDate:yyyy-MM-dd}", JsonOptions) ??
            throw new InvalidDataException();
    }

    private sealed class CountingParser : IMealParser
    {
        public int CallCount { get; private set; }

        public Task<MealDraft> ParseAsync(CaptureSession session, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return new FakeMealParser().ParseAsync(session, cancellationToken);
        }
    }
}
