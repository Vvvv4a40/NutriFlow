using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Ai;

public sealed class OpenAiNutritionLabelReader : INutritionLabelReader
{
    private const string Instructions = """
        Extract nutrition facts exactly as printed on the product label image.
        Prefer a table expressed per 100 grams when present. Do not convert between a serving,
        100 milliliters, and 100 grams. Do not infer or complete values that are not readable.
        Use null for every missing or unreadable value and add compact clarification questions.
        Do not provide confidence percentages. ProductName may be null when it is not visible.
        """;

    private static readonly JsonNode LabelSchema = JsonNode.Parse(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": [
            "productName",
            "basis",
            "calories",
            "proteinGrams",
            "fatGrams",
            "carbohydratesGrams",
            "clarificationQuestions"
          ],
          "properties": {
            "productName": { "type": ["string", "null"], "maxLength": 200 },
            "basis": {
              "type": "string",
              "enum": ["per_100g", "per_100ml", "per_serving", "unknown"]
            },
            "calories": { "type": ["number", "null"], "minimum": 0 },
            "proteinGrams": { "type": ["number", "null"], "minimum": 0 },
            "fatGrams": { "type": ["number", "null"], "minimum": 0 },
            "carbohydratesGrams": { "type": ["number", "null"], "minimum": 0 },
            "clarificationQuestions": {
              "type": "array",
              "maxItems": 10,
              "items": { "type": "string", "minLength": 1, "maxLength": 500 }
            }
          }
        }
        """)!;

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
            instructions = Instructions,
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
                            text = "Read this nutrition label and return only the requested structured data."
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
                    name = "nutriflow_nutrition_label",
                    strict = true,
                    schema = LabelSchema
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
            HttpCompletionOption.ResponseHeadersRead,
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

        string structuredOutput = ExtractStructuredOutput(responseBody);

        try
        {
            AiLabelDraft? parsed = JsonSerializer.Deserialize<AiLabelDraft>(
                structuredOutput,
                SerializerOptions);

            if (parsed?.ClarificationQuestions is null)
            {
                throw new InvalidDataException(
                    "OpenAI structured label output is incomplete.");
            }

            return new NutritionLabelDraft(
                parsed.ProductName,
                MapBasis(parsed.Basis),
                parsed.Calories,
                parsed.ProteinGrams,
                parsed.FatGrams,
                parsed.CarbohydratesGrams,
                parsed.ClarificationQuestions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "OpenAI returned malformed structured label output.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "OpenAI returned label data that violates domain rules.",
                exception);
        }
    }

    private static string ExtractStructuredOutput(OpenAiResponse? response)
    {
        string? outputText = response?.Output?
            .SelectMany(item => item.Content ?? Array.Empty<OpenAiContent>())
            .FirstOrDefault(content =>
                content.Type == "output_text" &&
                !string.IsNullOrWhiteSpace(content.Text))
            ?.Text;

        return outputText ?? throw new InvalidDataException(
            "OpenAI response does not contain structured label output.");
    }

    private static NutritionBasis MapBasis(string? basis)
    {
        return basis?.ToLowerInvariant() switch
        {
            "per_100g" => NutritionBasis.Per100Grams,
            "per_100ml" => NutritionBasis.Per100Milliliters,
            "per_serving" => NutritionBasis.PerServing,
            "unknown" => NutritionBasis.Unknown,
            _ => throw new InvalidDataException(
                $"OpenAI returned unsupported nutrition basis '{basis}'.")
        };
    }

    private sealed record OpenAiResponse(
        [property: JsonPropertyName("output")]
        IReadOnlyList<OpenAiOutputItem>? Output);

    private sealed record OpenAiOutputItem(
        [property: JsonPropertyName("content")]
        IReadOnlyList<OpenAiContent>? Content);

    private sealed record OpenAiContent(
        [property: JsonPropertyName("type")]
        string? Type,
        [property: JsonPropertyName("text")]
        string? Text);

    private sealed record AiLabelDraft(
        string? ProductName,
        string? Basis,
        decimal? Calories,
        decimal? ProteinGrams,
        decimal? FatGrams,
        decimal? CarbohydratesGrams,
        IReadOnlyList<string>? ClarificationQuestions);
}
