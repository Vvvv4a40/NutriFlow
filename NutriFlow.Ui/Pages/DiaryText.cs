using System.Globalization;
using NutriFlow.Api.Contracts;
using NutriFlow.Ui.Formatting;

namespace NutriFlow.Ui.Pages;

// texts of the diary screen, as MainPage.Diary.cs builds them on android
public static class DiaryText
{
    public const string LoadFailed = "Данные не удалены. Нажмите «Повторить»; если не поможет, перезапустите NutriFlow.";
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static string DateLabel(DateOnly date, DateOnly today) =>
        date == today ? $"Сегодня, {date.ToString("d MMMM", Russian)}"
        : date == today.AddDays(-1) ? $"Вчера, {date.ToString("d MMMM", Russian)}"
        : date.ToString("ddd, d MMMM", Russian);

    public static string FullDate(DateOnly date) => date.ToString("D", Russian);

    // a goal of zero calories is no goal: the arc and the tiles show what was eaten
    public static NutritionResponse? EffectiveGoal(NutritionResponse? goal) => goal is { Calories: > 0m } ? goal : null;

    public static DiaryHeadline Headline(NutritionResponse consumed, NutritionResponse? goal)
    {
        if (EffectiveGoal(goal) is not { } target)
        {
            return new DiaryHeadline(NutritionText.Kilocalories(consumed.Calories), "ккал съедено",
                $"Съедено {NutritionText.Amount(consumed.Calories)} килокалорий", false);
        }

        decimal difference = target.Calories - consumed.Calories;
        return difference >= 0m
            ? new DiaryHeadline(NutritionText.Kilocalories(difference), "ккал осталось",
                $"Осталось {NutritionText.Amount(difference)} килокалорий", false)
            : new DiaryHeadline($"+{NutritionText.Kilocalories(-difference)}", "ккал сверх цели",
                $"Сверх цели {NutritionText.Amount(-difference)} килокалорий", true);
    }

    public static MacroProgressText Macro(string name, decimal consumed, decimal? goal)
    {
        string amount = NutritionText.MacroAmount(consumed);
        string description = $"{name}: съедено {amount} граммов. " +
            (goal is null ? "Цель не задана" : $"Цель {NutritionText.MacroAmount(goal.Value)} граммов");
        string? excess = null;
        if (goal is not null && consumed > goal.Value)
        {
            string over = NutritionText.MacroAmount(consumed - goal.Value);
            // less than half a gram over reads as «+0», which says nothing
            if (over != "0")
            {
                excess = $"+{over}";
                description += $". Превышение на {over} граммов";
            }
        }

        return new MacroProgressText(amount, goal is null ? " г" : $" / {NutritionText.MacroAmount(goal.Value)} г", excess, description);
    }

    public static string EntriesSummary(IReadOnlyCollection<MealEntryResponse> entries) =>
        $"{Plural(entries.Count, "запись", "записи", "записей")} · {NutritionText.Kilocalories(entries.Sum(entry => entry.Nutrition.Calories))} ккал";

    public static string Plural(int count, string one, string few, string many)
    {
        int lastTwo = count % 100;
        int last = count % 10;
        string word = lastTwo is >= 11 and <= 14 ? many : last == 1 ? one : last is >= 2 and <= 4 ? few : many;
        return $"{count} {word}";
    }

    // «≈» marks an estimate and «?» unknown quality next to the calories of an entry
    public static string EntryCalories(MealEntryResponse entry)
    {
        string mark = entry.Quality switch { "Estimated" => "≈ ", "Exact" or "Verified" => "", _ => "? " };
        return $"{mark}{NutritionText.Kilocalories(entry.Nutrition.Calories)} ккал";
    }

    public static string EntryDescription(MealEntryResponse entry) =>
        $"{entry.Name}. {NutritionText.Amount(entry.WeightInGrams)} граммов. {NutritionText.SpokenNutrition(entry.Nutrition)}. {NutritionText.Quality(entry.Quality)}";

    public static string EntryActionsTitle(MealEntryResponse entry) =>
        $"{entry.Name} · {NutritionText.Amount(entry.WeightInGrams)} г\n{NutritionText.Quality(entry.Quality)}";
}

public sealed record DiaryHeadline(string Value, string Caption, string Spoken, bool Exceeded);

public sealed record MacroProgressText(string Amount, string Goal, string? Excess, string Description);
