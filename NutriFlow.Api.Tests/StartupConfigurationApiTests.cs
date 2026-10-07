using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NutriFlow.Api.Contracts;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Api.Tests;

public sealed class StartupConfigurationApiTests
{
    private const string UnrelatedKey = "startup-test-key-must-not-appear-in-errors";

    [Fact]
    public async Task DevelopmentDefaults_EnableFakeProviderAndDemoData()
    {
        await using StartupApiFactory factory = new("Development");
        using HttpClient client = factory.CreateClient();

        await AssertCapabilitiesAsync(client, "Fake", supportsAiInput: false, supportsSpeech: false);
        Assert.Equal(2, await factory.CountProductsAsync());
    }

    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "")]
    [InlineData("Production", " ")]
    [InlineData("Staging", null)]
    public async Task MissingProviderOutsideDevelopment_FailsBeforeCreatingStorage(
        string environment,
        string? provider)
    {
        await using StartupApiFactory factory = new(environment, provider);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => factory.CreateClient());

        Assert.Contains("Ai:Provider", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(UnrelatedKey, exception.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(factory.DirectoryPath));
    }

    [Fact]
    public async Task UnknownProvider_FailsBeforeCreatingStorage()
    {
        await using StartupApiFactory factory = new("Production", UnrelatedKey);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => factory.CreateClient());

        Assert.Contains("Unsupported AI provider", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(UnrelatedKey, exception.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(factory.DirectoryPath));
    }

    [Theory]
    [InlineData("Groq", null)]
    [InlineData("Groq", "")]
    [InlineData("Groq", " ")]
    [InlineData("OpenAI", null)]
    [InlineData("OpenAI", "")]
    [InlineData("OpenAI", " ")]
    public async Task RealProviderWithoutKey_FailsBeforeCreatingStorage(string provider, string? apiKey)
    {
        await using StartupApiFactory factory = new("Production", provider, apiKey: apiKey);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => factory.CreateClient());

        Assert.Contains($"Ai:{provider}:ApiKey", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(UnrelatedKey, exception.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(factory.DirectoryPath));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public async Task ExplicitFakeProviderInProduction_RespectsDemoSetting(bool? seedDemoData, int productCount)
    {
        await using StartupApiFactory factory = new("Production", "Fake", seedDemoData);
        using HttpClient client = factory.CreateClient();

        await AssertCapabilitiesAsync(client, "Fake", supportsAiInput: false, supportsSpeech: false);
        Assert.Equal(productCount, await factory.CountProductsAsync());
    }

    [Fact]
    public async Task DevelopmentWithExplicitSeedDisabled_DoesNotAddDemoProducts()
    {
        await using StartupApiFactory factory = new("Development", seedDemoData: false);
        using HttpClient client = factory.CreateClient();

        await AssertCapabilitiesAsync(client, "Fake", supportsAiInput: false, supportsSpeech: false);
        Assert.Equal(0, await factory.CountProductsAsync());
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task InvalidDemoSetting_FailsBeforeCreatingStorage(string environment)
    {
        await using StartupApiFactory factory = new(environment, "Fake", seedDemoDataValue: "invalid");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => factory.CreateClient());

        Assert.Contains("Demo:SeedData", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(factory.DirectoryPath));
    }

    [Theory]
    [InlineData("gRoQ", "Groq", true)]
    [InlineData("oPeNaI", "OpenAI", false)]
    public async Task RealProviderWithKey_StartsWithoutCallingExternalServicesOrSeedingDemoData(
        string configuredProvider,
        string expectedProvider,
        bool supportsSpeech)
    {
        await using StartupApiFactory factory = new(
            "Production", configuredProvider, seedDemoData: true, apiKey: "startup-dummy-key");
        using HttpClient client = factory.CreateClient();

        await AssertCapabilitiesAsync(client, expectedProvider, supportsAiInput: true, supportsSpeech);
        Assert.Equal(0, await factory.CountProductsAsync());
        Assert.Equal(0, factory.ExternalRequestCount);
    }

    private static async Task AssertCapabilitiesAsync(
        HttpClient client,
        string provider,
        bool supportsAiInput,
        bool supportsSpeech)
    {
        using HttpResponseMessage readiness = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        ApplicationCapabilitiesResponse capabilities = Assert.IsType<ApplicationCapabilitiesResponse>(
            await client.GetFromJsonAsync<ApplicationCapabilitiesResponse>("/api/capabilities"));
        Assert.Equal(provider, capabilities.AiProvider);
        Assert.Equal(supportsAiInput, capabilities.SupportsFreeText);
        Assert.Equal(supportsAiInput, capabilities.SupportsLabelPhotos);
        Assert.Equal(supportsSpeech, capabilities.SupportsSpeechTranscription);
    }

    private sealed class StartupApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _environment;
        private readonly string? _provider;
        private readonly bool? _seedDemoData;
        private readonly string? _apiKey;
        private readonly string? _seedDemoDataValue;
        private readonly string _contentRootPath = Path.Combine(
            Path.GetTempPath(), $"nutriflow-startup-{Guid.NewGuid():N}");
        private int _externalRequestCount;

        public StartupApiFactory(
            string environment,
            string? provider = null,
            bool? seedDemoData = null,
            string? apiKey = null,
            string? seedDemoDataValue = null)
        {
            _environment = environment;
            _provider = provider;
            _seedDemoData = seedDemoData;
            _apiKey = apiKey;
            _seedDemoDataValue = seedDemoDataValue;
        }

        public string DirectoryPath => Path.Combine(_contentRootPath, "data");
        public int ExternalRequestCount => Volatile.Read(ref _externalRequestCount);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            string sourceContentRoot = builder.GetSetting(WebHostDefaults.ContentRootKey) ??
                throw new InvalidOperationException("The API content root must be available to the test factory.");
            Directory.CreateDirectory(_contentRootPath);
            File.Copy(Path.Combine(sourceContentRoot, "appsettings.json"),
                Path.Combine(_contentRootPath, "appsettings.json"));
            string environmentSettings = $"appsettings.{_environment}.json";

            if (File.Exists(Path.Combine(sourceContentRoot, environmentSettings)))
            {
                File.Copy(Path.Combine(sourceContentRoot, environmentSettings),
                    Path.Combine(_contentRootPath, environmentSettings));
            }

            using ConfigurationManager configuration = new();
            configuration.SetBasePath(_contentRootPath)
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile(environmentSettings, optional: true);
            builder.UseEnvironment(_environment);
            builder.UseContentRoot(_contentRootPath);
            builder.UseSetting(WebHostDefaults.ApplicationKey, typeof(StartupConfigurationApiTests).Assembly.GetName().Name);
            builder.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureServices(services => services.ConfigureHttpClientDefaults(client =>
                client.ConfigurePrimaryHttpMessageHandler(() => new RejectExternalRequestsHandler(
                    () => Interlocked.Increment(ref _externalRequestCount)))));
            Dictionary<string, string?> overrides = new()
            {
                ["Database:Path"] = Path.Combine(DirectoryPath, "nutriflow.db"),
                ["Storage:LabelPhotosPath"] = Path.Combine(DirectoryPath, "label-photos"),
                ["Database:ApplyMigrationsOnStartup"] = "true",
                ["Ai:Provider"] = configuration["Ai:Provider"] ?? "",
                ["Demo:SeedData"] = configuration["Demo:SeedData"] ?? "false",
                ["Ai:Groq:ApiKey"] = "",
                ["Ai:OpenAI:ApiKey"] = "",
                ["Ai:Groq:BaseUrl"] = "https://groq.test/openai/v1/",
                ["Ai:Groq:TextModel"] = "test-text-model",
                ["Ai:Groq:VisionModel"] = "test-vision-model",
                ["Ai:Groq:SpeechModel"] = "test-speech-model",
                ["Ai:Groq:SpeechLanguage"] = "ru",
                ["Ai:OpenAI:BaseUrl"] = "https://openai.test/v1/",
                ["Ai:OpenAI:Model"] = "test-model"
            };

            if (_provider is not null)
            {
                overrides["Ai:Provider"] = _provider;
            }

            if (_seedDemoData is not null)
            {
                overrides["Demo:SeedData"] = _seedDemoData.Value.ToString();
            }

            if (_seedDemoDataValue is not null)
            {
                overrides["Demo:SeedData"] = _seedDemoDataValue;
            }

            if (_provider?.Equals("Groq", StringComparison.OrdinalIgnoreCase) == true)
            {
                overrides["Ai:OpenAI:ApiKey"] = UnrelatedKey;

                if (_apiKey is not null)
                {
                    overrides["Ai:Groq:ApiKey"] = _apiKey;
                }
            }
            else if (_provider?.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) == true)
            {
                overrides["Ai:Groq:ApiKey"] = UnrelatedKey;

                if (_apiKey is not null)
                {
                    overrides["Ai:OpenAI:ApiKey"] = _apiKey;
                }
            }
            else
            {
                overrides["Ai:OpenAI:ApiKey"] = UnrelatedKey;
                overrides["Ai:Groq:ApiKey"] = UnrelatedKey;
            }

            foreach (KeyValuePair<string, string?> setting in overrides)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
        }

        public async Task<int> CountProductsAsync()
        {
            IWebHostEnvironment environment = Services.GetRequiredService<IWebHostEnvironment>();
            Assert.Equal(_contentRootPath, environment.ContentRootPath);
            Assert.Equal(typeof(StartupConfigurationApiTests).Assembly.GetName().Name, environment.ApplicationName);
            await using AsyncServiceScope scope = Services.CreateAsyncScope();
            NutriFlowDbContext database = scope.ServiceProvider.GetRequiredService<NutriFlowDbContext>();
            Assert.Equal(Path.Combine(DirectoryPath, "nutriflow.db"),
                new SqliteConnectionStringBuilder(database.Database.GetConnectionString()).DataSource);
            Assert.Equal(Path.Combine(DirectoryPath, "label-photos"),
                Services.GetRequiredService<IConfiguration>()["Storage:LabelPhotosPath"]);
            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(DirectoryPath, "nutriflow.db"),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Products";
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            TestDatabasePool.Clear(Path.Combine(DirectoryPath, "nutriflow.db"));

            if (Directory.Exists(_contentRootPath))
            {
                Directory.Delete(_contentRootPath, recursive: true);
            }
        }
    }

    private sealed class RejectExternalRequestsHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            onRequest();
            throw new InvalidOperationException("External requests are not allowed in startup tests.");
        }
    }
}
