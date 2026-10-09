using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private async Task RenderProductsAsync(Guid profileId)
    {
        _body.Children.Add(Ui.Title("Продукты"));
        _body.Children.Add(Ui.Text("Пищевая ценность всегда хранится вместе с источником. Значения ниже указаны на 100 г.", true));
        Entry search = Ui.Field("Название продукта");
        _body.Children.Add(search);
        VerticalStackLayout results = new() { Spacing = 10 };
        _body.Children.Add(Ui.Action("Найти в каталоге", () => RunAsync(async () =>
        {
            RenderProductList(results, profileId, await _client.SearchProductsAsync(profileId, search.Text));
        }), true));
        _body.Children.Add(Ui.Row(Ui.Action("Ввести вручную", () => ProductEditorPageAsync(profileId), true),
            Ui.Action("Фото этикетки", () => LabelSourcePageAsync(profileId), true)));
        _body.Children.Add(Ui.Action("Найти по штрихкоду", () => BarcodePageAsync(profileId), true));
        RenderProductList(results, profileId, await _client.SearchProductsAsync(profileId));
        _body.Children.Add(results);
    }

    private void RenderProductList(VerticalStackLayout target, Guid profileId, IReadOnlyList<ProductResponse> products)
    {
        target.Children.Clear();
        if (products.Count == 0)
        {
            target.Children.Add(Ui.Text("Продуктов пока нет. Добавьте данные с упаковки или найдите продукт по штрихкоду.", true));
        }

        foreach (ProductResponse product in products)
        {
            target.Children.Add(Ui.Card(Ui.Text(product.Name), Ui.Text(Ui.Nutrition(product)),
                Ui.Text($"{product.SourceName} · {Ui.Quality(product.DataQuality)}", true),
                Ui.Text(product.Barcode is null ? $"Источник: {Ui.Source(product.SourceKind)}" : $"Штрихкод: {product.Barcode}", true),
                Ui.Action("Добавить порцию в дневник", () => ProductPortionPageAsync(profileId, product), true)));
        }
    }

    private Task ProductPortionPageAsync(Guid profileId, ProductResponse product)
    {
        FormPage page = new("Добавить продукт");
        page.Fields.Children.Add(Ui.Title(product.Name));
        page.Fields.Children.Add(Ui.Text($"На 100 г: {Ui.Nutrition(product)}"));
        Entry grams = AddNumber(page, "Вес порции, г");
        DatePicker date = new() { Date = _date.ToDateTime(TimeOnly.MinValue), Format = "dd.MM.yyyy" };
        page.Fields.Children.Add(date);
        Switch estimated = new();
        page.Fields.Children.Add(Ui.Row(estimated, Ui.Text("Вес указан примерно")));
        page.Fields.Children.Add(Ui.Action("Посмотреть расчёт", () => page.RunAsync(async () =>
        {
            if (!await CanStartDraftAsync())
            {
                return;
            }

            SetSession(profileId, await _client.CreateManualMealAsync(profileId, product, Ui.Number(grams, "Вес", true), DateOnly.FromDateTime(date.Date ?? DateTime.Today), estimated.IsToggled ? NutriFlow.Domain.DataQuality.Estimated : NutriFlow.Domain.DataQuality.Exact));
            await Navigation.PopAsync();
        })));
        return Navigation.PushAsync(page);
    }

    private Task ProductEditorPageAsync(Guid profileId, string? initialName = null, ProductResponse? draft = null, byte[]? photo = null, IReadOnlyList<string>? questions = null, string? basis = null, NutriFlow.Domain.NutritionLabelDraft? labelDraft = null, string? photoReference = null)
    {
        FormPage page = new(photo is null ? "Новый продукт" : "Проверка этикетки");
        page.Fields.Children.Add(Ui.Title(photo is null ? "Пищевая ценность на 100 г" : "Проверьте данные с фото"));
        if (photo is not null)
        {
            page.Fields.Children.Add(new Image { Source = ImageSource.FromStream(() => new MemoryStream(photo, false)), HeightRequest = 220, Aspect = Aspect.AspectFit });
            page.Fields.Children.Add(Ui.Text($"Распознанная основа: {basis ?? "не определена"}. Все поля можно исправить. Сохраняйте только значения на 100 г.", true));
            foreach (string question in questions ?? [])
            {
                page.Fields.Children.Add(Ui.Text(question));
            }
        }
        else
        {
            page.Fields.Children.Add(Ui.Text("Перенесите значения с упаковки или другого известного источника.", true));
        }

        Entry name = Ui.Field("Название", draft?.Name ?? initialName);
        page.Fields.Children.Add(name);
        Entry calories = AddNumber(page, "Калории, ккал", photo is null ? draft?.Calories : labelDraft?.Calories);
        Entry protein = AddNumber(page, "Белки, г", photo is null ? draft?.ProteinGrams : labelDraft?.ProteinGrams);
        Entry fat = AddNumber(page, "Жиры, г", photo is null ? draft?.FatGrams : labelDraft?.FatGrams);
        Entry carbs = AddNumber(page, "Углеводы, г", photo is null ? draft?.CarbohydratesGrams : labelDraft?.CarbohydratesGrams);
        Entry barcode = Ui.Field("Штрихкод, если есть", draft?.Barcode);
        barcode.Keyboard = Keyboard.Numeric;
        page.Fields.Children.Add(barcode);
        Switch estimated = new();
        page.Fields.Children.Add(Ui.Row(estimated, Ui.Text("Значения приблизительные")));
        CheckBox checkedValues = new();
        if (photo is not null)
        {
            page.Fields.Children.Add(Ui.Row(checkedValues, Ui.Text("Я сверил значения и основу на 100 г")));
        }

        page.Fields.Children.Add(Ui.Action("Сохранить продукт", () => page.RunAsync(async () =>
        {
            if (photo is not null && !checkedValues.IsChecked)
            {
                throw new ArgumentException("Сверьте все поля с этикеткой и подтвердите, что значения указаны на 100 г.");
            }

            ProductResponse product = new(Ui.Name(name, "Название"), Ui.Number(calories, "Калории"), Ui.Number(protein, "Белки"), Ui.Number(fat, "Жиры"), Ui.Number(carbs, "Углеводы"),
                photo is null ? "ManualInput" : "LabelPhoto", estimated.IsToggled ? "Estimated" : "Exact",
                photo is null ? "Ручной ввод пользователя" : "Этикетка, проверена пользователем", photoReference ?? draft?.SourceReference,
                string.IsNullOrWhiteSpace(barcode.Text) ? null : barcode.Text.Trim());
            bool added = await _client.AddProductAsync(profileId, product);
            if (!added)
            {
                throw new InvalidOperationException("Продукт с таким названием уже есть. Выберите другое название, например добавьте марку.");
            }

            await Navigation.PopAsync();
        })));
        return Navigation.PushAsync(page);
    }

    private Task BarcodePageAsync(Guid profileId)
    {
        FormPage page = new("Поиск по штрихкоду");
        page.Fields.Children.Add(Ui.Text("Введите цифры под штрихкодом. Поиск использует Open Food Facts и требует интернета."));
        Entry barcode = Ui.Field("Штрихкод", number: true);
        page.Fields.Children.Add(barcode);
        page.Fields.Children.Add(Ui.Action("Найти продукт", () => page.RunAsync(async () =>
        {
            ProductResponse? product = await _client.LookupBarcodeAsync(profileId, Ui.Name(barcode, "Штрихкод"));
            if (product is null)
            {
                await page.DisplayAlertAsync("Продукт не найден", "В каталоге нет полной пищевой ценности. Добавьте продукт вручную по упаковке.", "Понятно");
                return;
            }

            page.Fields.Children.Add(Ui.Card(Ui.Title(product.Name), Ui.Text($"На 100 г: {Ui.Nutrition(product)}"), Ui.Text($"{product.SourceName} · {Ui.Quality(product.DataQuality)}", true),
                Ui.Text("Продукт добавлен в каталог. Сверьте значения с вашей упаковкой.", true),
                Ui.Action("Готово", () => Navigation.PopAsync(), true)));
        })));
        return Navigation.PushAsync(page);
    }
}
