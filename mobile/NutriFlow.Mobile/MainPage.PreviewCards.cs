using Microsoft.Maui.Controls.Shapes;
using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private View DishPreviewCard(Guid profileId, DishPreviewResponse dish)
    {
        VerticalStackLayout content = new() { Spacing = 16 };
        Label title = Ui.Title(dish.Name);
        title.FontSize = 22;
        title.Margin = 0;
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level2);
        content.Children.Add(title);
        content.Children.Add(PreviewNote($"Готовый вес: {PreviewWeight(dish.FinalWeightInGrams)} · {Ui.Quality(dish.FinalWeightQuality)}"));
        foreach (PortionPreviewResponse portion in dish.Portions)
        {
            VerticalStackLayout details = new() { Spacing = 8 };
            details.Children.Add(PreviewHeading("Порция"));
            details.Children.Add(PreviewNote($"Масса: {PreviewWeight(portion.WeightInGrams)}"));
            if (portion.FractionOfDish is decimal fraction)
            {
                details.Children.Add(PreviewNote($"Доля блюда: {fraction:G29}"));
            }

            details.Children.Add(PreviewNote($"Качество массы / доли: {Ui.Quality(portion.WeightQuality)}"));
            PreviewNutritionValues(details, portion.Nutrition, portion.NutritionQuality);
            content.Children.Add(PreviewSurface(details));
        }

        content.Children.Add(PreviewNutrition("Всё блюдо", dish.TotalNutrition, dish.TotalNutritionQuality));
        content.Children.Add(PreviewNutrition("На 100 г готового блюда", dish.NutritionPer100Grams, dish.NutritionPer100GramsQuality));
        content.Children.Add(PreviewHeading("Состав"));
        if (dish.Ingredients.Count == 0)
        {
            content.Children.Add(PreviewNote("Состав пока неизвестен."));
        }

        foreach (IngredientPreviewResponse ingredient in dish.Ingredients)
        {
            content.Children.Add(PreviewIngredient(profileId, ingredient));
        }

        return Ui.Card(content);
    }

    private View PreviewIngredient(Guid profileId, IngredientPreviewResponse ingredient)
    {
        ProductResponse? product = ingredient.ResolvedProduct;
        VerticalStackLayout content = new() { Spacing = 7 };
        Label name = Ui.Text(product?.Name ?? ingredient.ProductName);
        name.FontAttributes = FontAttributes.Bold;
        content.Children.Add(name);
        content.Children.Add(PreviewNote($"Исходная масса: {PreviewWeight(ingredient.WeightInGrams)} · {Ui.Quality(ingredient.WeightQuality)}"));
        if (ingredient.RemovedWeightInGrams is null or > 0)
        {
            content.Children.Add(PreviewNote($"Удалено: {PreviewWeight(ingredient.RemovedWeightInGrams)} · {Ui.Quality(ingredient.RemovedWeightQuality)}"));
            content.Children.Add(PreviewNote($"Вошло в блюдо: {PreviewWeight(ingredient.IncludedWeightInGrams)}"));
        }

        if (product is not null)
        {
            content.Children.Add(PreviewNote($"Качество КБЖУ продукта: {Ui.Quality(product.DataQuality)}"));
            string sourceKind = string.IsNullOrWhiteSpace(product.SourceKind) ? "тип неизвестен" : Ui.Source(product.SourceKind);
            string sourceName = string.IsNullOrWhiteSpace(product.SourceName) ? "название неизвестно" : product.SourceName;
            content.Children.Add(PreviewNote($"Источник: {sourceKind} · {sourceName}"));
            if (!string.IsNullOrWhiteSpace(product.SourceReference))
            {
                Label reference = PreviewNote($"Данные источника: {product.SourceReference}");
                reference.LineBreakMode = LineBreakMode.CharacterWrap;
                content.Children.Add(reference);
            }
        }
        else
        {
            Label unresolved = Ui.Text("Пищевая ценность не определена");
            unresolved.FontSize = 13;
            unresolved.TextColor = Ui.Fat;
            content.Children.Add(unresolved);
            content.Children.Add(PreviewNote("Источник и КБЖУ неизвестны. Добавьте пищевую ценность с упаковки или другого известного источника."));
            string productName = ingredient.ProductName;
            Button add = Ui.Action("Добавить продукт", () => ProductEditorPageAsync(profileId, productName), true);
            SemanticProperties.SetDescription(add, $"Добавить в каталог продукт «{productName}»");
            content.Children.Add(add);
        }

        return PreviewSurface(content);
    }

    private static View PreviewNutrition(string title, NutritionResponse? nutrition, string? quality)
    {
        VerticalStackLayout content = new() { Spacing = 8 };
        content.Children.Add(PreviewHeading(title));
        PreviewNutritionValues(content, nutrition, quality);
        return PreviewSurface(content);
    }

    private static void PreviewNutritionValues(VerticalStackLayout content, NutritionResponse? nutrition, string? quality)
    {
        if (nutrition is not null)
        {
            Label calories = Ui.Title($"{Ui.Amount(nutrition.Calories)} ккал");
            calories.FontSize = 25;
            calories.Margin = 0;
            content.Children.Add(calories);
            Label macros = Ui.Text("");
            macros.FontSize = 13;
            macros.FormattedText = new FormattedString();
            macros.FormattedText.Spans.Add(new Span { Text = $"Белки {Ui.Amount(nutrition.ProteinGrams)} г", TextColor = Ui.Protein });
            macros.FormattedText.Spans.Add(new Span { Text = " · ", TextColor = Ui.Muted });
            macros.FormattedText.Spans.Add(new Span { Text = $"Жиры {Ui.Amount(nutrition.FatGrams)} г", TextColor = Ui.Fat });
            macros.FormattedText.Spans.Add(new Span { Text = " · ", TextColor = Ui.Muted });
            macros.FormattedText.Spans.Add(new Span { Text = $"Углеводы {Ui.Amount(nutrition.CarbohydratesGrams)} г", TextColor = Ui.Carbs });
            content.Children.Add(macros);
        }
        else
        {
            content.Children.Add(PreviewNote("КБЖУ пока неизвестны."));
        }

        content.Children.Add(PreviewNote($"Качество КБЖУ: {Ui.Quality(quality)}"));
    }

    private static Label PreviewHeading(string text)
    {
        Label heading = Ui.Text(text);
        heading.FontSize = 15;
        heading.FontAttributes = FontAttributes.Bold;
        SemanticProperties.SetHeadingLevel(heading, SemanticHeadingLevel.Level3);
        return heading;
    }

    private static Label PreviewNote(string text)
    {
        Label note = Ui.Text(text, true);
        note.FontSize = 12;
        return note;
    }

    private static Border PreviewSurface(View content) => new()
    {
        Content = content,
        Padding = 14,
        BackgroundColor = Ui.SurfaceRaised,
        StrokeThickness = 0,
        StrokeShape = new RoundRectangle { CornerRadius = 16 }
    };

    private static string PreviewWeight(decimal? weight) => weight is decimal grams ? $"{Ui.Amount(grams)} г" : "неизвестно";
}
