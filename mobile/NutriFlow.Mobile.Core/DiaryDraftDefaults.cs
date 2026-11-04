using NutriFlow.Domain;

namespace NutriFlow.Mobile.Core;

// what a diary meal does not need to ask: a dish without a final weight weighs what went into it and a dish without a
// portion was eaten whole. The whole dish holds all of its calories whatever the cooked weight, so the entry stays right
public static class DiaryDraftDefaults
{
    public static MealDraft Apply(MealDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        DishDraft[] dishes = draft.Dishes.Select(Apply).ToArray();
        bool complete = dishes.All(dish => dish.FinalWeightInGrams is not null && dish.Portions.Count > 0 &&
            dish.Ingredients.All(ingredient => ingredient.IncludedWeightInGrams is not null));
        return new MealDraft(dishes, complete ? [] : draft.ClarificationQuestions);
    }

    private static DishDraft Apply(DishDraft dish)
    {
        decimal? included = dish.Ingredients.All(ingredient => ingredient.IncludedWeightInGrams is not null)
            ? dish.Ingredients.Sum(ingredient => ingredient.IncludedWeightInGrams!.Value)
            : null;
        decimal portions = dish.Portions.Sum(portion => portion.WeightInGrams ?? 0m);
        decimal? finalWeight = dish.FinalWeightInGrams ?? (included is > 0m ? Math.Max(included.Value, portions) : null);
        if (finalWeight is null)
        {
            return dish;
        }

        DataQuality finalQuality = dish.FinalWeightInGrams is not null ? dish.FinalWeightQuality
            : dish.Ingredients is [{ } only] ? only.WeightQuality
            : DataQuality.Estimated;
        return new DishDraft(dish.Name, dish.Ingredients, finalWeight, finalQuality,
            dish.Portions.Count > 0 ? dish.Portions : [PortionDraft.FromFraction(1m)]);
    }
}
