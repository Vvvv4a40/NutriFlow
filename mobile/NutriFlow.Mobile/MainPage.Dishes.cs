using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private async Task RenderDishesAsync(Guid profileId)
    {
        Grid actions = new() { ColumnSpacing = 10, ColumnDefinitions = new ColumnDefinitionCollection { new(), new() } };
        Button manual = Ui.Action("Собрать блюдо", () => ManualDishPageAsync(profileId), true);
        manual.AutomationId = "dish-create-manual";
        SemanticProperties.SetHint(manual, "Из продуктов каталога и готового веса");
        Button describe = Ui.Action("Описать словами", () => RunAsync(async () =>
        {
            if (InputDraftBlocked || _session is not null || !string.IsNullOrWhiteSpace(_draftText))
            {
                await DisplayAlertAsync("Сначала завершите текущую запись", "Открою её: подтвердите или начните заново, затем опишите блюдо.", "Понятно");
                _tab = "Ввод";
            }
            else
            {
                _purpose = MealSessionPurpose.CreateDish;
                _tab = "Ввод";
                await QueueInputDraftSave(profileId, CaptureInputDraft());
            }

            await RenderAsync();
        }), true);
        describe.AutomationId = "dish-create-text";
        actions.Add(manual, 0);
        actions.Add(describe, 1);
        _body.Children.Add(actions);

        IReadOnlyList<SavedDishResponse> dishes = await _client.GetSavedDishesAsync(profileId);
        if (dishes.Count == 0)
        {
            _body.Children.Add(Ui.EmptyState(AppIconKind.Dishes, "Нет блюд",
                "Сохраните блюдо, которое готовите часто: состав и готовый вес посчитаются один раз, а порции добавляются в пару касаний.",
                automationId: "dish-catalog-empty"));
            return;
        }

        Label count = Ui.Caption(Plural(dishes.Count, "блюдо", "блюда", "блюд"), Ui.Subtle);
        count.FontSize = 12;
        count.AutomationId = "dish-results-count";
        count.Margin = new Thickness(4, 4, 0, -4);
        _body.Children.Add(count);
        _body.Children.Add(Ui.List(dishes.Select(dish => SavedDishRow(profileId, dish))));
    }

    private async Task DishDetailPageAsync(Guid profileId, SavedDishResponse savedDish)
    {
        await RunAsync(async () =>
        {
            SavedDishDetailResponse detail = await _client.FindSavedDishAsync(profileId, savedDish.Id)
                ?? throw new InvalidOperationException("Блюдо не найдено.");
            DateOnly mealDate = _date;
            FormPage page = new("Порция блюда");
            page.Fields.Children.Add(Ui.Headline(savedDish.Name, 24));
            HorizontalStackLayout meta = new() { Spacing = 8 };
            meta.Children.Add(Ui.Caption($"Готовый вес {Ui.Amount(savedDish.FinalWeightInGrams)} г"));
            AddBadge(meta, Ui.QualityBadge(savedDish.NutritionQuality));
            page.Fields.Children.Add(Ui.Card(Ui.NutritionBlock(savedDish.NutritionPer100Grams, "На 100 г"), meta));

            Entry grams = AddNumber(page, "Съеденная порция, г");
            Switch estimated = new();
            page.Fields.Children.Add(Ui.SwitchRow("Вес указан примерно", estimated));

            VerticalStackLayout ingredients = new() { Spacing = 0 };
            foreach (IngredientPreviewResponse ingredient in detail.Dish.Ingredients)
            {
                if (ingredients.Children.Count > 0)
                {
                    ingredients.Children.Add(Ui.Divider());
                }

                Grid row = new()
                {
                    Padding = new Thickness(0, 10),
                    ColumnSpacing = 12,
                    ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } }
                };
                VerticalStackLayout text = new() { Spacing = 2 };
                text.Children.Add(Ui.Strong(ingredient.ResolvedProduct?.Name ?? ingredient.ProductName, 15));
                if (ingredient.ResolvedProduct is not null)
                {
                    text.Children.Add(Ui.Caption($"{ingredient.ResolvedProduct.SourceName} · {Ui.Quality(ingredient.ResolvedProduct.DataQuality)}", Ui.Subtle));
                }

                row.Add(text, 0);
                string weight = $"{Ui.Amount(ingredient.WeightInGrams ?? 0m)} г";
                if (ingredient.RemovedWeightInGrams is > 0)
                {
                    weight += $"\n−{Ui.Amount(ingredient.RemovedWeightInGrams.Value)} г";
                }

                Label amount = Ui.Caption(weight);
                amount.HorizontalTextAlignment = TextAlignment.End;
                row.Add(amount, 1);
                ingredients.Children.Add(row);
            }

            page.Fields.Children.Add(Ui.Card(Ui.Expander($"Состав · {detail.Dish.Ingredients.Count}", ingredients)));
            page.AddFooter(Ui.Action("Посмотреть расчёт порции", () => page.RunAsync(async () =>
            {
                if (!await CanStartDraftAsync())
                {
                    return;
                }

                await SetSessionAsync(profileId, await _client.CreateSavedDishMealAsync(profileId, savedDish.Id, Ui.Number(grams, "Вес порции", true), mealDate, estimated.IsToggled ? DataQuality.Estimated : DataQuality.Exact));
                await Navigation.PopAsync();
            })));
            await Navigation.PushAsync(page);
        });
    }

    private async Task ManualDishPageAsync(Guid profileId)
    {
        await RunAsync(async () =>
        {
            if (!await CanStartDraftAsync())
            {
                return;
            }

            IReadOnlyList<ProductResponse> products = await _client.SearchProductsAsync(profileId);
            if (products.Count == 0)
            {
                await ProductEditorPageAsync(profileId, reason: "В каталоге пока нет продуктов. Добавьте первый ингредиент, затем вернитесь к сборке блюда.");
                return;
            }

            FormPage page = new("Блюдо из продуктов");
            List<ManualIngredient> ingredients = [];
            page.Fields.Children.Add(Ui.Text("Добавьте ингредиенты из каталога с весом до приготовления, затем укажите вес готового блюда — КБЖУ на 100 г посчитаются по нему.", true));
            Entry name = Ui.Field("Например, Плов");
            page.Fields.Children.Add(Ui.Labeled("Название блюда", name));

            Picker product = new InputPicker() { Title = "Ингредиент", ItemsSource = products.ToList(), ItemDisplayBinding = new Binding(nameof(ProductResponse.Name)), SelectedIndex = 0 };
            Entry weight = Ui.Field("", null, true);
            Entry removed = Ui.Field("0", "0", true);
            Switch estimated = new();
            Grid weights = new() { ColumnSpacing = 12, ColumnDefinitions = new ColumnDefinitionCollection { new(), new() } };
            weights.Add(Ui.Labeled("Вес до готовки, г", weight), 0);
            weights.Add(Ui.Labeled("Не вошло, г", removed), 1);
            VerticalStackLayout composition = new() { Spacing = 0 };
            Label compositionTitle = Ui.Overline("Состав");
            void RefreshIngredients()
            {
                composition.Children.Clear();
                compositionTitle.Text = ingredients.Count == 0 ? "СОСТАВ ПОКА ПУСТ" : $"СОСТАВ · {ingredients.Count}";
                foreach (ManualIngredient ingredient in ingredients.ToArray())
                {
                    if (composition.Children.Count > 0)
                    {
                        composition.Children.Add(Ui.Divider());
                    }

                    Grid row = new()
                    {
                        Padding = new Thickness(0, 6),
                        ColumnSpacing = 8,
                        ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } }
                    };
                    VerticalStackLayout text = new() { Spacing = 2, VerticalOptions = LayoutOptions.Center };
                    text.Children.Add(Ui.Strong(ingredient.Product.Name, 15));
                    text.Children.Add(Ui.Caption(ingredient.RemovedWeightInGrams > 0
                        ? $"{Ui.Amount(ingredient.WeightInGrams)} г, не вошло {Ui.Amount(ingredient.RemovedWeightInGrams)} г"
                        : $"{Ui.Amount(ingredient.WeightInGrams)} г"));
                    row.Add(text, 0);
                    row.Add(Ui.Ghost("Убрать", () => { ingredients.Remove(ingredient); RefreshIngredients(); return Task.CompletedTask; }, Ui.Danger), 1);
                    composition.Children.Add(row);
                }
            }

            Button addIngredient = Ui.Action("Добавить в состав", () => page.RunAsync(() =>
            {
                ProductResponse selected = (ProductResponse?)product.SelectedItem ?? throw new ArgumentException("Выберите продукт.");
                decimal originalWeight = Ui.Number(weight, "Вес ингредиента", true);
                decimal removedWeight = Ui.Number(removed, "Не вошло");
                if (removedWeight > originalWeight)
                {
                    throw new ArgumentException("Вес, который не вошёл в блюдо, не может быть больше исходного веса ингредиента.");
                }

                ingredients.Add(new ManualIngredient(selected, originalWeight, estimated.IsToggled ? DataQuality.Estimated : DataQuality.Exact, removedWeight, estimated.IsToggled ? DataQuality.Estimated : DataQuality.Exact));
                weight.Text = "";
                removed.Text = "0";
                RefreshIngredients();
                return Task.CompletedTask;
            }), true);
            SemanticProperties.SetDescription(product, "Ингредиент");
            page.Fields.Children.Add(Ui.Card(Ui.Overline("Ингредиент"), Ui.PickerField(product), weights,
                Ui.SwitchRow("Вес указан примерно", estimated), addIngredient));
            RefreshIngredients();
            page.Fields.Children.Add(Ui.Card(compositionTitle, composition));

            Entry finalWeight = Ui.Field("", null, true);
            page.Fields.Children.Add(Ui.Labeled("Вес готового блюда, г", finalWeight));
            Switch finalEstimated = new();
            page.Fields.Children.Add(Ui.SwitchRow("Готовый вес указан примерно", finalEstimated));
            page.AddFooter(Ui.Action("Посмотреть расчёт блюда", () => page.RunAsync(async () =>
            {
                if (ingredients.Count == 0)
                {
                    throw new ArgumentException("Добавьте хотя бы один ингредиент.");
                }

                await SetSessionAsync(profileId, await _client.CreateManualDishAsync(profileId, Ui.Name(name, "Название блюда"), ingredients.ToArray(), Ui.Number(finalWeight, "Готовый вес", true), _date, finalEstimated.IsToggled ? DataQuality.Estimated : DataQuality.Exact));
                await Navigation.PopAsync();
            })));
            await Navigation.PushAsync(page);
        });
    }
}
