using System.Net;
using System.Text;
using System.Text.Json;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Ai;

namespace NutriFlow.Infrastructure.Tests;

public sealed class OpenAiMealParserTests
{
    [Fact]
    public async Task ParseAsync_WithStructuredOutput_MapsDraftAndQualities()
    {
        string structuredOutput = """
            {
              "dishes": [
                {
                  "name": "Рагу",
                  "ingredients": [
                    {
                      "productName": "Говядина",
                      "weightInGrams": 600,
                      "weightQuality": "estimated",
                      "removedWeightInGrams": 50,
                      "removedWeightQuality": "exact"
                    },
                    {
                      "productName": "Овощная смесь",
                      "weightInGrams": null,
                      "weightQuality": "unknown",
                      "removedWeightInGrams": 0,
                      "removedWeightQuality": "exact"
                    }
                  ],
                  "finalWeightInGrams": 1180,
                  "finalWeightQuality": "exact",
                  "portions": [
                    {
                      "weightInGrams": 350,
                      "fractionOfDish": null,
                      "weightQuality": "exact"
                    }
                  ]
                }
              ],
              "clarificationQuestions": [
                "Какова масса овощной смеси?"
              ]
            }
            """;
        RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
            HttpStatusCode.OK,
            CreateOpenAiResponse(structuredOutput));
        OpenAiMealParser parser = CreateParser(handler);
        CaptureSession session = CreateReadySession(
            "Закинул примерно 600 г говядины.",
            "Добавил пачку овощной смеси.",
            "Готовое рагу весит 1180 г.",
            "Съел 350 г.");

        MealDraft draft = await parser.ParseAsync(session);

        DishDraft dish = Assert.Single(draft.Dishes);
        Assert.Equal("Рагу", dish.Name);
        Assert.Equal(600m, dish.Ingredients[0].WeightInGrams);
        Assert.Equal(DataQuality.Estimated, dish.Ingredients[0].WeightQuality);
        Assert.Equal(50m, dish.Ingredients[0].RemovedWeightInGrams);
        Assert.Equal(550m, dish.Ingredients[0].IncludedWeightInGrams);
        Assert.Null(dish.Ingredients[1].WeightInGrams);
        Assert.Equal(DataQuality.Unknown, dish.Ingredients[1].WeightQuality);
        Assert.Equal(1180m, dish.FinalWeightInGrams);
        Assert.Equal(DataQuality.Exact, dish.FinalWeightQuality);
        Assert.Equal(DataQuality.Exact, dish.Portions[0].WeightQuality);
        Assert.True(draft.RequiresClarification);
        Assert.Equal(1, handler.CallCount);

        using JsonDocument request = JsonDocument.Parse(handler.RequestBody!);
        JsonElement root = request.RootElement;
        Assert.Equal("gpt-test", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal(
            "json_schema",
            root.GetProperty("text")
                .GetProperty("format")
                .GetProperty("type")
                .GetString());
        Assert.Contains(
            "one-ingredient dish",
            root.GetProperty("instructions").GetString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "calories",
            root.GetProperty("text")
                .GetProperty("format")
                .GetProperty("schema")
                .GetRawText(),
            StringComparison.OrdinalIgnoreCase);
        string capturedInput = root.GetProperty("input")[0]
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()!;
        Assert.Contains(
            "1. Закинул примерно 600 г говядины.",
            capturedInput);
    }

    [Fact]
    public async Task ParseAsync_WithInvalidStructuredOutput_ThrowsInvalidDataException()
    {
        RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
            HttpStatusCode.OK,
            CreateOpenAiResponse("{ not json }"));
        OpenAiMealParser parser = CreateParser(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => parser.ParseAsync(CreateReadySession("Message")));
    }

    [Fact]
    public async Task ParseAsync_WithFractionalPortion_PreservesFractionForDomainCalculation()
    {
        string structuredOutput = """
            {
              "dishes": [{
                "name": "Рагу",
                "ingredients": [{
                  "productName": "Овощи",
                  "weightInGrams": 600,
                  "weightQuality": "exact",
                  "removedWeightInGrams": 0,
                  "removedWeightQuality": "exact"
                }],
                "finalWeightInGrams": 500,
                "finalWeightQuality": "exact",
                "portions": [{
                  "weightInGrams": null,
                  "fractionOfDish": 0.5,
                  "weightQuality": "exact"
                }]
              }],
              "clarificationQuestions": []
            }
            """;
        RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
            HttpStatusCode.OK,
            CreateOpenAiResponse(structuredOutput));
        OpenAiMealParser parser = CreateParser(handler);

        MealDraft draft = await parser.ParseAsync(
            CreateReadySession("Съел половину рагу."));
        PortionDraft portion = Assert.Single(Assert.Single(draft.Dishes).Portions);

        Assert.Null(portion.WeightInGrams);
        Assert.Equal(0.5m, portion.FractionOfDish);
        Assert.Equal(250m, portion.ResolveWeightInGrams(500m));
    }

    [Theory]
    [MemberData(nameof(StructuredOutputsWithNullArrayItems))]
    public async Task ParseAsync_WithNullArrayItem_ThrowsInvalidDataException(
        string structuredOutput)
    {
        RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
            HttpStatusCode.OK,
            CreateOpenAiResponse(structuredOutput));
        OpenAiMealParser parser = CreateParser(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => parser.ParseAsync(CreateReadySession("Message")));
    }

    [Fact]
    public async Task ParseAsync_WithoutOutputText_ThrowsInvalidDataException()
    {
        RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
            HttpStatusCode.OK,
            "{\"output\":[]}");
        OpenAiMealParser parser = CreateParser(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => parser.ParseAsync(CreateReadySession("Message")));
    }

    [Fact]
    public async Task ParseAsync_WhenServiceRejectsRequest_ThrowsHttpRequestException()
    {
        RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
            HttpStatusCode.TooManyRequests,
            "{}");
        OpenAiMealParser parser = CreateParser(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => parser.ParseAsync(CreateReadySession("Message")));
    }

    [Fact]
    public async Task ParseAsync_WhileSessionIsCollecting_DoesNotSendRequest()
    {
        RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
            HttpStatusCode.OK,
            "{}");
        OpenAiMealParser parser = CreateParser(handler);
        CaptureSession session = new CaptureSession();
        session.AddEvent(new InputEvent("Message"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => parser.ParseAsync(session));

        Assert.Equal(0, handler.CallCount);
    }

    private static OpenAiMealParser CreateParser(HttpMessageHandler handler)
    {
        HttpClient httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };

        return new OpenAiMealParser(httpClient, "gpt-test");
    }

    public static TheoryData<string> StructuredOutputsWithNullArrayItems =>
        new()
        {
            """
            { "dishes": [null], "clarificationQuestions": [] }
            """,
            """
            {
              "dishes": [{
                "name": "Блюдо",
                "ingredients": [null],
                "finalWeightInGrams": null,
                "finalWeightQuality": "unknown",
                "portions": []
              }],
              "clarificationQuestions": []
            }
            """,
            """
            {
              "dishes": [{
                "name": "Блюдо",
                "ingredients": [{
                  "productName": "Продукт",
                  "weightInGrams": 100,
                  "weightQuality": "exact"
                }],
                "finalWeightInGrams": 100,
                "finalWeightQuality": "exact",
                "portions": [null]
              }],
              "clarificationQuestions": []
            }
            """,
            """
            {
              "dishes": [{
                "name": "Блюдо",
                "ingredients": [{
                  "productName": "Продукт",
                  "weightInGrams": 100,
                  "weightQuality": "exact"
                }],
                "finalWeightInGrams": 100,
                "finalWeightQuality": "exact",
                "portions": []
              }],
              "clarificationQuestions": [null]
            }
            """
        };

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

    private static string CreateOpenAiResponse(string structuredOutput)
    {
        return JsonSerializer.Serialize(
            new
            {
                output = new[]
                {
                    new
                    {
                        type = "message",
                        content = new[]
                        {
                            new
                            {
                                type = "output_text",
                                text = structuredOutput
                            }
                        }
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
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
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
