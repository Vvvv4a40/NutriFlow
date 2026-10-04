using System.Globalization;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
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
        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails();
        builder.Services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(
                "sqlite",
                tags: new[] { "ready" },
                timeout: TimeSpan.FromSeconds(5));
        ConfigureRateLimiting(builder.Services);
        ConfigureStorage(builder);
        ConfigureExternalProducts(builder);
        return ConfigureAiProvider(builder);
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
        builder.Services.AddScoped<MealWorkflowService>();
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
        string configuredAiProvider = builder.Configuration["Ai:Provider"] ?? "Fake";
        string aiProvider;

        if (configuredAiProvider.Equals("Fake", StringComparison.OrdinalIgnoreCase))
        {
            aiProvider = "Fake";
            builder.Services.AddSingleton<IMealParser, FakeMealParser>();
            builder.Services.AddSingleton<INutritionLabelReader,
                UnavailableNutritionLabelReader>();
            builder.Services.AddSingleton<ISpeechTranscriber,
                UnavailableSpeechTranscriber>();
        }
        else if (configuredAiProvider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            aiProvider = "OpenAI";
            builder.Services.AddSingleton<ISpeechTranscriber,
                UnavailableSpeechTranscriber>();
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
            builder.Services.AddScoped<INutritionLabelReader>(serviceProvider =>
            {
                IHttpClientFactory httpClientFactory =
                    serviceProvider.GetRequiredService<IHttpClientFactory>();

                return new OpenAiNutritionLabelReader(
                    httpClientFactory.CreateClient("OpenAI"),
                    openAiModel);
            });
        }
        else if (configuredAiProvider.Equals("Groq", StringComparison.OrdinalIgnoreCase))
        {
            aiProvider = "Groq";
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
            string groqTextModel =
                builder.Configuration["Ai:Groq:TextModel"] ??
                "qwen/qwen3.8-27b";
            string groqVisionModel =
                builder.Configuration["Ai:Groq:VisionModel"] ??
                "qwen/qwen3.8-27b";
            string groqSpeechModel =
                builder.Configuration["Ai:Groq:SpeechModel"] ??
                "whisper-large-v3-turbo";
            string? groqSpeechLanguage =
                builder.Configuration["Ai:Groq:SpeechLanguage"];

            builder.Services.AddHttpClient(
                "Groq",
                client =>
                {
                    client.BaseAddress = groqBaseAddress;
                    client.Timeout = TimeSpan.FromSeconds(45);
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", apiKey);
                });
            builder.Services.AddScoped<IMealParser>(serviceProvider =>
            {
                IHttpClientFactory httpClientFactory =
                    serviceProvider.GetRequiredService<IHttpClientFactory>();

                return new GroqMealParser(
                    httpClientFactory.CreateClient("Groq"),
                    groqTextModel);
            });
            builder.Services.AddScoped<INutritionLabelReader>(serviceProvider =>
            {
                IHttpClientFactory httpClientFactory =
                    serviceProvider.GetRequiredService<IHttpClientFactory>();

                return new GroqNutritionLabelReader(
                    httpClientFactory.CreateClient("Groq"),
                    groqVisionModel);
            });
            builder.Services.AddScoped<ISpeechTranscriber>(serviceProvider =>
            {
                IHttpClientFactory httpClientFactory =
                    serviceProvider.GetRequiredService<IHttpClientFactory>();

                return new GroqSpeechTranscriber(
                    httpClientFactory.CreateClient("Groq"),
                    groqSpeechModel,
                    groqSpeechLanguage);
            });
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported AI provider '{configuredAiProvider}'. Use 'Fake', 'OpenAI', or 'Groq'.");
        }

        return aiProvider;
    }
}
