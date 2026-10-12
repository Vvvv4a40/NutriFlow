using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private View ProductCatalogCard(Guid profileId, ProductResponse product)
    {
        VerticalStackLayout content = new() { Spacing = 10 };
        Label name = Ui.Title(product.Name);
        name.FontSize = 20;
        name.Margin = 0;
        name.LineBreakMode = LineBreakMode.WordWrap;
        SemanticProperties.SetHeadingLevel(name, SemanticHeadingLevel.Level2);
        content.Children.Add(name);

        VerticalStackLayout nutrition = new() { Spacing = 8 };
        nutrition.Children.Add(ProductCatalogDetail("Пищевая ценность на 100 г"));
        Label calories = Ui.Title($"{Ui.Amount(product.Calories)} ккал");
        calories.FontSize = 25;
        calories.Margin = 0;
        nutrition.Children.Add(calories);
        Label macros = Ui.Text("");
        macros.FontSize = 13;
        macros.FormattedText = new FormattedString();
        macros.FormattedText.Spans.Add(new Span { Text = $"Белки {Ui.Amount(product.ProteinGrams)} г", TextColor = Ui.Protein });
        macros.FormattedText.Spans.Add(new Span { Text = " · ", TextColor = Ui.Muted });
        macros.FormattedText.Spans.Add(new Span { Text = $"Жиры {Ui.Amount(product.FatGrams)} г", TextColor = Ui.Fat });
        macros.FormattedText.Spans.Add(new Span { Text = " · ", TextColor = Ui.Muted });
        macros.FormattedText.Spans.Add(new Span { Text = $"Углеводы {Ui.Amount(product.CarbohydratesGrams)} г", TextColor = Ui.Carbs });
        nutrition.Children.Add(macros);
        content.Children.Add(nutrition);

        string sourceKind = string.IsNullOrWhiteSpace(product.SourceKind) ? "тип неизвестен" : Ui.Source(product.SourceKind);
        string sourceName = string.IsNullOrWhiteSpace(product.SourceName) ? "название неизвестно" : product.SourceName;
        VerticalStackLayout provenance = new() { Spacing = 8 };
        provenance.Children.Add(ProductCatalogDetail($"Качество КБЖУ: {Ui.Quality(product.DataQuality)}"));
        provenance.Children.Add(ProductCatalogDetail($"Источник: {sourceKind} · {sourceName}"));
        if (!string.IsNullOrWhiteSpace(product.Barcode))
        {
            Label barcode = ProductCatalogDetail($"Штрихкод: {product.Barcode}");
            barcode.LineBreakMode = LineBreakMode.CharacterWrap;
            provenance.Children.Add(barcode);
        }

        if (!string.IsNullOrWhiteSpace(product.SourceReference))
        {
            Label reference = ProductCatalogDetail($"Данные источника: {product.SourceReference}");
            reference.LineBreakMode = LineBreakMode.CharacterWrap;
            provenance.Children.Add(reference);
        }
        content.Children.Add(provenance);

        Button add = Ui.Action("Добавить порцию", () => ProductPortionPageAsync(profileId, product), true);
        SemanticProperties.SetDescription(add, $"Добавить порцию продукта «{product.Name}» в дневник");
        content.Children.Add(add);
        return Ui.Card(content);
    }

    private static Label ProductCatalogDetail(string text)
    {
        Label detail = Ui.Text(text, true);
        detail.FontSize = 12;
        return detail;
    }
}
