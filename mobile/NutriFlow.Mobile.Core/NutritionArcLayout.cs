namespace NutriFlow.Mobile.Core;

public enum NutritionMacro
{
    Protein,
    Fat,
    Carbohydrates
}

public readonly record struct NutritionArcSegment(
    NutritionMacro Macro,
    decimal StartAngle,
    decimal SweepAngle);

public static class NutritionArcLayout
{
    public static IReadOnlyList<NutritionArcSegment> Create(
        decimal proteinGrams, decimal fatGrams, decimal carbohydratesGrams)
    {
        if (proteinGrams < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(proteinGrams));
        }

        if (fatGrams < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(fatGrams));
        }

        if (carbohydratesGrams < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(carbohydratesGrams));
        }

        decimal maximum = Math.Max(proteinGrams, Math.Max(fatGrams, carbohydratesGrams));
        if (maximum == 0)
        {
            return Array.Empty<NutritionArcSegment>();
        }

        decimal scale = maximum > decimal.MaxValue / 3 ? maximum : 1m;
        (NutritionMacro Macro, decimal Grams)[] macros =
        [
            (NutritionMacro.Protein, proteinGrams),
            (NutritionMacro.Fat, fatGrams),
            (NutritionMacro.Carbohydrates, carbohydratesGrams)
        ];
        List<(NutritionMacro Macro, decimal Weight)> weights = [];
        decimal total = 0;
        foreach ((NutritionMacro macro, decimal grams) in macros)
        {
            decimal weight = grams / scale;
            if (weight > 0)
            {
                weights.Add((macro, weight));
                total += weight;
            }
        }

        List<NutritionArcSegment> segments = [];
        decimal remainingAngle = 180m;
        for (int index = 0; index < weights.Count; index++)
        {
            (NutritionMacro macro, decimal weight) = weights[index];
            decimal sweep = index == weights.Count - 1
                ? remainingAngle
                : Math.Min(remainingAngle, 180m * (weight / total));
            if (sweep > 0)
            {
                segments.Add(new NutritionArcSegment(macro, remainingAngle, sweep));
                remainingAngle -= sweep;
            }
        }

        return segments.AsReadOnly();
    }

    // the filled length follows calories against the goal; colors split it by each macro's share of energy
    public static IReadOnlyList<NutritionArcSegment> CreateProgress(
        decimal consumedCalories, decimal goalCalories,
        decimal proteinGrams, decimal fatGrams, decimal carbohydratesGrams)
    {
        decimal fraction = Progress(consumedCalories, goalCalories);
        if (fraction == 0m || Math.Max(proteinGrams, Math.Max(fatGrams, carbohydratesGrams)) <= 0m)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(proteinGrams);
            ArgumentOutOfRangeException.ThrowIfNegative(fatGrams);
            ArgumentOutOfRangeException.ThrowIfNegative(carbohydratesGrams);
            return Array.Empty<NutritionArcSegment>();
        }

        MacroGoalAllocation shares = MacroGoalAllocation.FromGrams(proteinGrams, fatGrams, carbohydratesGrams);
        (NutritionMacro Macro, decimal Percent)[] macros =
        [
            (NutritionMacro.Protein, shares.ProteinPercent),
            (NutritionMacro.Fat, shares.FatPercent),
            (NutritionMacro.Carbohydrates, shares.CarbohydratesPercent)
        ];
        decimal filled = 180m * fraction;
        decimal start = 180m;
        decimal end = 180m - filled;
        List<NutritionArcSegment> segments = [];
        int last = Array.FindLastIndex(macros, macro => macro.Percent > 0m);
        for (int index = 0; index <= last; index++)
        {
            (NutritionMacro macro, decimal percent) = macros[index];
            if (percent <= 0m)
            {
                continue;
            }

            decimal sweep = index == last ? start - end : Math.Min(start - end, filled * percent / 100m);
            if (sweep > 0m)
            {
                segments.Add(new NutritionArcSegment(macro, start, sweep));
                start -= sweep;
            }
        }

        return segments.AsReadOnly();
    }

    public static decimal Progress(decimal consumedCalories, decimal goalCalories)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(consumedCalories);
        ArgumentOutOfRangeException.ThrowIfNegative(goalCalories);
        return goalCalories == 0m ? 0m : Math.Min(1m, consumedCalories / goalCalories);
    }
}
