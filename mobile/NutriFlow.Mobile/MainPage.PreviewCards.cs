using Microsoft.Maui.Controls.Shapes;
using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private View DishPreviewCard(Guid profileId, DishPreviewResponse dish)
    {
        // a single catalog product has no meaningful "dish": its weight, total and composition repeat the portion
        IngredientPreviewResponse? only = dish.Ingredients.Count == 1 ? dish.Ingredients[0] : null;
        bool singleProduct = only?.ResolvedProduct is not null && dish.Portions.Count > 0 &&
            string.Equals(only.ResolvedProduct.Name, dish.Name, StringComparison.CurrentCultureIgnoreCase);
        bool wholeDishEaten = dish.Portions.Count == 1 && dish.Portions[0].WeightInGrams == dish.FinalWeightInGrams;
        VerticalStackLayout content = new() { Spacing = 16 };
        VerticalStackLayout heading = new() { Spacing = 6 };
        heading.Children.Add(Ui.Headline(dish.Name, 22));
        if (!singleProduct)
        {
            HorizontalStackLayout meta = new() { Spacing = 8 };
            meta.Children.Add(Ui.Caption($"Готовый вес {PreviewWeight(dish.FinalWeightInGrams)}"));
            AddBadge(meta, Ui.QualityBadge(dish.FinalWeightQuality));
            heading.Children.Add(meta);
        }

        content.Children.Add(heading);

        foreach (PortionPreviewResponse portion in dish.Portions)
        {
            VerticalStackLayout details = new() { Spacing = 8 };
            details.Children.Add(Ui.NutritionBlock(portion.Nutrition, "Ваша порция", 34));
            HorizontalStackLayout portionMeta = new() { Spacing = 8 };
            string weight = PreviewWeight(portion.WeightInGrams);
            portionMeta.Children.Add(Ui.Caption(portion.FractionOfDish is decimal fraction ? $"{weight} · доля блюда {fraction:G29}" : weight));
            AddBadge(portionMeta, Ui.QualityBadge(portion.WeightQuality) ?? Ui.QualityBadge(portion.NutritionQuality));
            details.Children.Add(portionMeta);
            content.Children.Add(new Border
            {
                Content = details,
                Padding = 16,
                BackgroundColor = Ui.AccentSoft.WithAlpha(0.5f),
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 18 }
            });
        }

        Grid totals = new() { ColumnSpacing = 10, ColumnDefinitions = new ColumnDefinitionCollection { new(), new() } };
        if (wholeDishEaten)
        {
            View per100 = TotalTile("На 100 г", dish.NutritionPer100Grams, dish.NutritionPer100GramsQuality);
            totals.Add(per100, 0);
            Grid.SetColumnSpan(per100, 2);
        }
        else
        {
            totals.Add(TotalTile("Всё блюдо", dish.TotalNutrition, dish.TotalNutritionQuality), 0);
            totals.Add(TotalTile("На 100 г", dish.NutritionPer100Grams, dish.NutritionPer100GramsQuality), 1);
        }

        content.Children.Add(totals);
        if (!singleProduct)
        {
            content.Children.Add(SectionHeader("Состав", dish.Ingredients.Count > 0 ? dish.Ingredients.Count.ToString(DiaryCulture) : null));
            if (dish.Ingredients.Count == 0)
            {
                content.Children.Add(Ui.Caption("Состав пока неизвестен."));
            }

            VerticalStackLayout ingredients = new() { Spacing = 0 };
            foreach (IngredientPreviewResponse ingredient in dish.Ingredients)
            {
                if (ingredients.Children.Count > 0)
                {
                    ingredients.Children.Add(Ui.Divider());
                }

                ingredients.Children.Add(PreviewIngredient(profileId, ingredient));
            }

            content.Children.Add(ingredients);
        }
        View? sources = PreviewSources(dish);
        if (sources is not null)
        {
            content.Children.Add(Ui.Expander("Источники данных", sources));
        }

        return Ui.Card(content);
    }

    private static void AddBadge(Layout layout, View? badge)
    {
        if (badge is not null)
        {
            layout.Children.Add(badge);
        }
    }

    private static View TotalTile(string title, NutritionResponse? nutrition, string? quality)
    {
        VerticalStackLayout content = new() { Spacing = 4 };
        content.Children.Add(Ui.Overline(title));
        if (nutrition is null)
        {
            content.Children.Add(Ui.Caption("неизвестно"));
        }
        else
        {
            Label calories = new() { FormattedText = new FormattedString(), FontAutoScalingEnabled = true };
            calories.FormattedText.Spans.Add(new Span { Text = Ui.Kilocalories(nutrition.Calories), FontSize = 20, FontAttributes = FontAttributes.Bold, TextColor = Ui.TextPrimary });
            calories.FormattedText.Spans.Add(new Span { Text = " ккал", FontSize = 13, TextColor = Ui.Muted });
            content.Children.Add(calories);
            Label macros = Ui.MacroLine(nutrition, 12);
            content.Children.Add(macros);
        }

        AddBadge(content, Ui.QualityBadge(quality));
        return new Border
        {
            Content = content,
            Padding = new Thickness(14, 12),
            BackgroundColor = Ui.SurfaceRaised.WithAlpha(0.55f),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 16 }
        };
    }

    private View PreviewIngredient(Guid profileId, IngredientPreviewResponse ingredient)
    {
        ProductResponse? product = ingredient.ResolvedProduct;
        Grid row = new()
        {
            Padding = new Thickness(0, 12),
            ColumnSpacing = 12,
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } }
        };
        VerticalStackLayout left = new() { Spacing = 4 };
        left.Children.Add(Ui.Strong(product?.Name ?? ingredient.ProductName, 15));
        string weight = PreviewWeight(ingredient.WeightInGrams);
        if (ingredient.RemovedWeightInGrams is null or > 0)
        {
            weight += $" · убрано {PreviewWeight(ingredient.RemovedWeightInGrams)} · вошло {PreviewWeight(ingredient.IncludedWeightInGrams)}";
        }

        left.Children.Add(Ui.Caption(weight));
        HorizontalStackLayout badges = new() { Spacing = 6 };
        AddBadge(badges, Ui.QualityBadge(ingredient.WeightQuality));
        if (product is not null)
        {
            AddBadge(badges, product.DataQuality == "Estimated" ? Ui.Badge("≈ КБЖУ", Ui.Warning) : null);
        }

        if (badges.Children.Count > 0)
        {
            left.Children.Add(badges);
        }

        row.Add(left, 0);
        if (product is null)
        {
            Label missing = Ui.Caption("Нет КБЖУ", Ui.Warning);
            missing.HorizontalTextAlignment = TextAlignment.End;
            row.Add(missing, 1);
            string productName = ingredient.ProductName;
            Button add = Ui.Ghost("Добавить пищевую ценность", () => ProductEditorPageAsync(profileId, productName));
            add.HorizontalOptions = LayoutOptions.Start;
            add.Margin = new Thickness(-12, 4, 0, -6);
            SemanticProperties.SetDescription(add, $"Добавить в каталог продукт «{productName}»");
            left.Children.Add(add);
        }
        else if (ingredient.Nutrition is not null)
        {
            Label calories = Ui.Strong($"{Ui.Kilocalories(ingredient.Nutrition.Calories)} ккал", 15);
            calories.HorizontalTextAlignment = TextAlignment.End;
            row.Add(calories, 1);
        }

        return row;
    }

    private static View? PreviewSources(DishPreviewResponse dish)
    {
        VerticalStackLayout sources = new() { Spacing = 12 };
        foreach (IngredientPreviewResponse ingredient in dish.Ingredients)
        {
            ProductResponse? product = ingredient.ResolvedProduct;
            if (product is null)
            {
                continue;
            }

            string sourceKind = string.IsNullOrWhiteSpace(product.SourceKind) ? "тип неизвестен" : Ui.Source(product.SourceKind);
            string sourceName = string.IsNullOrWhiteSpace(product.SourceName) ? "название неизвестно" : product.SourceName;
            VerticalStackLayout item = new() { Spacing = 2 };
            item.Children.Add(Ui.Strong(product.Name, 14));
            item.Children.Add(Ui.Caption($"{sourceKind} · {sourceName} · {Ui.Quality(product.DataQuality)}"));
            item.Children.Add(Ui.Caption($"На 100 г: {Ui.Nutrition(product)}", Ui.Subtle));
            if (!string.IsNullOrWhiteSpace(product.SourceReference))
            {
                Label reference = Ui.Caption(product.SourceReference, Ui.Subtle);
                reference.LineBreakMode = LineBreakMode.CharacterWrap;
                item.Children.Add(reference);
            }

            sources.Children.Add(item);
        }

        return sources.Children.Count == 0 ? null : sources;
    }

    private static string PreviewWeight(decimal? weight) => weight is decimal grams ? $"{Ui.Amount(grams)} г" : "неизвестно";
}
