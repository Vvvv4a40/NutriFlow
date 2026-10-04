using NutriFlow.Api.Contracts;
using NutriFlow.Api.Services;

namespace NutriFlow.Api.Endpoints;

internal static class DailyDiaryEndpoints
{
    public static void MapDailyDiaryEndpoints(this WebApplication app)
    {
        app.MapPut("/api/daily-goals/{date}", SetDailyGoalAsync)
            .WithName("SetDailyGoal")
            .WithSummary("Creates or replaces the nutrition goal for a date.")
            .WithTags("Daily diary")
            .Produces<DailyProgressResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        app.MapGet("/api/daily-progress/{date}", GetDailyProgressAsync)
            .WithName("GetDailyProgress")
            .WithSummary("Returns consumed, remaining and exceeded nutrition for a date.")
            .WithTags("Daily diary")
            .Produces<DailyProgressResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> SetDailyGoalAsync(
        DateOnly date,
        SetDailyGoalRequest? request,
        MealWorkflowService workflow,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return InvalidGoal("A daily goal is required.");
        }

        try
        {
            await workflow.SetDailyGoalAsync(date, request, cancellationToken);
            DailyProgressResponse response = await workflow.GetDailyProgressAsync(
                date,
                cancellationToken);

            return Results.Ok(response);
        }
        catch (ArgumentException exception)
        {
            return InvalidGoal(exception.Message);
        }
    }

    private static async Task<IResult> GetDailyProgressAsync(
        DateOnly date,
        MealWorkflowService workflow,
        CancellationToken cancellationToken)
    {
        return Results.Ok(await workflow.GetDailyProgressAsync(
            date,
            cancellationToken));
    }

    private static IResult InvalidGoal(string message)
    {
        return Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                ["goal"] = new[] { message }
            });
    }
}
