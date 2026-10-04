using System.Text.Json;
using NutriFlow.Domain;

namespace NutriFlow.Infrastructure;

internal static class MealDraftSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(MealDraft draft)
    {
        StoredDraft document = new StoredDraft(
            draft.Dishes.Select(dish => new StoredDish(
                dish.Name,
                dish.Ingredients.Select(ingredient => new StoredIngredient(
                    ingredient.ProductName,
                    ingredient.WeightInGrams,
                    ingredient.WeightQuality,
                    ingredient.RemovedWeightInGrams,
                    ingredient.RemovedWeightQuality,
                    RemovalSpecified: true)).ToArray(),
                dish.FinalWeightInGrams,
                dish.FinalWeightQuality,
                dish.Portions.Select(portion => new StoredPortion(
                    portion.WeightInGrams,
                    portion.FractionOfDish,
                    portion.WeightQuality)).ToArray())).ToArray(),
            draft.ClarificationQuestions.ToArray());

        return JsonSerializer.Serialize(document, SerializerOptions);
    }

    public static MealDraft Deserialize(string json)
    {
        try
        {
            StoredDraft? document = JsonSerializer.Deserialize<StoredDraft>(
                json,
                SerializerOptions);

            if (document?.Dishes is null || document.ClarificationQuestions is null)
            {
                throw new InvalidDataException("Stored meal draft is incomplete.");
            }

            return new MealDraft(
                document.Dishes.Select(ToDishDraft).ToArray(),
                document.ClarificationQuestions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Stored meal draft JSON is invalid.",
                exception);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new InvalidDataException(
                "Stored meal draft violates domain rules.",
                exception);
        }
    }

    private static DishDraft ToDishDraft(StoredDish? dish)
    {
        if (dish is null || dish.Ingredients is null || dish.Portions is null)
        {
            throw new InvalidDataException("Stored meal draft contains null nested values.");
        }

        return new DishDraft(
            dish.Name,
            dish.Ingredients.Select(ToIngredientDraft).ToArray(),
            dish.FinalWeightInGrams,
            dish.FinalWeightQuality,
            dish.Portions.Select(ToPortionDraft).ToArray());
    }

    private static IngredientDraft ToIngredientDraft(StoredIngredient? ingredient)
    {
        if (ingredient is null)
        {
            throw new InvalidDataException("Stored meal draft contains a null ingredient.");
        }

        return new IngredientDraft(
            ingredient.ProductName,
            ingredient.WeightInGrams,
            ingredient.WeightQuality,
            ingredient.RemovalSpecified ? ingredient.RemovedWeightInGrams : 0m,
            ingredient.RemovalSpecified ? ingredient.RemovedWeightQuality : DataQuality.Exact);
    }

    private static PortionDraft ToPortionDraft(StoredPortion? portion)
    {
        if (portion is null)
        {
            throw new InvalidDataException("Stored meal draft contains a null portion.");
        }

        if ((portion.WeightInGrams is null) == (portion.FractionOfDish is null))
        {
            throw new InvalidDataException("Stored portion must have either a weight or a fraction.");
        }

        return portion.WeightInGrams is decimal weight
            ? new PortionDraft(weight, portion.WeightQuality)
            : PortionDraft.FromFraction(portion.FractionOfDish!.Value, portion.WeightQuality);
    }

    private sealed record StoredDraft(
        IReadOnlyList<StoredDish> Dishes,
        IReadOnlyList<string> ClarificationQuestions);

    private sealed record StoredDish(
        string Name,
        IReadOnlyList<StoredIngredient> Ingredients,
        decimal? FinalWeightInGrams,
        DataQuality FinalWeightQuality,
        IReadOnlyList<StoredPortion> Portions);

    private sealed record StoredIngredient(
        string ProductName,
        decimal? WeightInGrams,
        DataQuality WeightQuality,
        decimal? RemovedWeightInGrams,
        DataQuality RemovedWeightQuality,
        bool RemovalSpecified);

    private sealed record StoredPortion(
        decimal? WeightInGrams,
        decimal? FractionOfDish,
        DataQuality WeightQuality);
}
