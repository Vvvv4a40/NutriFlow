using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Capture;
using NutriFlow.Ui.Pages;

namespace NutriFlow.Ui.Tests;

public sealed class CaptureStateTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nutriflow-capture-" + Guid.NewGuid().ToString("N"));
    private readonly FakeAppHost _host = new();
    private readonly FakeProfileSettings _settings = new() { GroqKey = "test-key" };
    private readonly GroqStub _groq = new();
    private readonly LocalProfileStore _profiles;
    private readonly LocalInputDraftStore _drafts;
    private readonly LocalMealClient _meals;

    public CaptureStateTests()
    {
        _profiles = new LocalProfileStore(_root);
        _drafts = new LocalInputDraftStore(_profiles);
        _meals = new LocalMealClient(_profiles, () => new HttpClient(_groq, disposeHandler: false));
    }

    [Fact]
    public async Task TypedTextIsSavedWithTheProfileFilesAndComesBackAfterARestart()
    {
        Guid profileId = await CreateProfileAsync();
        CaptureState state = await LoadAsync(profileId);
        state.Open(Today.AddDays(-1));
        state.SetPurpose(MealSessionPurpose.CreateDish);

        state.SetText("фарш 600 г");
        await state.FlushAsync();

        Assert.Equal(CaptureText.Saved, state.SaveStatus);
        Assert.Equal(1, _host.SavedFiles);
        CaptureState restarted = await LoadAsync(profileId);
        Assert.Equal("фарш 600 г", restarted.Text);
        Assert.Equal(MealSessionPurpose.CreateDish, restarted.Purpose);
        Assert.Equal(Today.AddDays(-1), restarted.Date);
        Assert.Equal(CaptureText.Saved, restarted.SaveStatus);
        Assert.False(restarted.Blocked);
    }

    [Fact]
    public async Task HiddenPageWritesTheTextWithoutWaitingForThePause()
    {
        Guid profileId = await CreateProfileAsync();
        CaptureState state = await LoadAsync(profileId, TimeSpan.FromHours(1));

        state.SetText("гречка 150 г");
        _host.Hide();

        LocalInputDraft? draft = null;
        for (int attempt = 0; attempt < 100 && draft is null; attempt++)
        {
            await Task.Delay(20);
            draft = await _drafts.ReadAsync(profileId);
        }

        Assert.Equal("гречка 150 г", draft?.Text);
    }

    [Fact]
    public async Task NewDescriptionTakesTheDiaryDayButTypedTextKeepsItsOwn()
    {
        Guid profileId = await CreateProfileAsync();
        CaptureState state = await LoadAsync(profileId);

        state.Open(Today.AddDays(-2));
        Assert.Equal(Today.AddDays(-2), state.Date);
        state.SetText("гречка 150 г");
        state.Open(Today);

        Assert.Equal(Today.AddDays(-2), state.Date);
    }

    [Fact]
    public async Task ParsedDescriptionBecomesTheSessionAndLeavesTheSavedInput()
    {
        Guid profileId = await CreateProfileAsync();
        await AddProductsAsync(profileId);
        CaptureState state = await LoadAsync(profileId);
        state.Open(Today);
        state.SetText("гречка 150 г, куриная грудка 120 г");
        List<string> progress = [];

        await state.SubmitAsync(progress.Add);

        MealSessionResponse session = Assert.IsType<MealSessionResponse>(state.Session);
        Assert.Equal("ReadyForConfirmation", session.Status);
        Assert.Equal(["Разбираю описание…"], progress);
        Assert.Equal(["гречка 150 г, куриная грудка 120 г"], session.Messages);
        Assert.Equal("", state.Text);
        Assert.Null(await _drafts.ReadAsync(profileId));
        Assert.Equal(session.Id, _settings.Sessions[profileId]);
        Assert.Equal("Bearer test-key", _groq.Authorization);
        // the unconfirmed session comes back after a restart
        Assert.Equal(session.Id, (await LoadAsync(profileId)).Session?.Id);
    }

    [Fact]
    public async Task ConfirmationWritesTheDiaryAndForgetsTheSession()
    {
        Guid profileId = await CreateProfileAsync();
        await AddProductsAsync(profileId);
        CaptureState state = await LoadAsync(profileId);
        state.Open(Today.AddDays(-1));
        state.SetText("гречка 150 г, куриная грудка 120 г");
        await state.SubmitAsync();

        ConfirmMealSessionResponse result = await state.ConfirmAsync();

        Assert.Equal("Confirmed", result.Session.Status);
        Assert.Null(state.Session);
        Assert.Empty(_settings.Sessions);
        DailyProgressResponse day = await _meals.GetDailyProgressAsync(profileId, Today.AddDays(-1));
        Assert.Equal(["Гречка", "Куриная грудка"], day.Entries.Select(entry => entry.Name).Order());
        // 150 g of 110 kcal and 120 g of 165 kcal per 100 g
        Assert.Equal(363m, day.Consumed.Calories);
    }

    [Fact]
    public async Task IngredientWithoutNutritionWaitsForAProductAndTheRefresh()
    {
        Guid profileId = await CreateProfileAsync();
        CaptureState state = await LoadAsync(profileId);
        state.SetText("гречка 150 г, куриная грудка 120 г");
        await state.SubmitAsync();
        Assert.Equal("NeedsProducts", state.Session!.Status);
        Assert.False(state.Session.CanConfirm);

        await AddProductsAsync(profileId);
        await state.RefreshAsync();

        Assert.Equal("ReadyForConfirmation", state.Session.Status);
        Assert.True(state.Session.CanConfirm);
    }

    [Fact]
    public async Task ClarificationIsAddedToTheSameSession()
    {
        Guid profileId = await CreateProfileAsync();
        await AddProductsAsync(profileId);
        CaptureState state = await LoadAsync(profileId);
        _groq.Draft = Draft(("Гречка", null), ("Куриная грудка", 120m));
        state.SetText("гречка, куриная грудка 120 г");
        await state.SubmitAsync();
        Assert.Equal("NeedsClarification", state.Session!.Status);
        Guid sessionId = state.Session.Id;

        _groq.Draft = Draft(("Гречка", 150m), ("Куриная грудка", 120m));
        state.SetText("гречки 150 г");
        List<string> progress = [];
        await state.SubmitAsync(progress.Add);

        Assert.Equal(sessionId, state.Session.Id);
        Assert.Equal("ReadyForConfirmation", state.Session.Status);
        Assert.Equal(["Добавляю уточнение…"], progress);
        Assert.Equal(2, state.Session.Messages.Count);
    }

    [Fact]
    public async Task UnreachableGroqKeepsTheTextAndSaysWhatToDo()
    {
        Guid profileId = await CreateProfileAsync();
        CaptureState state = await LoadAsync(profileId);
        _groq.Failure = new HttpRequestException("Failed to fetch");
        state.SetText("гречка 150 г");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => state.SubmitAsync());

        Assert.Equal(CaptureText.Unreachable, error.Message);
        Assert.Null(state.Session);
        Assert.Equal("гречка 150 г", state.Text);
        Assert.Equal("гречка 150 г", (await _drafts.ReadAsync(profileId))?.Text);
    }

    [Fact]
    public async Task WithoutTheKeyNothingIsSentAndTheTextStays()
    {
        Guid profileId = await CreateProfileAsync();
        _settings.GroqKey = null;
        CaptureState state = await LoadAsync(profileId);
        state.SetText("гречка 150 г");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => state.SubmitAsync());

        Assert.StartsWith("Добавьте личный ключ Groq", error.Message);
        Assert.Null(_groq.Authorization);
        Assert.Equal("гречка 150 г", state.Text);
    }

    [Fact]
    public async Task RestartDropsTheSessionWithoutTouchingTheDiary()
    {
        Guid profileId = await CreateProfileAsync();
        await AddProductsAsync(profileId);
        CaptureState state = await LoadAsync(profileId);
        state.SetText("гречка 150 г, куриная грудка 120 г");
        await state.SubmitAsync();
        state.SetText("ещё огурец");
        await state.FlushAsync();

        await state.RestartAsync();

        Assert.Null(state.Session);
        Assert.Equal("", state.Text);
        Assert.Empty(_settings.Sessions);
        Assert.Null(await _drafts.ReadAsync(profileId));
        Assert.Empty((await _meals.GetDailyProgressAsync(profileId, Today)).Entries);
    }

    [Fact]
    public async Task TextOfAFinishedSessionWaitsForAChoiceAndOpensAsANewDescription()
    {
        Guid profileId = await CreateProfileAsync();
        await _drafts.SaveAsync(profileId, new LocalInputDraft("огурец 80 г", Today, MealSessionPurpose.Diary, Guid.NewGuid()));
        CaptureState state = await LoadAsync(profileId);
        Assert.True(state.Blocked);
        Assert.Equal("", state.Text);
        state.SetText("не попадёт");
        Assert.Equal("", state.Text);
        List<string> questions = [];

        await state.RestoreDraftAsync((title, _, _) =>
        {
            questions.Add(title);
            return Task.FromResult(true);
        });

        Assert.Equal(["Открыть как новое описание?"], questions);
        Assert.False(state.Blocked);
        Assert.Equal("огурец 80 г", state.Text);
        Assert.Null(state.Session);
        Assert.Null((await _drafts.ReadAsync(profileId))?.SessionId);
    }

    [Fact]
    public async Task DiscardedTextIsGone()
    {
        Guid profileId = await CreateProfileAsync();
        await _drafts.SaveAsync(profileId, new LocalInputDraft("огурец 80 г", Today, MealSessionPurpose.Diary, Guid.NewGuid()));
        CaptureState state = await LoadAsync(profileId);

        await state.DiscardDraftAsync();

        Assert.False(state.Blocked);
        Assert.Null(await _drafts.ReadAsync(profileId));
    }

    [Fact]
    public async Task UnreadableDraftIsReportedAndLeftAsItIs()
    {
        Guid profileId = await CreateProfileAsync();
        string path = Path.Combine(_root, "profiles", profileId.ToString("N"), "input-draft.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ broken");

        CaptureState state = await LoadAsync(profileId);

        Assert.True(state.Blocked);
        Assert.Null(state.PendingDraft);
        Assert.Equal("Сохранённый ввод недоступен. Исходный файл не изменён.", state.DraftProblem);
        Assert.Equal("{ broken", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task AnotherProfileStartsWithItsOwnInput()
    {
        Guid anna = await CreateProfileAsync("Анна");
        Guid boris = await CreateProfileAsync("Борис");
        CaptureState state = await LoadAsync(anna);
        state.SetText("гречка 150 г");
        await state.FlushAsync();

        await state.LoadAsync(boris);

        Assert.Equal(boris, state.ProfileId);
        Assert.Equal("", state.Text);
        await state.LoadAsync(anna);
        Assert.Equal("гречка 150 г", state.Text);
    }

    private async Task<Guid> CreateProfileAsync(string name = "Тест") => (await _profiles.CreateAsync(name)).Id;

    private async Task<CaptureState> LoadAsync(Guid profileId, TimeSpan? saveDelay = null)
    {
        CaptureState state = new(_meals, _drafts, _settings, _host, saveDelay ?? TimeSpan.FromMilliseconds(1));
        await state.LoadAsync(profileId);
        return state;
    }

    private async Task AddProductsAsync(Guid profileId)
    {
        await _meals.AddProductAsync(profileId, Product("Гречка", 110m, 3.6m, 1.3m, 21.3m));
        await _meals.AddProductAsync(profileId, Product("Куриная грудка", 165m, 31m, 3.6m, 0m));
    }

    private static ProductResponse Product(string name, decimal calories, decimal protein, decimal fat, decimal carbs) =>
        new(name, calories, protein, fat, carbs, "ManualInput", "Exact", "Ручной ввод", null, null);

    // what Groq returns as structured output: one dish per eaten product, a question for a missing weight
    private static string Draft(params (string Name, decimal? Grams)[] items)
    {
        JsonArray dishes = [];
        JsonArray questions = [];
        foreach ((string name, decimal? grams) in items)
        {
            dishes.Add(new JsonObject
            {
                ["name"] = name,
                ["ingredients"] = new JsonArray(new JsonObject
                {
                    ["productName"] = name,
                    ["weightInGrams"] = grams,
                    ["weightQuality"] = grams is null ? "unknown" : "exact",
                    ["removedWeightInGrams"] = 0m,
                    ["removedWeightQuality"] = "exact"
                }),
                ["finalWeightInGrams"] = grams,
                ["finalWeightQuality"] = grams is null ? "unknown" : "exact",
                ["portions"] = grams is null
                    ? new JsonArray()
                    : new JsonArray(new JsonObject { ["weightInGrams"] = grams, ["fractionOfDish"] = null, ["weightQuality"] = "exact" })
            });
            if (grams is null)
            {
                questions.Add($"Сколько граммов «{name}»?");
            }
        }

        return new JsonObject { ["dishes"] = dishes, ["clarificationQuestions"] = questions }.ToJsonString();
    }

    private sealed class GroqStub : HttpMessageHandler
    {
        public string Draft { get; set; } = CaptureStateTests.Draft(("Гречка", 150m), ("Куриная грудка", 120m));

        public Exception? Failure { get; set; }

        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            if (Failure is not null)
            {
                throw Failure;
            }

            string body = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject { ["content"] = Draft },
                    ["finish_reason"] = "stop"
                })
            }.ToJsonString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    public void Dispose()
    {
        _groq.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
