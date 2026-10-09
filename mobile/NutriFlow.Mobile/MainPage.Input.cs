using NutriFlow.Api.Contracts;
using NutriFlow.Domain;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private void RenderInput(Guid profileId)
    {
        _body.Children.Add(Ui.Title("Добавить еду"));
        _body.Children.Add(Ui.Text("Опишите состав, веса и съеденную порцию. Проверьте расчёт перед подтверждением.", true));
        Picker purpose = new() { Title = "Что сделать", ItemsSource = new[] { "Записать в дневник", "Создать блюдо" }, SelectedIndex = (int)_purpose, IsEnabled = _session is null };
        purpose.SelectedIndexChanged += (_, _) => _purpose = purpose.SelectedIndex == 1 ? MealSessionPurpose.CreateDish : MealSessionPurpose.Diary;
        _body.Children.Add(purpose);
        DatePicker date = new() { Date = (_session?.MealDate ?? _date).ToDateTime(TimeOnly.MinValue), Format = "dd.MM.yyyy", IsEnabled = _session is null };
        date.DateSelected += (_, _) => _date = DateOnly.FromDateTime(date.Date ?? DateTime.Today);
        _body.Children.Add(date);
        Editor editor = new()
        {
            Text = _draftText,
            Placeholder = _session is null ? "Например: омлет из 2 яиц, 50 г молока. Готовый вес 180 г, съел 90 г." : "Дополните описание или ответьте на вопросы…",
            AutoSize = EditorAutoSizeOption.TextChanges,
            MinimumHeightRequest = 140,
            MaxLength = 4000,
            BackgroundColor = Colors.White,
            TextColor = Color.FromArgb("#18372C")
        };
        editor.TextChanged += (_, _) => _draftText = editor.Text ?? "";
        _body.Children.Add(editor);
        _body.Children.Add(Ui.Action(_session is null ? "Разобрать описание" : "Добавить уточнение", () => RunAsync(async () =>
        {
            string message = _draftText.Trim();
            if (message.Length == 0)
            {
                throw new ArgumentException("Введите описание еды или уточнение.");
            }

            var settings = await ProfileSettings.GetGroqAsync(profileId);
            MealSessionResponse session = _session is null
                ? await _client.CreateSessionAsync(profileId, new[] { message }, _date, _purpose, settings)
                : await _client.AddMessageAsync(profileId, _session.Id, message, settings)
                    ?? throw new InvalidOperationException("Черновик не найден. Начните новую запись.");
            SetSession(profileId, session);
            _draftText = "";
            await RenderAsync();
        })));
        _body.Children.Add(Ui.Row(Ui.Action("Голос / аудиофайл", () => AudioPageAsync(profileId), true),
            Ui.Action("Продукт вручную", () => ChooseManualMealPageAsync(profileId), true)));
        _body.Children.Add(Ui.Action("Блюдо из ингредиентов", () => ManualDishPageAsync(profileId), true));
        if (_session is not null)
        {
            RenderSession(profileId, _session);
            _body.Children.Add(Ui.Action("Начать новую запись", () => RunAsync(async () =>
            {
                if (await DisplayAlertAsync("Новая запись", "Текущий черновик ещё не подтверждён. Начать новую запись?", "Начать", "Отмена"))
                {
                    _session = null;
                    _draftText = "";
                    ProfileSettings.ClearSession(profileId);
                    await RenderAsync();
                }
            }), true));
        }
    }

    private void SetSession(Guid profileId, MealSessionResponse session)
    {
        if (_profile?.Id != profileId)
        {
            ProfileSettings.SaveSession(profileId, session.Id);
            return;
        }

        _session = session;
        _date = session.MealDate;
        _purpose = session.Purpose == "CreateDish" ? MealSessionPurpose.CreateDish : MealSessionPurpose.Diary;
        _tab = "Ввод";
        ProfileSettings.SaveSession(profileId, session.Id);
    }

    private void RenderSession(Guid profileId, MealSessionResponse session)
    {
        _body.Children.Add(Ui.Title(session.Purpose == "CreateDish" ? "Предпросмотр блюда" : "Предпросмотр записи"));
        foreach (string message in session.Messages)
        {
            _body.Children.Add(Ui.Card(Ui.Text(message, true)));
        }

        foreach (string question in session.ClarificationQuestions)
        {
            _body.Children.Add(Ui.Text($"Уточните: {question}"));
        }

        foreach (WorkflowIssueResponse issue in session.Issues)
        {
            _body.Children.Add(Ui.Text($"{issue.DishName}{(issue.IngredientName is null ? "" : $" · {issue.IngredientName}")}: {issue.Message}"));
        }

        foreach (DishPreviewResponse dish in session.Dishes)
        {
            VerticalStackLayout details = new() { Spacing = 8 };
            details.Children.Add(Ui.Title(dish.Name));
            details.Children.Add(Ui.Text($"Готовый вес: {(dish.FinalWeightInGrams is decimal grams ? Ui.Amount(grams) + " г" : "не указан")} · {Ui.Quality(dish.FinalWeightQuality)}", true));
            foreach (IngredientPreviewResponse ingredient in dish.Ingredients)
            {
                string weight = ingredient.WeightInGrams is decimal known ? Ui.Amount(known) + " г" : "вес не указан";
                details.Children.Add(Ui.Text($"{ingredient.ResolvedProduct?.Name ?? ingredient.ProductName}: {weight}"));
                if (ingredient.RemovedWeightInGrams is > 0)
                {
                    details.Children.Add(Ui.Text($"Убрано {Ui.Amount(ingredient.RemovedWeightInGrams.Value)} г", true));
                }

                if (ingredient.ResolvedProduct is ProductResponse product)
                {
                    details.Children.Add(Ui.Text($"Источник: {product.SourceName} · {Ui.Quality(product.DataQuality)}", true));
                }
                else
                {
                    details.Children.Add(Ui.Text("Добавьте пищевую ценность продукта в каталог.", true));
                    string productName = ingredient.ProductName;
                    details.Children.Add(Ui.Action("Добавить продукт", () => ProductEditorPageAsync(profileId, productName), true));
                }
            }

            if (dish.TotalNutrition is not null)
            {
                details.Children.Add(Ui.Text($"Всё блюдо: {Ui.Nutrition(dish.TotalNutrition)}"));
                details.Children.Add(Ui.Text(Ui.Quality(dish.TotalNutritionQuality), true));
            }

            if (dish.NutritionPer100Grams is not null)
            {
                details.Children.Add(Ui.Text($"На 100 г: {Ui.Nutrition(dish.NutritionPer100Grams)}"));
            }

            foreach (PortionPreviewResponse portion in dish.Portions)
            {
                string weight = portion.WeightInGrams is decimal known ? Ui.Amount(known) + " г" : "вес неизвестен";
                details.Children.Add(Ui.Text($"Порция: {weight}"));
                if (portion.Nutrition is not null)
                {
                    details.Children.Add(Ui.Text(Ui.Nutrition(portion.Nutrition)));
                    details.Children.Add(Ui.Text(Ui.Quality(portion.NutritionQuality), true));
                }
            }

            _body.Children.Add(Ui.Card(details));
        }

        _body.Children.Add(Ui.Action("Обновить расчёт после добавления продуктов", () => RunAsync(async () =>
        {
            _session = await _client.FindSessionAsync(profileId, session.Id)
                ?? throw new InvalidOperationException("Черновик не найден.");
            await RenderAsync();
        }), true));
        Button confirm = Ui.Action(session.Purpose == "CreateDish" ? "Подтвердить и сохранить блюдо" : "Подтвердить и записать в дневник", () => RunAsync(async () =>
        {
            ConfirmMealSessionResponse result = await _client.ConfirmAsync(profileId, session.Id, session.PreviewToken)
                ?? throw new InvalidOperationException("Черновик не найден.");
            _session = result.Session;
            if (result.Session.Status == "Confirmed")
            {
                _date = result.Session.MealDate;
                ProfileSettings.ClearSession(profileId);
                _session = null;
                _draftText = "";
                _tab = result.SavedDish is null ? "Дневник" : "Блюда";
                await RenderAsync();
                await DisplayAlertAsync("Сохранено", result.SavedDish is null ? "Запись добавлена в дневник." : "Блюдо сохранено. Его порции можно добавлять без интернета.", "Готово");
            }
            else
            {
                await RenderAsync();
                await DisplayAlertAsync("Проверьте черновик", result.Message ?? "Перед подтверждением нужны уточнения. Проверьте обновлённый расчёт.", "Понятно");
            }
        }));
        confirm.IsEnabled = session.CanConfirm;
        _body.Children.Add(confirm);
        if (!session.CanConfirm)
        {
            _body.Children.Add(Ui.Text("Подтверждение станет доступно после уточнений и заполнения пищевой ценности.", true));
        }
    }

    private async Task ChooseManualMealPageAsync(Guid profileId)
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

            FormPage page = new("Продукт в дневник");
            Picker product = new() { Title = "Продукт", ItemsSource = products.ToList(), ItemDisplayBinding = new Binding(nameof(ProductResponse.Name)), SelectedIndex = 0 };
            page.Fields.Children.Add(Ui.Text("Выберите продукт из своего каталога."));
            page.Fields.Children.Add(product);
            Entry grams = AddNumber(page, "Вес порции, г");
            DatePicker date = new() { Date = _date.ToDateTime(TimeOnly.MinValue), Format = "dd.MM.yyyy" };
            page.Fields.Children.Add(date);
            Switch estimated = new();
            page.Fields.Children.Add(Ui.Row(estimated, Ui.Text("Вес указан примерно")));
            page.Fields.Children.Add(Ui.Action("Посмотреть расчёт", () => page.RunAsync(async () =>
            {
                ProductResponse selected = (ProductResponse?)product.SelectedItem ?? throw new ArgumentException("Выберите продукт.");
                SetSession(profileId, await _client.CreateManualMealAsync(profileId, selected, Ui.Number(grams, "Вес", true), DateOnly.FromDateTime(date.Date ?? DateTime.Today), estimated.IsToggled ? DataQuality.Estimated : DataQuality.Exact));
                await Navigation.PopAsync();
            })));
            await Navigation.PushAsync(page);
        });
    }

    private Task<bool> CanStartDraftAsync() => _session is null
        ? Task.FromResult(true)
        : DisplayAlertAsync("Создать новый черновик?", "Текущий черновик ещё не подтверждён. Новый расчёт заменит его на экране.", "Создать", "Отмена");
}
