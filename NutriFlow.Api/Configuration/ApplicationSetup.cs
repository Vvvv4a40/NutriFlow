using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using NutriFlow.Api.Services;
using NutriFlow.Infrastructure;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Api.Configuration;

internal static class ApplicationSetup
{
    public static async Task ConfigureNutriFlowAsync(this WebApplication app, string aiProvider)
    {
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
                ? badRequest.StatusCode
                : StatusCodes.Status500InternalServerError
        });

        app.UseStatusCodePages(async statusCodeContext =>
        {
            HttpContext context = statusCodeContext.HttpContext;
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                await Results.Problem(statusCode: context.Response.StatusCode)
                    .ExecuteAsync(context);
            }
        });

        if (app.Configuration.GetValue(
                "Database:ApplyMigrationsOnStartup",
                true))
        {
            await ApplyDatabaseMigrationsAsync(app.Services);
        }

        if (aiProvider.Equals("Fake", StringComparison.OrdinalIgnoreCase) &&
            app.Configuration.GetValue("Demo:SeedData", true))
        {
            await SeedDemoDataAsync(app.Services);
        }

        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Permissions-Policy"] =
                "camera=(), geolocation=(), microphone=(self)";

            bool isDevelopmentSwagger = app.Environment.IsDevelopment() &&
                                        context.Request.Path.StartsWithSegments("/swagger");
            if (!isDevelopmentSwagger)
            {
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'self'; " +
                    "script-src 'self'; " +
                    "style-src 'self' 'unsafe-inline'; " +
                    "img-src 'self' data: blob:; " +
                    "connect-src 'self'; " +
                    "object-src 'none'; " +
                    "frame-ancestors 'none'; " +
                    "base-uri 'self'; " +
                    "form-action 'self'";
            }

            await next(context);
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRateLimiter();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint("/openapi/v1.json", "NutriFlow API v1");
            });
        }

        app.MapHealthChecks(
            "/health/live",
            new HealthCheckOptions
            {
                Predicate = _ => false
            });
        app.MapHealthChecks(
            "/health/ready",
            new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains("ready")
            });
    }

    private static async Task ApplyDatabaseMigrationsAsync(IServiceProvider services)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        NutriFlowDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();

        await dbContext.Database.MigrateAsync();
    }

    private static async Task SeedDemoDataAsync(IServiceProvider services)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        LocalProductCatalog catalog =
            scope.ServiceProvider.GetRequiredService<LocalProductCatalog>();

        await DemoCatalogSeeder.SeedAsync(catalog);
    }
}
