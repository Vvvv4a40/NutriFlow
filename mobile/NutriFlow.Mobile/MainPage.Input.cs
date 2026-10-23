using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private void RenderInput(Guid profileId, bool hasGroqKey)
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

        Label context = Ui.Caption("");
        Editor editor = new InputEditor()
        {
            AutomationId = "input-description",
            Text = _draftText,
            AutoSize = EditorAutoSizeOption.Disabled,
            HeightRequest = _session is null ? 156 : 104,
            MaxLength = MealInputText.MaximumLength,
            FontSize = 17,
            BackgroundColor = Ui.SurfaceRaised,
            TextColor = Ui.TextPrimary,
            PlaceholderColor = Ui.Subtle
        };
        SemanticProperties.SetDescription(editor, _session is null ? "Описание еды" : "Уточнение");
        _inputEditor = editor;
        Guid? inputSessionId = _session?.Id;
        editor.IsEnabled = !InputDraftBlocked;
        Button? submit = null;
        submit = Ui.Action(_session is null ? "Разобрать описание" : "Добавить уточнение", () => RunAsync(async () =>
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
            ShowProgress(submit!, _session is null ? "Разбираю описание…" : "Добавляю уточнение…");
            MealSessionResponse session = _session is null
                ? await _client.CreateSessionAsync(profileId, new[] { message }, mealDate, _purpose, settings)
                : await _client.AddMessageAsync(profileId, _session.Id, message, settings)
                    ?? throw new InvalidOperationException("Расчёт не найден. Начните новую запись.");
            await SetSessionAsync(profileId, session);
            await RenderAsync();
        }), _session is not null);
        submit.AutomationId = "input-submit";
        void RefreshSubmit()
        {
            bool enabled = !string.IsNullOrWhiteSpace(_draftText) && !InputDraftBlocked;
            if (_session is null)
            {
                Ui.SetEnabled(submit, enabled);
            }
            else
            {
                submit.IsEnabled = enabled;
            }
        }
        RefreshSubmit();

        // the counter only appears near the limit, where it helps
        Label count = Ui.Caption("", Ui.Subtle);
        count.FontSize = 12;
        void RefreshCount()
        {
            count.Text = $"{_draftText.Length}/{MealInputText.MaximumLength}";
            count.IsVisible = _draftText.Length > MealInputText.MaximumLength * 0.8;
        }
        RefreshCount();
        editor.TextChanged += (_, _) =>
        {
            if (!ReferenceEquals(_inputEditor, editor) || _profile?.Id != profileId ||
                _session?.Id != inputSessionId || InputDraftBlocked)
            {
                return;
            }

            _draftText = editor.Text ?? "";
            RefreshCount();
            RefreshSubmit();
            ScheduleInputDraftSave(profileId);
        };

        _inputSaveStatus = Ui.Caption(_draftSaveMessage, Ui.Subtle);
        _inputSaveStatus.AutomationId = "input-save-status";
        _inputSaveStatus.FontSize = 12;
        _inputSaveStatus.IsVisible = _draftSaveMessage.Length > 0;
        _inputSaveRetry = Ui.Ghost("Повторить сохранение", () => RunAsync(async () =>
        {
            await QueueInputDraftSave(profileId, CaptureInputDraft());
            await FlushInputDraftAsync();
            await RenderAsync();
        }), Ui.Warning);
        _inputSaveRetry.AutomationId = "input-draft-retry";
        _inputSaveRetry.IsVisible = _draftSaveError is not null && !InputDraftBlocked;

        VerticalStackLayout alternatives = new() { Spacing = 12 };
        void RefreshPurpose()
        {
            bool creatingDish = _purpose == MealSessionPurpose.CreateDish;
            _screenTitle.Text = ScreenTitle();
            context.Text = creatingDish
                ? "Опишите ингредиенты с весом и сколько весит готовое блюдо. Оно сохранится в «Мои блюда»."
                : $"Запись попадёт в дневник за {DiaryDay(mealDate)}. Перед сохранением вы увидите расчёт.";
            editor.Placeholder = _session is not null ? "Ответьте на вопросы или поправьте состав…"
                : creatingDish ? "Например: фарш 600 г, лук 150 г, рис 200 г сухого. Готовое блюдо 1180 г"
                : "Например: гречка 200 г, куриная грудка 150 г и огурец";
            RenderInputAlternatives(profileId, alternatives);
        }

        if (_session is null)
        {
            _body.Children.Add(InputPurposeSelector(RefreshPurpose));
            _body.Children.Add(context);
        }

        if (!hasGroqKey && !InputDraftBlocked)
        {
            Button settings = Ui.Ghost("Открыть настройки", () => SelectTabAsync("Настройки"));
            settings.AutomationId = "input-key-settings";
            _body.Children.Add(Notice(AppIconKind.Key, Ui.Warning,
                "Чтобы разобрать текст или голос, добавьте личный ключ Groq. Без ключа можно выбрать продукт или блюдо ниже.", settings));
        }

        _body.Children.Add(InputComposer(profileId, editor, count, submit, _session is null ? null : "Уточнение"));
        _body.Children.Add(_inputSaveRetry);
        _body.Children.Add(alternatives);
        RefreshPurpose();
        if (_session is not null)
        {
            Button restart = Ui.Ghost("Начать запись заново", () => RunAsync(async () =>
            {
                if (InputDraftBlocked)
                {
                    throw new InvalidOperationException("Сначала восстановите или удалите сохранённый ввод.");
                }

                await FlushInputDraftAsync();
                if (await DisplayAlertAsync("Начать заново?", "Текущий расчёт не подтверждён и будет закрыт. Дневник не изменится.", "Начать заново", "Оставить"))
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
            }), Ui.Danger);
            restart.AutomationId = "draft-restart";
            restart.HorizontalOptions = LayoutOptions.Center;
            _body.Children.Add(restart);
        }
    }

    private static string DiaryDay(DateOnly date)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        return date == today ? $"сегодня, {date.ToString("d MMMM", DiaryCulture)}"
            : date == today.AddDays(-1) ? $"вчера, {date.ToString("d MMMM", DiaryCulture)}"
            : date.ToString("d MMMM", DiaryCulture);
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
                await ProductEditorPageAsync(profileId, reason: "В каталоге пока нет продуктов. Добавьте первый, затем выберите его и укажите вес порции.");
                return;
            }

            DateOnly mealDate = _date;
            FormPage page = new("Продукт в дневник");
            Picker product = new InputPicker() { Title = "Продукт", ItemsSource = products.ToList(), ItemDisplayBinding = new Binding(nameof(ProductResponse.Name)), SelectedIndex = 0 };
            SemanticProperties.SetDescription(product, "Продукт");
            page.Fields.Children.Add(Ui.Labeled("Продукт", Ui.PickerField(product)));
            Entry grams = AddNumber(page, "Вес порции, г");
            Switch estimated = new();
            page.Fields.Children.Add(Ui.SwitchRow("Вес указан примерно", estimated));
            page.AddFooter(Ui.Action("Посмотреть расчёт", () => page.RunAsync(async () =>
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
            await DisplayAlertAsync("Сохранённый ввод", "Сначала восстановите или удалите сохранённый текст в разделе записи еды.", "Понятно");
            return false;
        }

        await FlushInputDraftAsync();
        if (_session is null && string.IsNullOrWhiteSpace(_draftText))
        {
            return true;
        }

        return await DisplayAlertAsync("Создать новый расчёт?", "Текущий расчёт и неотправленный текст будут заменены, только когда новый расчёт будет готов.", "Создать", "Отмена");
    }
}
