using NutriFlow.Domain;
using NutriFlow.Infrastructure;

namespace NutriFlow.Mobile.Core;

// foods the profile does not know get an estimate right away and stay in its catalog, so the same food gives the same
// numbers next time. A failed estimate is not a failed description: the food stays in the calculation without nutrition
internal sealed class AutoNutritionMealParser(
    IMealParser inner,
    LocalProductCatalog catalog,
    SavedDishStore dishes,
    INutritionEstimator estimator) : IMealParser
{
    internal const string SourceName = "Оценка AI";
    internal const string SourceReference = "ai-estimate";

    public async Task<MealDraft> ParseAsync(CaptureSession session, CancellationToken cancellationToken = default)
    {
        MealDraft draft = await inner.ParseAsync(session, cancellationToken);
        if (session.Purpose == MealSessionPurpose.Diary)
        {
            draft = DiaryDraftDefaults.Apply(draft);
        }

        try
        {
            await AddEstimatesAsync(catalog, dishes, estimator, draft, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or
            TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // the screen offers to look for the nutrition again
        }

        return draft;
    }

    internal static async Task AddEstimatesAsync(LocalProductCatalog catalog, SavedDishStore dishes, INutritionEstimator estimator,
        MealDraft draft, CancellationToken cancellationToken)
    {
        List<string> missing = [];
        foreach (string name in draft.Dishes.SelectMany(dish => dish.Ingredients).Select(ingredient => ingredient.ProductName)
                     .Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            if ((await catalog.FindByNameAsync(name, cancellationToken)).Count == 0 &&
                await dishes.FindByNameAsync(name, cancellationToken) is null)
            {
                missing.Add(name);
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        foreach (NutritionEstimate estimate in await estimator.EstimateAsync(missing, cancellationToken))
        {
            await catalog.AddAsync(new Product(estimate.Food, estimate.Per100Grams,
                new NutritionSource(NutritionSourceKind.ExternalService, DataQuality.Estimated, SourceName, SourceReference)), cancellationToken);
        }
    }
}

// the limits of a key are per model, so the reserve answers while the main model waits for its next minute
internal sealed class ReserveMealParser(IMealParser main, IMealParser reserve) : IMealParser
{
    public async Task<MealDraft> ParseAsync(CaptureSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            return await main.ParseAsync(session, cancellationToken);
        }
        catch (GroqRateLimitException)
        {
            return await reserve.ParseAsync(session, cancellationToken);
        }
    }
}

internal sealed class ReserveNutritionEstimator(INutritionEstimator main, INutritionEstimator reserve) : INutritionEstimator
{
    public async Task<IReadOnlyList<NutritionEstimate>> EstimateAsync(IReadOnlyList<string> foods, CancellationToken cancellationToken = default)
    {
        try
        {
            return await main.EstimateAsync(foods, cancellationToken);
        }
        catch (GroqRateLimitException)
        {
            return await reserve.EstimateAsync(foods, cancellationToken);
        }
    }
}
