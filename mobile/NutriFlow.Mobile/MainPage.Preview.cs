using Microsoft.Maui.Controls.Shapes;
using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private void RenderSession(Guid profileId, MealSessionResponse session)
    {
        _body.Children.Add(PreviewStatus(session));
        RenderPreviewQuestions(session);

        foreach (DishPreviewResponse dish in session.Dishes)
        {
            _body.Children.Add(DishPreviewCard(profileId, dish));
        }

        if (session.Dishes.Count == 0)
        {
            _body.Children.Add(Ui.EmptyState(AppIconKind.Sparkle, "Состав пока не определён",
                "Добавьте описание еды ниже или ответьте на вопросы."));
        }

        bool missingProducts = session.Dishes.Any(dish => dish.Ingredients.Any(ingredient => ingredient.ResolvedProduct is null));
        if (missingProducts)
        {
            Button refresh = Ui.Ghost("Обновить расчёт после добавления продуктов", () => RunAsync(async () =>
            {
                _session = await _client.FindSessionAsync(profileId, session.Id)
                    ?? throw new InvalidOperationException("Расчёт не найден. Начните новую запись.");
                await RenderAsync();
                await _scroll.ScrollToAsync(0, 0, false);
            }));
            refresh.AutomationId = "draft-refresh";
            _body.Children.Add(refresh);
        }

        if (session.Messages.Count > 0)
        {
            _body.Children.Add(PreviewHistory(session.Messages));
        }

        SetActionBar(ConfirmButton(profileId, session));
    }

    private Button ConfirmButton(Guid profileId, MealSessionResponse session)
    {
        bool dish = session.Purpose == "CreateDish";
        Button confirm = Ui.Action(dish ? "Сохранить блюдо" : "Записать в дневник", () => RunAsync(async () =>
        {
            await FlushInputDraftAsync();
            if (!await CanDiscardInputAsync("Подтвердить без нового текста?", "Набранное уточнение ещё не отправлено и не входит в этот расчёт. После подтверждения оно будет удалено."))
            {
                return;
            }

            ConfirmMealSessionResponse result = await _client.ConfirmAsync(profileId, session.Id, session.PreviewToken)
                ?? throw new InvalidOperationException("Расчёт не найден. Начните новую запись.");
            _session = result.Session;
            if (result.Session.Status == "Confirmed")
            {
                _date = result.Session.MealDate;
                ProfileSettings.ClearSession(profileId);
                _session = null;
                await ClearSubmittedInputAsync(profileId);
                _tab = result.SavedDish is null ? "Дневник" : "Блюда";
                await RenderAsync();
                // the diary with the new entry is already on screen, so only a dish or a draft problem needs a dialog
                if (result.SavedDish is null && _draftReadError is null)
                {
                    SemanticScreenReader.Announce("Запись добавлена в дневник");
                    return;
                }

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
                await DisplayAlertAsync("Проверьте расчёт", result.Message ?? "Перед подтверждением нужны уточнения. Проверьте обновлённый расчёт.", "Понятно");
            }
        }));
        confirm.AutomationId = "draft-confirm";
        confirm.MinimumHeightRequest = 56;
        SemanticProperties.SetHint(confirm, dish ? "Подтвердить расчёт и сохранить блюдо" : "Подтвердить расчёт и добавить запись в дневник");
        Ui.SetEnabled(confirm, session.CanConfirm);
        return confirm;
    }

    private static View PreviewStatus(MealSessionResponse session)
    {
        (Color color, AppIconKind icon, string title) = session.Status switch
        {
            "ReadyForConfirmation" => (Ui.Success, AppIconKind.Check, "Готово к подтверждению"),
            "NeedsClarification" => (Ui.Warning, AppIconKind.Warning, "Нужны уточнения"),
            "NeedsProducts" => (Ui.Warning, AppIconKind.Warning, "Не хватает пищевой ценности"),
            "Confirmed" => (Ui.Muted, AppIconKind.Check, "Уже подтверждено"),
            _ => (Ui.Warning, AppIconKind.Warning, "Проверьте расчёт")
        };
        string hint = session.Status == "Confirmed"
            ? "Этот расчёт уже подтверждён."
            : session.CanConfirm
                ? session.Purpose == "CreateDish" ? "Проверьте состав и готовый вес. Блюдо сохранится только после подтверждения."
                    : "Проверьте состав и порцию. До подтверждения запись не попадёт в дневник."
                : "Ответьте на вопросы в поле уточнения или добавьте недостающие продукты.";
        string context = session.Purpose == "CreateDish" ? "Своё блюдо" : $"В дневник · {DiaryDay(session.MealDate)}";

        Label status = Ui.Strong(title, 17);
        status.AutomationId = "draft-status";
        status.TextColor = color;
        VerticalStackLayout text = new() { Spacing = 4 };
        text.Children.Add(Ui.Overline(context));
        text.Children.Add(status);
        text.Children.Add(Ui.Caption(hint));
        Grid content = new()
        {
            ColumnSpacing = 14,
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star } }
        };
        content.Add(Ui.IconTile(icon, color, 40), 0);
        content.Add(text, 1);
        return new Border
        {
            Content = content,
            Padding = 16,
            BackgroundColor = color.WithAlpha(0.07f),
            Stroke = color.WithAlpha(0.3f),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = Ui.CardRadius }
        };
    }

    private void RenderPreviewQuestions(MealSessionResponse session)
    {
        if (session.ClarificationQuestions.Count == 0 && session.Issues.Count == 0)
        {
            return;
        }

        VerticalStackLayout content = new() { Spacing = 14 };
        VerticalStackLayout questions = new() { Spacing = 12, AutomationId = "draft-questions" };
        if (session.ClarificationQuestions.Count > 0)
        {
            content.Children.Add(Ui.Overline("Ответьте в поле уточнения"));
        }

        for (int index = 0; index < session.ClarificationQuestions.Count; index++)
        {
            Grid question = new()
            {
                ColumnSpacing = 12,
                ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star } }
            };
            Label number = new()
            {
                Text = (index + 1).ToString(DiaryCulture),
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                TextColor = Ui.Warning,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center
            };
            question.Add(new Border
            {
                WidthRequest = 26,
                HeightRequest = 26,
                VerticalOptions = LayoutOptions.Start,
                BackgroundColor = Ui.Warning.WithAlpha(0.15f),
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 13 },
                Content = number
            }, 0);
            question.Add(Ui.Text(session.ClarificationQuestions[index]), 1);
            questions.Children.Add(question);
        }

        content.Children.Add(questions);
        VerticalStackLayout issues = new() { Spacing = 12, AutomationId = "draft-issues" };
        foreach (WorkflowIssueResponse issue in session.Issues)
        {
            VerticalStackLayout details = new() { Spacing = 3 };
            details.Children.Add(Ui.Strong($"{issue.DishName}{(issue.IngredientName is null ? "" : $" · {issue.IngredientName}")}", 15));
            details.Children.Add(Ui.Caption(issue.Message));
            issues.Children.Add(details);
        }

        if (session.Issues.Count > 0)
        {
            content.Children.Add(Ui.Overline("Что проверить"));
        }

        content.Children.Add(issues);
        _body.Children.Add(Ui.Card(content));
    }

    private static View PreviewHistory(IReadOnlyList<string> messages)
    {
        VerticalStackLayout history = new() { Spacing = 12, AutomationId = "draft-history" };
        for (int index = 0; index < messages.Count; index++)
        {
            VerticalStackLayout message = new() { Spacing = 3 };
            message.Children.Add(Ui.Overline(index == 0 ? "Описание" : $"Уточнение {index}"));
            message.Children.Add(Ui.Text(messages[index], true));
            history.Children.Add(message);
        }

        return Ui.Card(Ui.Expander($"История ввода · {messages.Count}", history, "draft-history-toggle"));
    }
}
