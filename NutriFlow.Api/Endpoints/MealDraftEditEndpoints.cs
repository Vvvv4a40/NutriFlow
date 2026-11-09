using Microsoft.AspNetCore.Mvc;
using NutriFlow.Api.Contracts;
using NutriFlow.Api.Services;
using NutriFlow.Infrastructure;

namespace NutriFlow.Api.Endpoints;

internal static class MealDraftEditEndpoints
{
    public static void MapMealDraftEditEndpoints(this WebApplication app)
    {
        app.MapPut("/api/meal-sessions/{id:guid}/dishes/{index:int}/weight", SetDishWeightAsync)
            .WithName("SetMealSessionDishWeight")
            .WithSummary("Changes the weight of one calculation line by hand; If-Match carries the preview token.")
            .WithTags("Meal sessions")
            .Produces<MealSessionResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        app.MapDelete("/api/meal-sessions/{id:guid}/dishes/{index:int}", RemoveDishAsync)
            .WithName("RemoveMealSessionDish")
            .WithSummary("Removes one calculation line that was not eaten; If-Match carries the preview token.")
            .WithTags("Meal sessions")
            .Produces<MealSessionResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        app.MapPost("/api/meal-sessions/{id:guid}/estimate-missing", EstimateMissingAsync)
            .WithName("EstimateMissingNutrition")
            .RequireRateLimiting("external-services")
            .WithSummary("Asks the AI provider for approximate nutrition of foods the catalog does not know.")
            .WithTags("Meal sessions")
            .Produces<MealSessionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);
    }

    private static async Task<IResult> SetDishWeightAsync(
        Guid id,
        int index,
        SetDishWeightRequest? request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        MealDraftEditService edits,
        CancellationToken cancellationToken)
    {
        if (ReadPreviewToken(ifMatch, out string previewToken) is { } error)
        {
            return error;
        }

        if (request?.WeightInGrams is not decimal grams)
        {
            return ApiProblems.Invalid("weightInGrams", "The new weight is required.");
        }

        return await ChangeAsync(id, () => edits.SetDishWeightAsync(id, previewToken, index, grams, cancellationToken));
    }

    private static async Task<IResult> RemoveDishAsync(
        Guid id,
        int index,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        MealDraftEditService edits,
        CancellationToken cancellationToken)
    {
        if (ReadPreviewToken(ifMatch, out string previewToken) is { } error)
        {
            return error;
        }

        return await ChangeAsync(id, () => edits.RemoveDishAsync(id, previewToken, index, cancellationToken));
    }

    private static IResult? ReadPreviewToken(string? ifMatch, out string previewToken)
    {
        previewToken = string.Empty;
        if (ifMatch is null)
        {
            return ApiProblems.Problem(
                StatusCodes.Status428PreconditionRequired,
                "preview_token_required",
                "An If-Match preview token is required.",
                "Send the previewToken of the calculation as a quoted If-Match value.");
        }

        if (ifMatch is not { Length: >= 3 } || ifMatch[0] != '"' || ifMatch[^1] != '"' ||
            ifMatch.AsSpan(1, ifMatch.Length - 2).ContainsAny('"', ','))
        {
            return ApiProblems.Invalid("If-Match", "Send exactly one quoted preview token.");
        }

        previewToken = ifMatch[1..^1];
        return null;
    }

    private static async Task<IResult> EstimateMissingAsync(
        Guid id,
        HttpContext context,
        MealDraftEditService edits,
        CancellationToken cancellationToken)
    {
        try
        {
            MealSessionResponse? session = await edits.EstimateMissingAsync(id, cancellationToken);
            return session is null ? ApiProblems.MealSessionNotFound(id) : Results.Ok(session);
        }
        catch (NotSupportedException exception)
        {
            return ApiProblems.Problem(StatusCodes.Status503ServiceUnavailable, "ai_not_configured",
                "Nutrition estimates are not configured.", exception.Message);
        }
        catch (Exception exception) when (ApiProblems.AiFailure(exception, context, cancellationToken) is { } failure)
        {
            return failure;
        }
    }

    private static async Task<IResult> ChangeAsync(Guid id, Func<Task<MealSessionResponse?>> change)
    {
        try
        {
            MealSessionResponse? session = await change();
            return session is null ? ApiProblems.MealSessionNotFound(id) : Results.Ok(session);
        }
        catch (MealDraftEditException exception)
        {
            return ApiProblems.Problem(
                exception.Code == "dish_not_found" ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict,
                exception.Code, "The calculation line cannot be changed.", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiProblems.Invalid("weightInGrams", exception.Message);
        }
        catch (MealSessionConflictException)
        {
            return ApiProblems.Problem(StatusCodes.Status412PreconditionFailed, "meal_session_changed",
                "The meal session changed.", "Расчёт уже изменился. Посмотрите его ещё раз и повторите правку.");
        }
        catch (InvalidOperationException exception)
        {
            return ApiProblems.Problem(StatusCodes.Status409Conflict, "meal_session_confirmed",
                "The meal session cannot be changed.", exception.Message);
        }
    }
}
