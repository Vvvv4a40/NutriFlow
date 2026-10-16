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
}
