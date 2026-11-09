using System.Globalization;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NutriFlow.Api.Endpoints;
using NutriFlow.Api.Services;
using NutriFlow.Domain;
using NutriFlow.Infrastructure;
using NutriFlow.Infrastructure.Ai;
using NutriFlow.Infrastructure.Audio;
using NutriFlow.Infrastructure.ExternalProducts;
using NutriFlow.Infrastructure.LabelPhotos;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Api.Configuration;

internal static class ServiceRegistration
{
    public static string AddNutriFlowServices(this WebApplicationBuilder builder)
    {
        string aiProvider = ConfigureAiProvider(builder);
        builder.Services.AddOpenApi(OpenApiConfiguration.Configure);
        builder.Services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
                context.ProblemDetails.Extensions.TryAdd(
                    ApiProblems.CodeKey,
                    ApiProblems.DefaultCode(context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode)));
        builder.Services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(
                "sqlite",
                tags: new[] { "ready" },
                timeout: TimeSpan.FromSeconds(5));
        ConfigureRateLimiting(builder.Services);
        ConfigureStorage(builder);
        ConfigureExternalProducts(builder);
        return aiProvider;
    }

    private static void ConfigureRateLimiting(IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(
                        MetadataName.RetryAfter,
                        out TimeSpan retryAfter))
                {
                    context.HttpContext.Response.Headers["Retry-After"] =
                        Math.Ceiling(retryAfter.TotalSeconds)
                            .ToString(CultureInfo.InvariantCulture);
                }

                await Results.Problem(
                        statusCode: StatusCodes.Status429TooManyRequests,
                        title: "Too many requests",
                        detail: "Wait before calling an external-service endpoint again.")
                    .ExecuteAsync(context.HttpContext);
            };
            options.AddPolicy(
                "external-services",
                context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 12,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));
        });
    }

    private static void ConfigureStorage(WebApplicationBuilder builder)
    {
        string? configuredDatabasePath = builder.Configuration["Database:Path"];

        if (string.IsNullOrWhiteSpace(configuredDatabasePath))
        {
            throw new InvalidOperationException("Database:Path must be configured.");
        }

        string databasePath = Path.GetFullPath(
            configuredDatabasePath,
            builder.Environment.ContentRootPath);
        string? databaseDirectory = Path.GetDirectoryName(databasePath);

        if (databaseDirectory is not null)
        {
            Directory.CreateDirectory(databaseDirectory);
        }

        string connectionString = $"Data Source={databasePath}";
        builder.Services.AddDbContext<NutriFlowDbContext>(
            options => options.UseSqlite(connectionString));
        builder.Services.AddScoped<LocalProductCatalog>();
        builder.Services.AddScoped<ProductLookupService>();
        builder.Services.AddScoped<MealSessionStore>();
        builder.Services.AddScoped<DailyDiaryStore>();
        builder.Services.AddScoped<SavedDishStore>();
        builder.Services.AddScoped<MealWorkflowService>();
        builder.Services.AddScoped<ManualMealService>();
        builder.Services.AddScoped<MealDraftEditService>();
        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit =
                Math.Max(
                    LabelPhotoValidator.MaximumFileSizeInBytes,
                    AudioUploadValidator.MaximumFileSizeInBytes) + 64 * 1024;
        });

        string configuredLabelPhotoPath =
            builder.Configuration["Storage:LabelPhotosPath"] ??
            "data/label-photos";
        string labelPhotoPath = Path.GetFullPath(
            configuredLabelPhotoPath,
            builder.Environment.ContentRootPath);
        builder.Services.AddScoped(serviceProvider => new LabelPhotoStore(
            serviceProvider.GetRequiredService<NutriFlowDbContext>(),
            labelPhotoPath));
    }

    private static void ConfigureExternalProducts(WebApplicationBuilder builder)
    {
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
    }

    private static string ConfigureAiProvider(WebApplicationBuilder builder)
    {
        string? configuredAiProvider = builder.Configuration["Ai:Provider"];

        if (string.IsNullOrWhiteSpace(configuredAiProvider))
        {
            throw new InvalidOperationException(
                "Ai:Provider must be configured. Use 'Fake' or 'Groq'.");
        }

        if (configuredAiProvider.Equals("Fake", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddSingleton<IMealParser, FakeMealParser>();
            builder.Services.AddSingleton<INutritionEstimator,
                UnavailableNutritionEstimator>();
            builder.Services.AddSingleton<INutritionLabelReader,
                UnavailableNutritionLabelReader>();
            builder.Services.AddSingleton<ISpeechTranscriber,
                UnavailableSpeechTranscriber>();
            return "Fake";
        }

        if (configuredAiProvider.Equals("Groq", StringComparison.OrdinalIgnoreCase))
        {
            ConfigureGroq(builder);
            return "Groq";
        }

        throw new InvalidOperationException(
            "Unsupported AI provider. Use 'Fake' or 'Groq'.");
    }

    // the same chain as on the phone: the main text model gives way to the reserve one on a 429, failures are repeated,
    // and a food the catalog does not know gets an estimate that stays in the catalog
    private static void ConfigureGroq(WebApplicationBuilder builder)
    {
        string? apiKey = builder.Configuration["Ai:Groq:ApiKey"];

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "Ai:Groq:ApiKey must be configured when the Groq provider is enabled.");
        }

        string groqBaseUrl =
            builder.Configuration["Ai:Groq:BaseUrl"] ??
            "https://api.groq.com/openai/v1/";
        Uri groqBaseAddress = new Uri(
            $"{groqBaseUrl.TrimEnd('/')}/",
            UriKind.Absolute);
        string textModel =
            builder.Configuration["Ai:Groq:TextModel"] ?? GroqModels.DefaultText;
        string reserveTextModel =
            builder.Configuration["Ai:Groq:ReserveTextModel"] ?? GroqModels.ReserveFor(textModel);
        string visionModel =
            builder.Configuration["Ai:Groq:VisionModel"] ?? GroqModels.Vision;
        string speechModel =
            builder.Configuration["Ai:Groq:SpeechModel"] ?? GroqModels.Speech;
        string? speechLanguage =
            builder.Configuration["Ai:Groq:SpeechLanguage"];

        builder.Services.AddHttpClient(
            GroqClients.TransportName,
            client =>
            {
                client.BaseAddress = groqBaseAddress;
                client.Timeout = TimeSpan.FromSeconds(45);
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);
            });
        builder.Services.AddScoped(serviceProvider => new GroqClients(
            serviceProvider.GetRequiredService<IHttpClientFactory>(),
            groqBaseAddress,
            serviceProvider.GetService<GroqRetryDelay>()));
        builder.Services.AddScoped(serviceProvider =>
        {
            GroqClients clients = serviceProvider.GetRequiredService<GroqClients>();
            return new GroqTextChain(clients.Immediate, textModel, clients.Waiting, reserveTextModel);
        });
        builder.Services.AddScoped(serviceProvider =>
            serviceProvider.GetRequiredService<GroqTextChain>().CreateEstimator());
        builder.Services.AddScoped<IMealParser>(serviceProvider =>
        {
            LocalProductCatalog catalog = serviceProvider.GetRequiredService<LocalProductCatalog>();
            return new AutoNutritionMealParser(
                new CatalogAwareMealParser(serviceProvider.GetRequiredService<GroqTextChain>(), catalog),
                catalog,
                serviceProvider.GetRequiredService<SavedDishStore>(),
                serviceProvider.GetRequiredService<INutritionEstimator>());
        });
        builder.Services.AddScoped<INutritionLabelReader>(serviceProvider =>
            new GroqNutritionLabelReader(
                serviceProvider.GetRequiredService<GroqClients>().Waiting,
                visionModel));
        builder.Services.AddScoped<ISpeechTranscriber>(serviceProvider =>
            new GroqSpeechTranscriber(
                serviceProvider.GetRequiredService<GroqClients>().Waiting,
                speechModel,
                speechLanguage));
    }
}
