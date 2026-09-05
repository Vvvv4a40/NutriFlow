using NutriFlow.Api.Contracts;
using NutriFlow.Domain;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<FakeMealParser>();

WebApplication app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "NutriFlow API v1");
    });
}

app.MapPost("/api/meal-drafts/parse", ParseMealDraft)
    .WithName("ParseMealDraft")
    .WithSummary("Builds a structured meal draft from captured messages.")
    .WithTags("Meal drafts")
    .Produces<MealDraftResponse>(StatusCodes.Status200OK)
    .ProducesValidationProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

app.Run();

static IResult ParseMealDraft(
    ParseMealDraftRequest? request,
    FakeMealParser parser)
{
    if (request is null ||
        request.Messages is null ||
        request.Messages.Count == 0)
    {
        return InvalidMessages(
            "At least one captured message is required.");
    }

    CaptureSession session = new CaptureSession();

    foreach (string? message in request.Messages)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return InvalidMessages(
                "Messages cannot contain null, empty, or whitespace-only values.");
        }

        session.AddEvent(new InputEvent(message));
    }

    session.FinishCollecting();

    try
    {
        MealDraft mealDraft = parser.Parse(session);
        return Results.Ok(MapResponse(session, mealDraft));
    }
    catch (NotSupportedException exception)
    {
        return Results.Problem(
            detail: exception.Message,
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "The input sequence is not supported.");
    }
}

static IResult InvalidMessages(string message)
{
    return Results.ValidationProblem(
        new Dictionary<string, string[]>
        {
            [nameof(ParseMealDraftRequest.Messages)] = new[] { message }
        });
}

static MealDraftResponse MapResponse(
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
                    ingredient.WeightInGrams));
        }

        dishes.Add(
            new DishDraftResponse(
                dish.Name,
                ingredients,
                dish.FinalWeightInGrams,
                new List<decimal>(dish.PortionWeightsInGrams)));
    }

    return new MealDraftResponse(
        session.State.ToString(),
        dishes,
        new List<string>(mealDraft.ClarificationQuestions),
        mealDraft.RequiresClarification);
}
