using System.Text;
using System.Text.Json;
using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Ai;

internal static class AiMealDraftContract
{
    internal const string Instructions = """
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

    internal const string SchemaName = "nutriflow_meal_draft";

    internal static JsonElement Schema { get; } = ParseSchema(
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["dishes", "clarificationQuestions"],
          "properties": {
            "dishes": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name", "ingredients", "finalWeightInGrams", "finalWeightQuality", "portions"],
                "properties": {
                  "name": { "type": "string" },
                  "ingredients": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["productName", "weightInGrams", "weightQuality", "removedWeightInGrams", "removedWeightQuality"],
                      "properties": {
                        "productName": { "type": "string" },
                        "weightInGrams": { "type": ["number", "null"], "minimum": 0 },
                        "weightQuality": { "type": "string", "enum": ["exact", "estimated", "unknown"] },
                        "removedWeightInGrams": { "type": ["number", "null"], "minimum": 0 },
                        "removedWeightQuality": { "type": "string", "enum": ["exact", "estimated", "unknown"] }
                      }
                    }
                  },
                  "finalWeightInGrams": { "type": ["number", "null"], "minimum": 0 },
                  "finalWeightQuality": { "type": "string", "enum": ["exact", "estimated", "unknown"] },
                  "portions": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["weightInGrams", "fractionOfDish", "weightQuality"],
                      "properties": {
                        "weightInGrams": { "type": ["number", "null"], "minimum": 0 },
                        "fractionOfDish": { "type": ["number", "null"], "minimum": 0, "maximum": 1 },
                        "weightQuality": { "type": "string", "enum": ["exact", "estimated"] }
                      }
                    }
                  }
                }
              }
            },
            "clarificationQuestions": {
              "type": "array",
              "items": { "type": "string" }
            }
          }
        }
        """);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static string FormatInput(IReadOnlyList<InputEvent> inputEvents)
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

    internal static MealDraft Deserialize(
        string structuredOutput,
        string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(structuredOutput);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        try
        {
            AiMealDraft? parsed = JsonSerializer.Deserialize<AiMealDraft>(
                structuredOutput,
                SerializerOptions);

            return MapDraft(parsed, providerName);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"{providerName} returned malformed structured output.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"{providerName} returned a meal draft that violates domain rules.",
                exception);
        }
    }

    private static MealDraft MapDraft(AiMealDraft? draft, string providerName)
    {
        if (draft?.Dishes is null || draft.ClarificationQuestions is null)
        {
            throw new InvalidDataException(
                $"{providerName} structured output is incomplete.");
        }

        if (draft.Dishes.Count is 0 or > 10 ||
            draft.ClarificationQuestions.Count > 20 ||
            draft.Dishes.Any(static dish => dish is null) ||
            draft.ClarificationQuestions.Any(static question =>
                string.IsNullOrWhiteSpace(question) || question.Length > 500))
        {
            throw new InvalidDataException(
                $"{providerName} structured output contains invalid array items.");
        }

        List<DishDraft> dishes = draft.Dishes
            .Select(dish => MapDish(dish!, providerName))
            .ToList();
        string[] clarificationQuestions = draft.ClarificationQuestions
            .Select(question => question!)
            .ToArray();

        return new MealDraft(dishes, clarificationQuestions);
    }

    private static DishDraft MapDish(AiDish dish, string providerName)
    {
        if (string.IsNullOrWhiteSpace(dish.Name) ||
            dish.Name.Length > 200 ||
            dish.Ingredients is null ||
            dish.Ingredients.Count is 0 or > 50 ||
            dish.Portions is null ||
            dish.Portions.Count > 20)
        {
            throw new InvalidDataException(
                $"{providerName} returned an incomplete dish.");
        }

        if (dish.Ingredients.Any(static ingredient => ingredient is null) ||
            dish.Portions.Any(static portion => portion is null))
        {
            throw new InvalidDataException(
                $"{providerName} returned a dish with invalid array items.");
        }

        List<IngredientDraft> ingredients = dish.Ingredients
            .Select(ingredient => MapIngredient(ingredient!, providerName))
            .ToList();
        List<PortionDraft> portions = dish.Portions
            .Select(portion => MapPortion(portion, providerName))
            .ToList();

        return new DishDraft(
            dish.Name,
            ingredients,
            dish.FinalWeightInGrams,
            MapWeightQuality(dish.FinalWeightQuality, providerName),
            portions);
    }

    private static IngredientDraft MapIngredient(
        AiIngredient ingredient,
        string providerName)
    {
        if (string.IsNullOrWhiteSpace(ingredient.ProductName) ||
            ingredient.ProductName.Length > 200)
        {
            throw new InvalidDataException(
                $"{providerName} returned an invalid product name.");
        }

        bool omittedLegacyRemoval = ingredient.RemovedWeightInGrams is null &&
                                    ingredient.RemovedWeightQuality is null;

        return new IngredientDraft(
            ingredient.ProductName,
            ingredient.WeightInGrams,
            MapWeightQuality(ingredient.WeightQuality, providerName),
            omittedLegacyRemoval ? 0m : ingredient.RemovedWeightInGrams,
            omittedLegacyRemoval
                ? DataQuality.Exact
                : MapWeightQuality(
                    ingredient.RemovedWeightQuality,
                    providerName));
    }

    private static PortionDraft MapPortion(
        AiPortion? portion,
        string providerName)
    {
        if (portion is null ||
            (portion.WeightInGrams is null) ==
            (portion.FractionOfDish is null))
        {
            throw new InvalidDataException(
                $"{providerName} returned a portion that must have exactly one weight representation.");
        }

        DataQuality quality = MapWeightQuality(
            portion.WeightQuality,
            providerName);

        return portion.WeightInGrams is not null
            ? new PortionDraft(portion.WeightInGrams.Value, quality)
            : PortionDraft.FromFraction(portion.FractionOfDish!.Value, quality);
    }

    private static DataQuality MapWeightQuality(
        string? quality,
        string providerName)
    {
        return quality?.ToLowerInvariant() switch
        {
            "exact" => DataQuality.Exact,
            "estimated" => DataQuality.Estimated,
            "unknown" => DataQuality.Unknown,
            _ => throw new InvalidDataException(
                $"{providerName} returned unsupported weight quality '{quality}'.")
        };
    }

    private static JsonElement ParseSchema(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

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
