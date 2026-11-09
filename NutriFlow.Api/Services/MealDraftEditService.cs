using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure;
using NutriFlow.Infrastructure.Ai;

namespace NutriFlow.Api.Services;

// changes to a calculation that need no new description: a line changed or removed by hand, and nutrition looked for
// again when the estimate failed while the description was parsed or a food became unknown later
public sealed class MealDraftEditService(
    MealWorkflowService workflow,
    MealSessionStore sessions,
    LocalProductCatalog catalog,
    SavedDishStore dishes,
    INutritionEstimator estimator)
{
    public async Task<MealSessionResponse?> SetDishWeightAsync(
        Guid sessionId, string previewToken, int dishIndex, decimal grams, CancellationToken cancellationToken = default)
    {
        StoredMealSession? session = await sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        RequireDish(session, previewToken, dishIndex);
        return await workflow.ChangeDraftAsync(sessionId,
            draft => DraftEdits.SetWeight(draft, dishIndex, grams),
            draft => DraftEdits.DescribeWeight(draft, dishIndex, grams),
            cancellationToken, previewToken);
    }

    public async Task<MealSessionResponse?> RemoveDishAsync(
        Guid sessionId, string previewToken, int dishIndex, CancellationToken cancellationToken = default)
    {
        StoredMealSession? session = await sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        RequireDish(session, previewToken, dishIndex);
        if (session.Draft.Dishes.Count == 1)
        {
            throw new MealDraftEditException("last_dish", "В расчёте должна остаться хотя бы одна строка.");
        }

        return await workflow.ChangeDraftAsync(sessionId,
            draft => DraftEdits.Remove(draft, dishIndex),
            draft => DraftEdits.DescribeRemoval(draft, dishIndex),
            cancellationToken, previewToken);
    }

    public async Task<MealSessionResponse?> EstimateMissingAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        StoredMealSession? session = await sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        try
        {
            await AutoNutritionMealParser.AddEstimatesAsync(catalog, dishes, estimator, session.Draft, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            throw new MealParserResponseException(exception);
        }

        return await workflow.FindAsync(sessionId, cancellationToken);
    }

    // the client names the version of the calculation it saw, so an index always means the line it showed
    private static void RequireDish(StoredMealSession session, string previewToken, int dishIndex)
    {
        if (session.Status == MealSessionStatus.Confirmed)
        {
            throw new InvalidOperationException("A confirmed meal session cannot be changed.");
        }

        if (!string.Equals(session.PreviewToken, previewToken, StringComparison.Ordinal))
        {
            throw new MealSessionConflictException(
                "The meal session changed. Review the current calculation before changing it.");
        }

        if (dishIndex < 0 || dishIndex >= session.Draft.Dishes.Count)
        {
            throw new MealDraftEditException("dish_not_found", "Этой строки в расчёте уже нет. Обновите расчёт.");
        }
    }
}

public sealed class MealDraftEditException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
