using System.Net;
using System.Text;
using System.Text.Json;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Ai;

namespace NutriFlow.Infrastructure.Tests;

public sealed class OpenAiNutritionLabelReaderTests
{
    [Fact]
    public async Task ReadAsync_WithStructuredOutput_MapsLabelDraft()
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
        RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
            HttpStatusCode.OK,
            CreateOpenAiResponse(structuredOutput));
        OpenAiNutritionLabelReader reader = CreateReader(handler);
        byte[] image = { 0xFF, 0xD8, 0xFF, 0x00 };

        NutritionLabelDraft draft = await reader.ReadAsync(
            image,
            "image/jpeg");

        Assert.Equal("Йогурт", draft.ProductName);
        Assert.Equal(NutritionBasis.Per100Grams, draft.Basis);
        Assert.Equal(75.5m, draft.Calories);
        Assert.Equal(4.2m, draft.ProteinGrams);
        Assert.Equal(3.1m, draft.FatGrams);
        Assert.Equal(7.6m, draft.CarbohydratesGrams);
        Assert.True(draft.CanCreateProduct);

        using JsonDocument request = JsonDocument.Parse(handler.RequestBody!);
        JsonElement root = request.RootElement;
        Assert.False(root.GetProperty("store").GetBoolean());
        string instructions = root.GetProperty("instructions").GetString()!;
        Assert.Contains("kilocalories (kcal) only", instructions);
        Assert.Contains("Never copy kilojoules (kJ)", instructions);
        Assert.Contains("If only kJ is printed", instructions);
        JsonElement imageInput = root.GetProperty("input")[0]
            .GetProperty("content")[1];
        Assert.Equal("input_image", imageInput.GetProperty("type").GetString());
        Assert.StartsWith(
            "data:image/jpeg;base64,",
            imageInput.GetProperty("image_url").GetString());
        Assert.Equal(
            "json_schema",
            root.GetProperty("text")
                .GetProperty("format")
                .GetProperty("type")
                .GetString());
    }

    [Fact]
    public async Task ReadAsync_WithPerServingBasis_DoesNotMarkDraftAsReady()
    {
        string structuredOutput = """
            {
              "productName": "Батончик",
              "basis": "per_serving",
              "calories": 180,
              "proteinGrams": 5,
              "fatGrams": 8,
              "carbohydratesGrams": 20,
              "clarificationQuestions": ["Какова масса одной порции?"]
            }
            """;
        RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
            HttpStatusCode.OK,
            CreateOpenAiResponse(structuredOutput));
        OpenAiNutritionLabelReader reader = CreateReader(handler);

        NutritionLabelDraft draft = await reader.ReadAsync(
            new byte[] { 1 },
            "image/jpeg");

        Assert.Equal(NutritionBasis.PerServing, draft.Basis);
        Assert.False(draft.CanCreateProduct);
        Assert.True(draft.RequiresClarification);
    }

    [Fact]
    public async Task ReadAsync_WithImplausibleOutput_ThrowsInvalidDataException()
    {
        string structuredOutput = """
            {
              "productName": "Invalid",
              "basis": "per_100g",
              "calories": 1500,
              "proteinGrams": 5,
              "fatGrams": 8,
              "carbohydratesGrams": 20,
              "clarificationQuestions": []
            }
            """;
        RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
            HttpStatusCode.OK,
            CreateOpenAiResponse(structuredOutput));
        OpenAiNutritionLabelReader reader = CreateReader(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => reader.ReadAsync(new byte[] { 1 }, "image/jpeg"));
    }

    [Theory]
    [InlineData(
        """
        {
          "output": [
            null,
            { "content": [{ "type": "output_text", "text": "{}" }] }
          ]
        }
        """)]
    [InlineData(
        """
        {
          "output": [{
            "content": [
              null,
              { "type": "output_text", "text": "{}" }
            ]
          }]
        }
        """)]
    public async Task ReadAsync_WithNullOutputArrayItem_ThrowsInvalidDataException(
        string responseBody)
    {
        RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
            HttpStatusCode.OK,
            responseBody);
        OpenAiNutritionLabelReader reader = CreateReader(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => reader.ReadAsync(new byte[] { 1 }, "image/jpeg"));
    }

    [Fact]
    public async Task ReadAsync_WhenServiceFails_ThrowsHttpRequestException()
    {
        RecordingHttpMessageHandler handler = new RecordingHttpMessageHandler(
            HttpStatusCode.TooManyRequests,
            "{}");
        OpenAiNutritionLabelReader reader = CreateReader(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => reader.ReadAsync(new byte[] { 1 }, "image/jpeg"));
    }

    private static OpenAiNutritionLabelReader CreateReader(
        HttpMessageHandler handler)
    {
        HttpClient httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };

        return new OpenAiNutritionLabelReader(httpClient, "gpt-test");
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

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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
