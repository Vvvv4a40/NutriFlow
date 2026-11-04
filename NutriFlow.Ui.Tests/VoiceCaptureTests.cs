using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Capture;
using NutriFlow.Ui.Hosting;
using NutriFlow.Ui.Pages;

namespace NutriFlow.Ui.Tests;

// the voice form on the real client and a fake recorder: only Groq is replaced, its answers go through the real transcriber
public sealed class VoiceCaptureTests : IAsyncLifetime
{
    private static readonly RecordedAudio Audio = new([0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x02], "audio/webm;codecs=opus");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nutriflow-voice-" + Guid.NewGuid().ToString("N"));
    private readonly FakeVoiceRecorder _recorder = new();
    private readonly FakeProfileSettings _settings = new() { GroqKey = "key" };
    private readonly SpeechStub _groq = new();
    private LocalProfileStore _profiles = null!;
    private VoiceCapture _voice = null!;

    public async Task InitializeAsync()
    {
        _profiles = new LocalProfileStore(_root);
        LocalMealClient meals = new(_profiles, () => new HttpClient(_groq, disposeHandler: false));
        LocalProfile profile = await _profiles.CreateAsync("Анна");
        _voice = new VoiceCapture(_recorder, meals, _settings, profile.Id);
    }

    [Fact]
    public async Task RecordingBecomesTextTheUserCanEdit()
    {
        await _voice.StartAsync();
        _recorder.Begin();
        Assert.Equal(VoicePhase.Recording, _voice.Phase);
        Assert.Equal("Стоп · 0:00", VoiceText.Chip(_voice));
        Assert.Equal("Стоп, идёт запись", VoiceText.ChipDescription(_voice));

        await _voice.StopAsync();
        await _recorder.FinishAsync(Audio);

        Assert.Equal(1, _recorder.Stops);
        Assert.Equal(VoicePhase.Idle, _voice.Phase);
        Assert.Equal("гречка 150 г", _voice.Text);
        Assert.True(_voice.Recognised);
        Assert.False(_voice.StoppedByItself);
        Assert.Equal("Голос", VoiceText.Chip(_voice));
        Assert.Equal("audio/webm", _groq.ContentType);

        _voice.SetText("гречка 200 г");
        Assert.Equal("гречка 200 г", _voice.Text);
    }

    [Fact]
    public async Task ANewRecordingJoinsWhatIsAlreadyInTheField()
    {
        _voice.SetText("набрано руками");

        await _voice.StartAsync();
        _recorder.Begin();
        await _voice.StopAsync();
        await _recorder.FinishAsync(Audio);

        Assert.Equal("набрано руками\nгречка 150 г", _voice.Text);
    }

    [Fact]
    public async Task RecordingEndedByTheLimitIsTranscribedAndSaysItStoppedItself()
    {
        await _voice.StartAsync();
        _recorder.Begin();

        // no StopAsync: speech-capture.mjs stopped it at the limit
        await _recorder.FinishAsync(Audio);

        Assert.True(_voice.StoppedByItself);
        Assert.Equal("гречка 150 г", _voice.Text);
    }

    [Theory]
    [InlineData(110, "Достигнут максимум 110 с. Проверьте, всё ли вошло.")]
    [InlineData(109, "Достигнут максимум 110 с. Проверьте, всё ли вошло.")]
    [InlineData(37, "Запись прервалась на 37 с. Проверьте, всё ли вошло.")]
    public void RecordingThatStoppedItselfSaysWhy(int seconds, string text)
    {
        Assert.Equal(text, VoiceText.StoppedByItself(seconds));
    }

    [Fact]
    public async Task TypedTextIsNotCalledRecognised()
    {
        _voice.SetText("набрано руками");
        Assert.False(_voice.Recognised);

        await _voice.StartAsync();
        _recorder.Begin();
        await _voice.StopAsync();
        await _recorder.FinishAsync(Audio);
        Assert.True(_voice.Recognised);
    }

    [Fact]
    public async Task DeniedMicrophoneIsVisibleAndKeepsTheText()
    {
        _voice.SetText("уже введено");

        await _voice.StartAsync();
        _recorder.Fail("Нет доступа к микрофону. Разрешите его в настройках браузера или введите текст.");

        Assert.Equal(VoicePhase.Idle, _voice.Phase);
        Assert.StartsWith("Нет доступа к микрофону", _voice.Error);
        Assert.False(_voice.CanRetry);
        Assert.Equal("уже введено", _voice.Text);
    }

    [Fact]
    public async Task WithoutAKeyTheMicrophoneIsNotAsked()
    {
        _settings.GroqKey = null;

        await _voice.StartAsync();

        Assert.Equal(0, _recorder.Starts);
        Assert.Contains("ключ Groq", _voice.Error);
    }

    [Fact]
    public async Task FailedRequestKeepsTheAudioAndTheTextForARetry()
    {
        _voice.SetText("уже введено");
        _groq.Failure = new HttpRequestException("Failed to fetch");
        await _voice.StartAsync();
        _recorder.Begin();
        await _voice.StopAsync();
        await _recorder.FinishAsync(Audio);

        Assert.Equal("Не удалось связаться с Groq. Проверьте интернет и повторите или введите текст.", _voice.Error);
        Assert.True(_voice.CanRetry);
        Assert.Equal("уже введено", _voice.Text);
        Assert.Equal(VoicePhase.Idle, _voice.Phase);

        _groq.Failure = null;
        await _voice.RetryAsync();

        Assert.Null(_voice.Error);
        Assert.False(_voice.CanRetry);
        Assert.Equal("уже введено\nгречка 150 г", _voice.Text);
        Assert.Equal(1, _recorder.Starts);
    }

    [Fact]
    public async Task ARetryThatFailsAgainStaysAvailable()
    {
        _groq.Failure = new HttpRequestException("failed", null, HttpStatusCode.ServiceUnavailable);
        await _voice.StartAsync();
        _recorder.Begin();
        await _recorder.FinishAsync(Audio);

        await _voice.RetryAsync();

        Assert.Equal("Groq сейчас не отвечает. Повторите позже или введите текст.", _voice.Error);
        Assert.True(_voice.CanRetry);
        Assert.Equal(VoicePhase.Idle, _voice.Phase);
    }

    [Fact]
    public async Task AudioGroqCannotTakeIsNotSentAndOffersNoRetry()
    {
        await _voice.StartAsync();
        _recorder.Begin();

        await _recorder.FinishAsync(new RecordedAudio([1, 2, 3, 4, 5, 6, 7, 8], "audio/webm"));

        Assert.Equal(VoiceText.WrongFormat, _voice.Error);
        Assert.False(_voice.CanRetry);
        Assert.Equal(0, _groq.Requests);
    }

    [Fact]
    public async Task TextOverTheLimitIsNotCutAndCanBeRetriedAfterEditing()
    {
        string typed = new('а', MealInputText.MaximumLength);
        _voice.SetText(typed);
        await _voice.StartAsync();
        _recorder.Begin();

        await _recorder.FinishAsync(Audio);

        Assert.StartsWith("Общий лимит описания", _voice.Error);
        Assert.Equal(typed, _voice.Text);
        Assert.True(_voice.CanRetry);

        _voice.SetText("короче");
        await _voice.RetryAsync();
        Assert.Equal("короче\nгречка 150 г", _voice.Text);
    }

    [Fact]
    public async Task ClosingTheFormReleasesTheRecorder()
    {
        await _voice.StartAsync();
        _recorder.Begin();

        await _voice.DisposeAsync();
        _voice = null!;

        Assert.True(_recorder.Disposed);
    }

    public async Task DisposeAsync()
    {
        if (_voice is not null)
        {
            await _voice.DisposeAsync();
        }

        _groq.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class SpeechStub : HttpMessageHandler
    {
        public Exception? Failure { get; set; }

        public int Requests { get; private set; }

        public string? ContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Failure is not null)
            {
                throw Failure;
            }

            MultipartFormDataContent form = Assert.IsType<MultipartFormDataContent>(request.Content);
            ContentType = form.First(part => part.Headers.ContentDisposition?.Name == "file").Headers.ContentType?.MediaType;
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["text"] = "гречка 150 г" }.ToJsonString(), Encoding.UTF8, "application/json")
            };
        }
    }
}
