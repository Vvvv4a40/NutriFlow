namespace NutriFlow.Domain;

public interface INutritionEstimator
{
    Task<IReadOnlyList<NutritionEstimate>> EstimateAsync(
        IReadOnlyList<string> foods,
        CancellationToken cancellationToken = default);
}

public sealed record NutritionEstimate(string Food, NutritionValues Per100Grams);
