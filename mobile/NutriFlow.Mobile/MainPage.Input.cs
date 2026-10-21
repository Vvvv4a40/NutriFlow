using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private void RenderInput(Guid profileId)
    {
        DateOnly mealDate = _session?.MealDate ?? _date;
        if (_session is not null)
        {
            RenderSession(profileId, _session);
        }

        View? recovery = InputDraftRecovery(profileId);
        if (recovery is not null)
        {
            _body.Children.Add(recovery);
        }

        Label heading = Ui.Title("");
        heading.FontSize = 28;
        heading.Margin = 0;
        _body.Children.Add(heading);

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
        _inputEditor = editor;
        Guid? inputSessionId = _session?.Id;
        editor.IsEnabled = !InputDraftBlocked;
        Button submit = Ui.Action(_session is null ? "Разобрать описание" : "Добавить уточнение", () => RunAsync(async () =>
        {
            if (InputDraftBlocked)
            {
                throw new InvalidOperationException("Сначала восстановите или удалите сохранённый ввод.");
            }

            await FlushInputDraftAsync();
            string message = _draftText.Trim();
            if (message.Length == 0)
            {
                throw new ArgumentException("Введите описание еды или уточнение.");
            }

            var settings = await ProfileSettings.GetGroqAsync(profileId);
            MealSessionResponse session = _session is null
                ? await _client.CreateSessionAsync(profileId, new[] { message }, mealDate, _purpose, settings)
                : await _client.AddMessageAsync(profileId, _session.Id, message, settings)
                    ?? throw new InvalidOperationException("Черновик не найден. Начните новую запись.");
            await SetSessionAsync(profileId, session);
            await RenderAsync();
        }));
        submit.AutomationId = "input-submit";
        submit.FontSize = 14;
        void RefreshSubmit()
        {
            bool hasText = !string.IsNullOrWhiteSpace(_draftText);
            submit.IsEnabled = hasText && !InputDraftBlocked;
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
            if (!ReferenceEquals(_inputEditor, editor) || _profile?.Id != profileId ||
                _session?.Id != inputSessionId || InputDraftBlocked)
            {
                return;
            }

            _draftText = editor.Text ?? "";
            count.Text = $"{_draftText.Length}/{MealInputText.MaximumLength}";
            RefreshSubmit();
            ScheduleInputDraftSave(profileId);
        };

        VerticalStackLayout alternatives = new() { Spacing = 10 };
        void RefreshPurpose()
        {
            bool creatingDish = _purpose == MealSessionPurpose.CreateDish;
            heading.Text = _session is not null ? "Уточнение" : creatingDish ? "Создать блюдо" : "Добавить еду";
            editor.Placeholder = _session is not null ? "Ответьте на вопросы…"
                : creatingDish ? "Ингредиенты и готовый вес…"
                : "Что и сколько съели…";
            RenderInputAlternatives(profileId, alternatives);
        }

        _body.Children.Add(InputPurposeSelector(RefreshPurpose));
        _body.Children.Add(InputComposer(profileId, editor, count, submit));
        _inputSaveStatus = Ui.Text(_draftSaveMessage, true);
        _inputSaveStatus.AutomationId = "input-save-status";
        _inputSaveStatus.FontSize = 12;
        _inputSaveStatus.IsVisible = _draftSaveMessage.Length > 0;
        _body.Children.Add(_inputSaveStatus);
        _inputSaveRetry = Ui.Action("Повторить сохранение", () => RunAsync(async () =>
        {
            await QueueInputDraftSave(profileId, CaptureInputDraft());
            await FlushInputDraftAsync();
            await RenderAsync();
        }), true);
        _inputSaveRetry.AutomationId = "input-draft-retry";
        _inputSaveRetry.IsVisible = _draftSaveError is not null && !InputDraftBlocked;
        _body.Children.Add(_inputSaveRetry);
        _body.Children.Add(alternatives);
        RefreshPurpose();
        if (_session is not null)
        {
            Button restart = Ui.Action("Начать новую запись", () => RunAsync(async () =>
            {
                if (InputDraftBlocked)
                {
                    throw new InvalidOperationException("Сначала восстановите или удалите сохранённый ввод.");
                }

                await FlushInputDraftAsync();
                if (await DisplayAlertAsync("Новая запись", "Текущий черновик ещё не подтверждён. Начать новую запись?", "Начать", "Отмена"))
                {
                    await _inputDrafts.ClearAsync(profileId);
                    _inputEditor = null;
                    _session = null;
                    _draftText = "";
                    SetInputSaveStatus("");
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

            DateOnly mealDate = _date;
            FormPage page = new("Продукт в дневник");
            Picker product = new InputPicker() { Title = "Продукт", ItemsSource = products.ToList(), ItemDisplayBinding = new Binding(nameof(ProductResponse.Name)), SelectedIndex = 0 };
            page.Fields.Children.Add(product);
            Entry grams = AddNumber(page, "Вес порции, г");
            Switch estimated = new();
            page.Fields.Children.Add(Ui.Row(estimated, Ui.Text("Вес указан примерно")));
            page.Fields.Children.Add(Ui.Action("Посмотреть расчёт", () => page.RunAsync(async () =>
            {
                ProductResponse selected = (ProductResponse?)product.SelectedItem ?? throw new ArgumentException("Выберите продукт.");
                await SetSessionAsync(profileId, await _client.CreateManualMealAsync(profileId, selected, Ui.Number(grams, "Вес", true), mealDate, estimated.IsToggled ? DataQuality.Estimated : DataQuality.Exact));
                await Navigation.PopAsync();
            })));
            await Navigation.PushAsync(page);
        });
    }

    private async Task<bool> CanStartDraftAsync()
    {
        if (InputDraftBlocked)
        {
            await DisplayAlertAsync("Сохранённый ввод", "Сначала восстановите или удалите сохранённый текст в разделе добавления еды.", "Понятно");
            return false;
        }

        await FlushInputDraftAsync();
        if (_session is null && string.IsNullOrWhiteSpace(_draftText))
        {
            return true;
        }

        return await DisplayAlertAsync("Создать новый расчёт?", "Текущий черновик и неотправленный текст будут заменены только после успешного создания нового расчёта.", "Создать", "Отмена");
    }
}
