using Microsoft.AspNetCore.Mvc;
using NutriFlow.Api.Contracts;
using NutriFlow.Api.Services;
using NutriFlow.Domain;
using NutriFlow.Infrastructure;

namespace NutriFlow.Api.Endpoints;

internal static class ManualMealEndpoints
{
    public static void MapManualMealEndpoints(this WebApplication app)
    {
        app.MapPost("/api/meal-sessions/manual-meal", CreateManualMealAsync)
            .WithName("CreateManualMeal")
            .WithSummary("Creates a diary preview for a portion of a catalog product chosen by id.")
            .WithTags("Meal sessions")
            .Produces<MealSessionResponse>(StatusCodes.Status201Created)
            .Produces<MealSessionResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        app.MapPost("/api/meal-sessions/manual-dish", CreateManualDishAsync)
            .WithName("CreateManualDish")
            .WithSummary("Creates a saved dish preview from catalog products chosen by id.")
            .WithTags("Meal sessions")
            .Produces<MealSessionResponse>(StatusCodes.Status201Created)
            .Produces<MealSessionResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        app.MapPost("/api/meal-sessions/saved-dish-meal", CreateSavedDishMealAsync)
            .WithName("CreateSavedDishMeal")
            .WithSummary("Creates a diary preview for a portion of a saved dish.")
            .WithTags("Meal sessions")
            .Produces<MealSessionResponse>(StatusCodes.Status201Created)
            .Produces<MealSessionResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
    }

    private static async Task<IResult> CreateManualMealAsync(
        CreateManualMealRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKeyHeader,
        HttpContext context,
        ManualMealService manualMeals,
        CancellationToken cancellationToken)
    {
        IResult? error = ApiProblems.ReadIdempotencyKey(context, out Guid? idempotencyKey) ??
                         RequireQuality("weightQuality", request?.WeightQuality);
        if (error is not null)
        {
            return error;
        }

        if (request?.ProductId is not int productId)
        {
            return ApiProblems.Invalid("productId", "The id of a catalog product is required.");
        }

        if (request.WeightInGrams is not decimal grams)
        {
            return ApiProblems.Invalid("weightInGrams", "The portion weight is required.");
        }

        if (request.MealDate is not DateOnly mealDate)
        {
            return ApiProblems.Invalid("mealDate", "The meal date is required.");
        }

        return await CreateAsync(context, "product_not_found", () => manualMeals.CreateProductMealAsync(
            productId, grams, request.WeightQuality, mealDate, idempotencyKey, cancellationToken));
    }

    private static async Task<IResult> CreateManualDishAsync(
        CreateManualDishRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKeyHeader,
        HttpContext context,
        ManualMealService manualMeals,
        CancellationToken cancellationToken)
    {
        IResult? error = ApiProblems.ReadIdempotencyKey(context, out Guid? idempotencyKey) ??
                         RequireQuality("finalWeightQuality", request?.FinalWeightQuality);
        if (error is not null)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            return ApiProblems.Invalid("name", "The dish name is required.");
        }

        if (request.Ingredients is not { Count: > 0 } ingredients)
        {
            return ApiProblems.Invalid("ingredients", "At least one ingredient is required.");
        }

        if (ingredients.Count > ManualMealService.MaximumIngredientCount)
        {
            return ApiProblems.Invalid("ingredients",
                $"A dish cannot contain more than {ManualMealService.MaximumIngredientCount} ingredients.");
        }

        List<ManualDishIngredient> validated = [];
        foreach (ManualIngredientRequest? ingredient in ingredients)
        {
            if (ingredient?.ProductId is not int productId || ingredient.WeightInGrams is not decimal grams)
            {
                return ApiProblems.Invalid("ingredients", "Every ingredient needs a product id and a weight.");
            }

            error = RequireQuality("ingredients", ingredient.WeightQuality) ??
                    RequireQuality("ingredients", ingredient.RemovedWeightQuality);
            if (error is not null)
            {
                return error;
            }

            validated.Add(new ManualDishIngredient(productId, grams, ingredient.WeightQuality,
                ingredient.RemovedWeightInGrams, ingredient.RemovedWeightQuality));
        }

        if (request.FinalWeightInGrams is not decimal finalGrams)
        {
            return ApiProblems.Invalid("finalWeightInGrams", "The final dish weight is required.");
        }

        if (request.MealDate is not DateOnly mealDate)
        {
            return ApiProblems.Invalid("mealDate", "The meal date is required.");
        }

        return await CreateAsync(context, "product_not_found", () => manualMeals.CreateDishAsync(
            request.Name, validated, finalGrams, request.FinalWeightQuality, mealDate, idempotencyKey,
            cancellationToken));
    }

    private static async Task<IResult> CreateSavedDishMealAsync(
        CreateSavedDishMealRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKeyHeader,
        HttpContext context,
        ManualMealService manualMeals,
        CancellationToken cancellationToken)
    {
        IResult? error = ApiProblems.ReadIdempotencyKey(context, out Guid? idempotencyKey) ??
                         RequireQuality("weightQuality", request?.WeightQuality);
        if (error is not null)
        {
            return error;
        }

        if (request?.SavedDishId is not Guid dishId || dishId == Guid.Empty)
        {
            return ApiProblems.Invalid("savedDishId", "The id of a saved dish is required.");
        }

        if (request.WeightInGrams is not decimal grams)
        {
            return ApiProblems.Invalid("weightInGrams", "The portion weight is required.");
        }

        if (request.MealDate is not DateOnly mealDate)
        {
            return ApiProblems.Invalid("mealDate", "The meal date is required.");
        }

        return await CreateAsync(context, "saved_dish_not_found", () => manualMeals.CreateSavedDishMealAsync(
            dishId, grams, request.WeightQuality, mealDate, idempotencyKey, cancellationToken));
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context, string notFoundCode, Func<Task<MealSessionCreationResult>> create)
    {
        MealSessionCreationResult creation;
        try
        {
            creation = await create();
        }
        catch (KeyNotFoundException exception)
        {
            return ApiProblems.Problem(StatusCodes.Status404NotFound, notFoundCode,
                "The chosen item was not found.", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiProblems.Invalid("request", exception.Message);
        }
        catch (MealSessionConflictException exception)
        {
            return ApiProblems.Problem(StatusCodes.Status409Conflict, "idempotency_conflict",
                "The idempotency key conflicts with an existing request.", exception.Message);
        }

        string sessionPath = $"/api/meal-sessions/{creation.Session.Id}";
        if (creation.Created)
        {
            return Results.Created(sessionPath, creation.Session);
        }

        context.Response.Headers.Location = sessionPath;
        return Results.Ok(creation.Session);
    }

    // a weight entered by hand is either exact or approximate
    private static IResult? RequireQuality(string field, DataQuality? quality)
    {
        return quality is null or DataQuality.Exact or DataQuality.Estimated
            ? null
            : ApiProblems.Invalid(field, "A weight quality must be Exact or Estimated.");
    }
}
