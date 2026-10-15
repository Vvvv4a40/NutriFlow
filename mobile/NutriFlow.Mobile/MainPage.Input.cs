using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private void RenderInput(Guid profileId)
    {
        if (_session is not null)
        {
            RenderSession(profileId, _session);
        }

        Label heading = Ui.Title("");
        heading.FontSize = 28;
        heading.Margin = 0;
        Label explanation = Ui.Text("", true);
        _body.Children.Add(heading);
        _body.Children.Add(explanation);

        DatePicker date = new()
        {
            AutomationId = "input-date",
            Date = (_session?.MealDate ?? _date).ToDateTime(TimeOnly.MinValue),
            Format = "dd.MM.yyyy",
            IsEnabled = _session is null
        };
        SemanticProperties.SetDescription(date, "Дата записи в дневник");
        date.DateSelected += (_, _) => _date = DateOnly.FromDateTime(date.Date ?? DateTime.Today);
        Label dateLabel = Ui.Text("Дата записи", true);
        dateLabel.FontSize = 12;
        dateLabel.VerticalOptions = LayoutOptions.Center;
        Grid dateRow = new()
        {
            ColumnSpacing = 12,
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star } }
        };
        dateRow.Add(dateLabel, 0);
        dateRow.Add(date, 1);

        Editor editor = new InputEditor()
        {
            AutomationId = "input-description",
            Text = _draftText,
            AutoSize = EditorAutoSizeOption.Disabled,
            HeightRequest = 120,
            MaxLength = MealInputText.MaximumLength,
            FontSize = 16,
            BackgroundColor = Ui.Surface,
            TextColor = Ui.TextPrimary
        };
        Button submit = Ui.Action(_session is null ? "Разобрать описание" : "Добавить уточнение", () => RunAsync(async () =>
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
        }));
        submit.AutomationId = "input-submit";
        submit.FontSize = 14;
        void RefreshSubmit()
        {
            bool hasText = !string.IsNullOrWhiteSpace(_draftText);
            submit.IsEnabled = hasText;
            submit.BackgroundColor = hasText ? Ui.Accent : Ui.SurfaceRaised;
            submit.TextColor = hasText ? Ui.Background : Ui.Muted;
        }
        RefreshSubmit();
        Label count = Ui.Text($"{_draftText.Length}/{MealInputText.MaximumLength}", true);
        count.FontSize = 11;
        count.HorizontalTextAlignment = TextAlignment.End;
        count.VerticalOptions = LayoutOptions.Center;
        editor.TextChanged += (_, _) =>
        {
            _draftText = editor.Text ?? "";
            count.Text = $"{_draftText.Length}/{MealInputText.MaximumLength}";
            RefreshSubmit();
        };

        VerticalStackLayout alternatives = new() { Spacing = 10 };
        Label reassurance = Ui.Text("", true);
        reassurance.FontSize = 12;
        void RefreshPurpose()
        {
            bool creatingDish = _purpose == MealSessionPurpose.CreateDish;
            heading.Text = _session is not null ? "Уточнить описание" : creatingDish ? "Ваше блюдо. Ваш состав." : "Что сегодня на тарелке?";
            explanation.Text = _session is not null
                ? "Ответьте на вопросы или добавьте детали. Состав и КБЖУ обновятся перед подтверждением."
                : creatingDish ? "Сохраните состав один раз, чтобы позже добавлять порции этого блюда в дневник."
                : "Расскажите, что приготовили и съели. NutriFlow соберёт описание в понятный черновик.";
            editor.Placeholder = _session is not null ? "Дополните описание или ответьте на вопросы…"
                : creatingDish ? "Например: мой омлет — 2 яйца, 50 г молока и 5 г масла. Готовый вес 180 г."
                : "Например: омлет из 2 яиц и 50 г молока. Готовый вес 180 г, съел половину.";
            dateRow.IsVisible = !creatingDish;
            reassurance.Text = creatingDish ? "Блюдо сохранится только после вашего подтверждения."
                : "Ничего не попадёт в дневник без вашего подтверждения.";
            RenderInputAlternatives(profileId, alternatives);
        }

        _body.Children.Add(InputPurposeSelector(RefreshPurpose));
        if (_session is not null)
        {
            Label locked = Ui.Text("Режим зафиксирован для этого черновика. Для другого режима начните новую запись.", true);
            locked.FontSize = 12;
            locked.AutomationId = "input-draft-lock";
            _body.Children.Add(locked);
        }
        _body.Children.Add(dateRow);
        _body.Children.Add(InputComposer(profileId, editor, count, submit));
        _body.Children.Add(reassurance);
        _body.Children.Add(alternatives);
        RefreshPurpose();
        if (_session is not null)
        {
            Button restart = Ui.Action("Начать новую запись", () => RunAsync(async () =>
            {
                if (await DisplayAlertAsync("Новая запись", "Текущий черновик ещё не подтверждён. Начать новую запись?", "Начать", "Отмена"))
                {
                    _session = null;
                    _draftText = "";
                    ProfileSettings.ClearSession(profileId);
                    await RenderAsync();
                    await _scroll.ScrollToAsync(0, 0, false);
                }
            }), true);
            restart.AutomationId = "draft-restart";
            _body.Children.Add(restart);
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
        _scrollToPreview = true;
        ProfileSettings.SaveSession(profileId, session.Id);
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
