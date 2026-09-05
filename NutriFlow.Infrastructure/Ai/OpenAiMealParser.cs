using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Ai;

public sealed class OpenAiMealParser : IMealParser
{
    private const string Instructions = """
        You convert captured cooking and eating messages into a structured meal draft.
        Preserve the order and meaning of all messages. A meal may contain multiple dishes.
        Extract product names, ingredient masses, final cooked weights, and eaten portions.
        Never calculate or provide calories, protein, fat, carbohydrates, or other nutrition values.
        Never invent a missing mass. Use null with quality unknown and add one compact clarification question.
        Use quality estimated when the user says approximately, about, roughly, or an equivalent phrase.
        Use quality exact for a numeric mass that the user does not qualify as approximate.
        Assume an ingredient fully entered the dish unless the user explicitly describes a removed part.
        Do not ask about nutritionally insignificant spices.
        Ask a question only when at least two reasonable interpretations materially change the result,
        the answer is not present elsewhere, and a safe assumption could seriously distort the calculation.
        Write dish names, product names, and clarification questions in the language used by the user.
        """;

    private static readonly JsonNode MealDraftSchema = JsonNode.Parse(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["dishes", "clarificationQuestions"],
          "properties": {
            "dishes": {
              "type": "array",
              "minItems": 1,
              "maxItems": 10,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name", "ingredients", "finalWeightInGrams", "finalWeightQuality", "portions"],
                "properties": {
                  "name": { "type": "string", "minLength": 1, "maxLength": 200 },
                  "ingredients": {
                    "type": "array",
                    "minItems": 1,
                    "maxItems": 50,
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["productName", "weightInGrams", "weightQuality"],
                      "properties": {
                        "productName": { "type": "string", "minLength": 1, "maxLength": 200 },
                        "weightInGrams": { "type": ["number", "null"], "exclusiveMinimum": 0 },
                        "weightQuality": { "type": "string", "enum": ["exact", "estimated", "unknown"] }
                      }
                    }
                  },
                  "finalWeightInGrams": { "type": ["number", "null"], "exclusiveMinimum": 0 },
                  "finalWeightQuality": { "type": "string", "enum": ["exact", "estimated", "unknown"] },
                  "portions": {
                    "type": "array",
                    "maxItems": 20,
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["weightInGrams", "weightQuality"],
                      "properties": {
                        "weightInGrams": { "type": "number", "exclusiveMinimum": 0 },
                        "weightQuality": { "type": "string", "enum": ["exact", "estimated"] }
                      }
                    }
                  }
                }
              }
            },
            "clarificationQuestions": {
              "type": "array",
              "maxItems": 20,
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

    public OpenAiMealParser(HttpClient httpClient, string model)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _httpClient = httpClient;
        _model = model.Trim();
    }

    public async Task<MealDraft> ParseAsync(
        CaptureSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.State != CaptureSessionState.ReadyForReview)
        {
            throw new InvalidOperationException(
                "A capture session must be ready for review before parsing.");
        }

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
                    content = new[]
                    {
                        new
                        {
                            type = "input_text",
                            text = FormatInput(session.InputEvents)
                        }
                    }
                }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "nutriflow_meal_draft",
                    strict = true,
                    schema = MealDraftSchema
                }
            },
            max_output_tokens = 3000
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
            AiMealDraft? parsed = JsonSerializer.Deserialize<AiMealDraft>(
                structuredOutput,
                SerializerOptions);

            return MapDraft(parsed);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "OpenAI returned malformed structured output.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "OpenAI returned a meal draft that violates domain rules.",
                exception);
        }
    }

    private static string FormatInput(IReadOnlyList<InputEvent> inputEvents)
    {
        StringBuilder builder = new StringBuilder();
        builder.AppendLine("Captured messages in chronological order:");

        for (int index = 0; index < inputEvents.Count; index++)
        {
            builder.Append(index + 1);
            builder.Append(". ");
            builder.AppendLine(inputEvents[index].Text);
        }

        return builder.ToString();
    }

    private static string ExtractStructuredOutput(OpenAiResponse? response)
    {
        if (response?.Output is null)
        {
            throw new InvalidDataException(
                "OpenAI response does not contain structured output.");
        }

        foreach (OpenAiOutputItem item in response.Output)
        {
            if (item.Content is null)
            {
                continue;
            }

            string? outputText = item.Content
                .FirstOrDefault(content =>
                    content.Type == "output_text" &&
                    !string.IsNullOrWhiteSpace(content.Text))
                ?.Text;

            if (outputText is not null)
            {
                return outputText;
            }
        }

        throw new InvalidDataException(
            "OpenAI response does not contain structured output text.");
    }

    private static MealDraft MapDraft(AiMealDraft? draft)
    {
        if (draft?.Dishes is null || draft.ClarificationQuestions is null)
        {
            throw new InvalidDataException(
                "OpenAI structured output is incomplete.");
        }

        List<DishDraft> dishes = draft.Dishes
            .Select(MapDish)
            .ToList();

        return new MealDraft(dishes, draft.ClarificationQuestions);
    }

    private static DishDraft MapDish(AiDish? dish)
    {
        if (dish?.Ingredients is null || dish.Portions is null)
        {
            throw new InvalidDataException(
                "OpenAI returned an incomplete dish.");
        }

        List<IngredientDraft> ingredients = dish.Ingredients
            .Select(ingredient => new IngredientDraft(
                ingredient.ProductName!,
                ingredient.WeightInGrams,
                MapWeightQuality(ingredient.WeightQuality)))
            .ToList();
        List<PortionDraft> portions = dish.Portions
            .Select(portion => new PortionDraft(
                portion.WeightInGrams,
                MapWeightQuality(portion.WeightQuality)))
            .ToList();

        return new DishDraft(
            dish.Name!,
            ingredients,
            dish.FinalWeightInGrams,
            MapWeightQuality(dish.FinalWeightQuality),
            portions);
    }

    private static DataQuality MapWeightQuality(string? quality)
    {
        return quality?.ToLowerInvariant() switch
        {
            "exact" => DataQuality.Exact,
            "estimated" => DataQuality.Estimated,
            "unknown" => DataQuality.Unknown,
            _ => throw new InvalidDataException(
                $"OpenAI returned unsupported weight quality '{quality}'.")
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

    private sealed record AiMealDraft(
        IReadOnlyList<AiDish>? Dishes,
        IReadOnlyList<string>? ClarificationQuestions);

    private sealed record AiDish(
        string? Name,
        IReadOnlyList<AiIngredient>? Ingredients,
        decimal? FinalWeightInGrams,
        string? FinalWeightQuality,
        IReadOnlyList<AiPortion>? Portions);

    private sealed record AiIngredient(
        string? ProductName,
        decimal? WeightInGrams,
        string? WeightQuality);

    private sealed record AiPortion(
        decimal WeightInGrams,
        string? WeightQuality);
}
