using NutriFlow.Domain;

namespace NutriFlow.Mobile.Core;

public sealed class MacroGoalAllocation
{
    private MacroGoalAllocation(decimal proteinPercent, decimal fatPercent, decimal carbohydratesPercent)
    {
        ProteinPercent = PositiveZero(proteinPercent);
        FatPercent = PositiveZero(fatPercent);
        CarbohydratesPercent = PositiveZero(carbohydratesPercent);
    }

    public decimal ProteinPercent { get; }
    public decimal FatPercent { get; }
    public decimal CarbohydratesPercent { get; }

    public static MacroGoalAllocation FromGrams(decimal proteinGrams, decimal fatGrams, decimal carbohydratesGrams)
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
        if (maximum == 0m)
        {
            return new MacroGoalAllocation(30m, 30m, 40m);
        }

        // scale large inputs before multiplying energy by 100 to avoid decimal overflow.
        decimal scale = maximum > decimal.MaxValue / 1000m ? maximum : 1m;
        decimal proteinEnergy = proteinGrams / scale * 4m;
        decimal fatEnergy = fatGrams / scale * 9m;
        decimal carbohydratesEnergy = carbohydratesGrams / scale * 4m;
        decimal totalEnergy = proteinEnergy + fatEnergy + carbohydratesEnergy;
        decimal proteinPercent = proteinEnergy * 100m / totalEnergy;
        decimal fatPercent = Math.Min(100m - proteinPercent, fatEnergy * 100m / totalEnergy);

        return new MacroGoalAllocation(proteinPercent, fatPercent, 100m - (proteinPercent + fatPercent));
    }

    public MacroGoalAllocation WithShare(NutritionMacro macro, decimal percent)
    {
        if (!Enum.IsDefined(macro))
        {
            throw new ArgumentOutOfRangeException(nameof(macro));
        }

        if (percent < 0m || percent > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(percent));
        }

        (decimal first, decimal second) = macro switch
        {
            NutritionMacro.Protein => (FatPercent, CarbohydratesPercent),
            NutritionMacro.Fat => (ProteinPercent, CarbohydratesPercent),
            _ => (ProteinPercent, FatPercent)
        };
        decimal remaining = 100m - percent;
        decimal others = first + second;
        decimal firstPercent = others == 0m ? remaining / 2m : Math.Min(remaining, remaining * first / others);
        decimal secondPercent = remaining - firstPercent;

        return macro switch
        {
            NutritionMacro.Protein => new MacroGoalAllocation(percent, firstPercent, 100m - (percent + firstPercent)),
            NutritionMacro.Fat => new MacroGoalAllocation(firstPercent, percent, 100m - (firstPercent + percent)),
            _ => new MacroGoalAllocation(firstPercent, secondPercent, percent)
        };
    }

    public NutritionValues ToNutrition(decimal calories)
    {
        if (calories < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(calories));
        }

        return new NutritionValues(
            PositiveZero(calories),
            PositiveZero(calories * (ProteinPercent / 100m) / 4m),
            PositiveZero(calories * (FatPercent / 100m) / 9m),
            PositiveZero(calories * (CarbohydratesPercent / 100m) / 4m));
    }

    private static decimal PositiveZero(decimal value) => value == 0m ? 0m : value;
}
