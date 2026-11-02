using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Formatting;

namespace NutriFlow.Ui.Pages;

public sealed record GoalPreset(string Name, decimal ProteinPercent, decimal FatPercent, decimal CarbohydratesPercent)
{
    public string Description => $"{Name}: белки {ProteinPercent}, жиры {FatPercent}, углеводы {CarbohydratesPercent} процентов калорий";
}

// the goal form without markup: calories as typed, linked macro shares, grams from MacroGoalAllocation only
public sealed class GoalEditor
{
    public static readonly IReadOnlyList<GoalPreset> Presets =
    [
        new("Баланс", 30m, 30m, 40m),
        new("Больше белка", 40m, 30m, 30m),
        new("Меньше углеводов", 35m, 45m, 20m)
    ];

    public GoalEditor(NutritionResponse? goal)
    {
        CaloriesText = NutritionText.Amount(goal?.Calories ?? 2000m);
        Allocation = MacroGoalAllocation.FromGrams(goal?.ProteinGrams ?? 0m, goal?.FatGrams ?? 0m, goal?.CarbohydratesGrams ?? 0m);
        // a goal saved from a preset, and the 30/30/40 of a new goal, show their chip as chosen
        Preset = Presets.FirstOrDefault(preset =>
            Math.Abs(preset.ProteinPercent - Allocation.ProteinPercent) < 0.5m &&
            Math.Abs(preset.FatPercent - Allocation.FatPercent) < 0.5m &&
            Math.Abs(preset.CarbohydratesPercent - Allocation.CarbohydratesPercent) < 0.5m);
    }

    public string? CaloriesText { get; set; }

    public MacroGoalAllocation Allocation { get; private set; }

    public GoalPreset? Preset { get; private set; }

    public void ApplyPreset(GoalPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        Preset = preset;
        // the shares of a preset are of energy, FromGrams expects grams
        Allocation = MacroGoalAllocation.FromGrams(preset.ProteinPercent / 4m, preset.FatPercent / 9m, preset.CarbohydratesPercent / 4m);
    }

    // moving a slider leaves the preset: the shares are the user's own now
    public void SetShare(NutritionMacro macro, decimal percent)
    {
        Allocation = Allocation.WithShare(macro, Math.Clamp(percent, 0m, 100m));
        Preset = null;
    }

    public decimal Percent(NutritionMacro macro) => macro switch
    {
        NutritionMacro.Protein => Allocation.ProteinPercent,
        NutritionMacro.Fat => Allocation.FatPercent,
        _ => Allocation.CarbohydratesPercent
    };

    // zero is no goal for the diary, so it is not accepted as one
    public bool TryGetCalories(out decimal calories) => NutritionText.TryParseNumber(CaloriesText, true, out calories);

    public decimal? Grams(NutritionMacro macro)
    {
        if (!TryGetCalories(out decimal calories))
        {
            return null;
        }

        NutritionValues values = Allocation.ToNutrition(calories);
        return macro switch
        {
            NutritionMacro.Protein => values.ProteinGrams,
            NutritionMacro.Fat => values.FatGrams,
            _ => values.CarbohydratesGrams
        };
    }

    public NutritionResponse? ToGoal()
    {
        if (!TryGetCalories(out decimal calories))
        {
            return null;
        }

        NutritionValues values = Allocation.ToNutrition(calories);
        return new NutritionResponse(values.Calories, values.ProteinGrams, values.FatGrams, values.CarbohydratesGrams);
    }
}
