using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private async Task RenderDishesAsync(Guid profileId)
    {
        _body.Children.Add(Ui.Title("Сохранённые блюда"));
        _body.Children.Add(Ui.Text("Сохранённый состав и КБЖУ позволяют добавлять порции без повторного разбора AI.", true));
        _body.Children.Add(Ui.Action("Собрать блюдо вручную", () => ManualDishPageAsync(profileId)));
        IReadOnlyList<SavedDishResponse> dishes = await _client.GetSavedDishesAsync(profileId);
        if (dishes.Count == 0)
        {
            _body.Children.Add(Ui.Text("Сохранённых блюд пока нет. Соберите ингредиенты вручную или выберите «Создать блюдо» в разделе «Ввод».", true));
        }

        foreach (SavedDishResponse dish in dishes)
        {
            _body.Children.Add(Ui.Card(Ui.Title(dish.Name), Ui.Text($"Готовый вес {Ui.Amount(dish.FinalWeightInGrams)} г", true),
                Ui.Text($"На 100 г: {Ui.Nutrition(dish.NutritionPer100Grams)}"), Ui.Text(Ui.Quality(dish.NutritionQuality), true),
                Ui.Action("Состав и порция", () => DishDetailPageAsync(profileId, dish), true)));
        }
    }

    private async Task DishDetailPageAsync(Guid profileId, SavedDishResponse savedDish)
    {
        await RunAsync(async () =>
        {
            SavedDishDetailResponse detail = await _client.FindSavedDishAsync(profileId, savedDish.Id)
                ?? throw new InvalidOperationException("Блюдо не найдено.");
            FormPage page = new(savedDish.Name);
            page.Fields.Children.Add(Ui.Title(savedDish.Name));
            page.Fields.Children.Add(Ui.Text($"На 100 г: {Ui.Nutrition(savedDish.NutritionPer100Grams)}"));
            foreach (IngredientPreviewResponse ingredient in detail.Dish.Ingredients)
            {
                page.Fields.Children.Add(Ui.Text($"{ingredient.ResolvedProduct?.Name ?? ingredient.ProductName}: {Ui.Amount(ingredient.WeightInGrams ?? 0m)} г"));
                if (ingredient.RemovedWeightInGrams is > 0)
                {
                    page.Fields.Children.Add(Ui.Text($"Убрано: {Ui.Amount(ingredient.RemovedWeightInGrams.Value)} г", true));
                }

                if (ingredient.ResolvedProduct is not null)
                {
                    page.Fields.Children.Add(Ui.Text($"{ingredient.ResolvedProduct.SourceName} · {Ui.Quality(ingredient.ResolvedProduct.DataQuality)}", true));
                }
            }

            Entry grams = AddNumber(page, "Съеденная порция, г");
            DatePicker date = new() { Date = _date.ToDateTime(TimeOnly.MinValue), Format = "dd.MM.yyyy" };
            page.Fields.Children.Add(date);
            Switch estimated = new();
            page.Fields.Children.Add(Ui.Row(estimated, Ui.Text("Вес указан примерно")));
            page.Fields.Children.Add(Ui.Action("Посмотреть расчёт порции", () => page.RunAsync(async () =>
            {
                if (!await CanStartDraftAsync())
                {
                    return;
                }

                SetSession(profileId, await _client.CreateSavedDishMealAsync(profileId, savedDish.Id, Ui.Number(grams, "Вес порции", true), DateOnly.FromDateTime(date.Date ?? DateTime.Today), estimated.IsToggled ? DataQuality.Estimated : DataQuality.Exact));
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
                await ProductEditorPageAsync(profileId);
                return;
            }

            FormPage page = new("Блюдо из ингредиентов");
            List<ManualIngredient> ingredients = [];
            page.Fields.Children.Add(Ui.Text("Добавьте продукты из каталога. Укажите готовый вес после приготовления, чтобы рассчитать КБЖУ на 100 г."));
            Entry name = Ui.Field("Название блюда");
            page.Fields.Children.Add(name);
            Entry finalWeight = AddNumber(page, "Готовый вес блюда, г");
            Picker product = new() { Title = "Ингредиент", ItemsSource = products.ToList(), ItemDisplayBinding = new Binding(nameof(ProductResponse.Name)), SelectedIndex = 0 };
            page.Fields.Children.Add(product);
            Entry weight = AddNumber(page, "Вес ингредиента до приготовления, г");
            Entry removed = AddNumber(page, "Убрано / не попало в блюдо, г", 0m);
            Switch estimated = new();
            page.Fields.Children.Add(Ui.Row(estimated, Ui.Text("Вес ингредиента указан примерно")));
            VerticalStackLayout composition = new() { Spacing = 8 };
            void RefreshIngredients()
            {
                composition.Children.Clear();
                foreach (ManualIngredient ingredient in ingredients.ToArray())
                {
                    composition.Children.Add(Ui.Card(Ui.Text($"{ingredient.Product.Name}: {Ui.Amount(ingredient.WeightInGrams)} г, убрано {Ui.Amount(ingredient.RemovedWeightInGrams)} г"),
                        Ui.Action("Убрать ингредиент", () => { ingredients.Remove(ingredient); RefreshIngredients(); return Task.CompletedTask; }, true)));
                }
            }

            page.Fields.Children.Add(Ui.Action("Добавить ингредиент", () => page.RunAsync(() =>
            {
                ProductResponse selected = (ProductResponse?)product.SelectedItem ?? throw new ArgumentException("Выберите продукт.");
                decimal originalWeight = Ui.Number(weight, "Вес ингредиента", true);
                decimal removedWeight = Ui.Number(removed, "Убрано");
                if (removedWeight > originalWeight)
                {
                    throw new ArgumentException("Убранный вес не может быть больше исходного веса ингредиента.");
                }

                ingredients.Add(new ManualIngredient(selected, originalWeight, estimated.IsToggled ? DataQuality.Estimated : DataQuality.Exact, removedWeight, estimated.IsToggled ? DataQuality.Estimated : DataQuality.Exact));
                weight.Text = "";
                removed.Text = "0";
                RefreshIngredients();
                return Task.CompletedTask;
            }), true));
            page.Fields.Children.Add(composition);
            Switch finalEstimated = new();
            page.Fields.Children.Add(Ui.Row(finalEstimated, Ui.Text("Готовый вес указан примерно")));
            page.Fields.Children.Add(Ui.Action("Посмотреть расчёт блюда", () => page.RunAsync(async () =>
            {
                if (ingredients.Count == 0)
                {
                    throw new ArgumentException("Добавьте хотя бы один ингредиент.");
                }

                SetSession(profileId, await _client.CreateManualDishAsync(profileId, Ui.Name(name, "Название блюда"), ingredients.ToArray(), Ui.Number(finalWeight, "Готовый вес", true), _date, finalEstimated.IsToggled ? DataQuality.Estimated : DataQuality.Exact));
                await Navigation.PopAsync();
            })));
            await Navigation.PushAsync(page);
        });
    }
}
