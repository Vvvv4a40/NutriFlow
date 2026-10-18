using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private View SavedDishCard(Guid profileId, SavedDishResponse dish)
    {
        Label name = Ui.Title(dish.Name);
        name.FontSize = 20;
        name.Margin = 0;
        name.LineBreakMode = LineBreakMode.WordWrap;
        SemanticProperties.SetHeadingLevel(name, SemanticHeadingLevel.Level2);
        Label weight = SavedDishDetail($"Вес при сохранении: {Ui.Amount(dish.FinalWeightInGrams)} г");
        VerticalStackLayout identity = new() { Spacing = 8 };
        identity.Children.Add(name);
        identity.Children.Add(weight);

        NutritionResponse values = dish.NutritionPer100Grams;
        VerticalStackLayout nutrition = new() { Spacing = 8 };
        nutrition.Children.Add(SavedDishDetail("Пищевая ценность на 100 г"));
        Label calories = Ui.Title($"{Ui.Amount(values.Calories)} ккал");
        calories.FontSize = 25;
        calories.Margin = 0;
        nutrition.Children.Add(calories);
        Label macros = Ui.Text("");
        macros.FontSize = 13;
        macros.FormattedText = new FormattedString();
        macros.FormattedText.Spans.Add(new Span { Text = $"Белки {Ui.MacroAmount(values.ProteinGrams)} г", TextColor = Ui.Protein });
        macros.FormattedText.Spans.Add(new Span { Text = " · ", TextColor = Ui.Muted });
        macros.FormattedText.Spans.Add(new Span { Text = $"Жиры {Ui.MacroAmount(values.FatGrams)} г", TextColor = Ui.Fat });
        macros.FormattedText.Spans.Add(new Span { Text = " · ", TextColor = Ui.Muted });
        macros.FormattedText.Spans.Add(new Span { Text = $"Углеводы {Ui.MacroAmount(values.CarbohydratesGrams)} г", TextColor = Ui.Carbs });
        nutrition.Children.Add(macros);
        nutrition.Children.Add(SavedDishDetail($"Качество КБЖУ: {Ui.Quality(dish.NutritionQuality)}"));

        Button open = Ui.Action("Состав и порция", () => DishDetailPageAsync(profileId, dish), true);
        open.AutomationId = $"saved-dish-{dish.Id:N}-open";
        SemanticProperties.SetDescription(open, $"Состав и порция блюда «{dish.Name}»");
        Border card = Ui.Card(identity, nutrition, open);
        card.AutomationId = $"saved-dish-{dish.Id:N}";
        return card;
    }

    private static Label SavedDishDetail(string text)
    {
        Label detail = Ui.Text(text, true);
        detail.FontSize = 12;
        return detail;
    }
}
