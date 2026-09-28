using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Ai;
using NutriFlow.Infrastructure.Audio;
using NutriFlow.Infrastructure.LabelPhotos;

namespace NutriFlow.Api.Tests;

public sealed class MealWorkflowApiTests
{
    private static readonly string[] DemoMessages =
    {
        "Добавил 200 г демо-продукта A.",
        "Потом добавил 100 г демо-продукта B.",
        "Готовое блюдо весит 250 г.",
        "Съел 125 г, потом ещё две порции по 62,5 г."
    };

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Root_ReturnsWebDashboard()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("NutriFlow", html, StringComparison.Ordinal);
        Assert.Contains("id=\"voice-record-button\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"voice-cancel-button\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"voice-status\"", html, StringComparison.Ordinal);
        Assert.Contains("src=\"/js/app.js\" type=\"module\"", html, StringComparison.Ordinal);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            "default-src 'self'",
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")),
            StringComparison.Ordinal);
        Assert.Equal(
            "camera=(), geolocation=(), microphone=(self)",
            Assert.Single(response.Headers.GetValues("Permissions-Policy")));
    }

    [Fact]
    public async Task SpeechCaptureModule_IsServedAsJavaScriptWithSameOriginPolicy()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/js/speech-capture.mjs");
        string script = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("export function createSpeechCapture", script, StringComparison.Ordinal);
        Assert.Equal(
            "camera=(), geolocation=(), microphone=(self)",
            Assert.Single(response.Headers.GetValues("Permissions-Policy")));
    }

    [Fact]
    public async Task HealthEndpoints_ReportLiveAndMigratedDatabase()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage live = await client.GetAsync("/health/live");
        HttpResponseMessage ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Capabilities_WithFakeProvider_ReportsDemoLimitations()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();

        ApplicationCapabilitiesResponse capabilities =
            await client.GetFromJsonAsync<ApplicationCapabilitiesResponse>(
                "/api/capabilities",
                JsonOptions) ?? throw new InvalidDataException();

        Assert.Equal("Fake", capabilities.AiProvider);
        Assert.False(capabilities.SupportsFreeText);
        Assert.False(capabilities.SupportsLabelPhotos);
        Assert.False(capabilities.SupportsSpeechTranscription);

        using IServiceScope scope = factory.Services.CreateScope();
        Assert.IsType<UnavailableSpeechTranscriber>(
            scope.ServiceProvider.GetRequiredService<ISpeechTranscriber>());
    }

    [Fact]
    public async Task Capabilities_WithGroqProvider_ReportsAiFeaturesAndRegistrations()
    {
        await using TestApiFactory factory = new TestApiFactory(
            aiProvider: "Groq");
        using HttpClient client = factory.CreateClient();

        ApplicationCapabilitiesResponse capabilities =
            await client.GetFromJsonAsync<ApplicationCapabilitiesResponse>(
                "/api/capabilities",
                JsonOptions) ?? throw new InvalidDataException();

        Assert.Equal("Groq", capabilities.AiProvider);
        Assert.True(capabilities.SupportsFreeText);
        Assert.True(capabilities.SupportsLabelPhotos);
        Assert.True(capabilities.SupportsSpeechTranscription);

        using IServiceScope scope = factory.Services.CreateScope();
        Assert.IsType<GroqMealParser>(
            scope.ServiceProvider.GetRequiredService<IMealParser>());
        Assert.IsType<GroqNutritionLabelReader>(
            scope.ServiceProvider.GetRequiredService<INutritionLabelReader>());
        Assert.IsType<GroqSpeechTranscriber>(
            scope.ServiceProvider.GetRequiredService<ISpeechTranscriber>());

        IHttpClientFactory httpClientFactory =
            scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        using HttpClient groqClient = httpClientFactory.CreateClient("Groq");
        Assert.Equal(
            new Uri("https://api.groq.test/openai/v1/"),
            groqClient.BaseAddress);
        Assert.Equal(
            "Bearer",
            groqClient.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal(
            "test-key",
            groqClient.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Fact]
    public async Task Capabilities_WithOpenAiProvider_ReportsSpeechUnavailable()
    {
        await using TestApiFactory factory = new TestApiFactory(
            aiProvider: "OpenAI");
        using HttpClient client = factory.CreateClient();

        ApplicationCapabilitiesResponse capabilities =
            await client.GetFromJsonAsync<ApplicationCapabilitiesResponse>(
                "/api/capabilities",
                JsonOptions) ?? throw new InvalidDataException();

        Assert.Equal("OpenAI", capabilities.AiProvider);
        Assert.True(capabilities.SupportsFreeText);
        Assert.True(capabilities.SupportsLabelPhotos);
        Assert.False(capabilities.SupportsSpeechTranscription);

        using IServiceScope scope = factory.Services.CreateScope();
        Assert.IsType<UnavailableSpeechTranscriber>(
            scope.ServiceProvider.GetRequiredService<ISpeechTranscriber>());
    }

    [Theory]
    [InlineData("audio/wav")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    public async Task TranscribeAudio_WithValidUpload_ReturnsOnlyTextAndPreservesBytes(
        string mediaType)
    {
        RecordingSpeechTranscriber transcriber = new("Съел 250 граммов говядины.");
        await using TestApiFactory factory = new TestApiFactory(
            transcriber: transcriber);
        using HttpClient client = factory.CreateClient();
        byte[] uploadedAudio = CreateWaveAudio();
        using MultipartFormDataContent form = CreateAudioForm(uploadedAudio, mediaType);

        using HttpResponseMessage response = await client.PostAsync(
            "/api/audio/transcribe",
            form);
        using JsonDocument result = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        JsonProperty property = Assert.Single(result.RootElement.EnumerateObject());
        Assert.Equal("text", property.Name);
        Assert.Equal("Съел 250 граммов говядины.", property.Value.GetString());
        Assert.Equal(1, transcriber.CallCount);
        Assert.NotNull(transcriber.Audio);
        Assert.Equal(uploadedAudio, transcriber.Audio.Content.ToArray());
        Assert.Equal("audio/wav", transcriber.Audio.MediaType);
    }

    [Theory]
    [InlineData("empty", "audio/wav")]
    [InlineData("invalid", "audio/wav")]
    [InlineData("valid", "image/png")]
    [InlineData("invalid", "")]
    public async Task TranscribeAudio_WithInvalidUpload_RejectsBeforeProviderCall(
        string contentKind,
        string mediaType)
    {
        RecordingSpeechTranscriber transcriber = new();
        await using TestApiFactory factory = new TestApiFactory(
            transcriber: transcriber);
        using HttpClient client = factory.CreateClient();
        byte[] audio = contentKind switch
        {
            "empty" => Array.Empty<byte>(),
            "invalid" => new byte[] { 1, 2, 3 },
            _ => CreateWaveAudio()
        };
        using MultipartFormDataContent form = CreateAudioForm(audio, mediaType);

        using HttpResponseMessage response = await client.PostAsync(
            "/api/audio/transcribe",
            form);
        using JsonDocument problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty(problem.RootElement.GetProperty("errors")
            .GetProperty("audio").EnumerateArray());
        Assert.Equal(0, transcriber.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranscribeAudio_WithoutAudioField_RejectsBeforeProviderCall(
        bool includeWrongField)
    {
        RecordingSpeechTranscriber transcriber = new();
        await using TestApiFactory factory = new TestApiFactory(
            transcriber: transcriber);
        using HttpClient client = factory.CreateClient();
        using MultipartFormDataContent form = includeWrongField
            ? CreateAudioForm(CreateWaveAudio(), fieldName: "file")
            : new MultipartFormDataContent();

        using HttpResponseMessage response = await client.PostAsync(
            "/api/audio/transcribe",
            form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, transcriber.CallCount);
    }

    [Fact]
    public async Task TranscribeAudio_WithOversizedUpload_RejectsBeforeProviderCall()
    {
        RecordingSpeechTranscriber transcriber = new();
        await using TestApiFactory factory = new TestApiFactory(
            transcriber: transcriber);
        using HttpClient client = factory.CreateClient();
        byte[] audio = new byte[8 * 1024 * 1024 + 1];
        CreateWaveAudio().CopyTo(audio, 0);
        using MultipartFormDataContent form = CreateAudioForm(audio);

        using HttpResponseMessage response = await client.PostAsync(
            "/api/audio/transcribe",
            form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, transcriber.CallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public async Task TranscribeAudio_WithoutRecognizedSpeech_ReturnsUnprocessableEntity(
        string text)
    {
        RecordingSpeechTranscriber transcriber = new(text);
        await using TestApiFactory factory = new TestApiFactory(
            transcriber: transcriber);
        using HttpClient client = factory.CreateClient();
        using MultipartFormDataContent form = CreateAudioForm(CreateWaveAudio());

        using HttpResponseMessage response = await client.PostAsync(
            "/api/audio/transcribe",
            form);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(1, transcriber.CallCount);
    }

    [Fact]
    public async Task TranscribeAudio_WithFakeProvider_ReturnsServiceUnavailable()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        using MultipartFormDataContent form = CreateAudioForm(CreateWaveAudio());

        using HttpResponseMessage response = await client.PostAsync(
            "/api/audio/transcribe",
            form);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("invalid", HttpStatusCode.BadGateway)]
    [InlineData("http", HttpStatusCode.BadGateway)]
    [InlineData("timeout", HttpStatusCode.GatewayTimeout)]
    public async Task TranscribeAudio_WhenProviderFails_MapsStatusWithoutLeakingDetails(
        string failure,
        HttpStatusCode expectedStatus)
    {
        const string upstreamDetail = "sensitive upstream detail";
        Exception exception = failure switch
        {
            "invalid" => new InvalidDataException(upstreamDetail),
            "http" => new HttpRequestException(upstreamDetail),
            _ => new OperationCanceledException(upstreamDetail)
        };
        RecordingSpeechTranscriber transcriber = new(exception: exception);
        await using TestApiFactory factory = new TestApiFactory(
            transcriber: transcriber);
        using HttpClient client = factory.CreateClient();
        using MultipartFormDataContent form = CreateAudioForm(CreateWaveAudio());

        using HttpResponseMessage response = await client.PostAsync(
            "/api/audio/transcribe",
            form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(upstreamDetail, body, StringComparison.Ordinal);
        Assert.Equal(1, transcriber.CallCount);
    }

    [Fact]
    public async Task OpenApi_DescribesAudioMultipartUploadAndResponses()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        JsonElement operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/audio/transcribe").GetProperty("post");
        JsonElement uploadSchema = operation.GetProperty("requestBody")
            .GetProperty("content").GetProperty("multipart/form-data")
            .GetProperty("schema");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(uploadSchema.GetProperty("properties").TryGetProperty("audio", out _));
        JsonElement responses = operation.GetProperty("responses");
        foreach (string status in new[] { "200", "400", "422", "429", "502", "503", "504" })
        {
            Assert.True(responses.TryGetProperty(status, out _));
        }
    }

    [Fact]
    public async Task HealthEndpoints_WhenSchemaIsNotMigrated_ReportNotReady()
    {
        await using TestApiFactory factory = new TestApiFactory(
            applyMigrations: false,
            seedDemoData: false);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage live = await client.GetAsync("/health/live");
        HttpResponseMessage ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Swagger_InDevelopment_IsNotBlockedByDashboardPolicy()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public async Task ExternalEndpoint_WhenRequestLimitIsExceeded_ReturnsTooManyRequests()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        List<HttpStatusCode> statuses = new();

        for (int attempt = 0; attempt < 13; attempt++)
        {
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/meal-drafts/parse",
                new ParseMealDraftRequest(DemoMessages));
            statuses.Add(response.StatusCode);
        }

        Assert.All(
            statuses.Take(12),
            status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);

        using HttpResponseMessage rejected = await client.PostAsJsonAsync(
            "/api/meal-drafts/parse",
            new ParseMealDraftRequest(DemoMessages));
        using JsonDocument problem = JsonDocument.Parse(
            await rejected.Content.ReadAsStringAsync());

        Assert.Equal(
            "application/problem+json",
            rejected.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "Too many requests",
            problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task FullWorkflow_CalculatesConfirmsAndUpdatesDailyProgress()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        DateOnly date = new DateOnly(2026, 9, 6);

        HttpResponseMessage goalResponse = await client.PutAsJsonAsync(
            $"/api/daily-goals/{date:yyyy-MM-dd}",
            new SetDailyGoalRequest(2000m, 100m, 70m, 250m));
        MealSessionResponse session = await CreateDemoSessionAsync(
            client,
            date);

        Assert.Equal(HttpStatusCode.OK, goalResponse.StatusCode);
        Assert.Equal("ReadyForConfirmation", session.Status);
        Assert.True(session.CanConfirm);
        Assert.Equal(64, session.PreviewToken.Length);
        Assert.Empty(session.Issues);
        DishPreviewResponse dish = Assert.Single(session.Dishes);
        AssertNutrition(dish.TotalNutrition, 400m, 25m, 20m, 30m);
        Assert.Equal("Unknown", dish.TotalNutritionQuality);
        AssertNutrition(dish.NutritionPer100Grams, 160m, 10m, 8m, 12m);
        Assert.Equal("Unknown", dish.NutritionPer100GramsQuality);
        Assert.Collection(
            dish.Portions,
            portion =>
            {
                AssertNutrition(
                    portion.Nutrition,
                    200m,
                    12.5m,
                    10m,
                    15m);
                Assert.Equal("Unknown", portion.NutritionQuality);
            },
            portion =>
            {
                AssertNutrition(
                    portion.Nutrition,
                    100m,
                    6.25m,
                    5m,
                    7.5m);
                Assert.Equal("Unknown", portion.NutritionQuality);
            },
            portion =>
            {
                AssertNutrition(
                    portion.Nutrition,
                    100m,
                    6.25m,
                    5m,
                    7.5m);
                Assert.Equal("Unknown", portion.NutritionQuality);
            });

        HttpResponseMessage confirmationResponse = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{session.Id}/confirm",
            new ConfirmMealSessionRequest(session.PreviewToken));
        ConfirmMealSessionResponse confirmation = await ReadAsync<
            ConfirmMealSessionResponse>(confirmationResponse);

        Assert.Equal(HttpStatusCode.OK, confirmationResponse.StatusCode);
        Assert.Equal("Confirmed", confirmation.Outcome);
        Assert.Equal("Confirmed", confirmation.Session.Status);
        Assert.False(confirmation.Session.CanConfirm);
        Assert.Equal(3, confirmation.Entries.Count);
        Assert.All(
            confirmation.Entries,
            entry => Assert.Equal("Unknown", entry.Quality));

        DailyProgressResponse progress = await client.GetFromJsonAsync<
            DailyProgressResponse>(
            $"/api/daily-progress/{date:yyyy-MM-dd}",
            JsonOptions) ?? throw new InvalidDataException();

        AssertNutrition(progress.Consumed, 400m, 25m, 20m, 30m);
        AssertNutrition(progress.Remaining, 1600m, 75m, 50m, 220m);
        AssertNutrition(progress.Exceeded, 0m, 0m, 0m, 0m);
        Assert.Equal(3, progress.Entries.Count);
        Assert.All(
            progress.Entries,
            entry => Assert.Equal("Unknown", entry.Quality));

        HttpResponseMessage repeatedResponse = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{session.Id}/confirm",
            new ConfirmMealSessionRequest(session.PreviewToken));
        ConfirmMealSessionResponse repeated = await ReadAsync<
            ConfirmMealSessionResponse>(repeatedResponse);
        DailyProgressResponse progressAfterRetry = await client.GetFromJsonAsync<
            DailyProgressResponse>(
            $"/api/daily-progress/{date:yyyy-MM-dd}",
            JsonOptions) ?? throw new InvalidDataException();

        Assert.Equal(HttpStatusCode.OK, repeatedResponse.StatusCode);
        Assert.Equal("AlreadyConfirmed", repeated.Outcome);
        Assert.Equal(3, repeated.Entries.Count);
        Assert.Equal(3, progressAfterRetry.Entries.Count);
    }

    [Fact]
    public async Task Confirm_WhenCatalogChanges_RejectsStalePreview()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        MealSessionResponse session = await CreateDemoSessionAsync(
            client,
            new DateOnly(2026, 9, 6));
        CreateManualProductRequest betterProduct = new CreateManualProductRequest(
            "Демо-продукт A",
            150m,
            12m,
            5m,
            8m,
            IsEstimated: false);

        HttpResponseMessage productResponse = await client.PostAsJsonAsync(
            "/api/products/manual",
            betterProduct);
        HttpResponseMessage confirmationResponse = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{session.Id}/confirm",
            new ConfirmMealSessionRequest(session.PreviewToken));
        ConfirmMealSessionResponse confirmation = await ReadAsync<
            ConfirmMealSessionResponse>(confirmationResponse);

        Assert.Equal(HttpStatusCode.Created, productResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, confirmationResponse.StatusCode);
        Assert.Equal("StalePreview", confirmation.Outcome);
        Assert.NotEqual(session.PreviewToken, confirmation.Session.PreviewToken);
        Assert.Equal("ReadyForConfirmation", confirmation.Session.Status);
    }

    [Fact]
    public async Task Confirm_WhenDraftNeedsClarification_ReturnsConflict()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        string[] messages =
        {
            "Добавил 500 г макарон.",
            "Готовое блюдо весит 900 г.",
            "Съел 300 г."
        };
        HttpResponseMessage createResponse = await client.PostAsJsonAsync(
            "/api/meal-sessions",
            new CreateMealSessionRequest(messages, new DateOnly(2026, 9, 6)));
        MealSessionResponse session = await ReadAsync<MealSessionResponse>(
            createResponse);

        HttpResponseMessage confirmationResponse = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{session.Id}/confirm",
            new ConfirmMealSessionRequest(session.PreviewToken));
        ConfirmMealSessionResponse confirmation = await ReadAsync<
            ConfirmMealSessionResponse>(confirmationResponse);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal("NeedsClarification", session.Status);
        Assert.False(session.CanConfirm);
        Assert.Single(session.ClarificationQuestions);
        Assert.Equal(HttpStatusCode.Conflict, confirmationResponse.StatusCode);
        Assert.Equal("NotReady", confirmation.Outcome);
        Assert.Empty(confirmation.Entries);
    }

    [Fact]
    public async Task Confirm_WhenRequestsRace_CreatesOnlyOneSetOfEntries()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        DateOnly date = new DateOnly(2026, 9, 6);
        MealSessionResponse session = await CreateDemoSessionAsync(client, date);

        Task<HttpResponseMessage>[] requests = Enumerable.Range(0, 2)
            .Select(_ => client.PostAsJsonAsync(
                $"/api/meal-sessions/{session.Id}/confirm",
                new ConfirmMealSessionRequest(session.PreviewToken)))
            .ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(requests);
        ConfirmMealSessionResponse[] confirmations = await Task.WhenAll(
            responses.Select(ReadAsync<ConfirmMealSessionResponse>));
        DailyProgressResponse progress = await client.GetFromJsonAsync<
            DailyProgressResponse>(
            $"/api/daily-progress/{date:yyyy-MM-dd}",
            JsonOptions) ?? throw new InvalidDataException();

        Assert.All(responses, response => Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode));
        Assert.Contains(
            confirmations,
            confirmation => confirmation.Outcome == "Confirmed");
        Assert.Contains(
            confirmations,
            confirmation => confirmation.Outcome == "AlreadyConfirmed");
        Assert.Equal(3, progress.Entries.Count);
        AssertNutrition(progress.Consumed, 400m, 25m, 20m, 30m);
    }

    [Fact]
    public async Task ClarificationAndCatalogUpdate_MakeSessionConfirmable()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        DateOnly date = new DateOnly(2026, 9, 6);
        string[] initialMessages =
        {
            "Добавил 500 г макарон.",
            "Готовое блюдо весит 900 г.",
            "Съел 300 г."
        };
        HttpResponseMessage createResponse = await client.PostAsJsonAsync(
            "/api/meal-sessions",
            new CreateMealSessionRequest(initialMessages, date));
        MealSessionResponse initial = await ReadAsync<MealSessionResponse>(
            createResponse);

        HttpResponseMessage clarificationResponse = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{initial.Id}/messages",
            new AddMealSessionMessageRequest("В сухом виде."));
        MealSessionResponse clarified = await ReadAsync<MealSessionResponse>(
            clarificationResponse);

        Assert.Equal(HttpStatusCode.OK, clarificationResponse.StatusCode);
        Assert.Equal("NeedsProducts", clarified.Status);
        Assert.Empty(clarified.ClarificationQuestions);
        Assert.Contains(
            clarified.Issues,
            issue => issue.Code == "product_not_found" &&
                     issue.IngredientName == "Макароны сухие");

        HttpResponseMessage productResponse = await client.PostAsJsonAsync(
            "/api/products/manual",
            new CreateManualProductRequest(
                "Макароны сухие",
                350m,
                12m,
                1.5m,
                70m,
                IsEstimated: false));
        MealSessionResponse refreshed = await client.GetFromJsonAsync<
            MealSessionResponse>(
            $"/api/meal-sessions/{initial.Id}",
            JsonOptions) ?? throw new InvalidDataException();

        Assert.Equal(HttpStatusCode.Created, productResponse.StatusCode);
        Assert.Equal("ReadyForConfirmation", refreshed.Status);
        Assert.True(refreshed.CanConfirm);
        Assert.Empty(refreshed.Issues);
        Assert.NotEqual(clarified.PreviewToken, refreshed.PreviewToken);
        AssertNutrition(
            Assert.Single(refreshed.Dishes).TotalNutrition,
            1750m,
            60m,
            7.5m,
            350m);
    }

    [Fact]
    public async Task BarcodeAlias_MakesCapturedIngredientNameResolvable()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        const string barcode = "12345678";

        HttpResponseMessage productResponse = await client.PostAsJsonAsync(
            "/api/products/manual",
            new CreateManualProductRequest(
                "Manufacturer milk",
                60m,
                3m,
                3m,
                4m,
                IsEstimated: false,
                Barcode: barcode));
        HttpResponseMessage aliasResponse = await client.PostAsJsonAsync(
            "/api/products/aliases",
            new AddProductAliasRequest(barcode, "молоко"));
        IReadOnlyList<ProductResponse>? matches =
            await client.GetFromJsonAsync<IReadOnlyList<ProductResponse>>(
                "/api/products?name=%D0%BC%D0%BE%D0%BB%D0%BE%D0%BA%D0%BE",
                JsonOptions);

        Assert.Equal(HttpStatusCode.Created, productResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, aliasResponse.StatusCode);
        Assert.Equal("Manufacturer milk", Assert.Single(matches!).Name);
    }

    [Fact]
    public async Task CreateManualProduct_WithImpossibleNutrition_ReturnsBadRequest()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/products/manual",
            new CreateManualProductRequest(
                "Invalid product",
                1001m,
                10m,
                10m,
                10m,
                IsEstimated: false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Preview_WithoutPortion_StillCalculatesDishTotalAndPer100Grams()
    {
        MealDraft draft = new MealDraft(
            [
                new DishDraft(
                    "Тестовое блюдо",
                    [new IngredientDraft("Тестовый продукт", 200m)],
                    200m,
                    DataQuality.Exact,
                    [])
            ],
            []);
        await using TestApiFactory factory = new TestApiFactory(
            seedDemoData: false,
            parser: new StaticMealParser(draft));
        using HttpClient client = factory.CreateClient();
        await AddTestProductAsync(client);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/meal-sessions",
            new CreateMealSessionRequest(
                ["Тестовое сообщение"],
                new DateOnly(2026, 9, 7)));
        MealSessionResponse session = await ReadAsync<MealSessionResponse>(response);
        DishPreviewResponse dish = Assert.Single(session.Dishes);

        Assert.Equal("NeedsClarification", session.Status);
        Assert.Contains(session.Issues, issue => issue.Code == "portion_missing");
        AssertNutrition(dish.TotalNutrition, 200m, 20m, 8m, 12m);
        AssertNutrition(
            dish.NutritionPer100Grams,
            100m,
            10m,
            4m,
            6m);
    }

    [Fact]
    public async Task Preview_WithoutFinalWeight_StillCalculatesIngredientTotal()
    {
        MealDraft draft = new MealDraft(
            [
                new DishDraft(
                    "Тестовое блюдо",
                    [new IngredientDraft("Тестовый продукт", 200m)],
                    null,
                    DataQuality.Unknown,
                    [new PortionDraft(50m)])
            ],
            []);
        await using TestApiFactory factory = new TestApiFactory(
            seedDemoData: false,
            parser: new StaticMealParser(draft));
        using HttpClient client = factory.CreateClient();
        await AddTestProductAsync(client);

        MealSessionResponse session = await ReadAsync<MealSessionResponse>(
            await client.PostAsJsonAsync(
                "/api/meal-sessions",
                new CreateMealSessionRequest(
                    ["Тестовое сообщение"],
                    new DateOnly(2026, 9, 7))));
        DishPreviewResponse dish = Assert.Single(session.Dishes);

        Assert.Equal("NeedsClarification", session.Status);
        Assert.Contains(session.Issues, issue => issue.Code == "final_weight_missing");
        AssertNutrition(dish.TotalNutrition, 200m, 20m, 8m, 12m);
        Assert.Null(dish.NutritionPer100Grams);
        Assert.Null(Assert.Single(dish.Portions).Nutrition);
    }

    [Fact]
    public async Task Preview_WithRemovedIngredientWeight_UsesOnlyIncludedMass()
    {
        MealDraft draft = new MealDraft(
            [
                new DishDraft(
                    "Тестовое блюдо",
                    [
                        new IngredientDraft(
                            "Тестовый продукт",
                            200m,
                            DataQuality.Exact,
                            50m,
                            DataQuality.Exact)
                    ],
                    150m,
                    DataQuality.Exact,
                    [new PortionDraft(150m)])
            ],
            []);
        await using TestApiFactory factory = new TestApiFactory(
            seedDemoData: false,
            parser: new StaticMealParser(draft));
        using HttpClient client = factory.CreateClient();
        await AddTestProductAsync(client);

        MealSessionResponse session = await ReadAsync<MealSessionResponse>(
            await client.PostAsJsonAsync(
                "/api/meal-sessions",
                new CreateMealSessionRequest(
                    ["Добавил 200 г, затем удалил 50 г."],
                    new DateOnly(2026, 9, 7))));
        DishPreviewResponse dish = Assert.Single(session.Dishes);
        IngredientPreviewResponse ingredient = Assert.Single(dish.Ingredients);

        Assert.Equal("ReadyForConfirmation", session.Status);
        Assert.Equal(50m, ingredient.RemovedWeightInGrams);
        Assert.Equal(150m, ingredient.IncludedWeightInGrams);
        AssertNutrition(dish.TotalNutrition, 150m, 15m, 6m, 9m);
    }

    [Fact]
    public async Task Preview_WithFractionalPortion_CalculatesItsWeightAndNutrition()
    {
        MealDraft draft = new MealDraft(
            [
                new DishDraft(
                    "Тестовое блюдо",
                    [new IngredientDraft("Тестовый продукт", 200m)],
                    200m,
                    DataQuality.Exact,
                    [PortionDraft.FromFraction(0.25m)])
            ],
            []);
        await using TestApiFactory factory = new TestApiFactory(
            seedDemoData: false,
            parser: new StaticMealParser(draft));
        using HttpClient client = factory.CreateClient();
        await AddTestProductAsync(client);

        MealSessionResponse created = await ReadAsync<MealSessionResponse>(
            await client.PostAsJsonAsync(
                "/api/meal-sessions",
                new CreateMealSessionRequest(
                    ["Съел четверть блюда."],
                    new DateOnly(2026, 9, 7))));
        MealSessionResponse session = await client.GetFromJsonAsync<MealSessionResponse>(
            $"/api/meal-sessions/{created.Id}",
            JsonOptions) ?? throw new InvalidDataException();
        PortionPreviewResponse portion = Assert.Single(
            Assert.Single(session.Dishes).Portions);

        Assert.Equal("ReadyForConfirmation", session.Status);
        Assert.Equal(50m, portion.WeightInGrams);
        Assert.Equal(0.25m, portion.FractionOfDish);
        AssertNutrition(portion.Nutrition, 50m, 5m, 2m, 3m);
    }

    [Fact]
    public async Task CreateProductFromLabel_PreservesPhotoProvenance()
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        string photoReference;

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            LabelPhotoStore store =
                scope.ServiceProvider.GetRequiredService<LabelPhotoStore>();
            ValidatedLabelPhoto photo = LabelPhotoValidator.Validate(
                new byte[]
                {
                    0x89, 0x50, 0x4E, 0x47,
                    0x0D, 0x0A, 0x1A, 0x0A
                },
                "image/png");
            photoReference = await store.SaveAsync(photo);
        }

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/products/from-label",
            new CreateLabelProductRequest(
                photoReference,
                NutritionBasis.Per100Grams,
                "Творог с этикетки",
                121m,
                17m,
                5m,
                3m));
        ProductResponse product = await ReadAsync<ProductResponse>(response);
        string fileName = photoReference["label-photo:".Length..];
        HttpResponseMessage photoResponse = await client.GetAsync(
            $"/api/label-photos/{Uri.EscapeDataString(fileName)}");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("LabelPhoto", product.SourceKind);
        Assert.Equal("Verified", product.DataQuality);
        Assert.Equal(photoReference, product.SourceReference);
        Assert.Equal(HttpStatusCode.OK, photoResponse.StatusCode);
        Assert.Equal(
            "image/png",
            photoResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            new byte[]
            {
                0x89, 0x50, 0x4E, 0x47,
                0x0D, 0x0A, 0x1A, 0x0A
            },
            await photoResponse.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(NutritionBasis.Unknown)]
    [InlineData(NutritionBasis.Per100Milliliters)]
    [InlineData(NutritionBasis.PerServing)]
    public async Task CreateProductFromLabel_RejectsNonGramBasis(
        NutritionBasis basis)
    {
        await using TestApiFactory factory = new TestApiFactory();
        using HttpClient client = factory.CreateClient();
        string photoReference;

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            LabelPhotoStore store =
                scope.ServiceProvider.GetRequiredService<LabelPhotoStore>();
            ValidatedLabelPhoto photo = LabelPhotoValidator.Validate(
                new byte[]
                {
                    0x89, 0x50, 0x4E, 0x47,
                    0x0D, 0x0A, 0x1A, 0x0A
                },
                "image/png");
            photoReference = await store.SaveAsync(photo);
        }

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/products/from-label",
            new CreateLabelProductRequest(
                photoReference,
                basis,
                "Неверная база",
                121m,
                17m,
                5m,
                3m));
        using JsonDocument problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "Basis must be Per100Grams before label values can be saved.",
            problem.RootElement.GetProperty("errors")
                .GetProperty("product")[0]
                .GetString());
    }

    private static byte[] CreateWaveAudio()
    {
        return new byte[]
        {
            0x52, 0x49, 0x46, 0x46, 0x26, 0x00, 0x00, 0x00,
            0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74, 0x20,
            0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
            0x80, 0x3E, 0x00, 0x00, 0x00, 0x7D, 0x00, 0x00,
            0x02, 0x00, 0x10, 0x00, 0x64, 0x61, 0x74, 0x61,
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00
        };
    }

    private static MultipartFormDataContent CreateAudioForm(
        byte[] audio,
        string mediaType = "audio/wav",
        string fieldName = "audio")
    {
        MultipartFormDataContent form = new();
        ByteArrayContent file = new(audio);

        if (!string.IsNullOrEmpty(mediaType))
        {
            file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        }

        form.Add(file, fieldName, "capture.wav");
        return form;
    }

    private static async Task<MealSessionResponse> CreateDemoSessionAsync(
        HttpClient client,
        DateOnly date)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/meal-sessions",
            new CreateMealSessionRequest(DemoMessages, date));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<MealSessionResponse>(response);
    }

    private static async Task AddTestProductAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/products/manual",
            new CreateManualProductRequest(
                "Тестовый продукт",
                100m,
                10m,
                4m,
                6m,
                IsEstimated: false));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();

        return JsonSerializer.Deserialize<T>(json, JsonOptions) ??
               throw new InvalidDataException(
                   $"The response could not be read as {typeof(T).Name}: {json}");
    }

    private static void AssertNutrition(
        NutritionResponse? actual,
        decimal calories,
        decimal protein,
        decimal fat,
        decimal carbohydrates)
    {
        Assert.NotNull(actual);
        Assert.Equal(calories, actual.Calories);
        Assert.Equal(protein, actual.ProteinGrams);
        Assert.Equal(fat, actual.FatGrams);
        Assert.Equal(carbohydrates, actual.CarbohydratesGrams);
    }

    private sealed class RecordingSpeechTranscriber : ISpeechTranscriber
    {
        private readonly string _text;
        private readonly Exception? _exception;

        public RecordingSpeechTranscriber(
            string text = "Распознанный текст.",
            Exception? exception = null)
        {
            _text = text;
            _exception = exception;
        }

        public int CallCount { get; private set; }

        public ValidatedAudio? Audio { get; private set; }

        public Task<string> TranscribeAsync(
            ValidatedAudio audio,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            Audio = audio;

            return _exception is null
                ? Task.FromResult(_text)
                : Task.FromException<string>(_exception);
        }
    }

    private sealed class StaticMealParser : IMealParser
    {
        private readonly MealDraft _draft;

        public StaticMealParser(MealDraft draft)
        {
            _draft = draft;
        }

        public Task<MealDraft> ParseAsync(
            CaptureSession session,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_draft);
        }
    }
}
