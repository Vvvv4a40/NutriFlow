using System.Net;
using System.Text;
using System.Text.Json;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Ai;

namespace NutriFlow.Mobile.Core.Tests;

public sealed class GroqReliabilityTests : IDisposable
{
    private const string Draft = """
        {"dishes":[
          {"name":"Гречка варёная","ingredients":[{"productName":"Гречка варёная","weightInGrams":200,"weightQuality":"exact","removedWeightInGrams":0,"removedWeightQuality":"exact"}],"finalWeightInGrams":200,"finalWeightQuality":"exact","portions":[{"weightInGrams":200,"fractionOfDish":null,"weightQuality":"exact"}]}
        ],"clarificationQuestions":[]}
        """;

    private const string Estimate = """
        {"items":[{"name":"Гречка варёная","known":true,"calories":110,"proteinGrams":4,"fatGrams":1,"carbohydratesGrams":21}]}
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nutriflow-groq-tests", Guid.NewGuid().ToString("N"));
    private readonly DateOnly _date = new(2026, 11, 4);
    private readonly List<TimeSpan> _waits = [];
    private readonly List<string> _models = [];
    private readonly Queue<Func<HttpResponseMessage>> _script = new();
    private readonly GroqSettings _settings = new("key", "main-model");

    [Fact]
    public async Task ALostRequestIsRepeatedWithoutTheUserNoticing()
    {
        (LocalMealClient client, LocalProfile profile) = await CreateAsync();
        _script.Enqueue(() => throw new HttpRequestException("Failed to fetch"));
        _script.Enqueue(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        MealSessionResponse session = await client.CreateSessionAsync(profile.Id, ["гречка 200 г"], _date, MealSessionPurpose.Diary, _settings);

        Assert.Equal(2, _waits.Count);
        Assert.Equal("ReadyForConfirmation", session.Status);
    }

    [Fact]
    public async Task WhenTheMainModelIsLimitedTheReserveAnswersAtOnce()
    {
        (LocalMealClient client, LocalProfile profile) = await CreateAsync();
        _script.Enqueue(() => Limit("Rate limit reached on tokens per minute (TPM). Please try again in 9.5s."));

        await client.CreateSessionAsync(profile.Id, ["гречка 200 г"], _date, MealSessionPurpose.Diary, _settings);

        Assert.Empty(_waits);
        Assert.Equal(["main-model", GroqSettings.ReserveTextModel], _models.Take(2));
    }

    [Fact]
    public async Task TheReserveWaitsForAShortLimitByTheTimeInTheErrorText()
    {
        (LocalMealClient client, LocalProfile profile) = await CreateAsync();
        _script.Enqueue(() => Limit("Please try again in 9.5s."));
        _script.Enqueue(() => Limit("Please try again in 1m2.5s."));

        await Assert.ThrowsAsync<GroqRateLimitException>(() =>
            client.CreateSessionAsync(profile.Id, ["гречка 200 г"], _date, MealSessionPurpose.Diary, _settings));
        Assert.Empty(_waits);

        _script.Enqueue(() => Limit("Please try again in 9.5s."));
        _script.Enqueue(() => Limit("Please try again in 12s."));
        await client.CreateSessionAsync(profile.Id, ["гречка 200 г"], _date, MealSessionPurpose.Diary, _settings);
        Assert.Equal(TimeSpan.FromSeconds(12.3), Assert.Single(_waits));
    }

    [Fact]
    public async Task ADailyLimitIsToldApartAndNotWaitedFor()
    {
        (LocalMealClient client, LocalProfile profile) = await CreateAsync();
        _script.Enqueue(() => Limit("Rate limit reached on tokens per day (TPD). Please try again in 3h12m."));
        _script.Enqueue(() => Limit("Rate limit reached on tokens per day (TPD). Please try again in 3h12m."));

        GroqRateLimitException limit = await Assert.ThrowsAsync<GroqRateLimitException>(() =>
            client.CreateSessionAsync(profile.Id, ["гречка 200 г"], _date, MealSessionPurpose.Diary, _settings));

        Assert.True(limit.IsDailyLimit);
        Assert.Equal(TimeSpan.FromMinutes(192), limit.Wait);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task AnUnknownFoodGetsAnEstimateWhichStaysInTheCatalogAndIsNotAskedAgain()
    {
        (LocalMealClient client, LocalProfile profile) = await CreateAsync();

        MealSessionResponse first = await client.CreateSessionAsync(profile.Id, ["гречка 200 г"], _date, MealSessionPurpose.Diary, _settings);

        Assert.Equal("ReadyForConfirmation", first.Status);
        Assert.Equal(220m, first.Dishes[0].Portions[0].Nutrition!.Calories);
        Assert.Equal("Estimated", first.Dishes[0].Portions[0].NutritionQuality);
        ProductResponse product = Assert.Single(await client.SearchProductsAsync(profile.Id, "гречка"));
        Assert.Equal("Estimated", product.DataQuality);

        _models.Clear();
        await client.CreateSessionAsync(profile.Id, ["гречка 100 г"], _date, MealSessionPurpose.Diary, _settings);

        Assert.Single(_models);
    }

    [Fact]
    public async Task FailedEstimateLeavesTheFoodWithoutNutritionAndCanBeRepeated()
    {
        (LocalMealClient client, LocalProfile profile) = await CreateAsync();
        _script.Enqueue(() => Answer(Draft));
        _script.Enqueue(() => new HttpResponseMessage(HttpStatusCode.BadRequest));

        MealSessionResponse session = await client.CreateSessionAsync(profile.Id, ["гречка 200 г"], _date, MealSessionPurpose.Diary, _settings);

        Assert.Equal("NeedsProducts", session.Status);
        Assert.Empty(await client.SearchProductsAsync(profile.Id, "гречка"));

        MealSessionResponse repeated = (await client.EstimateMissingAsync(profile.Id, session.Id, _settings))!;
        Assert.Equal("ReadyForConfirmation", repeated.Status);
    }

    [Fact]
    public async Task ImpossibleNumbersFromTheModelAreNotSaved()
    {
        (LocalMealClient client, LocalProfile profile) = await CreateAsync();
        _script.Enqueue(() => Answer(Draft));
        _script.Enqueue(() => Answer("""
            {"items":[{"name":"Гречка варёная","known":true,"calories":10,"proteinGrams":40,"fatGrams":30,"carbohydratesGrams":20}]}
            """));

        MealSessionResponse session = await client.CreateSessionAsync(profile.Id, ["гречка 200 г"], _date, MealSessionPurpose.Diary, _settings);

        Assert.Equal("NeedsProducts", session.Status);
    }

    [Fact]
    public async Task ALineChangedByHandIsCalculatedWithoutTheModelAndKeptInTheMessages()
    {
        (LocalMealClient client, LocalProfile profile) = await CreateAsync();
        MealSessionResponse session = await client.CreateSessionAsync(profile.Id, ["гречка 200 г"], _date, MealSessionPurpose.Diary, _settings);
        _models.Clear();

        MealSessionResponse changed = (await client.SetDishWeightAsync(profile.Id, session.Id, 0, 150m))!;

        Assert.Empty(_models);
        Assert.Equal(165m, changed.Dishes[0].Portions[0].Nutrition!.Calories);
        Assert.Contains("150 г", changed.Messages[^1]);
        Assert.True(changed.CanConfirm);
    }

    private async Task<(LocalMealClient Client, LocalProfile Profile)> CreateAsync()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Мой профиль");
        LocalMealClient client = new(profiles, () => new HttpClient(new Handler(SendAsync), disposeHandler: false),
            retryDelay: (wait, _) =>
            {
                _waits.Add(wait);
                return Task.CompletedTask;
            });
        return (client, profile);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        _models.Add(body.RootElement.GetProperty("model").GetString()!);
        if (_script.TryDequeue(out Func<HttpResponseMessage>? next))
        {
            return next();
        }

        bool estimate = body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("name").GetString() == "nutriflow_nutrition_estimate";
        return Answer(estimate ? Estimate : Draft);
    }

    private static HttpResponseMessage Limit(string message) => new(HttpStatusCode.TooManyRequests)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { error = new { message } }), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Answer(string content)
    {
        string body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = "stop" } } });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
    }
}
