using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private async Task RenderProductsAsync(Guid profileId)
    {
        Label title = Ui.Title("Ваши продукты");
        title.FontSize = 28;
        title.Margin = 0;
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level1);
        _body.Children.Add(title);
        _body.Children.Add(Ui.Text("Сохранённая пищевая ценность для быстрых записей. Все значения — на 100 г.", true));

        Label searchLabel = Ui.Text("Поиск по названию");
        searchLabel.FontSize = 13;
        Entry search = Ui.Field("Например, овсянка");
        search.AutomationId = "product-search";
        search.ReturnType = ReturnType.Search;
        search.MaxLength = 200;
        VerticalStackLayout results = new() { Spacing = 10 };
        Task FindProductsAsync() => RunAsync(async () =>
        {
            string query = search.Text?.Trim() ?? "";
            RenderProductList(results, profileId, await _client.SearchProductsAsync(profileId, query), query);
        });
        Button find = Ui.Action("Найти", FindProductsAsync);
        find.AutomationId = "product-search-submit";
        Button clear = Ui.Action("Сбросить", async () =>
        {
            search.Text = "";
            await FindProductsAsync();
        }, true);
        clear.AutomationId = "product-search-clear";
        search.Completed += async (_, _) => await FindProductsAsync();
        Label localSearch = Ui.Text("Поиск среди сохранённых продуктов — без интернета.", true);
        localSearch.FontSize = 12;
        _body.Children.Add(Ui.Card(searchLabel, search, Ui.Row(find, clear), localSearch));

        Label addLabel = Ui.Text("Пополнить каталог");
        addLabel.FontAttributes = FontAttributes.Bold;
        Button manual = Ui.Action("Добавить продукт вручную", () => ProductEditorPageAsync(profileId), true);
        manual.AutomationId = "product-add-manual";
        Button photo = Ui.Action("Фото этикетки", () => LabelSourcePageAsync(profileId), true);
        photo.AutomationId = "product-add-photo";
        Button barcode = Ui.Action("По штрихкоду", () => BarcodePageAsync(profileId), true);
        barcode.AutomationId = "product-add-barcode";
        Label online = Ui.Text("Для фото нужен личный Groq-ключ; поиск по штрихкоду требует интернета. Ручной ввод работает офлайн.", true);
        online.FontSize = 12;
        VerticalStackLayout addOptions = new() { Spacing = 10, IsVisible = false, AutomationId = "product-add-methods" };
        addOptions.Children.Add(Ui.Card(addLabel, manual, Ui.Row(photo, barcode), online));
        Button add = Ui.Action("Добавить продукт", () =>
        {
            addOptions.IsVisible = !addOptions.IsVisible;
            return Task.CompletedTask;
        }, true);
        add.AutomationId = "product-add-options";
        SemanticProperties.SetHint(add, "Показать или скрыть способы добавления: вручную, фото этикетки или штрихкод");
        _body.Children.Add(add);
        _body.Children.Add(addOptions);
        RenderProductList(results, profileId, await _client.SearchProductsAsync(profileId));
        _body.Children.Add(results);
    }

    private void RenderProductList(VerticalStackLayout target, Guid profileId, IReadOnlyList<ProductResponse> products, string query = "")
    {
        target.Children.Clear();
        Label count = Ui.Text($"Показано: {products.Count}", true);
        count.AutomationId = "product-results-count";
        count.FontSize = 12;
        target.Children.Add(count);
        if (products.Count == 0)
        {
            bool filtered = !string.IsNullOrWhiteSpace(query);
            Label title = Ui.Title(filtered ? "Ничего не найдено" : "Каталог пока пуст");
            title.FontSize = 20;
            title.Margin = 0;
            Border empty = Ui.Card(title, Ui.Text(filtered
                ? "Попробуйте другое название или сбросьте поиск, чтобы увидеть сохранённые продукты."
                : "Добавьте первый продукт вручную по упаковке. Он сохранится в выбранном локальном профиле.", true));
            empty.AutomationId = filtered ? "product-search-empty" : "product-catalog-empty";
            target.Children.Add(empty);
            return;
        }

        foreach (ProductResponse product in products)
        {
            target.Children.Add(ProductCatalogCard(profileId, product));
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
