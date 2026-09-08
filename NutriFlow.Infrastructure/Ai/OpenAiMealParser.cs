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
        For every ingredient, keep the original added weight and the explicitly removed weight separate.
        Use removedWeightInGrams 0 with quality exact when no removal is mentioned. Never subtract it yourself.
        If removal is mentioned but its weight is unknown, use null with quality unknown and ask one question.
        A portion must contain either its weight in grams or its fraction of the final dish, never both.
        For a product eaten directly, create a one-ingredient dish and repeat the eaten mass as the
        ingredient weight, final weight, and portion weight; copying that mass is not nutrition arithmetic.
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
                      "required": ["productName", "weightInGrams", "weightQuality", "removedWeightInGrams", "removedWeightQuality"],
                      "properties": {
                        "productName": { "type": "string", "minLength": 1, "maxLength": 200 },
                        "weightInGrams": { "type": ["number", "null"], "exclusiveMinimum": 0 },
                        "weightQuality": { "type": "string", "enum": ["exact", "estimated", "unknown"] },
                        "removedWeightInGrams": { "type": ["number", "null"], "minimum": 0 },
                        "removedWeightQuality": { "type": "string", "enum": ["exact", "estimated", "unknown"] }
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
                      "required": ["weightInGrams", "fractionOfDish", "weightQuality"],
                      "properties": {
                        "weightInGrams": { "type": ["number", "null"], "exclusiveMinimum": 0 },
                        "fractionOfDish": { "type": ["number", "null"], "exclusiveMinimum": 0, "maximum": 1 },
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

        foreach (OpenAiOutputItem? item in response.Output)
        {
            if (item?.Content is null)
            {
                continue;
            }

            string? outputText = item.Content
                .FirstOrDefault(content =>
                    content?.Type == "output_text" &&
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

        if (draft.Dishes.Any(static dish => dish is null) ||
            draft.ClarificationQuestions.Any(
                static question => string.IsNullOrWhiteSpace(question)))
        {
            throw new InvalidDataException(
                "OpenAI structured output contains invalid array items.");
        }

        List<DishDraft> dishes = draft.Dishes
            .Select(dish => MapDish(dish!))
            .ToList();
        string[] clarificationQuestions = draft.ClarificationQuestions
            .Select(question => question!)
            .ToArray();

        return new MealDraft(dishes, clarificationQuestions);
    }

    private static DishDraft MapDish(AiDish? dish)
    {
        if (dish?.Ingredients is null || dish.Portions is null)
        {
            throw new InvalidDataException(
                "OpenAI returned an incomplete dish.");
        }

        if (dish.Ingredients.Any(static ingredient => ingredient is null) ||
            dish.Portions.Any(static portion => portion is null))
        {
            throw new InvalidDataException(
                "OpenAI returned a dish with invalid array items.");
        }

        List<IngredientDraft> ingredients = dish.Ingredients
            .Select(ingredient =>
            {
                AiIngredient value = ingredient!;
                bool omittedLegacyRemoval = value.RemovedWeightInGrams is null &&
                                            value.RemovedWeightQuality is null;

                return new IngredientDraft(
                    value.ProductName!,
                    value.WeightInGrams,
                    MapWeightQuality(value.WeightQuality),
                    omittedLegacyRemoval ? 0m : value.RemovedWeightInGrams,
                    omittedLegacyRemoval
                        ? DataQuality.Exact
                        : MapWeightQuality(value.RemovedWeightQuality));
            })
            .ToList();
        List<PortionDraft> portions = dish.Portions
            .Select(MapPortion)
            .ToList();

        return new DishDraft(
            dish.Name!,
            ingredients,
            dish.FinalWeightInGrams,
            MapWeightQuality(dish.FinalWeightQuality),
            portions);
    }

    private static PortionDraft MapPortion(AiPortion? portion)
    {
        if (portion is null ||
            (portion.WeightInGrams is null) ==
            (portion.FractionOfDish is null))
        {
            throw new InvalidDataException(
                "OpenAI returned a portion that must have exactly one weight representation.");
        }

        DataQuality quality = MapWeightQuality(portion.WeightQuality);

        return portion.WeightInGrams is not null
            ? new PortionDraft(portion.WeightInGrams.Value, quality)
            : PortionDraft.FromFraction(portion.FractionOfDish!.Value, quality);
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
        IReadOnlyList<OpenAiOutputItem?>? Output);

    private sealed record OpenAiOutputItem(
        [property: JsonPropertyName("content")]
        IReadOnlyList<OpenAiContent?>? Content);

    private sealed record OpenAiContent(
        [property: JsonPropertyName("type")]
        string? Type,
        [property: JsonPropertyName("text")]
        string? Text);

    private sealed record AiMealDraft(
        IReadOnlyList<AiDish?>? Dishes,
        IReadOnlyList<string?>? ClarificationQuestions);

    private sealed record AiDish(
        string? Name,
        IReadOnlyList<AiIngredient?>? Ingredients,
        decimal? FinalWeightInGrams,
        string? FinalWeightQuality,
        IReadOnlyList<AiPortion?>? Portions);

    private sealed record AiIngredient(
        string? ProductName,
        decimal? WeightInGrams,
        string? WeightQuality,
        decimal? RemovedWeightInGrams,
        string? RemovedWeightQuality);

    private sealed record AiPortion(
        decimal? WeightInGrams,
        decimal? FractionOfDish,
        string? WeightQuality);
}
