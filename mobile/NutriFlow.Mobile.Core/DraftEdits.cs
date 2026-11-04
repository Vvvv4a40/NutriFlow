using NutriFlow.Domain;

namespace NutriFlow.Mobile.Core;

// a change of one line of the calculation made by hand, without another request to the model
public static class DraftEdits
{
    public static MealDraft SetWeight(MealDraft draft, int dishIndex, decimal grams)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(grams);
        DishDraft dish = draft.Dishes[dishIndex];
        DishDraft changed;
        if (dish.Ingredients is [{ } only])
        {
            // a food eaten on its own: its weight, the dish and the portion are the same number
            IngredientDraft ingredient = new(only.ProductName, grams, DataQuality.Exact);
            changed = new DishDraft(dish.Name, [ingredient], grams, DataQuality.Exact, [new PortionDraft(grams)]);
        }
        else
        {
            if (dish.FinalWeightInGrams is { } final && grams > final)
            {
                throw new ArgumentException($"Это больше всего блюда: в нём {final:0.##} г. Уточните состав текстом ниже.");
            }

            changed = new DishDraft(dish.Name, dish.Ingredients, dish.FinalWeightInGrams, dish.FinalWeightQuality, [new PortionDraft(grams)]);
        }

        return new MealDraft(draft.Dishes.Select((item, index) => index == dishIndex ? changed : item).ToArray(), draft.ClarificationQuestions);
    }

    public static MealDraft Remove(MealDraft draft, int dishIndex)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new MealDraft(draft.Dishes.Where((_, index) => index != dishIndex).ToArray(), draft.ClarificationQuestions);
    }
}
