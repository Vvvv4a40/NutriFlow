using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using NutriFlow.Api.Contracts;
using NutriFlow.Api.Services;
using NutriFlow.Domain;
using NutriFlow.Infrastructure;

namespace NutriFlow.Api.Endpoints;

internal static class MealSessionEndpoints
{
    public static void MapMealSessionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/meal-sessions", CreateMealSessionAsync)
            .WithName("CreateMealSession")
            .RequireRateLimiting("external-services")
            .WithSummary("Creates a meal preview; an Idempotency-Key makes mobile retries safe.")
            .WithTags("Meal sessions")
            .Produces<MealSessionResponse>(StatusCodes.Status201Created)
            .Produces<MealSessionResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        app.MapGet("/api/meal-sessions/{id:guid}", GetMealSessionAsync)
            .WithName("GetMealSession")
            .WithSummary("Returns the current server-side preview of a meal session.")
            .WithTags("Meal sessions")
            .Produces<MealSessionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/api/meal-sessions/{id:guid}/messages", AddMealSessionMessageAsync)
            .WithName("AddMealSessionMessage")
            .RequireRateLimiting("external-services")
            .WithSummary("Adds a clarification; an Idempotency-Key makes retries safe.")
            .WithTags("Meal sessions")
            .Produces<MealSessionResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        app.MapPost("/api/meal-sessions/{id:guid}/confirm", ConfirmMealSessionAsync)
            .WithName("ConfirmMealSession")
            .WithSummary("Confirms the reviewed preview as diary entries or a saved dish.")
            .WithTags("Meal sessions")
            .Produces<ConfirmMealSessionResponse>(StatusCodes.Status200OK)
            .Produces<ConfirmMealSessionResponse>(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .AddOpenApiOperationTransformer(async (operation, context, cancellationToken) =>
            {
                IOpenApiSchema problem = await context.GetOrCreateSchemaAsync(
                    typeof(ProblemDetails), cancellationToken: cancellationToken);
                operation.Responses!["409"].Content!["application/problem+json"] = new OpenApiMediaType
                {
                    Schema = problem
                };
            });

        app.MapPost("/api/meal-drafts/parse", ParseMealDraftAsync)
            .WithName("ParseMealDraft")
            .RequireRateLimiting("external-services")
            .WithSummary("Builds a structured meal draft from captured messages.")
            .WithTags("Meal drafts")
            .Produces<MealDraftResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static async Task<IResult> CreateMealSessionAsync(
        CreateMealSessionRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKeyHeader,
        HttpContext context,
        MealWorkflowService workflow,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return InvalidMessages("A meal session request is required.");
        }

        if (ApiProblems.ReadIdempotencyKey(context, out Guid? idempotencyKey) is { } keyError)
        {
            return keyError;
        }

        if (idempotencyKey is not null && request.MealDate is null)
        {
            return ApiProblems.Invalid("mealDate", "An explicit meal date is required with an idempotency key.");
        }

        try
        {
            MealSessionCreationResult creation = await workflow.CreateAsync(
                request.Messages,
                request.MealDate ?? DateOnly.FromDateTime(DateTime.Today),
                cancellationToken,
                idempotencyKey,
                request.Purpose);
            string sessionPath = $"/api/meal-sessions/{creation.Session.Id}";

            if (creation.Created)
            {
                return Results.Created(sessionPath, creation.Session);
            }

            context.Response.Headers.Location = sessionPath;
            return Results.Ok(creation.Session);
        }
        catch (ArgumentException exception)
        {
            return InvalidMessages(exception.Message);
        }
        catch (MealSessionConflictException exception)
        {
            return IdempotencyConflict(exception);
        }
        catch (NotSupportedException exception)
        {
            return InputNotSupported(exception);
        }
        catch (Exception exception) when (ApiProblems.AiFailure(exception, context, cancellationToken) is { } failure)
        {
            return failure;
        }
    }

    private static async Task<IResult> GetMealSessionAsync(
        Guid id,
        MealWorkflowService workflow,
        CancellationToken cancellationToken)
    {
        MealSessionResponse? response = await workflow.FindAsync(
            id,
            cancellationToken);

        return response is null
            ? ApiProblems.MealSessionNotFound(id)
            : Results.Ok(response);
    }

    private static async Task<IResult> AddMealSessionMessageAsync(
        Guid id,
        AddMealSessionMessageRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKeyHeader,
        HttpContext context,
        MealWorkflowService workflow,
        ManualMealService manualMeals,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return InvalidMessages("A message request is required.");
        }

        if (ApiProblems.ReadIdempotencyKey(context, out Guid? idempotencyKey) is { } keyError)
        {
            return keyError;
        }

        try
        {
            await manualMeals.RequireUnambiguousSelectionAsync(id, cancellationToken);
            MealSessionResponse? response = await workflow.AddMessageAsync(
                id,
                request.Message,
                cancellationToken,
                idempotencyKey);

            return response is null
                ? ApiProblems.MealSessionNotFound(id)
                : Results.Ok(response);
        }
        catch (ArgumentException exception)
        {
            return InvalidMessages(exception.Message);
        }
        catch (ManualSelectionAmbiguousException exception)
        {
            return ApiProblems.Problem(
                StatusCodes.Status409Conflict,
                "product_selection_ambiguous",
                "The chosen product can no longer be told apart by name.",
                exception.Message);
        }
        catch (MealSessionConflictException exception)
        {
            return IdempotencyConflict(exception);
        }
        catch (InvalidOperationException exception)
        {
            return ApiProblems.Problem(
                StatusCodes.Status409Conflict,
                "meal_session_confirmed",
                "The meal session cannot be changed.",
                exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return InputNotSupported(exception);
        }
        catch (Exception exception) when (ApiProblems.AiFailure(exception, context, cancellationToken) is { } failure)
        {
            return failure;
        }
    }

    private static async Task<IResult> ConfirmMealSessionAsync(
        Guid id,
        ConfirmMealSessionRequest? request,
        MealWorkflowService workflow,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.PreviewToken))
        {
            return ApiProblems.Invalid("previewToken", "The preview token returned by the server is required.");
        }

        MealConfirmationOutcome? outcome;

        try
        {
            outcome = await workflow.ConfirmAsync(id, request.PreviewToken, cancellationToken);
        }
        catch (MealSessionConflictException exception)
        {
            return ApiProblems.Problem(
                StatusCodes.Status409Conflict,
                "saved_dish_name_conflict",
                "The saved dish conflicts with an existing name.",
                exception.Message);
        }

        if (outcome is null)
        {
            return ApiProblems.MealSessionNotFound(id);
        }

        string? message = outcome.Kind switch
        {
            MealConfirmationOutcomeKind.StalePreview =>
                "The preview changed. Review the current values before confirming again.",
            MealConfirmationOutcomeKind.NotReady =>
                "Resolve the listed questions and product issues before confirmation.",
            _ => null
        };
        ConfirmMealSessionResponse response = new ConfirmMealSessionResponse(
            outcome.Kind.ToString(),
            message,
            outcome.Session,
            outcome.Entries.Select(ResponseMapper.ToMealEntryResponse).ToArray(),
            outcome.SavedDish is null ? null : ResponseMapper.ToSavedDishResponse(outcome.SavedDish));

        return outcome.Kind is MealConfirmationOutcomeKind.Confirmed or
            MealConfirmationOutcomeKind.AlreadyConfirmed
            ? Results.Ok(response)
            : Results.Json(response, statusCode: StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> ParseMealDraftAsync(
        ParseMealDraftRequest? request,
        IMealParser parser,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (request is null ||
            request.Messages is null ||
            request.Messages.Count == 0)
        {
            return InvalidMessages(
                "At least one captured message is required.");
        }

        if (request.Messages.Count > 50)
        {
            return InvalidMessages("A capture session cannot contain more than 50 messages.");
        }

        CaptureSession session = new CaptureSession();
        int totalCharacterCount = 0;

        foreach (string? message in request.Messages)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return InvalidMessages(
                    "Messages cannot contain null, empty, or whitespace-only values.");
            }

            if (message.Length > 4000)
            {
                return InvalidMessages("A single message cannot exceed 4000 characters.");
            }

            totalCharacterCount += message.Length;

            if (totalCharacterCount > 20_000)
            {
                return InvalidMessages("Captured messages cannot exceed 20000 characters in total.");
            }

            session.AddEvent(new InputEvent(message));
        }

        session.FinishCollecting();

        try
        {
            MealDraft mealDraft = await parser.ParseAsync(
                session,
                cancellationToken);
            return Results.Ok(MapResponse(session, mealDraft));
        }
        catch (NotSupportedException exception)
        {
            return InputNotSupported(exception);
        }
        catch (InvalidDataException exception)
        {
            return ApiProblems.AiFailure(new MealParserResponseException(exception), context, cancellationToken)!;
        }
        catch (Exception exception) when (ApiProblems.AiFailure(exception, context, cancellationToken) is { } failure)
        {
            return failure;
        }
    }

    private static IResult InvalidMessages(string message)
    {
        return ApiProblems.Invalid(nameof(ParseMealDraftRequest.Messages), message);
    }

    private static IResult IdempotencyConflict(MealSessionConflictException exception)
    {
        return ApiProblems.Problem(
            StatusCodes.Status409Conflict,
            "idempotency_conflict",
            "The idempotency key conflicts with an existing request.",
            exception.Message);
    }

    private static IResult InputNotSupported(NotSupportedException exception)
    {
        return ApiProblems.Problem(
            StatusCodes.Status422UnprocessableEntity,
            "input_not_supported",
            "The input sequence is not supported.",
            exception.Message);
    }

    private static MealDraftResponse MapResponse(
        CaptureSession session,
        MealDraft mealDraft)
    {
        List<DishDraftResponse> dishes = new List<DishDraftResponse>();

        foreach (DishDraft dish in mealDraft.Dishes)
        {
            List<IngredientDraftResponse> ingredients =
                new List<IngredientDraftResponse>();

            foreach (IngredientDraft ingredient in dish.Ingredients)
            {
                ingredients.Add(
                    new IngredientDraftResponse(
                        ingredient.ProductName,
                        ingredient.WeightInGrams,
                        ingredient.WeightQuality.ToString(),
                        ingredient.RemovedWeightInGrams,
                        ingredient.RemovedWeightQuality.ToString(),
                        ingredient.IncludedWeightInGrams));
            }

            List<PortionDraftResponse> portions = dish.Portions
                .Select(portion => new PortionDraftResponse(
                    portion.WeightInGrams,
                    portion.FractionOfDish,
                    portion.WeightQuality.ToString()))
                .ToList();

            dishes.Add(
                new DishDraftResponse(
                    dish.Name,
                    ingredients,
                    dish.FinalWeightInGrams,
                    dish.FinalWeightQuality.ToString(),
                    portions));
        }

        return new MealDraftResponse(
            session.State.ToString(),
            dishes,
            new List<string>(mealDraft.ClarificationQuestions),
            mealDraft.RequiresClarification);
    }
}
