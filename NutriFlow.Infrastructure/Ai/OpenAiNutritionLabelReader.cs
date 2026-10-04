using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Ai;

public sealed class OpenAiNutritionLabelReader : INutritionLabelReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _model;

    public OpenAiNutritionLabelReader(HttpClient httpClient, string model)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _httpClient = httpClient;
        _model = model.Trim();
    }

    public async Task<NutritionLabelDraft> ReadAsync(
        ReadOnlyMemory<byte> image,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        if (image.IsEmpty)
        {
            throw new ArgumentException("A label image is required.", nameof(image));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        string imageDataUrl =
            $"data:{mediaType};base64,{Convert.ToBase64String(image.Span)}";
        object requestBody = new
        {
            model = _model,
            store = false,
            instructions = AiNutritionLabelContract.Instructions,
            input = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_text",
                            text = AiNutritionLabelContract.InputPrompt
                        },
                        new
                        {
                            type = "input_image",
                            image_url = imageDataUrl,
                            detail = "high"
                        }
                    }
                }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = AiNutritionLabelContract.SchemaName,
                    strict = true,
                    schema = AiNutritionLabelContract.Schema
                }
            },
            max_output_tokens = 1500
        };

        using HttpRequestMessage request = new(HttpMethod.Post, "responses")
        {
            Content = JsonContent.Create(requestBody)
        };
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        OpenAiResponse? responseBody;

        try
        {
            responseBody = await response.Content.ReadFromJsonAsync<OpenAiResponse>(
                SerializerOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "OpenAI returned malformed response JSON.",
                exception);
        }

        return AiNutritionLabelContract.Deserialize(
            ExtractStructuredOutput(responseBody),
            "OpenAI");
    }

    private static string ExtractStructuredOutput(OpenAiResponse? response)
    {
        if (response?.Output is null)
        {
            throw new InvalidDataException(
                "OpenAI response does not contain structured label output.");
        }

        if (response.Output.Any(item =>
                item is null ||
                item.Content?.Any(content => content is null) == true))
        {
            throw new InvalidDataException(
                "OpenAI response contains null output or content items.");
        }

        string? outputText = response.Output
            .SelectMany(item =>
                item!.Content ?? Array.Empty<OpenAiContent?>())
            .FirstOrDefault(content =>
                content!.Type == "output_text" &&
                !string.IsNullOrWhiteSpace(content.Text))
            ?.Text;

        return outputText ?? throw new InvalidDataException(
            "OpenAI response does not contain structured label output.");
    }

    private sealed record OpenAiResponse(
        [property: JsonPropertyName("output")]
        IReadOnlyList<OpenAiOutputItem?>? Output);

    private sealed record OpenAiOutputItem(
        [property: JsonPropertyName("content")]
        IReadOnlyList<OpenAiContent?>? Content);

    private sealed record OpenAiContent(
        [property: JsonPropertyName("type")]
        string? Type,
        [property: JsonPropertyName("text")]
        string? Text);
}
