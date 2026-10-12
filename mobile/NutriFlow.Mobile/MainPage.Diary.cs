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

        Grid macros = new() { ColumnSpacing = 8, ColumnDefinitions = new ColumnDefinitionCollection { new(), new(), new() } };
        macros.Add(MacroTile("Белки", progress.Consumed.ProteinGrams, progress.Goal?.ProteinGrams, Ui.Protein), 0);
        macros.Add(MacroTile("Жиры", progress.Consumed.FatGrams, progress.Goal?.FatGrams, Ui.Fat), 1);
        macros.Add(MacroTile("Углеводы", progress.Consumed.CarbohydratesGrams, progress.Goal?.CarbohydratesGrams, Ui.Carbs), 2);
        _body.Children.Add(macros);
        _body.Children.Add(DailyGoalCard(profileId, selectedDate, progress));

        Grid listHeading = new() { ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } } };
        Label mealsTitle = Ui.Title("Записи за день");
        mealsTitle.FontSize = 21;
        mealsTitle.Margin = 0;
        Label count = Ui.Text($"{progress.Entries.Count}", true);
        count.FontSize = 16;
        count.VerticalOptions = LayoutOptions.Center;
        listHeading.Add(mealsTitle, 0);
        listHeading.Add(count, 1);
        _body.Children.Add(listHeading);

        if (progress.Entries.Count == 0)
        {
            Label emptyTitle = Ui.Title("Пока чистый лист");
            emptyTitle.FontSize = 20;
            Border empty = Ui.Card(emptyTitle,
                Ui.Text("Добавьте первую порцию — здесь появится понятная картина вашего питания.", true),
                Ui.Action("Добавить первую порцию", () => SelectTabAsync("Ввод"), true));
            _body.Children.Add(empty);
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
        Label hint = Ui.Text("Нажмите на запись, чтобы изменить вес или удалить порцию.", true);
        hint.FontSize = 12;
        _body.Children.Add(hint);
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
            RowDefinitions = new RowDefinitionCollection { new() { Height = 62 }, new() { Height = GridLength.Auto } }
        };
        NutritionArcView arc = new(progress.Consumed, progress.Goal) { HeightRequest = 175, VerticalOptions = LayoutOptions.Start };
        hero.Add(arc, 0, 0);
        Grid.SetColumnSpan(arc, 3);
        Grid.SetRowSpan(arc, 2);
        Label calories = Ui.Title(progress.Consumed.Calories.ToString("N0", DiaryCulture));
        calories.FontSize = 46;
        calories.CharacterSpacing = -1.5;
        calories.Margin = 0;
        calories.HorizontalTextAlignment = TextAlignment.Center;
        calories.TextColor = progress.Exceeded?.Calories > 0 ? Ui.Carbs : Ui.TextPrimary;
        SemanticProperties.SetDescription(calories, $"Съедено {Ui.Amount(progress.Consumed.Calories)} килокалорий");
        Label consumed = Ui.Text("Съедено за день", true);
        consumed.FontSize = 12;
        consumed.HorizontalTextAlignment = TextAlignment.Center;
        Label target = Ui.Text(progress.Goal is null ? "ккал" : $"из {Ui.Amount(progress.Goal.Calories)} ккал", true);
        target.FontSize = 14;
        target.HorizontalTextAlignment = TextAlignment.Center;
        VerticalStackLayout center = new() { Spacing = 2, VerticalOptions = LayoutOptions.End, Margin = new Thickness(0, 0, 0, 8) };
        center.Children.Add(calories);
        center.Children.Add(target);
        center.Children.Add(consumed);
        hero.Add(center, 1, 1);
        return hero;
    }

    private static View MacroTile(string name, decimal consumed, decimal? goal, Color color)
    {
        Label label = Ui.Text(name);
        label.FontSize = 12;
        label.TextColor = color;
        Label amount = Ui.Title($"{Ui.Amount(consumed)} г");
        amount.FontSize = 20;
        amount.Margin = new Thickness(0, 2, 0, 0);
        Label target = Ui.Text(goal is null ? "без цели" : $"из {Ui.Amount(goal.Value)} г", true);
        target.FontSize = 11;
        VerticalStackLayout values = new() { Spacing = 5 };
        values.Children.Add(label);
        values.Children.Add(amount);
        values.Children.Add(target);
        if (goal is not null && consumed > goal.Value)
        {
            Label excess = Ui.Text($"+{Ui.Amount(consumed - goal.Value)} г", true);
            excess.FontSize = 11;
            SemanticProperties.SetDescription(excess, $"Превышение цели по {name.ToLower(DiaryCulture)}: {Ui.Amount(consumed - goal.Value)} граммов");
            values.Children.Add(excess);
        }

        return new Border { Content = values, Padding = new Thickness(12, 14), StrokeThickness = 0, BackgroundColor = Ui.Surface, StrokeShape = new RoundRectangle { CornerRadius = 20 } };
    }

    private View DailyGoalCard(Guid profileId, DateOnly date, DailyProgressResponse progress)
    {
        string title = progress.Goal is null ? "Настройте цель на день"
            : progress.Exceeded?.Calories > 0 ? $"Сверх цели: {Ui.Amount(progress.Exceeded.Calories)} ккал"
            : progress.Remaining?.Calories > 0 ? $"Осталось {Ui.Amount(progress.Remaining.Calories)} ккал"
            : "Дневная цель достигнута";
        Label headline = Ui.Text(title);
        headline.FontAttributes = FontAttributes.Bold;
        Label detail = Ui.Text(progress.Goal is null ? "Прогресс появится после выбора КБЖУ" : "Цель можно изменить под свой день", true);
        detail.FontSize = 12;
        VerticalStackLayout copy = new() { Spacing = 4, VerticalOptions = LayoutOptions.Center, InputTransparent = true };
        copy.Children.Add(headline);
        if (progress.Goal is null)
        {
            copy.Children.Add(detail);
        }
        Button edit = Ui.Action("", () => EditGoalPageAsync(profileId, date, progress.Goal), true);
        edit.BackgroundColor = Colors.Transparent;
        edit.MinimumHeightRequest = 52;
        SemanticProperties.SetDescription(edit, $"{title}. Настроить цель");
        Grid content = new() { ColumnSpacing = 12, Padding = new Thickness(14, 12), InputTransparent = true, ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Auto }, new() { Width = GridLength.Star } } };
        content.Add(new AppIcon(AppIconKind.Target, Ui.Accent) { VerticalOptions = LayoutOptions.Center }, 0);
        content.Add(copy, 1);
        content.SetValue(AutomationProperties.ExcludedWithChildrenProperty, true);
        Grid card = new();
        card.Add(edit);
        card.Add(content);
        return new Border { Content = card, StrokeThickness = 0, BackgroundColor = Ui.Surface, StrokeShape = new RoundRectangle { CornerRadius = 20 } };
    }

    private View DiaryEntryRow(Guid profileId, MealEntryResponse entry)
    {
        Label name = Ui.Text(entry.Name);
        name.FontSize = 16;
        name.FontAttributes = FontAttributes.Bold;
        Label nutrition = new() { FontSize = 12, FormattedText = new FormattedString() };
        nutrition.FormattedText.Spans.Add(new Span { Text = $"Б {Ui.Amount(entry.Nutrition.ProteinGrams)}", TextColor = Ui.Protein });
        nutrition.FormattedText.Spans.Add(new Span { Text = "  ·  ", TextColor = Ui.Muted });
        nutrition.FormattedText.Spans.Add(new Span { Text = $"Ж {Ui.Amount(entry.Nutrition.FatGrams)}", TextColor = Ui.Fat });
        nutrition.FormattedText.Spans.Add(new Span { Text = "  ·  ", TextColor = Ui.Muted });
        nutrition.FormattedText.Spans.Add(new Span { Text = $"У {Ui.Amount(entry.Nutrition.CarbohydratesGrams)}", TextColor = Ui.Carbs });
        Label quality = Ui.Text(Ui.Quality(entry.Quality), true);
        quality.FontSize = 11;
        VerticalStackLayout left = new() { Spacing = 6 };
        left.Children.Add(name);
        left.Children.Add(nutrition);
        left.Children.Add(quality);
        Label weight = Ui.Text($"{Ui.Amount(entry.WeightInGrams)} г");
        weight.FontSize = 15;
        weight.HorizontalTextAlignment = TextAlignment.End;
        Label calories = Ui.Text($"{Ui.Amount(entry.Nutrition.Calories)} ккал", true);
        calories.FontSize = 12;
        calories.HorizontalTextAlignment = TextAlignment.End;
        VerticalStackLayout right = new() { Spacing = 6, MaximumWidthRequest = 100 };
        right.Children.Add(weight);
        right.Children.Add(calories);
        right.Children.Add(new AppIcon(AppIconKind.More, Ui.Muted, 20) { HorizontalOptions = LayoutOptions.End });
        Grid content = new() { Padding = new Thickness(16, 18), ColumnSpacing = 14, InputTransparent = true, ColumnDefinitions = new ColumnDefinitionCollection { new() { Width = GridLength.Star }, new() { Width = GridLength.Auto } } };
        content.Add(left, 0);
        content.Add(right, 1);
        content.SetValue(AutomationProperties.ExcludedWithChildrenProperty, true);
        Button action = Ui.Action("", () => EntryActionsAsync(profileId, entry), true);
        action.BackgroundColor = Colors.Transparent;
        action.CornerRadius = 0;
        action.MinimumHeightRequest = 96;
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

        string? action = await DisplayActionSheetAsync(entry.Name, "Отмена", "Удалить", "Изменить вес");
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
