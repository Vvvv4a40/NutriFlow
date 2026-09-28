using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Audio;

namespace NutriFlow.Api.Tests;

internal sealed class TestApiFactory : WebApplicationFactory<Program>
{
    private readonly bool _applyMigrations;
    private readonly string _directoryPath = Path.Combine(
        Path.GetTempPath(),
        $"nutriflow-api-{Guid.NewGuid():N}");
    private readonly bool _seedDemoData;
    private readonly IMealParser? _parser;
    private readonly string _aiProvider;
    private readonly ISpeechTranscriber? _transcriber;
    private readonly string? _databasePath;
    private readonly string _environment;

    public TestApiFactory(
        bool applyMigrations = true,
        bool seedDemoData = true,
        IMealParser? parser = null,
        string aiProvider = "Fake",
        ISpeechTranscriber? transcriber = null,
        string? databasePath = null,
        string environment = "Development")
    {
        _applyMigrations = applyMigrations;
        _seedDemoData = seedDemoData;
        _parser = parser;
        _aiProvider = aiProvider;
        _transcriber = transcriber;
        _databasePath = databasePath;
        _environment = environment;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_directoryPath);
        builder.UseEnvironment(_environment);
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.UseSetting(
            "Database:Path",
            _databasePath ?? Path.Combine(_directoryPath, "nutriflow.db"));
        builder.UseSetting(
            "Database:ApplyMigrationsOnStartup",
            _applyMigrations.ToString());
        builder.UseSetting(
            "Storage:LabelPhotosPath",
            Path.Combine(_directoryPath, "label-photos"));
        builder.UseSetting("Ai:Provider", _aiProvider);
        builder.UseSetting("Ai:OpenAI:ApiKey", "test-key");
        builder.UseSetting("Ai:Groq:ApiKey", "test-key");
        builder.UseSetting("Ai:Groq:BaseUrl", "https://api.groq.test/openai/v1");
        builder.UseSetting("Ai:Groq:TextModel", "qwen-test");
        builder.UseSetting("Ai:Groq:VisionModel", "qwen-vision-test");
        builder.UseSetting("Ai:Groq:SpeechModel", "whisper-test");
        builder.UseSetting("Demo:SeedData", _seedDemoData.ToString());

        if (_parser is not null)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMealParser>();
                services.AddSingleton(_parser);
            });
        }

        if (_transcriber is not null)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISpeechTranscriber>();
                services.AddSingleton(_transcriber);
            });
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_directoryPath))
        {
            Directory.Delete(_directoryPath, recursive: true);
        }
    }
}
