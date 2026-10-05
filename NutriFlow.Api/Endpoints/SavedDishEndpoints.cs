using NutriFlow.Api.Contracts;
using NutriFlow.Api.Services;

namespace NutriFlow.Api.Endpoints;

internal static class SavedDishEndpoints
{
    public static void MapSavedDishEndpoints(this WebApplication app)
    {
        app.MapGet("/api/saved-dishes", async (MealWorkflowService workflow, CancellationToken cancellationToken) =>
                Results.Ok(await workflow.GetSavedDishesAsync(cancellationToken)))
            .WithName("GetSavedDishes")
            .WithSummary("Lists the current owner's saved dishes.")
            .WithTags("Saved dishes")
            .Produces<IReadOnlyList<SavedDishResponse>>();

        app.MapGet("/api/saved-dishes/{id:guid}", async (
                Guid id, MealWorkflowService workflow, CancellationToken cancellationToken) =>
            {
                SavedDishDetailResponse? dish = await workflow.FindSavedDishAsync(id, cancellationToken);
                return dish is null
                    ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Saved dish not found.")
                    : Results.Ok(dish);
            })
            .WithName("GetSavedDish")
            .WithSummary("Returns the saved composition and calculated nutrition snapshot.")
            .WithTags("Saved dishes")
            .Produces<SavedDishDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}
