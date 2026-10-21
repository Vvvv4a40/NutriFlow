using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private void RenderSession(Guid profileId, MealSessionResponse session)
    {
        Label title = Ui.Title(session.Purpose == "CreateDish" ? "Проверьте блюдо" : "Проверьте запись");
        title.FontSize = 28;
        title.Margin = 0;
        SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level1);
        _body.Children.Add(title);
        _body.Children.Add(PreviewStatus(session));
        RenderPreviewQuestions(session);

        foreach (DishPreviewResponse dish in session.Dishes)
        {
            _body.Children.Add(DishPreviewCard(profileId, dish));
        }

        if (session.Dishes.Count == 0)
        {
            _body.Children.Add(Ui.Card(Ui.Text("Состав пока не определён."),
                PreviewNote("Добавьте описание еды или ответьте на вопросы.")));
        }

        if (!session.CanConfirm && session.Status != "Confirmed")
        {
            _body.Children.Add(PreviewNote("Подтверждение станет доступно после уточнений и заполнения пищевой ценности."));
        }

        Button confirm = Ui.Action(session.Purpose == "CreateDish" ? "Подтвердить и сохранить блюдо" : "Подтвердить и записать в дневник", () => RunAsync(async () =>
        {
            await FlushInputDraftAsync();
            if (!await CanDiscardInputAsync("Подтвердить без нового текста?", "Набранное уточнение ещё не отправлено и не входит в этот расчёт. После подтверждения оно будет удалено."))
            {
                return;
            }

            ConfirmMealSessionResponse result = await _client.ConfirmAsync(profileId, session.Id, session.PreviewToken)
                ?? throw new InvalidOperationException("Черновик не найден.");
            _session = result.Session;
            if (result.Session.Status == "Confirmed")
            {
                _date = result.Session.MealDate;
                ProfileSettings.ClearSession(profileId);
                _session = null;
                await ClearSubmittedInputAsync(profileId);
                _tab = result.SavedDish is null ? "Дневник" : "Блюда";
                await RenderAsync();
                string message = result.SavedDish is null ? "Запись добавлена в дневник." : "Блюдо сохранено. Его порции можно добавлять без интернета.";
                if (_draftReadError is not null)
                {
                    message += "\n" + _draftReadError;
                }

                await DisplayAlertAsync("Сохранено", message, "Готово");
            }
            else
            {
                await RenderAsync();
                await DisplayAlertAsync("Проверьте черновик", result.Message ?? "Перед подтверждением нужны уточнения. Проверьте обновлённый расчёт.", "Понятно");
            }
        }));
        confirm.AutomationId = "draft-confirm";
        confirm.MinimumHeightRequest = 56;
        confirm.IsEnabled = session.CanConfirm;
        if (!session.CanConfirm)
        {
            confirm.BackgroundColor = Ui.SurfaceRaised;
            confirm.TextColor = Ui.Muted;
        }
        _body.Children.Add(confirm);

        Button refresh = Ui.Action("Обновить расчёт после добавления продуктов", () => RunAsync(async () =>
        {
            _session = await _client.FindSessionAsync(profileId, session.Id)
                ?? throw new InvalidOperationException("Черновик не найден.");
            await RenderAsync();
            await _scroll.ScrollToAsync(0, 0, false);
        }), true);
        refresh.AutomationId = "draft-refresh";
        _body.Children.Add(refresh);
        _body.Children.Add(PreviewHistory(session.Messages));
    }

    private static View PreviewStatus(MealSessionResponse session)
    {
        Label context = PreviewNote(session.Purpose == "CreateDish"
            ? "СОБСТВЕННОЕ БЛЮДО"
            : $"В ДНЕВНИК · {session.MealDate:dd.MM.yyyy}");
        context.CharacterSpacing = 1;
        Label status = Ui.Text(session.Status switch
        {
            "ReadyForConfirmation" => "Готов к подтверждению",
            "NeedsClarification" => "Нужны уточнения",
            "NeedsProducts" => "Нужна пищевая ценность",
            "Confirmed" => "Уже подтверждён",
            _ => "Проверьте черновик"
        });
        status.AutomationId = "draft-status";
        status.FontSize = 18;
        status.FontAttributes = FontAttributes.Bold;
        status.TextColor = session.CanConfirm ? Ui.Accent : Ui.Fat;
        Label hint = PreviewNote(session.Status == "Confirmed"
            ? "Этот черновик уже подтверждён."
            : session.CanConfirm
                ? session.Purpose == "CreateDish" ? "Проверьте состав. Блюдо сохранится только после подтверждения."
                    : "Проверьте состав и порцию. До подтверждения запись не попадёт в дневник."
                : "Проверьте вопросы и недостающие данные. Ответы можно добавить в поле уточнения.");
        return Ui.Card(context, status, hint);
    }

    private void RenderPreviewQuestions(MealSessionResponse session)
    {
        if (session.ClarificationQuestions.Count == 0 && session.Issues.Count == 0)
        {
            return;
        }

        VerticalStackLayout content = new() { Spacing = 12 };
        Label title = PreviewHeading("Что нужно проверить");
        title.TextColor = Ui.Fat;
        content.Children.Add(title);
        VerticalStackLayout questions = new() { Spacing = 10, AutomationId = "draft-questions" };
        for (int index = 0; index < session.ClarificationQuestions.Count; index++)
        {
            questions.Children.Add(Ui.Text($"{index + 1}. {session.ClarificationQuestions[index]}"));
        }
        content.Children.Add(questions);
        VerticalStackLayout issues = new() { Spacing = 10, AutomationId = "draft-issues" };
        foreach (WorkflowIssueResponse issue in session.Issues)
        {
            Label context = Ui.Text($"{issue.DishName}{(issue.IngredientName is null ? "" : $" · {issue.IngredientName}")}");
            context.FontSize = 13;
            context.FontAttributes = FontAttributes.Bold;
            VerticalStackLayout details = new() { Spacing = 4 };
            details.Children.Add(context);
            details.Children.Add(PreviewNote(issue.Message));
            issues.Children.Add(details);
        }
        content.Children.Add(issues);
        _body.Children.Add(Ui.Card(content));
    }

    private static View PreviewHistory(IReadOnlyList<string> messages)
    {
        VerticalStackLayout history = new() { Spacing = 10, IsVisible = false, AutomationId = "draft-history" };
        for (int index = 0; index < messages.Count; index++)
        {
            history.Children.Add(PreviewHeading($"Сообщение {index + 1}"));
            history.Children.Add(Ui.Text(messages[index], true));
        }

        Button toggle = new()
        {
            Text = $"Показать историю ввода · {messages.Count}",
            AutomationId = "draft-history-toggle",
            BackgroundColor = Ui.SurfaceRaised,
            TextColor = Ui.TextPrimary
        };
        toggle.Clicked += (_, _) =>
        {
            history.IsVisible = !history.IsVisible;
            toggle.Text = history.IsVisible ? "Скрыть историю ввода" : $"Показать историю ввода · {messages.Count}";
        };
        return Ui.Card(toggle, history);
    }
}
