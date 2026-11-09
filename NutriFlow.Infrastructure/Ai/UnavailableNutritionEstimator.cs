using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Ai;

public sealed class UnavailableNutritionEstimator : INutritionEstimator
{
    public Task<IReadOnlyList<NutritionEstimate>> EstimateAsync(
        IReadOnlyList<string> foods,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "Nutrition estimates require an AI provider to be configured.");
    }
}
