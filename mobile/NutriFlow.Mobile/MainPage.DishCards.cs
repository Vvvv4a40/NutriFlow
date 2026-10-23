using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private View SavedDishRow(Guid profileId, SavedDishResponse dish)
    {
        NutritionResponse values = dish.NutritionPer100Grams;
        string qualityMark = dish.NutritionQuality == "Estimated" ? "≈ " : "";
        View row = Ui.Row(dish.Name, null, $"{qualityMark}{Ui.Kilocalories(values.Calories)}", "ккал / 100 г",
            () => DishDetailPageAsync(profileId, dish),
            $"{dish.Name}. На 100 граммов: {Ui.SpokenNutrition(values)}. {Ui.Quality(dish.NutritionQuality)}",
            subtitleSpans: Ui.MacroSpans(values), automationId: $"saved-dish-{dish.Id:N}-open", hint: "Состав и порция");
        row.AutomationId = $"saved-dish-{dish.Id:N}";
        return row;
    }
}
