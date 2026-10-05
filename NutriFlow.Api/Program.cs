using NutriFlow.Api.Configuration;
using NutriFlow.Api.Contracts;
using NutriFlow.Api.Endpoints;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
bool seedDemoData = builder.Configuration.GetValue("Demo:SeedData", false);
string aiProvider = builder.AddNutriFlowServices();
WebApplication app = builder.Build();

await app.ConfigureNutriFlowAsync(aiProvider, seedDemoData);

bool supportsAiInput = !aiProvider.Equals(
    "Fake",
    StringComparison.Ordinal);
app.MapGet(
        "/api/capabilities",
        () => Results.Ok(new ApplicationCapabilitiesResponse(
            aiProvider,
            supportsAiInput,
            supportsAiInput,
            aiProvider == "Groq")))
    .WithName("GetApplicationCapabilities")
    .WithSummary("Reports input features enabled by the configured AI provider.")
    .WithTags("Application")
    .Produces<ApplicationCapabilitiesResponse>(StatusCodes.Status200OK);

app.MapMealSessionEndpoints();
app.MapDailyDiaryEndpoints();
app.MapMealEntryEndpoints();
app.MapProductEndpoints();
app.MapMediaEndpoints();
app.MapSavedDishEndpoints();

app.Run();

public partial class Program
{
}
