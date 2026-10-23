using Microsoft.Maui.Controls.Shapes;
using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private CancellationTokenSource? _productSearch;

    private async Task RenderProductsAsync(Guid profileId)
    {
        Entry search = Ui.Field("Найти продукт");
        search.AutomationId = "product-search";
        search.ReturnType = ReturnType.Search;
        search.MaxLength = 200;
        SemanticProperties.SetDescription(search, "Поиск продукта по названию");
        VerticalStackLayout results = new() { Spacing = 12 };
        search.TextChanged += (_, _) => _ = SearchProductsAsync(profileId, search.Text ?? "", results);
        search.Completed += (_, _) => _ = SearchProductsAsync(profileId, search.Text ?? "", results, immediate: true);
        _body.Children.Add(search);

        Grid methods = new() { ColumnSpacing = 10, ColumnDefinitions = new ColumnDefinitionCollection { new(), new(), new() } };
        methods.Add(AddMethod("Вручную", AppIconKind.Edit, "product-add-manual", "Добавить продукт вручную",
            () => ProductEditorPageAsync(profileId, string.IsNullOrWhiteSpace(search.Text) ? null : search.Text.Trim())), 0);
        methods.Add(AddMethod("Этикетка", AppIconKind.Camera, "product-add-photo", "Добавить продукт по фото этикетки", () => LabelSourcePageAsync(profileId)), 1);
        methods.Add(AddMethod("Штрихкод", AppIconKind.Barcode, "product-add-barcode", "Найти продукт по штрихкоду", () => BarcodePageAsync(profileId)), 2);
        _body.Children.Add(SectionHeader("Добавить продукт"));
        _body.Children.Add(methods);

        RenderProductList(results, profileId, await _client.SearchProductsAsync(profileId));
        _body.Children.Add(results);
    }

    // local search is fast, so results follow typing after a short pause instead of a search button
    private async Task SearchProductsAsync(Guid profileId, string text, VerticalStackLayout results, bool immediate = false)
    {
        _productSearch?.Cancel();
        CancellationTokenSource search = new();
        _productSearch = search;
        try
        {
            if (!immediate)
            {
                await Task.Delay(250, search.Token);
            }

            string query = text.Trim();
            IReadOnlyList<ProductResponse> products = await _client.SearchProductsAsync(profileId, query, search.Token);
            if (ReferenceEquals(_productSearch, search) && _profile?.Id == profileId && _tab == "Продукты")
            {
                RenderProductList(results, profileId, products, query);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_productSearch, search))
            {
                results.Children.Clear();
                results.Children.Add(Ui.Caption(FormPage.ErrorText(exception), Ui.Warning));
            }
        }
    }

    private static View AddMethod(string title, AppIconKind icon, string automationId, string description, Func<Task> action)
    {
        VerticalStackLayout content = new() { Spacing = 8, Padding = new Thickness(8, 14), HorizontalOptions = LayoutOptions.Center };
        Border tile = Ui.IconTile(icon, Ui.Accent, 40);
        tile.HorizontalOptions = LayoutOptions.Center;
        content.Children.Add(tile);
        Label caption = Ui.Strong(title, 14);
        caption.HorizontalTextAlignment = TextAlignment.Center;
        caption.MaxLines = 1;
        caption.LineBreakMode = LineBreakMode.TailTruncation;
        content.Children.Add(caption);
        Border card = new()
        {
            Content = content,
            BackgroundColor = Ui.Surface,
            Stroke = Ui.Stroke,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 18 }
        };
        Grid pressable = Ui.Pressable(card, action, description, null, automationId);
        Ui.FillPressable(pressable, card, Ui.Surface);
        return pressable;
    }

    private void RenderProductList(VerticalStackLayout target, Guid profileId, IReadOnlyList<ProductResponse> products, string query = "")
    {
        target.Children.Clear();
        if (products.Count == 0)
        {
            bool filtered = !string.IsNullOrWhiteSpace(query);
            target.Children.Add(filtered
                ? Ui.EmptyState(AppIconKind.Search, "Ничего не найдено", $"В каталоге нет «{query}». Добавьте продукт одним из способов выше.", automationId: "product-search-empty")
                : Ui.EmptyState(AppIconKind.Products, "Нет продуктов", "Добавьте продукт вручную, по фото этикетки или штрихкоду — дальше он считается без интернета.", automationId: "product-catalog-empty"));
            return;
        }

        Label count = Ui.Caption(Plural(products.Count, "продукт", "продукта", "продуктов"), Ui.Subtle);
        count.AutomationId = "product-results-count";
        count.FontSize = 12;
        count.Margin = new Thickness(4, 4, 0, -4);
        target.Children.Add(count);
        target.Children.Add(Ui.List(products.Select(product => ProductRow(profileId, product))));
    }

    private static string Plural(int count, string one, string few, string many)
    {
        int lastTwo = count % 100;
        int last = count % 10;
        string word = lastTwo is >= 11 and <= 14 ? many : last == 1 ? one : last is >= 2 and <= 4 ? few : many;
        return $"{count} {word}";
    }

    private Task ProductPortionPageAsync(Guid profileId, ProductResponse product)
    {
        DateOnly mealDate = _date;
        FormPage page = new("Порция продукта");
        page.Fields.Children.Add(Ui.Headline(product.Name, 24));
        page.Fields.Children.Add(ProductNutritionCard(product));
        Entry grams = AddNumber(page, "Вес порции, г");
        Switch estimated = new();
        page.Fields.Children.Add(Ui.SwitchRow("Вес указан примерно", estimated));
        page.AddFooter(Ui.Action("Посмотреть расчёт", () => page.RunAsync(async () =>
        {
            if (!await CanStartDraftAsync())
            {
                return;
            }

            await SetSessionAsync(profileId, await _client.CreateManualMealAsync(profileId, product, Ui.Number(grams, "Вес", true), mealDate, estimated.IsToggled ? NutriFlow.Domain.DataQuality.Estimated : NutriFlow.Domain.DataQuality.Exact));
            await Navigation.PopAsync();
        })));
        return Navigation.PushAsync(page);
    }

    private Task ProductEditorPageAsync(Guid profileId, string? initialName = null, ProductResponse? draft = null, byte[]? photo = null, IReadOnlyList<string>? questions = null, string? basis = null, NutriFlow.Domain.NutritionLabelDraft? labelDraft = null, string? photoReference = null, string? reason = null)
    {
        FormPage page = new(photo is null ? "Новый продукт" : "Проверка этикетки");
        if (reason is not null)
        {
            page.Fields.Children.Add(Notice(AppIconKind.Products, Ui.Accent, reason));
        }

        if (photo is not null)
        {
            page.Fields.Children.Add(new Border
            {
                Content = new Image { Source = ImageSource.FromStream(() => new MemoryStream(photo, false)), HeightRequest = 220, Aspect = Aspect.AspectFill },
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = Ui.CardRadius }
            });
            VerticalStackLayout notes = new() { Spacing = 8 };
            notes.Children.Add(Ui.Text(basis == "на 100 г"
                ? "Распознаны значения на 100 г. Сверьте каждое поле с упаковкой."
                : $"На этикетке значения {basis ?? "с неизвестной основой"}. Пересчитайте их на 100 г перед сохранением: сохраняются только значения на 100 г."));
            foreach (string question in questions ?? [])
            {
                notes.Children.Add(Ui.Caption($"• {question}", Ui.Warning));
            }

            page.Fields.Children.Add(Notice(AppIconKind.Warning, Ui.Warning, "Проверьте данные с фото", notes));
        }
        else
        {
            page.Fields.Children.Add(Ui.Text("Перенесите значения на 100 г с упаковки или из другого известного источника.", true));
        }

        Entry name = Ui.Field("Например, Творог 5% «Простоквашино»", draft?.Name ?? initialName);
        page.Fields.Children.Add(Ui.Labeled("Название", name));
        page.Fields.Children.Add(Ui.Overline("Пищевая ценность на 100 г"));
        Entry calories = Ui.Field("", Format(photo is null ? draft?.Calories : labelDraft?.Calories), true);
        Entry protein = Ui.Field("", Format(photo is null ? draft?.ProteinGrams : labelDraft?.ProteinGrams), true);
        Entry fat = Ui.Field("", Format(photo is null ? draft?.FatGrams : labelDraft?.FatGrams), true);
        Entry carbs = Ui.Field("", Format(photo is null ? draft?.CarbohydratesGrams : labelDraft?.CarbohydratesGrams), true);
        Grid values = new()
        {
            ColumnSpacing = 12,
            RowSpacing = 14,
            ColumnDefinitions = new ColumnDefinitionCollection { new(), new() },
            RowDefinitions = new RowDefinitionCollection { new(), new() }
        };
        values.Add(Ui.Labeled("Калории, ккал", calories), 0, 0);
        values.Add(Ui.Labeled("Белки, г", protein), 1, 0);
        values.Add(Ui.Labeled("Жиры, г", fat), 0, 1);
        values.Add(Ui.Labeled("Углеводы, г", carbs), 1, 1);
        page.Fields.Children.Add(values);
        Entry barcode = Ui.Field("Необязательно", draft?.Barcode);
        barcode.Keyboard = Keyboard.Numeric;
        page.Fields.Children.Add(Ui.Labeled("Штрихкод", barcode));
        Switch estimated = new();
        page.Fields.Children.Add(Ui.SwitchRow("Значения приблизительные", estimated, "Например, взяты по похожему продукту"));
        Switch checkedValues = new();
        if (photo is not null)
        {
            page.Fields.Children.Add(Ui.SwitchRow("Значения сверены с этикеткой", checkedValues, "Они указаны на 100 г"));
        }

        page.AddFooter(Ui.Action("Сохранить продукт", () => page.RunAsync(async () =>
        {
            if (photo is not null && !checkedValues.IsToggled)
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

    private static string? Format(decimal? value) => value is decimal number ? Ui.Amount(number) : null;

    private Task BarcodePageAsync(Guid profileId)
    {
        FormPage page = new("Поиск по штрихкоду");
        page.Fields.Children.Add(Ui.Text("Введите цифры под штрихкодом. Поиск идёт по открытой базе Open Food Facts и требует интернета.", true));
        Entry barcode = Ui.Field("Например, 4600000000000", number: true);
        page.Fields.Children.Add(Ui.Labeled("Штрихкод", barcode));
        VerticalStackLayout result = new() { Spacing = 12 };
        page.Fields.Children.Add(result);
        Button? find = null;
        find = Ui.Action("Найти продукт", () => page.RunAsync(async () =>
        {
            string code = Ui.Name(barcode, "Штрихкод");
            page.ShowProgress(find!, "Ищу продукт…");
            ProductResponse? product = await _client.LookupBarcodeAsync(profileId, code);
            result.Children.Clear();
            if (product is null)
            {
                await page.DisplayAlertAsync("Продукт не найден", "В базе нет полной пищевой ценности. Добавьте продукт вручную по упаковке.", "Понятно");
                return;
            }

            result.Children.Add(Notice(AppIconKind.Check, Ui.Success, "Продукт добавлен в каталог. Сверьте значения с упаковкой.",
                Ui.Ghost("Готово", () => Navigation.PopAsync())));
            result.Children.Add(Ui.Headline(product.Name, 20));
            result.Children.Add(ProductNutritionCard(product));
        }));
        page.AddFooter(find);
        return Navigation.PushAsync(page);
    }
}
