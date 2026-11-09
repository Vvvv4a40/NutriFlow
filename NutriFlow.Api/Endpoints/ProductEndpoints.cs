using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure;
using NutriFlow.Infrastructure.LabelPhotos;

namespace NutriFlow.Api.Endpoints;

internal static class ProductEndpoints
{
    private const int DefaultSearchLimit = 50;

    public static void MapProductEndpoints(this WebApplication app)
    {
        app.MapPost("/api/products/manual", CreateManualProductAsync)
            .WithName("CreateManualProduct")
            .WithSummary("Stores manually entered nutrition values in the local catalog.")
            .WithTags("Products")
            .Produces<ProductResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        app.MapPost("/api/products/from-label", CreateLabelProductAsync)
            .WithName("CreateLabelProduct")
            .WithSummary("Stores reviewed per-100-gram values from a saved label photo.")
            .WithTags("Products")
            .Produces<ProductResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        app.MapPost("/api/products/aliases", AddProductAliasAsync)
            .WithName("AddProductAlias")
            .WithSummary("Links a captured ingredient name to a product identified by barcode.")
            .WithTags("Products")
            .Produces<ProductResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        app.MapGet("/api/products", FindLocalProductsAsync)
            .WithName("FindLocalProducts")
            .WithSummary("Finds products by an exact name, or searches names and barcodes by q.")
            .WithTags("Products")
            .Produces<List<ProductResponse>>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest);

        app.MapGet("/api/products/barcode/{barcode}", FindProductByBarcodeAsync)
            .WithName("FindProductByBarcode")
            .RequireRateLimiting("external-services")
            .WithSummary("Finds a product locally or retrieves and caches it from Open Food Facts.")
            .WithTags("Products")
            .Produces<ProductResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static async Task<IResult> CreateManualProductAsync(
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

            StoredProduct? added = await catalog.AddStoredAsync(product, cancellationToken);

            if (added is null)
            {
                return ApiProblems.Problem(
                    StatusCodes.Status409Conflict,
                    "product_exists",
                    "The product already exists.",
                    "A matching product already exists, or this barcode has data of equal or better quality.");
            }

            string location =
                $"/api/products?name={Uri.EscapeDataString(product.Name)}";
            return Results.Created(location, ResponseMapper.ToProductResponse(added));
        }
        catch (ArgumentException exception)
        {
            return InvalidProduct(exception.Message);
        }
    }

    private static async Task<IResult> CreateLabelProductAsync(
        CreateLabelProductRequest? request,
        LocalProductCatalog catalog,
        LabelPhotoStore photoStore,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return InvalidProduct("A reviewed label product is required.");
        }

        if (request.Basis != NutritionBasis.Per100Grams)
        {
            return InvalidProduct(
                "Basis must be Per100Grams before label values can be saved.");
        }

        if (!await photoStore.ContainsAsync(request.PhotoReference!, cancellationToken))
        {
            return InvalidProduct(
                "PhotoReference must identify a label photo saved by NutriFlow.");
        }

        try
        {
            NutritionValues nutrition = new NutritionValues(
                request.Calories,
                request.ProteinGrams,
                request.FatGrams,
                request.CarbohydratesGrams);
            NutritionSource source = new NutritionSource(
                NutritionSourceKind.LabelPhoto,
                DataQuality.Verified,
                "Reviewed nutrition label",
                request.PhotoReference);
            Product product = new Product(
                request.Name!,
                nutrition,
                source,
                request.Barcode);
            StoredProduct? added = await catalog.AddStoredAsync(product, cancellationToken);

            if (added is null)
            {
                return ApiProblems.Problem(
                    StatusCodes.Status409Conflict,
                    "product_exists",
                    "The product already exists.",
                    "A matching product already exists, or this barcode has data of equal or better quality.");
            }

            string location =
                $"/api/products?name={Uri.EscapeDataString(product.Name)}";
            return Results.Created(location, ResponseMapper.ToProductResponse(added));
        }
        catch (ArgumentException exception)
        {
            return InvalidProduct(exception.Message);
        }
    }

    private static async Task<IResult> FindProductByBarcodeAsync(
        string barcode,
        ProductLookupService lookupService,
        CancellationToken cancellationToken)
    {
        try
        {
            StoredProduct? product = await lookupService.FindStoredByBarcodeAsync(
                barcode,
                cancellationToken);

            return product is null
                ? ApiProblems.Problem(
                    StatusCodes.Status404NotFound,
                    "product_not_found",
                    "Product not found.",
                    "The product was not found in the local catalog or Open Food Facts.")
                : Results.Ok(ResponseMapper.ToProductResponse(product));
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

    private static async Task<IResult> AddProductAliasAsync(
        AddProductAliasRequest? request,
        LocalProductCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return InvalidProduct("A product alias request is required.");
        }

        try
        {
            await catalog.AddAliasByBarcodeAsync(
                request.Barcode!,
                request.Alias!,
                cancellationToken);
            StoredProduct product = await catalog.FindStoredByBarcodeAsync(request.Barcode!, cancellationToken)
                ?? throw new KeyNotFoundException("The product disappeared while its alias was added.");

            return Results.Ok(ResponseMapper.ToProductResponse(product));
        }
        catch (ArgumentException exception)
        {
            return InvalidProduct(exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            return ApiProblems.Problem(
                StatusCodes.Status404NotFound,
                "product_not_found",
                "Product not found.",
                exception.Message);
        }
    }

    private static async Task<IResult> FindLocalProductsAsync(
        string? name,
        string? q,
        int? limit,
        LocalProductCatalog catalog,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<StoredProduct> products = name is not null
                ? await catalog.FindStoredByNameAsync(name, cancellationToken)
                : await catalog.SearchStoredAsync(q, limit ?? DefaultSearchLimit, cancellationToken);
            List<ProductResponse> response = products
                .Select(product => ResponseMapper.ToProductResponse(product))
                .ToList();

            return Results.Ok(response);
        }
        catch (ArgumentException exception)
        {
            return InvalidProduct(exception.Message);
        }
    }

    private static IResult InvalidProduct(string message)
    {
        return ApiProblems.Invalid("product", message);
    }
}
