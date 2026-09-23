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
