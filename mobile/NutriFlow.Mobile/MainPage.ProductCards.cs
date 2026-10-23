using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private View ProductRow(Guid profileId, ProductResponse product)
    {
        string qualityMark = product.DataQuality == "Estimated" ? "≈ " : "";
        return Ui.Row(product.Name, null, $"{qualityMark}{Ui.Kilocalories(product.Calories)}", "ккал / 100 г",
            () => ProductPortionPageAsync(profileId, product),
            $"{product.Name}. На 100 граммов: {Ui.SpokenNutrition(product)}. {Ui.Quality(product.DataQuality)}",
            subtitleSpans: Ui.MacroSpans(product), hint: "Добавить порцию в дневник");
    }

    private static View ProductNutritionCard(ProductResponse product)
    {
        NutritionResponse per100 = new(product.Calories, product.ProteinGrams, product.FatGrams, product.CarbohydratesGrams);
        string sourceKind = string.IsNullOrWhiteSpace(product.SourceKind) ? "тип неизвестен" : Ui.Source(product.SourceKind);
        string sourceName = string.IsNullOrWhiteSpace(product.SourceName) ? "название неизвестно" : product.SourceName;
        HorizontalStackLayout meta = new() { Spacing = 8 };
        meta.Children.Add(Ui.Caption(sourceKind));
        AddBadge(meta, Ui.QualityBadge(product.DataQuality));

        VerticalStackLayout provenance = new() { Spacing = 6 };
        provenance.Children.Add(Ui.Caption($"Источник: {sourceKind} · {sourceName}"));
        provenance.Children.Add(Ui.Caption($"Качество: {Ui.Quality(product.DataQuality)}"));
        if (!string.IsNullOrWhiteSpace(product.Barcode))
        {
            Label barcode = Ui.Caption($"Штрихкод: {product.Barcode}");
            barcode.LineBreakMode = LineBreakMode.CharacterWrap;
            provenance.Children.Add(barcode);
        }

        if (!string.IsNullOrWhiteSpace(product.SourceReference))
        {
            Label reference = Ui.Caption($"Данные источника: {product.SourceReference}", Ui.Subtle);
            reference.LineBreakMode = LineBreakMode.CharacterWrap;
            provenance.Children.Add(reference);
        }

        return Ui.Card(Ui.NutritionBlock(per100, "На 100 г"), meta, Ui.Expander("Источник данных", provenance));
    }
}
