using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Mobile.Core;
using NutriFlow.Ui.Hosting;
using NutriFlow.Ui.Pages;

namespace NutriFlow.Ui.Capture;

// the food input of the current profile: the unconfirmed meal session, the typed text and its autosave to
// input-draft.json (MainPage.Input.cs and MainPage.Drafts.cs on android). It outlives the input screen, so leaving
// the section keeps the text, and the host saves the profile files after every change of the draft
public sealed class CaptureState : IDisposable
{
    private readonly LocalMealClient _meals;
    private readonly LocalInputDraftStore _drafts;
    private readonly IProfileSettings _settings;
    private readonly IAppHost _host;
    private readonly TimeSpan _saveDelay;
    private Task _loading = Task.CompletedTask;
    private Task _saving = Task.CompletedTask;
    private CancellationTokenSource? _delayedSave;
    private int _saveVersion;
    private Exception? _saveError;

    public CaptureState(LocalMealClient meals, LocalInputDraftStore drafts, IProfileSettings settings, IAppHost host)
        : this(meals, drafts, settings, host, TimeSpan.FromMilliseconds(400))
    {
    }

    internal CaptureState(LocalMealClient meals, LocalInputDraftStore drafts, IProfileSettings settings, IAppHost host,
        TimeSpan saveDelay)
    {
        _meals = meals;
        _drafts = drafts;
        _settings = settings;
        _host = host;
        _saveDelay = saveDelay;
        _host.Hidden += SaveNow;
    }

    public Guid? ProfileId { get; private set; }

    public MealSessionResponse? Session { get; private set; }

    public MealSessionPurpose Purpose { get; private set; }

    public DateOnly Date { get; private set; }

    public string Text { get; private set; } = "";

    // saved text of another session, or a draft that could not be read: it waits for an explicit choice
    public LocalInputDraft? PendingDraft { get; private set; }

    public string? DraftProblem { get; private set; }

    public bool Blocked => PendingDraft is not null || DraftProblem is not null;

    public string SaveStatus { get; private set; } = "";

    public bool SaveFailed => _saveError is not null;

    public event Action? Changed;

    // once per profile; the screens of the same profile wait for the same load, a failed one is repeated
    public Task LoadAsync(Guid profileId)
    {
        if (ProfileId != profileId || _loading.IsFaulted)
        {
            ProfileId = profileId;
            _loading = LoadProfileAsync(profileId);
        }

        return _loading;
    }

    private async Task LoadProfileAsync(Guid profileId)
    {
        CancelDelayedSave();
        ++_saveVersion;
        Session = null;
        Text = "";
        Purpose = MealSessionPurpose.Diary;
        Date = DateOnly.FromDateTime(DateTime.Today);
        PendingDraft = null;
        DraftProblem = null;
        _saveError = null;
        SaveStatus = "";
        await ResumeSessionAsync(profileId);
        if (ProfileId == profileId)
        {
            await ReadDraftAsync(profileId);
        }

        Changed?.Invoke();
    }

    private async Task ResumeSessionAsync(Guid profileId)
    {
        if (await _settings.GetSessionIdAsync(profileId) is not Guid id)
        {
            return;
        }

        MealSessionResponse? session = await _meals.FindSessionAsync(profileId, id);
        if (session is not null && session.Status != "Confirmed")
        {
            if (ProfileId == profileId)
            {
                UseSession(session);
            }
        }
        else
        {
            await _settings.ClearSessionIdAsync(profileId);
        }
    }

    private async Task ReadDraftAsync(Guid profileId)
    {
        PendingDraft = null;
        DraftProblem = null;
        _saveError = null;
        SaveStatus = "";
        try
        {
            LocalInputDraft? draft = await _drafts.ReadAsync(profileId);
            if (draft is null || ProfileId != profileId)
            {
                return;
            }

            bool matching = draft.SessionId == Session?.Id && (Session is null ||
                (draft.MealDate == Session.MealDate && draft.Purpose == Purpose));
            bool alreadySubmitted = Session is not null && draft.MatchesLastSubmittedMessage(Session.Messages);
            if (!matching || alreadySubmitted)
            {
                PendingDraft = draft;
                if (alreadySubmitted)
                {
                    DraftProblem = "Этот текст уже отправлен в текущий расчёт. Удалите копию или явно восстановите текст.";
                }

                return;
            }

            Text = draft.Text;
            if (Session is null)
            {
                Date = draft.MealDate;
                Purpose = draft.Purpose;
            }

            SaveStatus = CaptureText.Saved;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            DraftProblem = "Сохранённый ввод недоступен. Исходный файл не изменён.";
        }
    }

    // the input opened from the diary: a new description goes to the shown day, typed text keeps its own day
    public void Open(DateOnly diaryDate)
    {
        if (Session is null && !Blocked && string.IsNullOrWhiteSpace(Text))
        {
            Date = diaryDate;
        }
    }

    public void SetPurpose(MealSessionPurpose purpose)
    {
        if (Session is not null || Blocked || Purpose == purpose || ProfileId is null)
        {
            return;
        }

        Purpose = purpose;
        // an empty draft is no file at all, the mode is kept only with text
        if (!string.IsNullOrWhiteSpace(Text))
        {
            _ = QueueSave(ProfileId.Value, Capture());
        }

        Changed?.Invoke();
    }

    // typing writes one snapshot after a short pause instead of a file per key;
    // leaving the section, another profile or a hidden page write it at once
    public void SetText(string? text)
    {
        if (Blocked || ProfileId is not Guid profileId)
        {
            return;
        }

        Text = text ?? "";
        CancelDelayedSave();
        CancellationTokenSource delay = new();
        _delayedSave = delay;
        _ = SaveAfterDelayAsync(profileId, delay);
    }

    private async Task SaveAfterDelayAsync(Guid profileId, CancellationTokenSource delay)
    {
        try
        {
            await Task.Delay(_saveDelay, delay.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ReferenceEquals(_delayedSave, delay))
        {
            CancelDelayedSave();
            if (ProfileId == profileId && !Blocked)
            {
                _ = QueueSave(profileId, Capture());
            }
        }
    }

    public void SaveNow()
    {
        if (_delayedSave is null)
        {
            return;
        }

        CancelDelayedSave();
        if (ProfileId is Guid profileId && !Blocked)
        {
            _ = QueueSave(profileId, Capture());
        }
    }

    // waits for every pending write; a failed one stops the action that needs the saved text
    public async Task FlushAsync()
    {
        SaveNow();
        Task pending;
        do
        {
            pending = _saving;
            await pending;
        } while (!ReferenceEquals(pending, _saving));

        if (_saveError is not null)
        {
            throw new InvalidOperationException("Не удалось сохранить набранный текст. Вернитесь к вводу и повторите сохранение.", _saveError);
        }
    }

    public async Task RetrySaveAsync()
    {
        if (ProfileId is Guid profileId && !Blocked)
        {
            await QueueSave(profileId, Capture());
            await FlushAsync();
        }
    }

    private LocalInputDraft Capture() => new(Text, Session?.MealDate ?? Date, Purpose, Session?.Id);

    private Task QueueSave(Guid profileId, LocalInputDraft draft)
    {
        CancelDelayedSave();
        int version = ++_saveVersion;
        Task previous = _saving;
        _saving = PersistAsync(previous, profileId, draft, version);
        return _saving;
    }

    private async Task PersistAsync(Task previous, Guid profileId, LocalInputDraft draft, int version)
    {
        await previous;
        try
        {
            await _drafts.SaveAsync(profileId, draft);
            await _host.SaveProfileFilesAsync();
            if (version == _saveVersion && ProfileId == profileId)
            {
                _saveError = null;
                SaveStatus = string.IsNullOrWhiteSpace(draft.Text) ? "" : CaptureText.Saved;
                Changed?.Invoke();
            }
        }
        catch (Exception exception)
        {
            if (version == _saveVersion && ProfileId == profileId)
            {
                UiLog.Error(exception);
                _saveError = exception;
                SaveStatus = CaptureText.SaveFailed;
                Changed?.Invoke();
            }
        }
    }

    private void CancelDelayedSave()
    {
        _delayedSave?.Cancel();
        _delayedSave?.Dispose();
        _delayedSave = null;
    }

    // the description or a clarification goes to Groq; the text is in the saved input until the session has it
    public async Task SubmitAsync(Action<string>? showProgress = null)
    {
        Guid profileId = RequireProfile();
        RequireUnblocked();
        await FlushAsync();
        string message = Text.Trim();
        if (message.Length == 0)
        {
            throw new ArgumentException("Введите описание еды или уточнение.");
        }

        GroqSettings groq = await _settings.GetGroqAsync(profileId);
        MealSessionResponse? current = Session;
        showProgress?.Invoke(current is null ? "Разбираю описание…" : "Добавляю уточнение…");
        MealSessionResponse session;
        try
        {
            session = current is null
                ? await _meals.CreateSessionAsync(profileId, [message], Date, Purpose, groq)
                : await _meals.AddMessageAsync(profileId, current.Id, message, groq)
                    ?? throw new InvalidOperationException(CaptureText.SessionMissing);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or InvalidDataException)
        {
            throw new InvalidOperationException(CaptureText.GroqError(exception), exception);
        }

        await _settings.SaveSessionIdAsync(profileId, session.Id);
        if (ProfileId == profileId)
        {
            UseSession(session);
            await ClearSubmittedInputAsync(profileId);
        }
    }

    // a calculation made without Groq (a catalog product, a saved dish, a dish from products) replaces the current
    // one and the typed text only once it is ready (CanStartDraftAsync + SetSessionAsync on android); false when
    // the user keeps them
    public async Task<bool> StartAsync(Func<Guid, Task<MealSessionResponse>> create, Func<Task<bool>> confirmReplace)
    {
        Guid profileId = RequireProfile();
        RequireUnblocked();
        await FlushAsync();
        if ((Session is not null || !string.IsNullOrWhiteSpace(Text)) && !await confirmReplace())
        {
            return false;
        }

        MealSessionResponse session = await create(profileId);
        await _settings.SaveSessionIdAsync(profileId, session.Id);
        if (ProfileId == profileId)
        {
            UseSession(session);
            await ClearSubmittedInputAsync(profileId);
        }

        Changed?.Invoke();
        return true;
    }

    public async Task RefreshAsync()
    {
        Guid profileId = RequireProfile();
        MealSessionResponse session = Session ?? throw new InvalidOperationException(CaptureText.SessionMissing);
        MealSessionResponse found = await _meals.FindSessionAsync(profileId, session.Id)
            ?? throw new InvalidOperationException(CaptureText.SessionMissing);
        if (ProfileId == profileId)
        {
            UseSession(found);
            Changed?.Invoke();
        }
    }

    // a confirmed session leaves the screen and its saved input; another outcome shows the updated preview
    public async Task<ConfirmMealSessionResponse> ConfirmAsync()
    {
        Guid profileId = RequireProfile();
        MealSessionResponse session = Session ?? throw new InvalidOperationException(CaptureText.SessionMissing);
        ConfirmMealSessionResponse result = await _meals.ConfirmAsync(profileId, session.Id, session.PreviewToken)
            ?? throw new InvalidOperationException(CaptureText.SessionMissing);
        if (result.Session.Status == "Confirmed")
        {
            await _settings.ClearSessionIdAsync(profileId);
            if (ProfileId == profileId)
            {
                Session = null;
                Date = result.Session.MealDate;
                Purpose = MealSessionPurpose.Diary;
                await ClearSubmittedInputAsync(profileId);
            }
        }
        else if (ProfileId == profileId)
        {
            UseSession(result.Session);
        }

        Changed?.Invoke();
        return result;
    }

    // the unconfirmed session is dropped from the screen, the diary does not change
    public async Task RestartAsync()
    {
        Guid profileId = RequireProfile();
        RequireUnblocked();
        CancelDelayedSave();
        ++_saveVersion;
        await _drafts.ClearAsync(profileId);
        await _host.SaveProfileFilesAsync();
        await _settings.ClearSessionIdAsync(profileId);
        Session = null;
        Text = "";
        _saveError = null;
        SaveStatus = "";
        Changed?.Invoke();
    }

    // confirm(title, message, accept) asks the user; false keeps everything as it is
    public async Task RestoreDraftAsync(Func<string, string, string, Task<bool>> confirm)
    {
        Guid profileId = RequireProfile();
        LocalInputDraft draft = PendingDraft ?? throw new InvalidOperationException("Сохранённого текста уже нет.");
        MealSessionResponse? previous = draft.SessionId is Guid id ? await _meals.FindSessionAsync(profileId, id) : null;
        bool canResume = previous is not null && previous.Status != "Confirmed";
        if (canResume && draft.MatchesLastSubmittedMessage(previous!.Messages) &&
            !await confirm("Вернуть уже отправленный текст?", "Это уточнение уже есть в расчёте. Восстановление позволит отправить его ещё раз.", "Вернуть"))
        {
            return;
        }

        if (Session is not null && Session.Id != previous?.Id &&
            !await confirm("Восстановить текст?", "Текущий расчёт будет заменён на экране. Записи дневника не изменятся.", "Восстановить"))
        {
            return;
        }

        if (draft.SessionId is not null && !canResume &&
            !await confirm("Открыть как новое описание?", "Исходный расчёт завершён или недоступен. Текст не будет добавлен к другому расчёту.", "Открыть"))
        {
            return;
        }

        MealSessionPurpose purpose = canResume ? PurposeOf(previous!) : draft.Purpose;
        LocalInputDraft restored = new(draft.Text, canResume ? previous!.MealDate : draft.MealDate, purpose,
            canResume ? previous!.Id : null);
        await QueueSave(profileId, restored);
        await FlushAsync();
        Session = canResume ? previous : null;
        Date = restored.MealDate;
        Purpose = restored.Purpose;
        Text = restored.Text;
        if (Session is null)
        {
            await _settings.ClearSessionIdAsync(profileId);
        }
        else
        {
            await _settings.SaveSessionIdAsync(profileId, Session.Id);
        }

        PendingDraft = null;
        DraftProblem = null;
        Changed?.Invoke();
    }

    public async Task DiscardDraftAsync()
    {
        Guid profileId = RequireProfile();
        await _drafts.DiscardAsync(profileId);
        await _host.SaveProfileFilesAsync();
        CancelDelayedSave();
        ++_saveVersion;
        PendingDraft = null;
        DraftProblem = null;
        _saveError = null;
        Text = "";
        SaveStatus = "";
        Changed?.Invoke();
    }

    public async Task ReloadDraftAsync()
    {
        Guid profileId = RequireProfile();
        await ReadDraftAsync(profileId);
        Changed?.Invoke();
    }

    private async Task ClearSubmittedInputAsync(Guid profileId)
    {
        Text = "";
        PendingDraft = null;
        DraftProblem = null;
        CancelDelayedSave();
        ++_saveVersion;
        try
        {
            await _drafts.ClearAsync(profileId);
            await _host.SaveProfileFilesAsync();
            _saveError = null;
            SaveStatus = "";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            DraftProblem = "Расчёт сохранён, но не удалось убрать отправленный текст. Проверьте восстановление ввода.";
        }
    }

    private void UseSession(MealSessionResponse session)
    {
        Session = session;
        Date = session.MealDate;
        Purpose = PurposeOf(session);
    }

    private static MealSessionPurpose PurposeOf(MealSessionResponse session) =>
        CaptureText.IsDish(session) ? MealSessionPurpose.CreateDish : MealSessionPurpose.Diary;

    private Guid RequireProfile() => ProfileId ?? throw new InvalidOperationException("Сначала создайте или выберите профиль.");

    private void RequireUnblocked()
    {
        if (Blocked)
        {
            throw new InvalidOperationException(CaptureText.Blocked);
        }
    }

    public void Dispose()
    {
        _host.Hidden -= SaveNow;
        CancelDelayedSave();
    }
}
