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
        _body.Children.Add(NutritionSummary(progress));
        _body.Children.Add(DiaryArcLegend(profileId, selectedDate, progress));

        Grid macros = new() { ColumnSpacing = 8, ColumnDefinitions = new ColumnDefinitionCollection { new(), new(), new() } };
        macros.Add(MacroTile("Белки", progress.Consumed.ProteinGrams, progress.Goal?.ProteinGrams, Ui.Protein), 0);
        macros.Add(MacroTile("Жиры", progress.Consumed.FatGrams, progress.Goal?.FatGrams, Ui.Fat), 1);
        macros.Add(MacroTile("Углеводы", progress.Consumed.CarbohydratesGrams, progress.Goal?.CarbohydratesGrams, Ui.Carbs), 2);
        _body.Children.Add(macros);

        if (progress.Entries.Count == 0)
        {
            return;
        }

        VerticalStackLayout entries = new() { Spacing = 0 };
        for (int index = 0; index < progress.Entries.Count; index++)
        {
            if (index > 0)
            {
                entries.Children.Add(new BoxView { HeightRequest = 1, Color = Ui.SurfaceRaised, Margin = new Thickness(16, 0) });
            }

            entries.Children.Add(DiaryEntryRow(profileId, progress.Entries[index]));
        }

        _body.Children.Add(new Border
        {
            Content = entries,
            BackgroundColor = Ui.Surface,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 24 }
        });
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
        string dateText = selectedDate == DateOnly.FromDateTime(DateTime.Today)
            ? $"Сегодня, {selectedDate.ToString("d MMM", DiaryCulture)}"
            : selectedDate.ToString("ddd, d MMM", DiaryCulture);
        Button chooseDate = Ui.Action(dateText, () => { picker.Unfocus(); picker.Focus(); return Task.CompletedTask; }, true);
        chooseDate.AutomationId = "diary-date";
        chooseDate.BackgroundColor = Colors.Transparent;
        chooseDate.FontSize = 17;
        chooseDate.Padding = new Thickness(4, 8);
        SemanticProperties.SetDescription(chooseDate, $"Выбрать дату. {selectedDate.ToString("D", DiaryCulture)}");
        Grid dateControl = new();
        dateControl.Add(picker);
        dateControl.Add(chooseDate);
        Grid row = new() { ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } } };
        row.Add(IconAction(AppIconKind.Previous, "Предыдущий день", () => ChangeDiaryDateAsync(profileId, selectedDate.AddDays(-1))), 0);
        row.Add(dateControl, 1);
        row.Add(IconAction(AppIconKind.Next, "Следующий день", () => ChangeDiaryDateAsync(profileId, selectedDate.AddDays(1))), 2);
        return row;
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

    private static View NutritionSummary(DailyProgressResponse progress)
    {
        Grid hero = new()
        {
            AutomationId = "diary-summary",
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new() { Width = GridLength.Star }, new() { Width = new GridLength(3, GridUnitType.Star) }, new() { Width = GridLength.Star }
            },
            RowDefinitions = new RowDefinitionCollection { new() { Height = GridLength.Star } },
            HeightRequest = 210
        };
        NutritionArcView arc = new(progress.Consumed, progress.Goal) { HeightRequest = 210 };
        hero.Add(arc, 0, 0);
        Grid.SetColumnSpan(arc, 3);
        Label calories = Ui.Title(progress.Consumed.Calories.ToString("N0", DiaryCulture));
        calories.AutomationId = "diary-calories";
        calories.FontSize = 38;
        calories.CharacterSpacing = -1.5;
        calories.Margin = 0;
        calories.HorizontalTextAlignment = TextAlignment.Center;
        calories.TextColor = progress.Exceeded?.Calories > 0 ? Ui.Carbs : Ui.TextPrimary;
        SemanticProperties.SetDescription(calories, $"Съедено {Ui.Amount(progress.Consumed.Calories)} килокалорий");
        Label target = Ui.Text(progress.Goal is null ? "ккал" : $"из {Ui.Amount(progress.Goal.Calories)} ккал", true);
        target.FontSize = 12;
        target.HorizontalTextAlignment = TextAlignment.Center;
        VerticalStackLayout center = new() { Spacing = 2, VerticalOptions = LayoutOptions.End, Margin = new Thickness(0, 0, 0, 4) };
        center.Children.Add(calories);
        center.Children.Add(target);
        hero.Add(center, 1, 0);
        return hero;
    }

    private View DiaryArcLegend(Guid profileId, DateOnly date, DailyProgressResponse progress)
    {
        Button goal = Ui.Action("Цель · внешняя", () => EditGoalPageAsync(profileId, date, progress.Goal), true);
        goal.AutomationId = "diary-goal";
        goal.BackgroundColor = Colors.Transparent;
        goal.FontSize = 12;
        goal.Padding = new Thickness(4, 8);
        SemanticProperties.SetDescription(goal, progress.Goal is null ? "Цель не задана. Задать цель" : "Внешняя дуга: цель. Изменить цель");
        SemanticProperties.SetHint(goal, "Длины сегментов показывают доли БЖУ в граммах, не процент выполнения");
        Label consumed = Ui.Text("Съедено · внутренняя", true);
        consumed.FontSize = 12;
        consumed.VerticalOptions = LayoutOptions.Center;
        consumed.HorizontalTextAlignment = TextAlignment.End;
        Grid legend = new() { ColumnSpacing = 8, ColumnDefinitions = new ColumnDefinitionCollection { new(), new() } };
        legend.Add(goal, 0);
        legend.Add(consumed, 1);
        return legend;
    }

    private static View MacroTile(string name, decimal consumed, decimal? goal, Color color)
    {
        Label label = Ui.Text(name);
        label.FontSize = 12;
        label.TextColor = color;
        Label amount = Ui.Title($"{Ui.MacroAmount(consumed)} г");
        amount.FontSize = 20;
        amount.Margin = new Thickness(0, 2, 0, 0);
        Label target = Ui.Text(goal is null ? "цель —" : $"из {Ui.MacroAmount(goal.Value)} г", true);
        target.FontSize = 12;
        SemanticProperties.SetDescription(amount, $"{name}: съедено {Ui.MacroAmount(consumed)} граммов. " +
            (goal is null ? "Цель не задана" : $"Цель {Ui.MacroAmount(goal.Value)} граммов"));
        VerticalStackLayout values = new() { Spacing = 4, Padding = new Thickness(4, 0) };
        values.Children.Add(label);
        values.Children.Add(amount);
        values.Children.Add(target);
        if (goal is not null && consumed > goal.Value)
        {
            string excessAmount = Ui.MacroAmount(consumed - goal.Value);
            if (excessAmount != "0")
            {
                Label excess = Ui.Text($"+{excessAmount} г", true);
                excess.FontSize = 11;
                SemanticProperties.SetDescription(excess, $"Превышение цели по {name.ToLower(DiaryCulture)}: {excessAmount} граммов");
                values.Children.Add(excess);
            }
        }

        return values;
    }

    private View DiaryEntryRow(Guid profileId, MealEntryResponse entry)
    {
        Label name = Ui.Text(entry.Name);
        name.FontSize = 16;
        name.FontAttributes = FontAttributes.Bold;
        Label nutrition = new() { FontSize = 12, FormattedText = new FormattedString() };
        nutrition.FormattedText.Spans.Add(new Span { Text = $"Б {Ui.MacroAmount(entry.Nutrition.ProteinGrams)}", TextColor = Ui.Protein });
        nutrition.FormattedText.Spans.Add(new Span { Text = "  ·  ", TextColor = Ui.Muted });
        nutrition.FormattedText.Spans.Add(new Span { Text = $"Ж {Ui.MacroAmount(entry.Nutrition.FatGrams)}", TextColor = Ui.Fat });
        nutrition.FormattedText.Spans.Add(new Span { Text = "  ·  ", TextColor = Ui.Muted });
        nutrition.FormattedText.Spans.Add(new Span { Text = $"У {Ui.MacroAmount(entry.Nutrition.CarbohydratesGrams)}", TextColor = Ui.Carbs });
        VerticalStackLayout left = new() { Spacing = 6 };
        left.Children.Add(name);
        left.Children.Add(nutrition);
        Label weight = Ui.Text($"{Ui.Amount(entry.WeightInGrams)} г");
        weight.FontSize = 15;
        weight.HorizontalTextAlignment = TextAlignment.End;
        string qualityMark = entry.Quality switch { "Estimated" => "≈ ", "Exact" or "Verified" => "", _ => "? " };
        Label calories = Ui.Text($"{qualityMark}{Ui.Amount(entry.Nutrition.Calories)} ккал", true);
        calories.FontSize = 12;
        calories.HorizontalTextAlignment = TextAlignment.End;
        VerticalStackLayout right = new() { Spacing = 6, MaximumWidthRequest = 100 };
        right.Children.Add(weight);
        right.Children.Add(calories);
        Grid content = new() { Padding = new Thickness(16, 18), ColumnSpacing = 14, InputTransparent = true, ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } } };
        content.Add(left, 0);
        content.Add(right, 1);
        content.SetValue(AutomationProperties.ExcludedWithChildrenProperty, true);
        Button action = Ui.Action("", () => EntryActionsAsync(profileId, entry), true);
        action.BackgroundColor = Colors.Transparent;
        action.CornerRadius = 0;
        action.MinimumHeightRequest = 80;
        SemanticProperties.SetDescription(action, $"{entry.Name}. {Ui.Amount(entry.WeightInGrams)} граммов. {Ui.Nutrition(entry.Nutrition)}. {Ui.Quality(entry.Quality)}");
        SemanticProperties.SetHint(action, "Изменить вес или удалить запись");
        Grid row = new();
        row.Add(action);
        row.Add(content);
        return row;
    }

    private async Task EntryActionsAsync(Guid profileId, MealEntryResponse entry)
    {
        if (_busy)
        {
            return;
        }

        string? action = await DisplayActionSheetAsync($"{entry.Name}\n{Ui.Quality(entry.Quality)}", "Отмена", "Удалить", "Изменить вес");
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
                if (await DisplayAlertAsync("Удалить запись?", $"{entry.Name}, {Ui.Amount(entry.WeightInGrams)} г", "Удалить", "Отмена"))
                {
                    await _client.DeleteEntryAsync(profileId, entry.Id, entry.Revision);
                    await RenderAsync();
                }
            });
        }
    }
}
