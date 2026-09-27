using System.Text.Json;
using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Ai;

internal static class AiNutritionLabelContract
{
    internal const string Instructions = """
        Extract nutrition facts exactly as printed on the product label image.
        Calories must contain kilocalories (kcal) only. Never copy kilojoules (kJ)
        into calories. If only kJ is printed, return null for calories and add a
        compact clarification question.
        Prefer a table expressed per 100 grams when present. Do not convert between a serving,
        100 milliliters, and 100 grams. Do not infer or complete values that are not readable.
        Use null for every missing or unreadable requested value and add compact clarification questions.
        Extract only productName, basis, calories, proteinGrams, fatGrams, and carbohydratesGrams.
        Ask questions only about missing, unreadable, or genuinely ambiguous requested nutrition values
        or their basis. Do not ask about sugar, fiber, salt, ingredients, or other unrequested details.
        Copy the printed carbohydrate value without asking whether it includes sugar or fiber.
        A complete readable table per 100 grams must have an empty clarificationQuestions list.
        Do not provide confidence percentages. ProductName may be null when it is not visible.
        """;

    internal const string InputPrompt =
        "Read this nutrition label and return only the requested structured data.";

    internal const string SchemaName = "nutriflow_nutrition_label";

    internal static JsonElement Schema { get; } = ParseSchema(
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
            "productName": { "type": ["string", "null"] },
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
              "items": { "type": "string" }
            }
          }
        }
        """);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static NutritionLabelDraft Deserialize(
        string structuredOutput,
        string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(structuredOutput);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        try
        {
            AiLabelDraft? parsed = JsonSerializer.Deserialize<AiLabelDraft>(
                structuredOutput,
                SerializerOptions);

            if (parsed?.ClarificationQuestions is null ||
                parsed.ClarificationQuestions.Count > 10 ||
                parsed.ClarificationQuestions.Any(static question =>
                    string.IsNullOrWhiteSpace(question) || question.Length > 500) ||
                parsed.ProductName?.Length > 200)
            {
                throw new InvalidDataException(
                    $"{providerName} structured label output is incomplete.");
            }

            return new NutritionLabelDraft(
                parsed.ProductName,
                MapBasis(parsed.Basis, providerName),
                parsed.Calories,
                parsed.ProteinGrams,
                parsed.FatGrams,
                parsed.CarbohydratesGrams,
                parsed.ClarificationQuestions.Select(question => question!).ToArray());
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"{providerName} returned malformed structured label output.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"{providerName} returned label data that violates domain rules.",
                exception);
        }
    }

    private static NutritionBasis MapBasis(
        string? basis,
        string providerName)
    {
        return basis?.ToLowerInvariant() switch
        {
            "per_100g" => NutritionBasis.Per100Grams,
            "per_100ml" => NutritionBasis.Per100Milliliters,
            "per_serving" => NutritionBasis.PerServing,
            "unknown" => NutritionBasis.Unknown,
            _ => throw new InvalidDataException(
                $"{providerName} returned unsupported nutrition basis '{basis}'.")
        };
    }

    private static JsonElement ParseSchema(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record AiLabelDraft(
        string? ProductName,
        string? Basis,
        decimal? Calories,
        decimal? ProteinGrams,
        decimal? FatGrams,
        decimal? CarbohydratesGrams,
        IReadOnlyList<string?>? ClarificationQuestions);
}
