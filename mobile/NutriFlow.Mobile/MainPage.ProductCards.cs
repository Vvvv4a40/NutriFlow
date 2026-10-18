using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private View ProductCatalogCard(Guid profileId, ProductResponse product)
    {
        VerticalStackLayout content = new() { Spacing = 8 };
        Label name = Ui.Title(product.Name);
        name.FontSize = 20;
        name.Margin = 0;
        name.LineBreakMode = LineBreakMode.WordWrap;
        SemanticProperties.SetHeadingLevel(name, SemanticHeadingLevel.Level2);
        content.Children.Add(name);

        VerticalStackLayout nutrition = new() { Spacing = 6 };
        Label calories = Ui.Title($"{Ui.Amount(product.Calories)} ккал · на 100 г");
        calories.FontSize = 20;
        calories.Margin = 0;
        nutrition.Children.Add(calories);
        Label macros = Ui.Text("");
        macros.FontSize = 13;
        macros.FormattedText = new FormattedString();
        macros.FormattedText.Spans.Add(new Span { Text = $"Белки {Ui.MacroAmount(product.ProteinGrams)} г", TextColor = Ui.Protein });
        macros.FormattedText.Spans.Add(new Span { Text = " · ", TextColor = Ui.Muted });
        macros.FormattedText.Spans.Add(new Span { Text = $"Жиры {Ui.MacroAmount(product.FatGrams)} г", TextColor = Ui.Fat });
        macros.FormattedText.Spans.Add(new Span { Text = " · ", TextColor = Ui.Muted });
        macros.FormattedText.Spans.Add(new Span { Text = $"Углеводы {Ui.MacroAmount(product.CarbohydratesGrams)} г", TextColor = Ui.Carbs });
        nutrition.Children.Add(macros);
        content.Children.Add(nutrition);

        string sourceKind = string.IsNullOrWhiteSpace(product.SourceKind) ? "тип неизвестен" : Ui.Source(product.SourceKind);
        string sourceName = string.IsNullOrWhiteSpace(product.SourceName) ? "название неизвестно" : product.SourceName;
        content.Children.Add(ProductCatalogDetail($"{Ui.Quality(product.DataQuality)} · {sourceKind}"));
        VerticalStackLayout provenance = new() { Spacing = 8, IsVisible = false };
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
        Button source = Ui.Action("Источник", () =>
        {
            provenance.IsVisible = !provenance.IsVisible;
            return Task.CompletedTask;
        }, true);
        SemanticProperties.SetDescription(source, $"Источник продукта «{product.Name}»");
        SemanticProperties.SetHint(source, "Показать или скрыть подробности источника, штрихкод и ссылку");
        Button add = Ui.Action("Добавить порцию", () => ProductPortionPageAsync(profileId, product), true);
        SemanticProperties.SetDescription(add, $"Добавить порцию продукта «{product.Name}» в дневник");
        content.Children.Add(Ui.Row(source, add));
        content.Children.Add(provenance);
        return Ui.Card(content);
    }

    private static Label ProductCatalogDetail(string text)
    {
        Label detail = Ui.Text(text, true);
        detail.FontSize = 12;
        return detail;
    }
}
