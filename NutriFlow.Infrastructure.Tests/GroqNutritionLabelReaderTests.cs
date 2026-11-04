using System.Net;
using System.Text;
using System.Text.Json;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Ai;

namespace NutriFlow.Infrastructure.Tests;

public sealed class GroqNutritionLabelReaderTests
{
    [Fact]
    public async Task ReadAsync_WithStructuredOutput_UsesVisionMessageAndMapsDraft()
    {
        string structuredOutput = """
            {
              "productName": "Йогурт",
              "basis": "per_100g",
              "calories": 75.5,
              "proteinGrams": 4.2,
              "fatGrams": 3.1,
              "carbohydratesGrams": 7.6,
              "clarificationQuestions": []
            }
            """;
        RecordingHttpMessageHandler handler = new(
            HttpStatusCode.OK,
            CreateGroqResponse(structuredOutput));
        GroqNutritionLabelReader reader = CreateReader(handler);

        NutritionLabelDraft draft = await reader.ReadAsync(
            new byte[] { 0xFF, 0xD8, 0xFF, 0x00 },
            "image/jpeg");

        Assert.Equal("Йогурт", draft.ProductName);
        Assert.Equal(NutritionBasis.Per100Grams, draft.Basis);
        Assert.Equal(75.5m, draft.Calories);
        Assert.True(draft.CanCreateProduct);
        Assert.Equal(HttpMethod.Post, handler.RequestMethod);
        Assert.Equal(
            "/openai/v1/chat/completions",
            handler.RequestUri!.AbsolutePath);

        using JsonDocument request = JsonDocument.Parse(handler.RequestBody!);
        JsonElement root = request.RootElement;
        Assert.Equal("qwen-vision-test", root.GetProperty("model").GetString());
        Assert.Equal(
            "none",
            root.GetProperty("reasoning_effort").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        string instructions = root.GetProperty("messages")[0]
            .GetProperty("content")
            .GetString()!;
        Assert.Contains("kilocalories (kcal) only", instructions);
        Assert.Contains("Never copy kilojoules (kJ)", instructions);

        JsonElement userContent = root.GetProperty("messages")[1]
            .GetProperty("content");
        Assert.Equal("text", userContent[0].GetProperty("type").GetString());
        Assert.Equal(
            "image_url",
            userContent[1].GetProperty("type").GetString());
        Assert.StartsWith(
            "data:image/jpeg;base64,",
            userContent[1]
                .GetProperty("image_url")
                .GetProperty("url")
                .GetString());
        Assert.True(root.GetProperty("response_format")
            .GetProperty("json_schema")
            .GetProperty("strict")
            .GetBoolean());
    }

    [Fact]
    public async Task ReadAsync_WhenNumbersDisagree_LooksAgainWithReasoning()
    {
        // 3,3 g of fat read as 33
        const string misread = """
            {"printedTable":"","productName":"Гречка","basis":"per_100g","calories":378,"proteinGrams":12.5,"fatGrams":33,"carbohydratesGrams":72,"clarificationQuestions":[]}
            """;
        const string correct = """
            {"printedTable":"","productName":"Гречка","basis":"per_100g","calories":378,"proteinGrams":12.5,"fatGrams":3.3,"carbohydratesGrams":72,"clarificationQuestions":[]}
            """;
        List<string?> efforts = [];
        Queue<string> answers = new([misread, correct]);
        using HttpClient httpClient = new(new DelegateHandler(async request =>
        {
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            efforts.Add(body.RootElement.GetProperty("reasoning_effort").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CreateGroqResponse(answers.Dequeue()), Encoding.UTF8, "application/json")
            };
        }))
        {
            BaseAddress = new Uri("https://api.groq.com/openai/v1/")
        };

        NutritionLabelDraft draft = await new GroqNutritionLabelReader(httpClient, "qwen-vision-test")
            .ReadAsync(new byte[] { 0xFF, 0xD8, 0xFF, 0x00 }, "image/jpeg");

        Assert.Equal(12.5m, draft.ProteinGrams);
        Assert.Equal(["none", "medium"], efforts);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    [Theory]
    [InlineData("{\"choices\":[]}")]
    [InlineData(
        "{\"choices\":[{\"message\":{\"content\":\"{}\"},\"finish_reason\":\"length\"}]}")]
    public async Task ReadAsync_WithIncompleteCompletion_ThrowsInvalidDataException(
        string responseBody)
    {
        RecordingHttpMessageHandler handler = new(
            HttpStatusCode.OK,
            responseBody);
        GroqNutritionLabelReader reader = CreateReader(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => reader.ReadAsync(new byte[] { 1 }, "image/jpeg"));
    }

    [Fact]
    public async Task ReadAsync_WhenServiceRejectsRequest_ThrowsHttpRequestException()
    {
        RecordingHttpMessageHandler handler = new(
            HttpStatusCode.TooManyRequests,
            "{}");
        GroqNutritionLabelReader reader = CreateReader(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => reader.ReadAsync(new byte[] { 1 }, "image/jpeg"));
    }

    [Fact]
    public async Task ReadAsync_WhenOutputExceedsContractLimits_ThrowsInvalidDataException()
    {
        string structuredOutput = JsonSerializer.Serialize(
            new
            {
                productName = new string('x', 201),
                basis = "per_100g",
                calories = (decimal?)75m,
                proteinGrams = (decimal?)4m,
                fatGrams = (decimal?)3m,
                carbohydratesGrams = (decimal?)8m,
                clarificationQuestions = Array.Empty<string>()
            });
        RecordingHttpMessageHandler handler = new(
            HttpStatusCode.OK,
            CreateGroqResponse(structuredOutput));
        GroqNutritionLabelReader reader = CreateReader(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => reader.ReadAsync(new byte[] { 1 }, "image/jpeg"));
    }

    private static GroqNutritionLabelReader CreateReader(
        HttpMessageHandler handler)
    {
        HttpClient httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.groq.com/openai/v1/")
        };

        return new GroqNutritionLabelReader(
            httpClient,
            "qwen-vision-test");
    }

    private static string CreateGroqResponse(string structuredOutput)
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
                        finish_reason = "stop"
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

        public HttpMethod? RequestMethod { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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
