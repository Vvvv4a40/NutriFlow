using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure;
using NutriFlow.Infrastructure.Ai;
using NutriFlow.Infrastructure.ExternalProducts;
using NutriFlow.Infrastructure.Persistence;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
string? configuredDatabasePath = builder.Configuration["Database:Path"];

if (string.IsNullOrWhiteSpace(configuredDatabasePath))
{
    throw new InvalidOperationException("Database:Path must be configured.");
}

string databasePath = Path.GetFullPath(
    configuredDatabasePath,
    builder.Environment.ContentRootPath);
string connectionString = $"Data Source={databasePath}";
builder.Services.AddDbContext<NutriFlowDbContext>(
    options => options.UseSqlite(connectionString));
builder.Services.AddScoped<LocalProductCatalog>();
builder.Services.AddScoped<ProductLookupService>();

string openFoodFactsBaseUrl =
    builder.Configuration["OpenFoodFacts:BaseUrl"] ??
    "https://world.openfoodfacts.org/";
string openFoodFactsUserAgent =
    builder.Configuration["OpenFoodFacts:UserAgent"] ??
    "NutriFlow/1.0 (https://github.com/Vvvv4a40/NutriFlow)";

builder.Services.AddHttpClient<IExternalProductProvider, OpenFoodFactsClient>(
    client =>
    {
        client.BaseAddress = new Uri(openFoodFactsBaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            openFoodFactsUserAgent);
    });

string aiProvider = builder.Configuration["Ai:Provider"] ?? "Fake";

if (aiProvider.Equals("Fake", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IMealParser, FakeMealParser>();
}
else if (aiProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
{
    string? apiKey = builder.Configuration["Ai:OpenAI:ApiKey"];

    if (string.IsNullOrWhiteSpace(apiKey))
    {
        throw new InvalidOperationException(
            "Ai:OpenAI:ApiKey must be configured when the OpenAI provider is enabled.");
    }

    string openAiBaseUrl =
        builder.Configuration["Ai:OpenAI:BaseUrl"] ??
        "https://api.openai.com/v1/";
    string openAiModel =
        builder.Configuration["Ai:OpenAI:Model"] ??
        "gpt-5.4-mini";

    builder.Services.AddHttpClient(
        "OpenAI",
        client =>
        {
            client.BaseAddress = new Uri(openAiBaseUrl, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(45);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        });
    builder.Services.AddScoped<IMealParser>(serviceProvider =>
    {
        IHttpClientFactory httpClientFactory =
            serviceProvider.GetRequiredService<IHttpClientFactory>();

        return new OpenAiMealParser(
            httpClientFactory.CreateClient("OpenAI"),
            openAiModel);
    });
}
else
{
    throw new InvalidOperationException(
        $"Unsupported AI provider '{aiProvider}'. Use 'Fake' or 'OpenAI'.");
}

WebApplication app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await ApplyDatabaseMigrationsAsync(app.Services);
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "NutriFlow API v1");
    });
}

app.MapPost("/api/meal-drafts/parse", ParseMealDraftAsync)
    .WithName("ParseMealDraft")
    .WithSummary("Builds a structured meal draft from captured messages.")
    .WithTags("Meal drafts")
    .Produces<MealDraftResponse>(StatusCodes.Status200OK)
    .ProducesValidationProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
    .ProducesProblem(StatusCodes.Status502BadGateway)
    .ProducesProblem(StatusCodes.Status504GatewayTimeout);

app.MapPost("/api/products/manual", CreateManualProductAsync)
    .WithName("CreateManualProduct")
    .WithSummary("Stores manually entered nutrition values in the local catalog.")
    .WithTags("Products")
    .Produces<ProductResponse>(StatusCodes.Status201Created)
    .ProducesValidationProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status409Conflict);

app.MapGet("/api/products", FindLocalProductsAsync)
    .WithName("FindLocalProducts")
    .WithSummary("Finds local products by an exact normalized name.")
    .WithTags("Products")
    .Produces<List<ProductResponse>>(StatusCodes.Status200OK)
    .ProducesValidationProblem(StatusCodes.Status400BadRequest);

app.MapGet("/api/products/barcode/{barcode}", FindProductByBarcodeAsync)
    .WithName("FindProductByBarcode")
    .WithSummary("Finds a product locally or retrieves and caches it from Open Food Facts.")
    .WithTags("Products")
    .Produces<ProductResponse>(StatusCodes.Status200OK)
    .ProducesValidationProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
    .ProducesProblem(StatusCodes.Status502BadGateway)
    .ProducesProblem(StatusCodes.Status504GatewayTimeout);

app.Run();

static async Task ApplyDatabaseMigrationsAsync(IServiceProvider services)
{
    await using AsyncServiceScope scope = services.CreateAsyncScope();
    NutriFlowDbContext dbContext =
        scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();

    await dbContext.Database.MigrateAsync();
}

static async Task<IResult> CreateManualProductAsync(
    CreateManualProductRequest? request,
    LocalProductCatalog catalog,
    CancellationToken cancellationToken)
{
    if (request is null)
    {
        return InvalidProduct("A product is required.");
    }

    if (request.IsEstimated is null)
    {
        return InvalidProduct("IsEstimated must be specified.");
    }

    try
    {
        NutritionValues nutrition = new NutritionValues(
            request.Calories,
            request.ProteinGrams,
            request.FatGrams,
            request.CarbohydratesGrams);
        NutritionSource source = new NutritionSource(
            NutritionSourceKind.ManualInput,
            request.IsEstimated.Value
                ? DataQuality.Estimated
                : DataQuality.Exact,
            "Manual input through NutriFlow API");
        Product product = new Product(
            request.Name!,
            nutrition,
            source,
            request.Barcode);

        bool wasAdded = await catalog.AddAsync(product, cancellationToken);

        if (!wasAdded)
        {
            return Results.Problem(
                detail: "An identical product already exists in the local catalog.",
                statusCode: StatusCodes.Status409Conflict,
                title: "The product already exists.");
        }

        string location =
            $"/api/products?name={Uri.EscapeDataString(product.Name)}";
        return Results.Created(location, MapProductResponse(product));
    }
    catch (ArgumentException exception)
    {
        return InvalidProduct(exception.Message);
    }
}

static async Task<IResult> FindProductByBarcodeAsync(
    string barcode,
    ProductLookupService lookupService,
    CancellationToken cancellationToken)
{
    try
    {
        Product? product = await lookupService.FindByBarcodeAsync(
            barcode,
            cancellationToken);

        return product is null
            ? Results.Problem(
                detail: "The product was not found in the local catalog or Open Food Facts.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Product not found.")
            : Results.Ok(MapProductResponse(product));
    }
    catch (ArgumentException exception)
    {
        return Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                ["barcode"] = new[] { exception.Message }
            });
    }
    catch (InvalidDataException exception)
    {
        return Results.Problem(
            detail: exception.Message,
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "External product data is incomplete.");
    }
    catch (HttpRequestException)
    {
        return Results.Problem(
            detail: "Open Food Facts is temporarily unavailable.",
            statusCode: StatusCodes.Status502BadGateway,
            title: "External product lookup failed.");
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem(
            detail: "Open Food Facts did not respond before the timeout.",
            statusCode: StatusCodes.Status504GatewayTimeout,
            title: "External product lookup timed out.");
    }
}

static async Task<IResult> FindLocalProductsAsync(
    string? name,
    LocalProductCatalog catalog,
    CancellationToken cancellationToken)
{
    try
    {
        IReadOnlyList<Product> products =
            await catalog.FindByNameAsync(name!, cancellationToken);
        List<ProductResponse> response = products
            .Select(MapProductResponse)
            .ToList();

        return Results.Ok(response);
    }
    catch (ArgumentException exception)
    {
        return InvalidProduct(exception.Message);
    }
}

static async Task<IResult> ParseMealDraftAsync(
    ParseMealDraftRequest? request,
    IMealParser parser,
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
        return Results.Problem(
            detail: exception.Message,
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "The input sequence is not supported.");
    }
    catch (InvalidDataException)
    {
        return Results.Problem(
            detail: "The AI provider returned an invalid structured meal draft.",
            statusCode: StatusCodes.Status502BadGateway,
            title: "Meal parsing failed.");
    }
    catch (HttpRequestException)
    {
        return Results.Problem(
            detail: "The AI provider is temporarily unavailable.",
            statusCode: StatusCodes.Status502BadGateway,
            title: "Meal parsing failed.");
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem(
            detail: "The AI provider did not respond before the timeout.",
            statusCode: StatusCodes.Status504GatewayTimeout,
            title: "Meal parsing timed out.");
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

static IResult InvalidProduct(string message)
{
    return Results.ValidationProblem(
        new Dictionary<string, string[]>
        {
            ["product"] = new[] { message }
        });
}

static ProductResponse MapProductResponse(Product product)
{
    return new ProductResponse(
        product.Name,
        product.NutritionPer100Grams.Calories,
        product.NutritionPer100Grams.ProteinGrams,
        product.NutritionPer100Grams.FatGrams,
        product.NutritionPer100Grams.CarbohydratesGrams,
        product.Source.Kind.ToString(),
        product.Source.Quality.ToString(),
        product.Source.Name,
        product.Source.Reference,
        product.Barcode);
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
                    ingredient.WeightInGrams,
                    ingredient.WeightQuality.ToString()));
        }

        List<PortionDraftResponse> portions = dish.Portions
            .Select(portion => new PortionDraftResponse(
                portion.WeightInGrams,
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
