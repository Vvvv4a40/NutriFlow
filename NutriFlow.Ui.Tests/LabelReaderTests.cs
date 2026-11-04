using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Catalog;
using NutriFlow.Ui.Hosting;
using NutriFlow.Ui.Pages;

namespace NutriFlow.Ui.Tests;

// the label on the real client and a temporary profile folder: only Groq is replaced, its answers go through the real reader
public sealed class LabelReaderTests : IDisposable
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    private static readonly PickedPhoto Png = new(Convert.FromHexString("89504E470D0A1A0A0000"), "image/png");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nutriflow-label-" + Guid.NewGuid().ToString("N"));
    private readonly FakeAppHost _host = new();
    private readonly FakeProfileSettings _settings = new() { GroqKey = "key" };
    private readonly LabelStub _groq = new();
    private readonly LocalProfileStore _profiles;
    private readonly LocalMealClient _meals;
    private readonly LabelReader _reader;

    public LabelReaderTests()
    {
        _profiles = new LocalProfileStore(_root);
        _meals = new LocalMealClient(_profiles, () => new HttpClient(_groq, disposeHandler: false), retryDelay: (_, _) => Task.CompletedTask);
        _reader = new LabelReader(_meals, _settings, _host);
    }

    [Fact]
    public async Task ReadPhotoIsAFileOfTheProfileAndIsSavedInTheSameStep()
    {
        Guid profileId = (await _profiles.CreateAsync("Анна")).Id;

        LabelReading reading = await _reader.ReadAsync(profileId, Png);

        Assert.Equal("Йогурт", reading.Capture.Draft.ProductName);
        Assert.Equal(NutritionBasis.Per100Grams, reading.Capture.Draft.Basis);
        Assert.StartsWith("label-photo:", reading.Capture.PhotoReference);
        Assert.Equal(1, _host.SavedFiles);
        Assert.Equal(Png.Content, File.ReadAllBytes(Assert.Single(Photos())));
    }

    [Fact]
    public async Task FailedRequestLeavesNoPhotoBehindAndNothingToSave()
    {
        Guid profileId = (await _profiles.CreateAsync("Анна")).Id;
        _groq.Failure = new HttpRequestException("Failed to fetch");

        Exception error = await Assert.ThrowsAsync<HttpRequestException>(() => _reader.ReadAsync(profileId, Png));

        Assert.Equal("Не удалось связаться с Groq. Проверьте интернет и повторите или заполните продукт вручную.", LabelText.Error(error));
        Assert.True(LabelText.CanRetry(error));
        Assert.Equal(0, _host.SavedFiles);
        Assert.Empty(Photos());
    }

    [Theory]
    [InlineData("89504E470D0A1A0A0000", "image/jpeg")]
    [InlineData("0102030405060708", "image/jpeg")]
    public async Task FileThatIsNotTheDeclaredPhotoIsNotSentToGroq(string hex, string mediaType)
    {
        Guid profileId = (await _profiles.CreateAsync("Анна")).Id;

        Exception error = await Assert.ThrowsAsync<ArgumentException>(() =>
            _reader.ReadAsync(profileId, new PickedPhoto(Convert.FromHexString(hex), mediaType)));

        Assert.Equal(LabelText.UnreadablePhoto, LabelText.Error(error));
        Assert.False(LabelText.CanRetry(error));
        Assert.Equal(0, _groq.Requests);
    }

    [Fact]
    public async Task WithoutAKeyTheMessageLeadsToTheSettings()
    {
        Guid profileId = (await _profiles.CreateAsync("Анна")).Id;
        _settings.GroqKey = null;

        Exception error = await Assert.ThrowsAsync<InvalidOperationException>(() => _reader.ReadAsync(profileId, Png));

        Assert.Contains("ключ Groq", LabelText.Error(error));
        Assert.Equal(0, _groq.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "Фото слишком большое для Groq.")]
    [InlineData(HttpStatusCode.BadRequest, "Groq не принял запрос. Проверьте модель для этикеток в настройках и повторите.")]
    [InlineData(HttpStatusCode.Unauthorized, "Groq не принял ключ. Проверьте ключ в настройках или заполните продукт вручную.")]
    public void GroqErrorsNameWhatToDoWithALabel(HttpStatusCode status, string start)
    {
        Assert.StartsWith(start, LabelText.Error(new HttpRequestException("failed", null, status)));
    }

    [Fact]
    public async Task CheckedValuesAreExactAndReachTheDiaryAsExact()
    {
        Guid profileId = (await _profiles.CreateAsync("Анна")).Id;
        LabelReading reading = await _reader.ReadAsync(profileId, Png);
        ProductResponse product = LabelText.Product("Йогурт", 62m, 4.1m, 1.5m, 6.8m, true, reading.Capture.PhotoReference, null);

        Assert.True(await _meals.AddProductAsync(profileId, product));

        ProductResponse stored = Assert.Single(await _meals.SearchProductsAsync(profileId, "йогурт"));
        Assert.Equal("LabelPhoto", stored.SourceKind);
        Assert.Equal("Exact", stored.DataQuality);
        Assert.Equal(reading.Capture.PhotoReference, stored.SourceReference);
    }

    [Fact]
    public async Task UncheckedValuesAreAnEstimateEvenInTheDiary()
    {
        Guid profileId = (await _profiles.CreateAsync("Анна")).Id;
        LabelReading reading = await _reader.ReadAsync(profileId, Png);
        ProductResponse product = LabelText.Product("Йогурт", 62m, 4.1m, 1.5m, 6.8m, false, reading.Capture.PhotoReference, "4600000000000");

        Assert.True(await _meals.AddProductAsync(profileId, product));

        ProductResponse stored = Assert.Single(await _meals.SearchProductsAsync(profileId, "йогурт"));
        Assert.Equal("Estimated", stored.DataQuality);
        Assert.Equal("Этикетка, не сверена", stored.SourceName);
        Assert.Equal("4600000000000", stored.Barcode);

        MealSessionResponse session = await _meals.CreateManualMealAsync(profileId, stored, 200m, Today);
        Assert.Equal("Estimated", Assert.Single(session.Dishes).Portions.Single().NutritionQuality);
    }

    [Theory]
    [InlineData(NutritionBasis.PerServing, false, true)]
    [InlineData(NutritionBasis.Per100Milliliters, false, true)]
    [InlineData(NutritionBasis.Unknown, false, true)]
    [InlineData(NutritionBasis.PerServing, true, false)]
    [InlineData(NutritionBasis.Per100Grams, false, false)]
    [InlineData(NutritionBasis.Per100Grams, true, false)]
    public void ValuesNotPer100GramsCannotBeSavedWithoutTheCheck(NutritionBasis basis, bool isChecked, bool problem)
    {
        Assert.Equal(problem, LabelText.CheckProblem(basis, isChecked) is not null);
    }

    [Fact]
    public void NotesSayWhatTheLabelGave()
    {
        Assert.Equal("Распознаны значения на 100 г. Сверьте каждое поле с упаковкой.", LabelText.Note(NutritionBasis.Per100Grams));
        Assert.StartsWith("На этикетке значения на порцию. Пересчитайте их на 100 г", LabelText.Note(NutritionBasis.PerServing));
        Assert.StartsWith("Не удалось определить, на 100 г или на порцию", LabelText.Note(NutritionBasis.Unknown));
        Assert.Equal("Пищевая ценность на 100 г", LabelText.ValuesTitle(NutritionBasis.Per100Grams));
        Assert.Equal("Значения на порцию — пересчитайте на 100 г", LabelText.ValuesTitle(NutritionBasis.PerServing));
        Assert.Equal("Значения на 100 мл — пересчитайте на 100 г", LabelText.ValuesTitle(NutritionBasis.Per100Milliliters));
        Assert.Contains("оценка", LabelText.CheckedHint(NutritionBasis.Per100Grams));
    }

    [Fact]
    public async Task PhotoOfAProductGoesIntoTheBackup()
    {
        LocalProfile profile = await _profiles.CreateAsync("Анна");
        LabelReading reading = await _reader.ReadAsync(profile.Id, Png);
        await _meals.AddProductAsync(profile.Id, LabelText.Product("Йогурт", 62m, 4.1m, 1.5m, 6.8m, true, reading.Capture.PhotoReference, null));
        string file = reading.Capture.PhotoReference["label-photo:".Length..];
        string archive = Path.Combine(_root, "copy.zip");

        await new LocalProfileBackup(_profiles).ExportAsync(profile.Id, archive);

        using ZipArchive zip = ZipFile.OpenRead(archive);
        ZipArchiveEntry entry = Assert.Single(zip.Entries, candidate => candidate.Name == file);
        using MemoryStream content = new();
        await using (Stream source = entry.Open())
        {
            await source.CopyToAsync(content);
        }

        Assert.Equal(Png.Content, content.ToArray());
    }

    private string[] Photos() => Directory.GetFiles(_root, "*.png", SearchOption.AllDirectories);

    public void Dispose()
    {
        _groq.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class LabelStub : HttpMessageHandler
    {
        public Exception? Failure { get; set; }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Failure is not null)
            {
                throw Failure;
            }

            string label = new JsonObject
            {
                ["productName"] = "Йогурт",
                ["basis"] = "per_100g",
                ["calories"] = 62m,
                ["proteinGrams"] = 4.1m,
                ["fatGrams"] = 1.5m,
                ["carbohydratesGrams"] = 6.8m,
                ["clarificationQuestions"] = new JsonArray()
            }.ToJsonString();
            string body = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject { ["content"] = label },
                    ["finish_reason"] = "stop"
                })
            }.ToJsonString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
