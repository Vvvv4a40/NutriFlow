using System.Net;
using System.Net.Http.Json;
using System.Text;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;

namespace NutriFlow.Api.Tests;

public sealed class ApiResponseCacheTests
{
    private static readonly DateOnly MealDate = new(2026, 10, 5);
    private static readonly string[] DemoMessages =
    [
        "Добавил 200 г демо-продукта A.",
        "Потом добавил 100 г демо-продукта B.",
        "Готовое блюдо весит 250 г.",
        "Съел 125 г, потом ещё две порции по 62,5 г."
    ];

    [Theory]
    [InlineData("/api/capabilities")]
    [InlineData("/API/capabilities")]
    [InlineData("/api/daily-progress/2026-10-05")]
    [InlineData("/api/saved-dishes")]
    [InlineData("/api/products?name=missing")]
    public async Task SuccessfulGet_DisablesPrivateAndSharedHttpCaching(string path)
    {
        await using TestApiFactory factory = new(environment: "Production");
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
    }

    [Theory]
    [InlineData("GET", "/api", null, null, 404)]
    [InlineData("GET", "/API/unknown", null, null, 404)]
    [InlineData("GET", "/api/daily-progress/not-a-date", null, null, 400)]
    [InlineData("POST", "/api/meal-sessions", "{\"messages\":", "application/json", 400)]
    [InlineData("POST", "/api/meal-sessions", "{}", "text/plain", 415)]
    public async Task RoutingAndBindingErrors_KeepCachePolicy(
        string method, string path, string? body, string? mediaType, int status)
    {
        await using TestApiFactory factory = new(environment: "Production");
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, mediaType!);
        }
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AssertNoStore(response);
    }

    [Fact]
    public async Task DiaryChanges_PreserveEtagsAndDisableCachingForEveryOutcome()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage creation = await client.PostAsJsonAsync(
            "/api/meal-sessions", new CreateMealSessionRequest(DemoMessages, MealDate));
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        AssertNoStore(creation);
        MealSessionResponse session = (await creation.Content.ReadFromJsonAsync<MealSessionResponse>())!;

        using HttpResponseMessage sessionRead = await client.GetAsync($"/api/meal-sessions/{session.Id}");
        Assert.Equal(HttpStatusCode.OK, sessionRead.StatusCode);
        AssertNoStore(sessionRead);
        using HttpResponseMessage goal = await client.PutAsJsonAsync(
            $"/api/daily-goals/{MealDate:yyyy-MM-dd}", new SetDailyGoalRequest(500m, 30m, 30m, 50m));
        Assert.Equal(HttpStatusCode.OK, goal.StatusCode);
        AssertNoStore(goal);

        using HttpResponseMessage confirmation = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{session.Id}/confirm", new ConfirmMealSessionRequest(session.PreviewToken));
        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        AssertNoStore(confirmation);
        ConfirmMealSessionResponse confirmed = (await confirmation.Content.ReadFromJsonAsync<ConfirmMealSessionResponse>())!;
        string entryPath = $"/api/meal-entries/{confirmed.Entries[0].Id}";
        using HttpResponseMessage entryRead = await client.GetAsync(entryPath);
        Assert.Equal(HttpStatusCode.OK, entryRead.StatusCode);
        Assert.Equal("\"0\"", entryRead.Headers.ETag!.Tag);
        Assert.False(entryRead.Headers.ETag.IsWeak);
        AssertNoStore(entryRead);

        using HttpResponseMessage missingRevision = await client.PutAsJsonAsync(
            entryPath, new UpdateMealEntryRequest(100m));
        Assert.Equal((HttpStatusCode)428, missingRevision.StatusCode);
        AssertNoStore(missingRevision);
        using HttpRequestMessage update = CreateUpdate(entryPath, "\"0\"");
        using HttpResponseMessage updated = await client.SendAsync(update);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("\"1\"", updated.Headers.ETag!.Tag);
        AssertNoStore(updated);
        using HttpRequestMessage staleUpdate = CreateUpdate(entryPath, "\"0\"");
        using HttpResponseMessage conflict = await client.SendAsync(staleUpdate);
        Assert.Equal(HttpStatusCode.PreconditionFailed, conflict.StatusCode);
        AssertNoStore(conflict);

        using HttpRequestMessage deletion = new(HttpMethod.Delete, entryPath);
        deletion.Headers.IfMatch.Add(updated.Headers.ETag);
        using HttpResponseMessage deleted = await client.SendAsync(deletion);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        AssertNoStore(deleted);
        using HttpResponseMessage deletedRead = await client.GetAsync(entryPath);
        Assert.Equal(HttpStatusCode.NotFound, deletedRead.StatusCode);
        AssertNoStore(deletedRead);
    }

    [Fact]
    public async Task UnhandledException_KeepsCachePolicyAfterResponseIsCleared()
    {
        await using TestApiFactory factory = new(parser: new FailingParser(), environment: "Production");
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/meal-sessions", new CreateMealSessionRequest(DemoMessages, MealDate));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("internal failure", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        AssertNoStore(response);
    }

    [Fact]
    public async Task UnavailableExternalService_DisablesCaching()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using MultipartFormDataContent content = new();
        using ByteArrayContent audio = new(Encoding.ASCII.GetBytes("RIFF0000WAVE0000"));
        audio.Headers.ContentType = new("audio/wav");
        content.Add(audio, "audio", "recording.wav");
        using HttpResponseMessage response = await client.PostAsync("/api/audio/transcribe", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertNoStore(response);
    }

    [Fact]
    public async Task RateLimitRejection_KeepsCachePolicy()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        for (int attempt = 0; attempt < 12; attempt++)
        {
            using HttpResponseMessage allowed = await client.PostAsJsonAsync(
                "/api/meal-drafts/parse", new ParseMealDraftRequest(DemoMessages));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            AssertNoStore(allowed);
        }
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/meal-drafts/parse", new ParseMealDraftRequest(DemoMessages));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        AssertNoStore(response);
    }

    [Theory]
    [InlineData("/", 200)]
    [InlineData("/js/app.js", 200)]
    [InlineData("/health/live", 200)]
    [InlineData("/health/ready", 200)]
    [InlineData("/openapi/v1.json", 200)]
    [InlineData("/swagger/index.html", 200)]
    [InlineData("/apiary", 404)]
    [InlineData("/api-other", 404)]
    public async Task NonApiRoutes_DoNotReceiveApiCachePolicy(string path, int status)
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.False(response.Headers.CacheControl is { Private: true, NoStore: true });
    }

    [Fact]
    public async Task StaticFile_StillSupportsConditionalGet()
    {
        await using TestApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage original = await client.GetAsync("/js/app.js");
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        using HttpRequestMessage request = new(HttpMethod.Get, "/js/app.js");
        request.Headers.IfNoneMatch.Add(original.Headers.ETag!);
        using HttpResponseMessage unchanged = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.False(unchanged.Headers.CacheControl?.Private ?? false);
    }

    private static HttpRequestMessage CreateUpdate(string path, string revision)
    {
        HttpRequestMessage request = new(HttpMethod.Put, path);
        request.Headers.IfMatch.ParseAdd(revision);
        request.Content = JsonContent.Create(new UpdateMealEntryRequest(100m));
        return request;
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl.Private);
        Assert.True(response.Headers.CacheControl.NoStore);
        Assert.False(response.Headers.CacheControl.Public);
        Assert.Null(response.Headers.CacheControl.MaxAge);
        Assert.Single(response.Headers.GetValues("Cache-Control"));
    }

    private sealed class FailingParser : IMealParser
    {
        public Task<MealDraft> ParseAsync(CaptureSession session, CancellationToken cancellationToken = default)
        {
            return Task.FromException<MealDraft>(new InvalidOperationException("internal failure"));
        }
    }
}
