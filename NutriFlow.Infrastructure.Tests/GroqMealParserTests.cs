using System.Net;
using System.Text;
using System.Text.Json;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Ai;

namespace NutriFlow.Infrastructure.Tests;

public sealed class GroqMealParserTests
{
    [Fact]
    public async Task ParseAsync_WithStructuredOutput_UsesChatCompletionsAndMapsDraft()
    {
        string structuredOutput = """
            {
              "dishes": [{
                "name": "Рагу",
                "ingredients": [{
                  "productName": "Говядина",
                  "weightInGrams": 600,
                  "weightQuality": "estimated",
                  "removedWeightInGrams": 0,
                  "removedWeightQuality": "exact"
                }],
                "finalWeightInGrams": 500,
                "finalWeightQuality": "exact",
                "portions": [{
                  "weightInGrams": 250,
                  "fractionOfDish": null,
                  "weightQuality": "exact"
                }]
              }],
              "clarificationQuestions": []
            }
            """;
        RecordingHttpMessageHandler handler = new(
            HttpStatusCode.OK,
            CreateGroqResponse(structuredOutput));
        GroqMealParser parser = CreateParser(handler);

        MealDraft draft = await parser.ParseAsync(
            CreateReadySession("Добавил примерно 600 г говядины."));

        DishDraft dish = Assert.Single(draft.Dishes);
        IngredientDraft ingredient = Assert.Single(dish.Ingredients);
        Assert.Equal("Рагу", dish.Name);
        Assert.Equal(600m, ingredient.WeightInGrams);
        Assert.Equal(DataQuality.Estimated, ingredient.WeightQuality);
        Assert.Equal(250m, Assert.Single(dish.Portions).WeightInGrams);
        Assert.Equal(HttpMethod.Post, handler.RequestMethod);
        Assert.Equal(
            "/openai/v1/chat/completions",
            handler.RequestUri!.AbsolutePath);

        using JsonDocument request = JsonDocument.Parse(handler.RequestBody!);
        JsonElement root = request.RootElement;
        Assert.Equal("qwen-test", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(
            "none",
            root.GetProperty("reasoning_effort").GetString());
        Assert.Equal(3000, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(
            "system",
            root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Contains(
            "one-ingredient dish",
            root.GetProperty("messages")[0].GetProperty("content").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "1. Добавил примерно 600 г говядины.",
            root.GetProperty("messages")[1].GetProperty("content").GetString(),
            StringComparison.Ordinal);

        JsonElement jsonSchema = root.GetProperty("response_format")
            .GetProperty("json_schema");
        Assert.Equal(
            "json_schema",
            root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.True(jsonSchema.GetProperty("strict").GetBoolean());
        string schemaText = jsonSchema.GetProperty("schema").GetRawText();
        Assert.DoesNotContain("calories", schemaText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("minLength", schemaText, StringComparison.Ordinal);
        Assert.DoesNotContain("minItems", schemaText, StringComparison.Ordinal);
        Assert.DoesNotContain("exclusiveMinimum", schemaText, StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("store", out _));
    }

    [Fact]
    public async Task ParseAsync_WithIncompleteCompletion_ThrowsInvalidDataException()
    {
        RecordingHttpMessageHandler handler = new(
            HttpStatusCode.OK,
            CreateGroqResponse("{}", "length"));
        GroqMealParser parser = CreateParser(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => parser.ParseAsync(CreateReadySession("Message")));
    }

    [Fact]
    public async Task ParseAsync_WhenOutputExceedsContractLimits_ThrowsInvalidDataException()
    {
        string structuredOutput = JsonSerializer.Serialize(
            new
            {
                dishes = new[]
                {
                    new
                    {
                        name = "Блюдо",
                        ingredients = new[]
                        {
                            new
                            {
                                productName = "Продукт",
                                weightInGrams = (decimal?)100m,
                                weightQuality = "exact",
                                removedWeightInGrams = (decimal?)0m,
                                removedWeightQuality = "exact"
                            }
                        },
                        finalWeightInGrams = (decimal?)100m,
                        finalWeightQuality = "exact",
                        portions = Array.Empty<object>()
                    }
                },
                clarificationQuestions = Enumerable.Repeat("Вопрос", 21)
            });
        RecordingHttpMessageHandler handler = new(
            HttpStatusCode.OK,
            CreateGroqResponse(structuredOutput));
        GroqMealParser parser = CreateParser(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => parser.ParseAsync(CreateReadySession("Message")));
    }

    [Fact]
    public async Task ParseAsync_WhenServiceRejectsRequest_ThrowsHttpRequestException()
    {
        RecordingHttpMessageHandler handler = new(
            HttpStatusCode.TooManyRequests,
            "{}");
        GroqMealParser parser = CreateParser(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => parser.ParseAsync(CreateReadySession("Message")));
    }

    [Fact]
    public async Task ParseAsync_WhileSessionIsCollecting_DoesNotSendRequest()
    {
        RecordingHttpMessageHandler handler = new(HttpStatusCode.OK, "{}");
        GroqMealParser parser = CreateParser(handler);
        CaptureSession session = new CaptureSession();
        session.AddEvent(new InputEvent("Message"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => parser.ParseAsync(session));

        Assert.Equal(0, handler.CallCount);
    }

    private static GroqMealParser CreateParser(HttpMessageHandler handler)
    {
        HttpClient httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.groq.com/openai/v1/")
        };

        return new GroqMealParser(httpClient, "qwen-test");
    }

    private static CaptureSession CreateReadySession(params string[] messages)
    {
        CaptureSession session = new CaptureSession();

        foreach (string message in messages)
        {
            session.AddEvent(new InputEvent(message));
        }

        session.FinishCollecting();
        return session;
    }

    private static string CreateGroqResponse(
        string structuredOutput,
        string finishReason = "stop")
    {
        return JsonSerializer.Serialize(
            new
            {
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            role = "assistant",
                            content = structuredOutput
                        },
                        finish_reason = finishReason
                    }
                }
            });
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public RecordingHttpMessageHandler(
            HttpStatusCode statusCode,
            string responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        public int CallCount { get; private set; }
        public HttpMethod? RequestMethod { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestMethod = request.Method;
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(
                    _responseBody,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
