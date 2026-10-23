using System.Globalization;
using Microsoft.Maui.Controls.Shapes;
using NutriFlow.Api.Contracts;

namespace NutriFlow.Mobile;

public partial class MainPage
{
    private static readonly CultureInfo DiaryCulture = CultureInfo.GetCultureInfo("ru-RU");

    private async Task RenderDiaryAsync(Guid profileId)
    {
        DateOnly selectedDate = _date;
        DailyProgressResponse progress = await _client.GetDailyProgressAsync(profileId, selectedDate);
        _body.Children.Add(DiaryDateNavigation(profileId, selectedDate));
        _body.Children.Add(DiarySummary(profileId, selectedDate, progress));

        if (progress.Entries.Count == 0)
        {
            Button log = Ui.Action("Записать еду", () => SelectTabAsync("Ввод"));
            log.AutomationId = "diary-empty-add";
            _body.Children.Add(Ui.EmptyState(AppIconKind.Diary, "Пока пусто",
                "Опишите, что съели, — текстом, голосом или выбрав продукт из каталога.", log, "diary-empty"));
            return;
        }

        decimal entriesCalories = progress.Entries.Sum(entry => entry.Nutrition.Calories);
        _body.Children.Add(SectionHeader("Записи", $"{Plural(progress.Entries.Count, "запись", "записи", "записей")} · {Ui.Kilocalories(entriesCalories)} ккал"));
        _body.Children.Add(Ui.List(progress.Entries.Select(entry => DiaryEntryRow(profileId, entry))));
    }

    private static View SectionHeader(string title, string? detail = null)
    {
        Grid header = new()
        {
            Margin = new Thickness(4, 8, 4, -4),
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } }
        };
        header.Add(Ui.Overline(title), 0);
        if (detail is not null)
        {
            Label caption = Ui.Caption(detail, Ui.Subtle);
            caption.FontSize = 12;
            caption.VerticalOptions = LayoutOptions.Center;
            header.Add(caption, 1);
        }

        return header;
    }

    private View DiaryDateNavigation(Guid profileId, DateOnly selectedDate)
    {
        DatePicker picker = new() { Date = selectedDate.ToDateTime(TimeOnly.MinValue), Opacity = 0, InputTransparent = true };
        AutomationProperties.SetIsInAccessibleTree(picker, false);
        picker.DateSelected += async (_, _) =>
        {
            DateOnly date = DateOnly.FromDateTime(picker.Date ?? DateTime.Today);
            if (date != _date)
            {
                await ChangeDiaryDateAsync(profileId, date);
            }
        };
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        string dateText = selectedDate == today ? $"Сегодня, {selectedDate.ToString("d MMMM", DiaryCulture)}"
            : selectedDate == today.AddDays(-1) ? $"Вчера, {selectedDate.ToString("d MMMM", DiaryCulture)}"
            : selectedDate.ToString("ddd, d MMMM", DiaryCulture);
        Button chooseDate = new()
        {
            Text = dateText,
            AutomationId = "diary-date",
            FontSize = 16,
            FontFamily = Ui.MediumFont,
            FontAutoScalingEnabled = true,
            TextColor = Ui.TextPrimary,
            BackgroundColor = Colors.Transparent,
            Padding = new Thickness(8, 6),
            MinimumHeightRequest = 48
        };
        Ui.ApplyStates(chooseDate, Ui.SurfaceRaised, Ui.TextPrimary);
        chooseDate.Clicked += (_, _) =>
        {
            picker.Unfocus();
            picker.Focus();
        };
        SemanticProperties.SetDescription(chooseDate, $"Выбрать дату. {selectedDate.ToString("D", DiaryCulture)}");
        Grid dateControl = new();
        dateControl.Add(picker);
        dateControl.Add(chooseDate);
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } }
        };
        row.Add(Ui.IconButton(AppIconKind.Previous, "Предыдущий день", () => ChangeDiaryDateAsync(profileId, selectedDate.AddDays(-1)), Ui.Muted), 0);
        row.Add(dateControl, 1);
        row.Add(Ui.IconButton(AppIconKind.Next, "Следующий день", () => ChangeDiaryDateAsync(profileId, selectedDate.AddDays(1)), Ui.Muted), 2);
        return new Border
        {
            Content = row,
            BackgroundColor = Ui.Surface,
            Stroke = Ui.Stroke,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 26 }
        };
    }

    private Task ChangeDiaryDateAsync(Guid profileId, DateOnly date) => RunAsync(async () =>
    {
        if (_profile?.Id != profileId)
        {
            return;
        }

        if (!InputDraftBlocked && !string.IsNullOrWhiteSpace(_draftText) && _session is null)
        {
            await QueueInputDraftSave(profileId, CaptureInputDraft() with { MealDate = date });
            await FlushInputDraftAsync();
        }

        _date = date;
        await RenderAsync();
    });

    private View DiarySummary(Guid profileId, DateOnly date, DailyProgressResponse progress)
    {
        NutritionResponse consumed = progress.Consumed;
        NutritionResponse? goal = progress.Goal is { Calories: > 0m } ? progress.Goal : null;

        Grid hero = new() { AutomationId = "diary-summary" };
        hero.Add(new NutritionArcView(consumed, goal));
        Label headline = new()
        {
            FontSize = 44,
            FontAttributes = FontAttributes.Bold,
            CharacterSpacing = -1.6,
            FontAutoScalingEnabled = false,
            HorizontalTextAlignment = TextAlignment.Center,
            TextColor = Ui.TextPrimary
        };
        Label caption = Ui.Caption("");
        caption.HorizontalTextAlignment = TextAlignment.Center;
        if (goal is null)
        {
            headline.Text = Ui.Kilocalories(consumed.Calories);
            caption.Text = "ккал съедено";
        }
        else if (consumed.Calories <= goal.Calories)
        {
            headline.Text = Ui.Kilocalories(goal.Calories - consumed.Calories);
            caption.Text = "ккал осталось";
        }
        else
        {
            headline.Text = $"+{Ui.Kilocalories(consumed.Calories - goal.Calories)}";
            headline.TextColor = Ui.Danger;
            caption.Text = "ккал сверх цели";
        }

        SemanticProperties.SetDescription(headline, $"{headline.Text} {caption.Text}");
        AutomationProperties.SetIsInAccessibleTree(caption, false);
        VerticalStackLayout center = new() { Spacing = 0, VerticalOptions = LayoutOptions.End, HorizontalOptions = LayoutOptions.Center };
        center.Children.Add(headline);
        center.Children.Add(caption);
        hero.Add(center);

        Grid stats = new()
        {
            ColumnSpacing = 10,
            ColumnDefinitions = new ColumnDefinitionCollection { new(), new() }
        };
        // without a goal the arc centre already shows what was eaten, so the goal prompt takes the whole row
        Label eaten = goal is null ? headline : Ui.Strong(Ui.Kilocalories(consumed.Calories), 20);
        eaten.AutomationId = "diary-calories";
        SemanticProperties.SetDescription(eaten, $"Съедено {Ui.Amount(consumed.Calories)} килокалорий");
        if (goal is not null)
        {
            eaten.FontAttributes = FontAttributes.Bold;
            stats.Add(StatTile("Съедено", eaten, "ккал"), 0);
        }

        Label target = Ui.Strong(goal is null ? "Задать цель" : Ui.Kilocalories(goal.Calories), goal is null ? 17 : 20);
        target.FontAttributes = FontAttributes.Bold;
        target.TextColor = goal is null ? Ui.Accent : Ui.TextPrimary;
        View goalTile = goal is null
            ? StatTile("Сколько осталось?", target, null, AppIconKind.Target)
            : StatTile("Цель", target, "ккал", AppIconKind.Edit);
        View goalAction = Ui.Pressable(goalTile, () => EditGoalPageAsync(profileId, date, progress.Goal),
            goal is null ? "Цель не задана. Задать цель" : $"Цель {Ui.Amount(goal.Calories)} килокалорий. Изменить цель",
            "Калории и доли белков, жиров и углеводов", "diary-goal");
        stats.Add(goalAction, goal is null ? 0 : 1);
        if (goal is null)
        {
            Grid.SetColumnSpan(goalAction, 2);
        }

        VerticalStackLayout macros = new() { Spacing = 14 };
        macros.Children.Add(MacroProgress("Белки", consumed.ProteinGrams, goal?.ProteinGrams, Ui.Protein));
        macros.Children.Add(MacroProgress("Жиры", consumed.FatGrams, goal?.FatGrams, Ui.Fat));
        macros.Children.Add(MacroProgress("Углеводы", consumed.CarbohydratesGrams, goal?.CarbohydratesGrams, Ui.Carbs));

        VerticalStackLayout content = new() { Spacing = 16 };
        content.Children.Add(hero);
        content.Children.Add(stats);
        content.Children.Add(Ui.Divider());
        content.Children.Add(macros);
        return Ui.Panel(content, 18);
    }

    private static View StatTile(string title, Label value, string? unit, AppIconKind? icon = null)
    {
        HorizontalStackLayout amount = new() { Spacing = 5 };
        amount.Children.Add(value);
        if (unit is not null)
        {
            Label unitLabel = Ui.Caption(unit);
            unitLabel.VerticalOptions = LayoutOptions.End;
            unitLabel.Margin = new Thickness(0, 0, 0, 2);
            AutomationProperties.SetIsInAccessibleTree(unitLabel, false);
            amount.Children.Add(unitLabel);
        }

        VerticalStackLayout text = new() { Spacing = 2 };
        Label overline = Ui.Overline(title);
        // the value carries the whole spoken description, so the caption and unit would only repeat it
        AutomationProperties.SetIsInAccessibleTree(overline, false);
        text.Children.Add(overline);
        text.Children.Add(amount);
        Grid tile = new()
        {
            Padding = new Thickness(14, 10),
            ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } }
        };
        tile.Add(text, 0);
        if (icon is AppIconKind kind)
        {
            tile.Add(new AppIcon(kind, Ui.Subtle, 18) { VerticalOptions = LayoutOptions.Center }, 1);
        }

        return new Border
        {
            Content = tile,
            BackgroundColor = Ui.SurfaceRaised.WithAlpha(0.55f),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 16 }
        };
    }

    private static View MacroProgress(string name, decimal consumed, decimal? goal, Color color)
    {
        Grid caption = new()
        {
            ColumnSpacing = 8,
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Auto }, new() { Width = GridLength.Star }, new() { Width = GridLength.Auto }
            }
        };
        caption.Add(new Ellipse { Fill = color, WidthRequest = 8, HeightRequest = 8, VerticalOptions = LayoutOptions.Center }, 0);
        Label label = Ui.Text(name);
        label.VerticalOptions = LayoutOptions.Center;
        AutomationProperties.SetIsInAccessibleTree(label, false);
        caption.Add(label, 1);

        Label amount = new() { FontSize = 14, FontAutoScalingEnabled = true, FormattedText = new FormattedString(), VerticalOptions = LayoutOptions.Center };
        amount.FormattedText.Spans.Add(new Span { Text = Ui.MacroAmount(consumed), TextColor = Ui.TextPrimary, FontFamily = Ui.MediumFont });
        amount.FormattedText.Spans.Add(new Span { Text = goal is null ? " г" : $" / {Ui.MacroAmount(goal.Value)} г", TextColor = Ui.Muted });
        string description = $"{name}: съедено {Ui.MacroAmount(consumed)} граммов. " +
            (goal is null ? "Цель не задана" : $"Цель {Ui.MacroAmount(goal.Value)} граммов");
        if (goal is not null && consumed > goal.Value)
        {
            string excess = Ui.MacroAmount(consumed - goal.Value);
            if (excess != "0")
            {
                amount.FormattedText.Spans.Add(new Span { Text = $"  +{excess}", TextColor = Ui.Danger, FontFamily = Ui.MediumFont });
                description += $". Превышение на {excess} граммов";
            }
        }

        SemanticProperties.SetDescription(amount, description);
        caption.Add(amount, 2);
        VerticalStackLayout row = new() { Spacing = 8 };
        row.Children.Add(caption);
        if (goal is not null)
        {
            Border bar = Ui.ProgressBar(consumed, goal, color);
            AutomationProperties.SetIsInAccessibleTree(bar, false);
            row.Children.Add(bar);
        }

        return row;
    }

    private View DiaryEntryRow(Guid profileId, MealEntryResponse entry)
    {
        string qualityMark = entry.Quality switch { "Estimated" => "≈ ", "Exact" or "Verified" => "", _ => "? " };
        return Ui.Row(entry.Name, null, $"{qualityMark}{Ui.Kilocalories(entry.Nutrition.Calories)} ккал", $"{Ui.Amount(entry.WeightInGrams)} г",
            () => EntryActionsAsync(profileId, entry),
            $"{entry.Name}. {Ui.Amount(entry.WeightInGrams)} граммов. {Ui.SpokenNutrition(entry.Nutrition)}. {Ui.Quality(entry.Quality)}",
            subtitleSpans: Ui.MacroSpans(entry.Nutrition), hint: "Изменить вес или удалить запись");
    }

    private async Task EntryActionsAsync(Guid profileId, MealEntryResponse entry)
    {
        if (Busy)
        {
            return;
        }

        string? action = await DisplayActionSheetAsync($"{entry.Name} · {Ui.Amount(entry.WeightInGrams)} г\n{Ui.Quality(entry.Quality)}", "Отмена", "Удалить", "Изменить вес");
        if (_profile?.Id != profileId)
        {
            return;
        }

        if (action == "Изменить вес")
        {
            await EditEntryPageAsync(profileId, entry);
        }
        else if (action == "Удалить")
        {
            await RunAsync(async () =>
            {
                if (await DisplayAlertAsync("Удалить запись?", $"{entry.Name}, {Ui.Amount(entry.WeightInGrams)} г. Это нельзя отменить.", "Удалить", "Оставить"))
                {
                    await _client.DeleteEntryAsync(profileId, entry.Id, entry.Revision);
                    await RenderAsync();
                }
            });
        }
    }
}
